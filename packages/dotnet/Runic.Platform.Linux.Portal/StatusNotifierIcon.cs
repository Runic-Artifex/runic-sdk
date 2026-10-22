using System.Collections.Immutable;
using System.Text;
using Runic.Platform.Runtime;
using Tmds.DBus.Protocol;

namespace Runic.Platform.Linux.Portal;

// A StatusNotifierItem with a com.canonical.dbusmenu menu, exported on the session bus and
// registered with org.kde.StatusNotifierWatcher, which Plasma, Waybar and GNOME's AppIndicator
// extension provide. There is no portal for status icons; sandboxed applications need to own
// org.kde.StatusNotifierItem-* names and talk to the watcher.
internal sealed class StatusNotifierIcon(string? address = null, string watcher = "org.kde.StatusNotifierWatcher",
    PortalApplication? application = null) : IDesktopStatusIcon
{
    internal const string ItemPath = "/StatusNotifierItem", MenuPath = "/MenuBar";
    private const string ItemInterface = "org.kde.StatusNotifierItem", WatcherInterface = "org.kde.StatusNotifierWatcher",
        MenuInterface = "com.canonical.dbusmenu", PropertiesInterface = "org.freedesktop.DBus.Properties";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    // Hosts call ProvideXdgActivationToken just before Activate; an older token has expired.
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(10);
    // Any program on the session bus can call the item and its menu; these bound what one call, or a flood of them, costs.
    // Activations wait for the application's handler in a queue this long; later ones are dropped until it drains.
    internal const int ActivationQueueLimit = 32;
    // dbusmenu id lists (GetGroupProperties, EventGroup) longer than this are refused before they are read in full.
    internal const int MenuIdLimit = 1024;
    // XDG activation tokens are short; a longer one is ignored rather than kept.
    private const int TokenLengthLimit = 4096;
    private const string InvalidArgs = "org.freedesktop.DBus.Error.InvalidArgs", LimitsExceeded = "org.freedesktop.DBus.Error.LimitsExceeded";
    private static int s_instances;
    private readonly PortalApplication _application = application ?? new PortalApplication();
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Bus handlers read the state on the connection's thread; ShowAsync replaces it under this lock.
    private readonly Lock _lock = new();
    // dbusmenu IDs stay with their entries across menu changes, so a click in flight never lands on another entry.
    private readonly Menu.Ids _ids = new();
    // Activations reach the application one at a time, in order, and never after disposal. The queue is bounded, so a
    // peer that clicks faster than the handler runs cannot grow it.
    private readonly System.Threading.Channels.Channel<DesktopStatusIconActivation> _activations =
        System.Threading.Channels.Channel.CreateBounded<DesktopStatusIconActivation>(new System.Threading.Channels.BoundedChannelOptions(ActivationQueueLimit)
        {
            SingleReader = true, FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
        });
    // 1 while activations are being dropped, so the drop is diagnosed once until the queue drains.
    private int _dropping;
    private Task? _pump;
    private DesktopStatusIconState? _state;
    private Menu _menu = Menu.Empty;
    private uint _revision;
    private (string Token, long Timestamp)? _activationToken;
    private DBusConnection? _connection;
    private IDisposable? _watcherOwner;
    private string? _name;
    // Whether the current watcher knows this item; a watcher that starts or restarts does not.
    private volatile bool _registered;
    private volatile bool _disposed;

    public event EventHandler<DesktopStatusIconActivation>? Activated;

    public async ValueTask<PlatformResult<PlatformUnit>> ShowAsync(DesktopStatusIconState state, CancellationToken cancellationToken = default)
    {
        DesktopServiceValidation.StatusIcon(state);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DesktopStatusIconState? previous;
            lock (_lock)
            {
                previous = _state;
                _state = state;
                _menu = Menu.Build(state.Menu, _ids);
                _revision++;
            }
            if (_connection is null)
            {
                try { await ConnectAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is DBusExceptionBase or TimeoutException or NativeBackendUnavailableException)
                {
                    Diagnose("status-icon-bus-unavailable", error,
                        "Check that a D-Bus session bus is running and that a sandbox allows owning org.kde.StatusNotifierItem-* names.");
                    Reset();
                    return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
                }
            }
            else if (previous is not null) Announce(previous, state);
            // A failed registration keeps the icon ready: the next watcher start registers it.
            try { return await RegisterAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is DBusExceptionBase or TimeoutException)
            {
                Diagnose("status-icon-register-failed", error, "The icon registers when the status area restarts or on the next ShowAsync.");
                return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<PlatformResult<PlatformUnit>> HideAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // The watcher drops an item when its bus name disappears.
            Reset();
            return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _activations.Writer.TryComplete();
            Reset();
        }
        finally { _gate.Release(); }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var connection = new DBusConnection(address ?? DBusAddress.Session ?? throw new NativeBackendUnavailableException());
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
            connection.AddMethodHandler(new ItemHandler(this));
            connection.AddMethodHandler(new MenuHandler(this));
            string name = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-{Interlocked.Increment(ref s_instances)}";
            if (!await connection.TryRequestNameAsync(name, RequestNameOptions.None).WaitAsync(Timeout, cancellationToken).ConfigureAwait(false))
                throw new NativeBackendUnavailableException();
            // A watcher that starts or restarts later forgets earlier items; register again then.
            _watcherOwner = await connection.AddMatchAsync(new MatchRule
            {
                Type = MessageType.Signal, Sender = "org.freedesktop.DBus", Path = "/org/freedesktop/DBus",
                Interface = "org.freedesktop.DBus", Member = "NameOwnerChanged", Arg0 = watcher,
            }, static (message, _) =>
            {
                var reader = message.GetBodyReader();
                _ = reader.ReadString(); _ = reader.ReadString();
                return reader.ReadString();
            }, notification =>
            {
                if (!notification.HasValue) return;
                _registered = false;
                if (notification.Value.Length != 0) _ = RegisterAgainAsync(connection);
            }, emitOnCapturedContext: false, flags: ObserverFlags.EmitAll).AsTask().WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
            _connection = connection;
            _name = name;
            _registered = false;
            _pump ??= Task.Run(PumpAsync, CancellationToken.None);
            _ = ReconnectWhenLostAsync(connection);
        }
        catch { connection.Dispose(); throw; }
    }

    private async Task<PlatformResult<PlatformUnit>> RegisterAsync(CancellationToken cancellationToken)
    {
        var connection = _connection!;
        if (!_registered)
        {
            try
            {
                await connection.CallMethodAsync(WatcherCall(connection, "RegisterStatusNotifierItem", "s", _name!))
                    .WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (DBusErrorReplyException error) when (error.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner")
            {
                _application.Diagnose("status-icon-no-watcher", "No StatusNotifierWatcher is running.",
                    "The icon appears once a status area starts: Plasma, Waybar's tray, or GNOME with the AppIndicator extension.");
                return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            }
            _registered = true;
        }
        bool host = await connection.CallMethodAsync(WatcherCall(connection, "Get", "ss", WatcherInterface, "IsStatusNotifierHostRegistered"),
                static (message, _) => message.GetBodyReader().ReadVariantValue().GetBool())
            .WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
        if (!host)
        {
            _application.Diagnose("status-icon-no-host", "The StatusNotifierWatcher has no status area showing icons.",
                "The icon appears once a status area registers with the watcher.");
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    }

    // A call to the watcher's own interface, or with Get to its Properties interface.
    private MessageBuffer WatcherCall(DBusConnection connection, string member, string signature, params string[] arguments)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: watcher, path: "/StatusNotifierWatcher",
                @interface: member == "Get" ? PropertiesInterface : WatcherInterface, member: member, signature: signature);
            foreach (var argument in arguments) writer.WriteString(argument);
            return writer.CreateMessage();
        }
        finally { writer.Dispose(); }
    }

    private async Task RegisterAgainAsync(DBusConnection connection)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _connection != connection) return;
            await RegisterAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DBusExceptionBase or TimeoutException)
        {
            Diagnose("status-icon-register-failed", error, "The icon registers when the status area restarts or on the next ShowAsync.");
        }
        finally { _gate.Release(); }
    }

    // A lost session bus connection takes the icon with it; connect and register again.
    private async Task ReconnectWhenLostAsync(DBusConnection connection)
    {
        try { await connection.DisconnectedAsync().ConfigureAwait(false); }
        catch (Exception) { /* Any end of the connection counts as lost. */ }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _connection != connection) return;
            _application.Diagnose("status-icon-bus-lost", "The session bus connection ended.", "Reconnecting and registering the icon again.");
            Disconnect();
            if (_state is null) return;
            await ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await RegisterAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DBusExceptionBase or TimeoutException or NativeBackendUnavailableException)
        {
            Diagnose("status-icon-bus-unavailable", error, "The icon appears again on the next ShowAsync.");
            Disconnect();
        }
        finally { _gate.Release(); }
    }

    private void Diagnose(string code, Exception error, string remedy) =>
        _application.Diagnose(code, error is DBusErrorReplyException reply ? reply.ErrorName : error.GetType().Name, remedy);

    private void Disconnect()
    {
        _watcherOwner?.Dispose(); _watcherOwner = null;
        var connection = _connection;
        _connection = null;
        connection?.Dispose();
        _name = null;
        _registered = false;
    }

    private void Reset()
    {
        Disconnect();
        lock (_lock) { _state = null; _activationToken = null; }
    }

    // Tells hosts what changed; they read the new values through Properties.
    private void Announce(DesktopStatusIconState previous, DesktopStatusIconState state)
    {
        if (previous.Title != state.Title) Signal(ItemPath, ItemInterface, "NewTitle");
        if (previous.Icon != state.Icon) Signal(ItemPath, ItemInterface, "NewIcon");
        if (previous.AttentionIcon != state.AttentionIcon) Signal(ItemPath, ItemInterface, "NewAttentionIcon");
        if (previous.Title != state.Title || previous.Tooltip != state.Tooltip || previous.Icon != state.Icon)
            Signal(ItemPath, ItemInterface, "NewToolTip");
        if (previous.Status != state.Status)
        {
            var writer = _connection!.GetMessageWriter();
            try
            {
                writer.WriteSignalHeader(path: ItemPath, @interface: ItemInterface, member: "NewStatus", signature: "s");
                writer.WriteString(StatusName(state.Status));
                _connection.TrySendMessage(writer.CreateMessage());
            }
            finally { writer.Dispose(); }
        }
        uint revision;
        lock (_lock) revision = _revision;
        var layout = _connection!.GetMessageWriter();
        try
        {
            layout.WriteSignalHeader(path: MenuPath, @interface: MenuInterface, member: "LayoutUpdated", signature: "ui");
            layout.WriteUInt32(revision);
            layout.WriteInt32(0);
            _connection.TrySendMessage(layout.CreateMessage());
        }
        finally { layout.Dispose(); }
    }

    private void Signal(string path, string @interface, string member)
    {
        var writer = _connection!.GetMessageWriter();
        try
        {
            writer.WriteSignalHeader(path: path, @interface: @interface, member: member);
            _connection.TrySendMessage(writer.CreateMessage());
        }
        finally { writer.Dispose(); }
    }

    private void Raise(DesktopStatusIconActivationKind kind, string? commandId = null)
    {
        (string Token, long Timestamp)? token;
        lock (_lock) { token = _activationToken; _activationToken = null; }
        DesktopActivationContext? context = null;
        if (token is { } fresh && System.Diagnostics.Stopwatch.GetElapsedTime(fresh.Timestamp) < TokenLifetime)
        {
            try { context = new DesktopActivationContext(activationToken: fresh.Token); }
            catch (ArgumentException) { /* An unusable token cannot raise a window; activate without it. */ }
        }
        // TryWrite fails when the queue is full, or after disposal; the newest activation is the one dropped.
        if (!_activations.Writer.TryWrite(new DesktopStatusIconActivation(kind, commandId) { PlatformContext = context })
            && !_disposed && Interlocked.Exchange(ref _dropping, 1) == 0)
            _application.Diagnose("status-icon-activations-dropped",
                $"More than {ActivationQueueLimit} status icon activations waited for the Activated handler; later ones were dropped.",
                "Return from the Activated handler quickly and move longer work off it.");
    }

    private async Task PumpAsync()
    {
        await foreach (var activation in _activations.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_disposed) return;
            if (!_activations.Reader.TryPeek(out _)) Volatile.Write(ref _dropping, 0);
            try { Activated?.Invoke(this, activation); }
            catch (Exception error)
            {
                // As with an event raised on the thread pool, a throwing handler fails the process; later activations still arrive until then.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo capture = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
                ThreadPool.QueueUserWorkItem(static state => ((System.Runtime.ExceptionServices.ExceptionDispatchInfo)state!).Throw(), capture);
            }
        }
    }

    private static string StatusName(DesktopStatusIconStatus status) => status switch
    {
        DesktopStatusIconStatus.Passive => "Passive",
        DesktopStatusIconStatus.NeedsAttention => "NeedsAttention",
        _ => "Active",
    };

    private static readonly string[] ItemProperties =
    [
        "Category", "Id", "Title", "Status", "WindowId", "IconName", "IconPixmap", "OverlayIconName", "OverlayIconPixmap",
        "AttentionIconName", "AttentionIconPixmap", "AttentionMovieName", "ToolTip", "ItemIsMenu", "Menu", "IconThemePath",
    ];

    // Writes one property as a variant: its signature, then its value.
    private bool WriteItemProperty(ref MessageWriter writer, string name)
    {
        DesktopStatusIconState state;
        lock (_lock)
        {
            if (_state is null) return false;
            state = _state;
        }
        var attention = state.AttentionIcon ?? state.Icon;
        switch (name)
        {
            case "Category": writer.WriteVariantString("ApplicationStatus"); break;
            case "Id": writer.WriteVariantString(_application.ApplicationId ?? AppDomain.CurrentDomain.FriendlyName); break;
            case "Title": writer.WriteVariantString(state.Title); break;
            case "Status": writer.WriteVariantString(StatusName(state.Status)); break;
            case "WindowId": writer.WriteVariantInt32(0); break;
            case "IconName": writer.WriteVariantString(state.Icon.IconName ?? ""); break;
            case "IconPixmap": writer.WriteSignature("a(iiay)"); WritePixmaps(ref writer, state.Icon); break;
            case "OverlayIconName": writer.WriteVariantString(""); break;
            case "OverlayIconPixmap": writer.WriteSignature("a(iiay)"); WritePixmaps(ref writer, null); break;
            case "AttentionIconName": writer.WriteVariantString(attention.IconName ?? ""); break;
            case "AttentionIconPixmap": writer.WriteSignature("a(iiay)"); WritePixmaps(ref writer, attention); break;
            case "AttentionMovieName": writer.WriteVariantString(""); break;
            case "ToolTip":
                writer.WriteSignature("(sa(iiay)ss)");
                writer.WriteStructureStart();
                writer.WriteString(state.Icon.IconName ?? "");
                WritePixmaps(ref writer, state.Icon);
                writer.WriteString(state.Title);
                writer.WriteString(state.Tooltip ?? "");
                break;
            case "ItemIsMenu": writer.WriteVariantBool(false); break;
            case "Menu": writer.WriteVariantObjectPath(MenuPath); break;
            case "IconThemePath": writer.WriteVariantString(""); break;
            default: return false;
        }
        return true;
    }

    // StatusNotifierItem pixmaps are ARGB32 in network byte order.
    private static void WritePixmaps(ref MessageWriter writer, DesktopIcon? icon)
    {
        var images = writer.WriteArrayStart(DBusType.Struct);
        foreach (var image in icon?.Images ?? [])
        {
            writer.WriteStructureStart();
            writer.WriteInt32(image.Width);
            writer.WriteInt32(image.Height);
            var argb = new byte[image.Rgba.Length];
            for (int i = 0; i < argb.Length; i += 4)
            {
                argb[i] = image.Rgba[i + 3];
                argb[i + 1] = image.Rgba[i];
                argb[i + 2] = image.Rgba[i + 1];
                argb[i + 3] = image.Rgba[i + 2];
            }
            writer.WriteArray(argb);
        }
        writer.WriteArrayEnd(images);
    }

    private sealed class ItemHandler(StatusNotifierIcon icon) : IPathMethodHandler
    {
        public string Path => ItemPath;
        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            if (context.IsDBusIntrospectRequest)
                context.ReplyIntrospectXml([ItemXml], []);
            else if (request.InterfaceAsString == PropertiesInterface)
                icon.HandleItemProperties(context);
            else if (request.InterfaceAsString == ItemInterface)
            {
                string? signature = request.MemberAsString switch
                {
                    "Activate" or "SecondaryActivate" or "ContextMenu" => "ii",
                    "Scroll" => "is",
                    "ProvideXdgActivationToken" => "s",
                    _ => null,
                };
                if (signature is null) { context.ReplyUnknownMethodError(); return ValueTask.CompletedTask; }
                if (!Takes(context, signature)) return ValueTask.CompletedTask;
                switch (request.MemberAsString)
                {
                    case "Activate": icon.Raise(DesktopStatusIconActivationKind.Primary); break;
                    case "SecondaryActivate": icon.Raise(DesktopStatusIconActivationKind.Secondary); break;
                    case "ProvideXdgActivationToken":
                        string token = request.GetBodyReader().ReadString();
                        if (token.Length <= TokenLengthLimit)
                            lock (icon._lock) icon._activationToken = (token, System.Diagnostics.Stopwatch.GetTimestamp());
                        break;
                    // The menu is served over dbusmenu; scrolling has no meaning here.
                    default: break;
                }
                Empty(context);
            }
            else context.ReplyUnknownMethodError();
            return ValueTask.CompletedTask;
        }
    }

    private void HandleItemProperties(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        switch (context.Request.MemberAsString)
        {
            case "Get" when !Takes(context, "ss"): break;
            case "GetAll" when !Takes(context, "s"): break;
            case "Get":
            {
                string @interface = reader.ReadString(), name = reader.ReadString();
                var reply = context.CreateReplyWriter("v");
                try
                {
                    if (@interface != ItemInterface || !WriteItemProperty(ref reply, name))
                    { context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"No property {name}."); return; }
                    context.Reply(reply.CreateMessage());
                }
                finally { reply.Dispose(); }
                break;
            }
            case "GetAll":
            {
                string @interface = reader.ReadString();
                var reply = context.CreateReplyWriter("a{sv}");
                try
                {
                    var properties = reply.WriteDictionaryStart();
                    if (@interface == ItemInterface)
                        foreach (var name in ItemProperties)
                        {
                            reply.WriteDictionaryEntryStart();
                            reply.WriteString(name);
                            if (!WriteItemProperty(ref reply, name)) reply.WriteVariantString("");
                        }
                    reply.WriteDictionaryEnd(properties);
                    context.Reply(reply.CreateMessage());
                }
                finally { reply.Dispose(); }
                break;
            }
            case "Set": context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", "Status icon properties are read-only."); break;
            default: context.ReplyUnknownMethodError(); break;
        }
    }

    // Checks a call's signature before its body is read, so a peer cannot make a handler misread it.
    private static bool Takes(MethodContext context, string signature)
    {
        string actual = context.Request.SignatureAsString ?? "";
        if (actual == signature) return true;
        context.ReplyError(InvalidArgs, $"{context.Request.MemberAsString} takes \"{signature}\", not \"{actual}\".");
        return false;
    }

    // Reads an "ai" of at most MenuIdLimit ids, without repeats, in their first order; null once the limit is passed.
    private static List<int>? ReadIds(ref Reader reader)
    {
        var ids = new List<int>();
        var seen = new HashSet<int>();
        var end = reader.ReadArrayStart(DBusType.Int32);
        int count = 0;
        while (reader.HasNext(end))
        {
            if (++count > MenuIdLimit) return null;
            int id = reader.ReadInt32();
            if (seen.Add(id)) ids.Add(id);
        }
        return ids;
    }

    private sealed class MenuHandler(StatusNotifierIcon icon) : IPathMethodHandler
    {
        public string Path => MenuPath;
        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.IsDBusIntrospectRequest) { context.ReplyIntrospectXml([MenuXml], []); return ValueTask.CompletedTask; }
            Menu menu; uint revision;
            lock (icon._lock) { menu = icon._menu; revision = icon._revision; }
            var request = context.Request;
            var reader = request.GetBodyReader();
            if (request.InterfaceAsString == PropertiesInterface) { MenuProperties(context, reader); return ValueTask.CompletedTask; }
            if (request.InterfaceAsString != MenuInterface) { context.ReplyUnknownMethodError(); return ValueTask.CompletedTask; }
            string? signature = request.MemberAsString switch
            {
                "GetLayout" => "iias",
                "GetGroupProperties" => "aias",
                "GetProperty" => "is",
                "Event" => "isvu",
                "EventGroup" => "a(isvu)",
                "AboutToShow" => "i",
                "AboutToShowGroup" => "ai",
                _ => null,
            };
            if (signature is null) { context.ReplyUnknownMethodError(); return ValueTask.CompletedTask; }
            if (!Takes(context, signature)) return ValueTask.CompletedTask;
            switch (request.MemberAsString)
            {
                case "GetLayout":
                {
                    int parent = reader.ReadInt32(), depth = reader.ReadInt32();
                    if (!menu.Nodes.TryGetValue(parent, out var node)) { UnknownId(context, parent); break; }
                    var reply = context.CreateReplyWriter("u(ia{sv}av)");
                    try
                    {
                        reply.WriteUInt32(revision);
                        menu.WriteLayout(ref reply, node, depth);
                        context.Reply(reply.CreateMessage());
                    }
                    finally { reply.Dispose(); }
                    break;
                }
                case "GetGroupProperties":
                {
                    // Each id is answered once, so the reply is no longer than the menu.
                    if (ReadIds(ref reader) is not { } ids) { TooMany(context); break; }
                    var reply = context.CreateReplyWriter("a(ia{sv})");
                    try
                    {
                        var items = reply.WriteArrayStart(DBusType.Struct);
                        foreach (int id in ids)
                        {
                            if (!menu.Nodes.TryGetValue(id, out var node)) continue;
                            reply.WriteStructureStart();
                            reply.WriteInt32(id);
                            Menu.WriteProperties(ref reply, node);
                        }
                        reply.WriteArrayEnd(items);
                        context.Reply(reply.CreateMessage());
                    }
                    finally { reply.Dispose(); }
                    break;
                }
                case "GetProperty":
                {
                    int id = reader.ReadInt32(); string name = reader.ReadString();
                    if (!menu.Nodes.TryGetValue(id, out var node)) { UnknownId(context, id); break; }
                    var reply = context.CreateReplyWriter("v");
                    try
                    {
                        if (!Menu.WriteProperty(ref reply, node, name))
                        { context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", $"No property {name}."); break; }
                        context.Reply(reply.CreateMessage());
                    }
                    finally { reply.Dispose(); }
                    break;
                }
                case "Event":
                {
                    int id = reader.ReadInt32(); string kind = reader.ReadString();
                    if (!menu.Nodes.ContainsKey(id)) { UnknownId(context, id); break; }
                    Choose(menu, id, kind);
                    Empty(context);
                    break;
                }
                case "EventGroup":
                {
                    // The events are read in full before any is chosen, so a refused group activates nothing. An entry
                    // clicked twice in one group is chosen once, and an unknown id is reported once.
                    var unknown = new List<int>();
                    var chosen = new List<(int Id, string Kind)>();
                    var seen = new HashSet<(int, string)>();
                    var events = reader.ReadArrayStart(DBusType.Struct);
                    int count = 0;
                    bool tooMany = false;
                    while (reader.HasNext(events))
                    {
                        if (++count > MenuIdLimit) { tooMany = true; break; }
                        reader.AlignStruct();
                        int id = reader.ReadInt32(); string kind = reader.ReadString();
                        _ = reader.ReadVariantValue(); _ = reader.ReadUInt32();
                        if (!seen.Add((id, kind))) continue;
                        if (menu.Nodes.ContainsKey(id)) chosen.Add((id, kind));
                        else if (!unknown.Contains(id)) unknown.Add(id);
                    }
                    if (tooMany) { TooMany(context); break; }
                    foreach (var (id, kind) in chosen) Choose(menu, id, kind);
                    var reply = context.CreateReplyWriter("ai");
                    try { reply.WriteArray(unknown.ToArray()); context.Reply(reply.CreateMessage()); }
                    finally { reply.Dispose(); }
                    break;
                }
                case "AboutToShow":
                {
                    var reply = context.CreateReplyWriter("b");
                    try { reply.WriteBool(false); context.Reply(reply.CreateMessage()); }
                    finally { reply.Dispose(); }
                    break;
                }
                case "AboutToShowGroup":
                {
                    var reply = context.CreateReplyWriter("aiai");
                    try { reply.WriteArray(Array.Empty<int>()); reply.WriteArray(Array.Empty<int>()); context.Reply(reply.CreateMessage()); }
                    finally { reply.Dispose(); }
                    break;
                }
                default: context.ReplyUnknownMethodError(); break;
            }
            return ValueTask.CompletedTask;
        }

        private void Choose(Menu menu, int id, string kind)
        {
            // Hosts also report "hovered", "opened" and "closed"; only a click chooses.
            if (kind == "clicked" && menu.Nodes[id].Item is DesktopMenuItem { IsEnabled: true } command)
                icon.Raise(DesktopStatusIconActivationKind.MenuItem, command.CommandId);
        }

        private static void UnknownId(MethodContext context, int id) =>
            context.ReplyError(InvalidArgs, $"No menu entry {id}.");

        private static void TooMany(MethodContext context) =>
            context.ReplyError(LimitsExceeded, $"A menu call takes at most {MenuIdLimit} ids or events.");

        private static void MenuProperties(MethodContext context, Reader reader)
        {
            switch (context.Request.MemberAsString)
            {
                case "Set": context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", "Menu properties are read-only."); return;
                case "Get" when !Takes(context, "ss"): return;
                case "GetAll" when !Takes(context, "s"): return;
                case "Get" or "GetAll": break;
                default: context.ReplyUnknownMethodError(); return;
            }
            string @interface = reader.ReadString();
            bool all = context.Request.MemberAsString == "GetAll";
            string? name = all ? null : reader.ReadString();
            var reply = context.CreateReplyWriter(all ? "a{sv}" : "v");
            try
            {
                if (all)
                {
                    var properties = reply.WriteDictionaryStart();
                    if (@interface == MenuInterface)
                        foreach (var property in MenuObjectProperties)
                        { reply.WriteDictionaryEntryStart(); reply.WriteString(property); WriteMenuProperty(ref reply, property); }
                    reply.WriteDictionaryEnd(properties);
                }
                else if (@interface != MenuInterface || !WriteMenuProperty(ref reply, name!))
                { context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"No property {name}."); return; }
                context.Reply(reply.CreateMessage());
            }
            finally { reply.Dispose(); }
        }

        private static readonly string[] MenuObjectProperties = ["Version", "TextDirection", "Status", "IconThemePath"];
        private static bool WriteMenuProperty(ref MessageWriter writer, string name)
        {
            switch (name)
            {
                case "Version": writer.WriteVariantUInt32(3); return true;
                case "TextDirection": writer.WriteVariantString("ltr"); return true;
                case "Status": writer.WriteVariantString("normal"); return true;
                case "IconThemePath": writer.WriteSignature("as"); writer.WriteArray(Array.Empty<string>()); return true;
                default: return false;
            }
        }
    }

    private static void Empty(MethodContext context)
    {
        if (context.NoReplyExpected) return;
        var reply = context.CreateReplyWriter(null);
        try { context.Reply(reply.CreateMessage()); }
        finally { reply.Dispose(); }
    }

    // A dbusmenu tree with 0 for the root. Entries keep their numeric ID across menu changes: commands by their ID,
    // separators and submenus by their place among their parent's entries of the same kind. IDs are never reused.
    private sealed class Menu
    {
        internal sealed record Node(int Id, DesktopMenuEntry? Item, ImmutableArray<int> Children);
        internal Dictionary<int, Node> Nodes { get; } = [];
        internal static Menu Empty { get; } = Build([], new Ids());

        internal sealed class Ids
        {
            private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
            private int _next = 1;
            internal int For(string key) => _ids.TryGetValue(key, out int id) ? id : _ids[key] = _next++;
        }

        internal static Menu Build(ImmutableArray<DesktopMenuEntry> items, Ids ids)
        {
            var menu = new Menu();
            menu.Add(0, "", null, items, ids);
            return menu;
        }

        private void Add(int id, string path, DesktopMenuEntry? item, ImmutableArray<DesktopMenuEntry> children, Ids ids)
        {
            var childIds = ImmutableArray.CreateBuilder<int>(children.Length);
            int separators = 0;
            var submenus = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var child in children)
            {
                string? key = child switch
                {
                    DesktopMenuItem command => "command:" + command.CommandId,
                    DesktopMenu submenu => $"{path}/submenu:{submenu.Title}#{(submenus[submenu.Title] = submenus.GetValueOrDefault(submenu.Title) + 1)}",
                    DesktopMenuSeparator => $"{path}/separator#{++separators}",
                    // An entry kind of a later release.
                    _ => null,
                };
                if (key is null) continue;
                int childId = ids.For(key);
                childIds.Add(childId);
                Add(childId, key, child, child is DesktopMenu nested ? nested.Items : [], ids);
            }
            Nodes[id] = new Node(id, item, childIds.ToImmutable());
        }

        internal void WriteLayout(ref MessageWriter writer, Node node, int depth)
        {
            writer.WriteStructureStart();
            writer.WriteInt32(node.Id);
            WriteProperties(ref writer, node);
            var children = writer.WriteArrayStart(DBusType.Variant);
            if (depth != 0)
                foreach (int child in node.Children)
                {
                    writer.WriteSignature("(ia{sv}av)");
                    WriteLayout(ref writer, Nodes[child], depth - 1);
                }
            writer.WriteArrayEnd(children);
        }

        private static readonly string[] Properties = ["type", "label", "enabled", "toggle-type", "toggle-state", "children-display"];

        // Only properties that differ from dbusmenu's defaults are sent.
        internal static void WriteProperties(ref MessageWriter writer, Node node)
        {
            var properties = writer.WriteDictionaryStart();
            foreach (var name in Properties)
            {
                if (!Has(node, name)) continue;
                writer.WriteDictionaryEntryStart();
                writer.WriteString(name);
                WriteProperty(ref writer, node, name);
            }
            writer.WriteDictionaryEnd(properties);
        }

        private static bool Has(Node node, string name) => (node.Item, name) switch
        {
            (null, "children-display") => true,
            (DesktopMenuSeparator, "type") => true,
            (DesktopMenuItem, "label") or (DesktopMenu, "label") => true,
            (DesktopMenuItem { IsEnabled: false }, "enabled") or (DesktopMenu { IsEnabled: false }, "enabled") => true,
            (DesktopMenuItem { IsChecked: not null }, "toggle-type" or "toggle-state") => true,
            (DesktopMenu, "children-display") => true,
            _ => false,
        };

        internal static bool WriteProperty(ref MessageWriter writer, Node node, string name)
        {
            switch (name)
            {
                case "type": writer.WriteVariantString(node.Item is DesktopMenuSeparator ? "separator" : "standard"); return true;
                // An underscore marks dbusmenu's access key; double it to show it literally.
                case "label": writer.WriteVariantString(Label(node.Item).Replace("_", "__", StringComparison.Ordinal)); return true;
                case "enabled": writer.WriteVariantBool(node.Item switch { DesktopMenuItem c => c.IsEnabled, DesktopMenu s => s.IsEnabled, _ => true }); return true;
                case "visible": writer.WriteVariantBool(true); return true;
                case "toggle-type": writer.WriteVariantString(node.Item is DesktopMenuItem { IsChecked: not null } ? "checkmark" : ""); return true;
                case "toggle-state": writer.WriteVariantInt32(node.Item is DesktopMenuItem { IsChecked: true } ? 1 : 0); return true;
                case "children-display": writer.WriteVariantString(node.Item is null or DesktopMenu ? "submenu" : ""); return true;
                default: return false;
            }
        }

        private static string Label(DesktopMenuEntry? item) => item switch
        {
            DesktopMenuItem command => command.Title,
            DesktopMenu submenu => submenu.Title,
            _ => "",
        };
    }

    private static readonly ReadOnlyMemory<byte> ItemXml = Encoding.UTF8.GetBytes("""
        <interface name="org.kde.StatusNotifierItem">
          <property name="Category" type="s" access="read"/>
          <property name="Id" type="s" access="read"/>
          <property name="Title" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="WindowId" type="i" access="read"/>
          <property name="IconName" type="s" access="read"/>
          <property name="IconPixmap" type="a(iiay)" access="read"/>
          <property name="OverlayIconName" type="s" access="read"/>
          <property name="OverlayIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionIconName" type="s" access="read"/>
          <property name="AttentionIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionMovieName" type="s" access="read"/>
          <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
          <property name="ItemIsMenu" type="b" access="read"/>
          <property name="Menu" type="o" access="read"/>
          <property name="IconThemePath" type="s" access="read"/>
          <method name="ContextMenu"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Activate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="SecondaryActivate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Scroll"><arg name="delta" type="i" direction="in"/><arg name="orientation" type="s" direction="in"/></method>
          <method name="ProvideXdgActivationToken"><arg name="token" type="s" direction="in"/></method>
          <signal name="NewTitle"/>
          <signal name="NewIcon"/>
          <signal name="NewAttentionIcon"/>
          <signal name="NewOverlayIcon"/>
          <signal name="NewToolTip"/>
          <signal name="NewStatus"><arg name="status" type="s"/></signal>
        </interface>
        """);

    private static readonly ReadOnlyMemory<byte> MenuXml = Encoding.UTF8.GetBytes("""
        <interface name="com.canonical.dbusmenu">
          <property name="Version" type="u" access="read"/>
          <property name="TextDirection" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="IconThemePath" type="as" access="read"/>
          <method name="GetLayout"><arg type="i" name="parentId" direction="in"/><arg type="i" name="recursionDepth" direction="in"/><arg type="as" name="propertyNames" direction="in"/><arg type="u" name="revision" direction="out"/><arg type="(ia{sv}av)" name="layout" direction="out"/></method>
          <method name="GetGroupProperties"><arg type="ai" name="ids" direction="in"/><arg type="as" name="propertyNames" direction="in"/><arg type="a(ia{sv})" name="properties" direction="out"/></method>
          <method name="GetProperty"><arg type="i" name="id" direction="in"/><arg type="s" name="name" direction="in"/><arg type="v" name="value" direction="out"/></method>
          <method name="Event"><arg type="i" name="id" direction="in"/><arg type="s" name="eventId" direction="in"/><arg type="v" name="data" direction="in"/><arg type="u" name="timestamp" direction="in"/></method>
          <method name="EventGroup"><arg type="a(isvu)" name="events" direction="in"/><arg type="ai" name="idErrors" direction="out"/></method>
          <method name="AboutToShow"><arg type="i" name="id" direction="in"/><arg type="b" name="needUpdate" direction="out"/></method>
          <method name="AboutToShowGroup"><arg type="ai" name="ids" direction="in"/><arg type="ai" name="updatesNeeded" direction="out"/><arg type="ai" name="idErrors" direction="out"/></method>
          <signal name="ItemsPropertiesUpdated"><arg type="a(ia{sv})" name="updatedProps"/><arg type="a(ias)" name="removedProps"/></signal>
          <signal name="LayoutUpdated"><arg type="u" name="revision"/><arg type="i" name="parent"/></signal>
          <signal name="ItemActivationRequested"><arg type="i" name="id"/><arg type="u" name="timestamp"/></signal>
        </interface>
        """);
}
