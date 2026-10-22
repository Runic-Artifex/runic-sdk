using System.Collections.Immutable;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

// A StatusNotifierItem with a com.canonical.dbusmenu menu, exported on the session bus and
// registered with org.kde.StatusNotifierWatcher, which Plasma, Waybar and GNOME's AppIndicator
// extension provide. There is no portal for status icons; sandboxed applications need to own
// org.kde.StatusNotifierItem-* names and talk to the watcher. The icon uses the process's shared session connection;
// hosts look for the item and menu at fixed paths, so a second icon shown at the same time gets a connection of its own.
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
    private Session? _session;
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
            if (_session is null)
            {
                try { await ConnectAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is DBusException or TimeoutException or NativeBackendUnavailableException)
                {
                    Diagnose("status-icon-bus-unavailable", error,
                        "Check that a D-Bus session bus is running and that a sandbox allows owning org.kde.StatusNotifierItem-* names.");
                    await ResetAsync().ConfigureAwait(false);
                    return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
                }
            }
            else if (previous is not null) Announce(_session, previous, state);
            // A failed registration keeps the icon ready: the next watcher start registers it.
            try { return await RegisterAsync(_session!, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is DBusException or TimeoutException)
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
            await ResetAsync().ConfigureAwait(false);
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
            await ResetAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // The icon's place on the bus: its connection, its exported item and menu, its name and the watch on the watcher.
    private sealed class Session(DBusConnection connection, DBusConnection? own, string name)
    {
        internal DBusConnection Connection { get; } = connection;
        // A connection of the icon's own, when another icon of the process holds the paths on the shared one.
        internal DBusConnection? Own { get; } = own;
        internal string Name { get; } = name;
        internal ItemObject Item { get; set; } = null!;
        internal MenuObject Menu { get; set; } = null!;
        internal List<IAsyncDisposable> Exports { get; } = [];
        internal DBusSignalSubscription? WatcherOwner { get; set; }
        internal CancellationTokenSource Stopped { get; } = new();
        // Whether the current watcher knows this item; a watcher that starts or restarts does not.
        internal volatile bool Registered;
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var connection = (await PortalBus.Session(address, PortalConnection.RegisteredIdentity(_application)).ConnectAsync(cancellationToken).ConfigureAwait(false)).Connection;
        string name = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-{Interlocked.Increment(ref s_instances)}";
        var session = new Session(connection, null, name);
        if (!TryExport(session))
        {
            // Another status icon of this process holds the item and menu paths on the shared connection.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Timeout);
            PlatformResult<DBusConnection> own;
            try
            {
                own = address is null ? await DBusConnection.ConnectAsync(DBusBus.Session, deadline.Token).ConfigureAwait(false)
                    : await DBusConnection.ConnectToAddressAsync(address, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException(); }
            if (own is not PlatformResult<DBusConnection>.Success(var dedicated)) throw new NativeBackendUnavailableException();
            session = new Session(dedicated, dedicated, name);
            TryExport(session);
        }
        try
        {
            if (await session.Connection.RequestNameAsync(name, DBusRequestNameOptions.DoNotQueue, cancellationToken).AsTask().WaitAsync(Timeout, cancellationToken)
                .ConfigureAwait(false) != DBusRequestNameReply.PrimaryOwner)
                throw new NativeBackendUnavailableException();
            // A watcher that starts or restarts later forgets earlier items; register again then.
            session.WatcherOwner = await session.Connection.SubscribeAsync(new DBusSignalFilter
            {
                Sender = "org.freedesktop.DBus", Path = new DBusObjectPath("/org/freedesktop/DBus"),
                Interface = "org.freedesktop.DBus", Member = "NameOwnerChanged", Arg0 = watcher,
            }, cancellationToken).AsTask().WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
            _ = PortalValues.PumpAsync(session.WatcherOwner, signal =>
            {
                if (signal.Arguments is not [_, _, { Signature.Value: "s" } owner]) return;
                session.Registered = false;
                if (owner.GetString().Length != 0) _ = RegisterAgainAsync(session);
            }, static _ => { });
        }
        catch
        {
            await CloseAsync(session, releaseName: true).ConfigureAwait(false);
            throw;
        }
        _session = session;
        _pump ??= Task.Run(PumpAsync, CancellationToken.None);
        _ = ReconnectWhenLostAsync(session);
    }

    // Exports the item and the menu; false when the paths are taken on the connection.
    private bool TryExport(Session session)
    {
        session.Item = new ItemObject(this);
        session.Menu = new MenuObject(this);
        try
        {
            session.Exports.Add(session.Connection.Export(new DBusObjectPath(ItemPath), session.Item));
            session.Exports.Add(session.Connection.Export(new DBusObjectPath(MenuPath), session.Menu));
            return true;
        }
        catch (InvalidOperationException) when (session.Own is null)
        {
            foreach (var export in session.Exports) _ = export.DisposeAsync().AsTask();
            session.Exports.Clear();
            return false;
        }
    }

    private async Task<PlatformResult<PlatformUnit>> RegisterAsync(Session session, CancellationToken cancellationToken)
    {
        var proxy = new DBusProxy(session.Connection, watcher, new DBusObjectPath("/StatusNotifierWatcher"));
        if (!session.Registered)
        {
            try
            {
                await proxy.CallAsync(WatcherInterface, "RegisterStatusNotifierItem", [DBusValue.From(session.Name)], cancellationToken: cancellationToken)
                    .AsTask().WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (DBusException error) when (error.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner")
            {
                _application.Diagnose("status-icon-no-watcher", "No StatusNotifierWatcher is running.",
                    "The icon appears once a status area starts: Plasma, Waybar's tray, or GNOME with the AppIndicator extension.");
                return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            }
            session.Registered = true;
        }
        bool host = (await proxy.GetPropertyAsync(WatcherInterface, "IsStatusNotifierHostRegistered", cancellationToken).AsTask()
            .WaitAsync(Timeout, cancellationToken).ConfigureAwait(false)).GetBoolean();
        if (!host)
        {
            _application.Diagnose("status-icon-no-host", "The StatusNotifierWatcher has no status area showing icons.",
                "The icon appears once a status area registers with the watcher.");
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    }

    private async Task RegisterAgainAsync(Session session)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _session != session) return;
            await RegisterAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DBusException or TimeoutException)
        {
            Diagnose("status-icon-register-failed", error, "The icon registers when the status area restarts or on the next ShowAsync.");
        }
        finally { _gate.Release(); }
    }

    // A lost session bus connection takes the icon with it; connect and register again.
    private async Task ReconnectWhenLostAsync(Session session)
    {
        using (var watch = CancellationTokenSource.CreateLinkedTokenSource(session.Connection.Disconnected, session.Stopped.Token))
        {
            try { await Task.Delay(System.Threading.Timeout.Infinite, watch.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (session.Stopped.IsCancellationRequested) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _session != session) return;
            _application.Diagnose("status-icon-bus-lost", "The session bus connection ended.", "Reconnecting and registering the icon again.");
            await DisconnectAsync().ConfigureAwait(false);
            if (_state is null) return;
            await ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await RegisterAsync(_session!, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DBusException or TimeoutException or NativeBackendUnavailableException)
        {
            Diagnose("status-icon-bus-unavailable", error, "The icon appears again on the next ShowAsync.");
            await DisconnectAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private void Diagnose(string code, Exception error, string remedy) =>
        _application.Diagnose(code, error is DBusException reply ? reply.ErrorName : error.GetType().Name, remedy);

    private async Task DisconnectAsync()
    {
        var session = _session;
        _session = null;
        if (session is not null) await CloseAsync(session, releaseName: true).ConfigureAwait(false);
    }

    // Takes the icon off the bus. On the shared connection the name is given up, which tells the watcher the item is gone.
    private static async Task CloseAsync(Session session, bool releaseName)
    {
        session.Stopped.Cancel();
        if (session.WatcherOwner is { } subscription) await subscription.DisposeAsync().ConfigureAwait(false);
        if (session.Own is { } own)
        {
            await own.DisposeAsync().ConfigureAwait(false);
            return;
        }
        if (releaseName && !session.Connection.Disconnected.IsCancellationRequested)
        {
            try { await session.Connection.ReleaseNameAsync(session.Name).AsTask().WaitAsync(Timeout).ConfigureAwait(false); }
            catch (Exception error) when (error is DBusException or TimeoutException) { /* Closing the connection releases the name too. */ }
        }
        foreach (var export in session.Exports) await export.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ResetAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        lock (_lock) { _state = null; _activationToken = null; }
    }

    // Tells hosts what changed; they read the new values through Properties.
    private void Announce(Session session, DesktopStatusIconState previous, DesktopStatusIconState state)
    {
        var item = session.Item;
        if (previous.Title != state.Title) item.Emit("NewTitle");
        if (previous.Icon != state.Icon) item.Emit("NewIcon");
        if (previous.AttentionIcon != state.AttentionIcon) item.Emit("NewAttentionIcon");
        if (previous.Title != state.Title || previous.Tooltip != state.Tooltip || previous.Icon != state.Icon) item.Emit("NewToolTip");
        if (previous.Status != state.Status) item.Emit("NewStatus", DBusValue.From(StatusName(state.Status)));
        uint revision;
        lock (_lock) revision = _revision;
        session.Menu.Emit("LayoutUpdated", DBusValue.From(revision), DBusValue.From(0));
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

    // One item property; null for an unknown name or while nothing is shown.
    private DBusValue? ItemProperty(string name)
    {
        DesktopStatusIconState state;
        lock (_lock)
        {
            if (_state is null) return null;
            state = _state;
        }
        var attention = state.AttentionIcon ?? state.Icon;
        return name switch
        {
            "Category" => DBusValue.From("ApplicationStatus"),
            "Id" => DBusValue.From(_application.ApplicationId ?? AppDomain.CurrentDomain.FriendlyName),
            "Title" => DBusValue.From(state.Title),
            "Status" => DBusValue.From(StatusName(state.Status)),
            "WindowId" => DBusValue.From(0),
            "IconName" => DBusValue.From(state.Icon.IconName ?? ""),
            "IconPixmap" => Pixmaps(state.Icon),
            "OverlayIconName" => DBusValue.From(""),
            "OverlayIconPixmap" => Pixmaps(null),
            "AttentionIconName" => DBusValue.From(attention.IconName ?? ""),
            "AttentionIconPixmap" => Pixmaps(attention),
            "AttentionMovieName" => DBusValue.From(""),
            "ToolTip" => DBusValue.Struct(DBusValue.From(state.Icon.IconName ?? ""), Pixmaps(state.Icon), DBusValue.From(state.Title), DBusValue.From(state.Tooltip ?? "")),
            "ItemIsMenu" => DBusValue.From(false),
            "Menu" => DBusValue.From(new DBusObjectPath(MenuPath)),
            "IconThemePath" => DBusValue.From(""),
            _ => null,
        };
    }

    // StatusNotifierItem pixmaps are ARGB32 in network byte order.
    private static DBusValue Pixmaps(DesktopIcon? icon) => DBusValue.Array("(iiay)", (icon?.Images ?? []).Select(static image =>
    {
        var argb = new byte[image.Rgba.Length];
        for (int i = 0; i < argb.Length; i += 4)
        {
            argb[i] = image.Rgba[i + 3];
            argb[i + 1] = image.Rgba[i];
            argb[i + 2] = image.Rgba[i + 1];
            argb[i + 3] = image.Rgba[i + 2];
        }
        return DBusValue.Struct(DBusValue.From(image.Width), DBusValue.From(image.Height), DBusValue.ByteArray(argb));
    }));

    // Checks a call's signature before its arguments are used, so a peer cannot make a handler misread them.
    private static void Takes(DBusMethodContext context, string signature)
    {
        if (context.Signature.Value != signature)
            throw new DBusException(DBusException.InvalidArgs, $"{context.Member} takes \"{signature}\", not \"{context.Signature.Value}\".");
    }

    private static DBusException UnknownMethod(DBusMethodContext context) =>
        new(DBusException.UnknownMethod, $"No method {context.Member} on {context.Interface}.");

    // The org.kde.StatusNotifierItem object. Calls are answered one at a time, in the order they arrive, so the activation
    // token a host provides just before Activate reaches that activation.
    private sealed class ItemObject(StatusNotifierIcon icon) : DBusObject
    {
        protected override int MaxConcurrentCalls => 1;
        protected override string IntrospectionXml => ItemXml;

        internal void Emit(string member, params ReadOnlySpan<DBusValue> arguments) => EmitSignal(ItemInterface, member, arguments);

        protected override ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context)
        {
            if (context.Interface != ItemInterface) throw UnknownMethod(context);
            Takes(context, context.Member switch
            {
                "Activate" or "SecondaryActivate" or "ContextMenu" => "ii",
                "Scroll" => "is",
                "ProvideXdgActivationToken" => "s",
                _ => throw UnknownMethod(context),
            });
            switch (context.Member)
            {
                case "Activate": icon.Raise(DesktopStatusIconActivationKind.Primary); break;
                case "SecondaryActivate": icon.Raise(DesktopStatusIconActivationKind.Secondary); break;
                case "ProvideXdgActivationToken":
                    string token = context.Arguments[0].GetString();
                    if (token.Length <= TokenLengthLimit)
                        lock (icon._lock) icon._activationToken = (token, System.Diagnostics.Stopwatch.GetTimestamp());
                    break;
                // The menu is served over dbusmenu; scrolling has no meaning here.
                default: break;
            }
            return ValueTask.FromResult(ImmutableArray<DBusValue>.Empty);
        }

        protected override ValueTask<DBusValue> GetPropertyAsync(string interfaceName, string name, DBusMethodContext context) =>
            interfaceName == ItemInterface && icon.ItemProperty(name) is { } value ? ValueTask.FromResult(value)
                : throw new DBusException(DBusException.UnknownProperty, $"No property {name}.");

        protected override ValueTask<ImmutableDictionary<string, DBusValue>> GetAllPropertiesAsync(string interfaceName, DBusMethodContext context)
        {
            var properties = ImmutableDictionary.CreateBuilder<string, DBusValue>(StringComparer.Ordinal);
            if (interfaceName == ItemInterface)
                foreach (var name in ItemProperties) properties[name] = icon.ItemProperty(name) ?? DBusValue.From("");
            return ValueTask.FromResult(properties.ToImmutable());
        }

        protected override ValueTask SetPropertyAsync(string interfaceName, string name, DBusValue value, DBusMethodContext context) =>
            throw new DBusException(DBusException.PropertyReadOnly, "Status icon properties are read-only.");
    }

    // The com.canonical.dbusmenu object, answered one call at a time like the item.
    private sealed class MenuObject(StatusNotifierIcon icon) : DBusObject
    {
        private static readonly string[] MenuObjectProperties = ["Version", "TextDirection", "Status", "IconThemePath"];

        protected override int MaxConcurrentCalls => 1;
        protected override string IntrospectionXml => MenuXml;

        internal void Emit(string member, params ReadOnlySpan<DBusValue> arguments) => EmitSignal(MenuInterface, member, arguments);

        protected override ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context)
        {
            if (context.Interface != MenuInterface) throw UnknownMethod(context);
            Menu menu; uint revision;
            lock (icon._lock) { menu = icon._menu; revision = icon._revision; }
            Takes(context, context.Member switch
            {
                "GetLayout" => "iias",
                "GetGroupProperties" => "aias",
                "GetProperty" => "is",
                "Event" => "isvu",
                "EventGroup" => "a(isvu)",
                "AboutToShow" => "i",
                "AboutToShowGroup" => "ai",
                _ => throw UnknownMethod(context),
            });
            var arguments = context.Arguments;
            ImmutableArray<DBusValue> reply;
            switch (context.Member)
            {
                case "GetLayout":
                {
                    int parent = arguments[0].GetInt32(), depth = arguments[1].GetInt32();
                    if (!menu.Nodes.TryGetValue(parent, out var node)) throw UnknownId(parent);
                    reply = [DBusValue.From(revision), menu.Layout(node, depth)];
                    break;
                }
                case "GetGroupProperties":
                {
                    // Each id is answered once, so the reply is no longer than the menu.
                    var items = arguments[0].GetItems();
                    if (items.Length > MenuIdLimit) throw TooMany();
                    var answered = new List<DBusValue>();
                    var seen = new HashSet<int>();
                    foreach (var item in items)
                    {
                        int id = item.GetInt32();
                        if (seen.Add(id) && menu.Nodes.TryGetValue(id, out var node)) answered.Add(DBusValue.Struct(DBusValue.From(id), Menu.Properties(node)));
                    }
                    reply = [DBusValue.Array("(ia{sv})", answered)];
                    break;
                }
                case "GetProperty":
                {
                    int id = arguments[0].GetInt32(); string name = arguments[1].GetString();
                    if (!menu.Nodes.TryGetValue(id, out var node)) throw UnknownId(id);
                    reply = Menu.Property(node, name) is { } value ? [DBusValue.Variant(value)]
                        : throw new DBusException(DBusException.InvalidArgs, $"No property {name}.");
                    break;
                }
                case "Event":
                {
                    int id = arguments[0].GetInt32(); string kind = arguments[1].GetString();
                    if (!menu.Nodes.ContainsKey(id)) throw UnknownId(id);
                    Choose(menu, id, kind);
                    reply = [];
                    break;
                }
                case "EventGroup":
                {
                    // The events are read in full before any is chosen, so a refused group activates nothing. An entry
                    // clicked twice in one group is chosen once, and an unknown id is reported once.
                    var events = arguments[0].GetItems();
                    if (events.Length > MenuIdLimit) throw TooMany();
                    var unknown = new List<int>();
                    var chosen = new List<(int Id, string Kind)>();
                    var seen = new HashSet<(int, string)>();
                    foreach (var entry in events)
                    {
                        var fields = entry.GetFields();
                        int id = fields[0].GetInt32(); string kind = fields[1].GetString();
                        if (!seen.Add((id, kind))) continue;
                        if (menu.Nodes.ContainsKey(id)) chosen.Add((id, kind));
                        else if (!unknown.Contains(id)) unknown.Add(id);
                    }
                    foreach (var (id, kind) in chosen) Choose(menu, id, kind);
                    reply = [DBusValue.Array("i", unknown.Select(static id => DBusValue.From(id)))];
                    break;
                }
                case "AboutToShow": reply = [DBusValue.From(false)]; break;
                default: reply = [DBusValue.Array("i", []), DBusValue.Array("i", [])]; break; // AboutToShowGroup
            }
            return ValueTask.FromResult(reply);
        }

        private void Choose(Menu menu, int id, string kind)
        {
            // Hosts also report "hovered", "opened" and "closed"; only a click chooses.
            if (kind == "clicked" && menu.Nodes[id].Item is DesktopMenuItem { IsEnabled: true } command)
                icon.Raise(DesktopStatusIconActivationKind.MenuItem, command.CommandId);
        }

        private static DBusException UnknownId(int id) => new(DBusException.InvalidArgs, $"No menu entry {id}.");

        private static DBusException TooMany() => new(DBusException.LimitsExceeded, $"A menu call takes at most {MenuIdLimit} ids or events.");

        protected override ValueTask<DBusValue> GetPropertyAsync(string interfaceName, string name, DBusMethodContext context) =>
            interfaceName == MenuInterface && MenuProperty(name) is { } value ? ValueTask.FromResult(value)
                : throw new DBusException(DBusException.UnknownProperty, $"No property {name}.");

        protected override ValueTask<ImmutableDictionary<string, DBusValue>> GetAllPropertiesAsync(string interfaceName, DBusMethodContext context)
        {
            var properties = ImmutableDictionary.CreateBuilder<string, DBusValue>(StringComparer.Ordinal);
            if (interfaceName == MenuInterface)
                foreach (var name in MenuObjectProperties) properties[name] = MenuProperty(name)!.Value;
            return ValueTask.FromResult(properties.ToImmutable());
        }

        protected override ValueTask SetPropertyAsync(string interfaceName, string name, DBusValue value, DBusMethodContext context) =>
            throw new DBusException(DBusException.PropertyReadOnly, "Menu properties are read-only.");

        private static DBusValue? MenuProperty(string name) => name switch
        {
            "Version" => DBusValue.From(3u),
            "TextDirection" => DBusValue.From("ltr"),
            "Status" => DBusValue.From("normal"),
            "IconThemePath" => DBusValue.StringArray([]),
            _ => null,
        };
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

        internal DBusValue Layout(Node node, int depth) => DBusValue.Struct(DBusValue.From(node.Id), Properties(node),
            DBusValue.Array("v", depth == 0 ? [] : node.Children.Select(child => DBusValue.Variant(Layout(Nodes[child], depth - 1)))));

        private static readonly string[] PropertyNames = ["type", "label", "enabled", "toggle-type", "toggle-state", "children-display"];

        // Only properties that differ from dbusmenu's defaults are sent.
        internal static DBusValue Properties(Node node) => DBusValue.VariantDictionary(PropertyNames.Where(name => Has(node, name))
            .Select(name => KeyValuePair.Create(name, Property(node, name)!.Value)));

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

        internal static DBusValue? Property(Node node, string name) => name switch
        {
            "type" => DBusValue.From(node.Item is DesktopMenuSeparator ? "separator" : "standard"),
            // An underscore marks dbusmenu's access key; double it to show it literally.
            "label" => DBusValue.From(Label(node.Item).Replace("_", "__", StringComparison.Ordinal)),
            "enabled" => DBusValue.From(node.Item switch { DesktopMenuItem c => c.IsEnabled, DesktopMenu s => s.IsEnabled, _ => true }),
            "visible" => DBusValue.From(true),
            "toggle-type" => DBusValue.From(node.Item is DesktopMenuItem { IsChecked: not null } ? "checkmark" : ""),
            "toggle-state" => DBusValue.From(node.Item is DesktopMenuItem { IsChecked: true } ? 1 : 0),
            "children-display" => DBusValue.From(node.Item is null or DesktopMenu ? "submenu" : ""),
            _ => null,
        };

        private static string Label(DesktopMenuEntry? item) => item switch
        {
            DesktopMenuItem command => command.Title,
            DesktopMenu submenu => submenu.Title,
            _ => "",
        };
    }

    private const string ItemXml = """
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
        """;

    private const string MenuXml = """
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
        """;
}
