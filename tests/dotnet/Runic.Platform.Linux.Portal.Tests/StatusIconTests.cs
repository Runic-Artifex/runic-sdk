using System.Collections.Immutable;
using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Runic.Platform.Runtime;
using TUnit.Core;
using Tmds.DBus.Protocol;

internal sealed class StatusIconTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly int[] RootMenu = [0];

    private static DesktopStatusIconState State(DesktopStatusIconStatus status = DesktopStatusIconStatus.Active) =>
        new(new DesktopIcon { ThemeName = "system-software-update", Images = [new DesktopIconImage(1, 1, [0x10, 0x20, 0x30, 0x40])] }, "Nix Manager")
        {
            Status = status,
            Tooltip = "2 updates",
            AttentionIcon = new DesktopIcon { ThemeName = "software-update-available" },
            Menu =
            [
                new DesktopMenuCommand("open", "Open_dashboard"),
                new DesktopMenuSeparator(),
                new DesktopMenuCommand("check", "Check daily") { Checked = true },
                new DesktopMenuSubmenu("More", [new DesktopMenuCommand("disabled", "Unavailable") { Enabled = false }]),
            ],
        };

    [Test, DisplayName("an unavailable status icon reports every request unavailable")]
    public async Task UnavailableAsync()
    {
        await using var icon = DesktopStatusIcons.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        Check(await icon.ShowAsync(State()) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "show unavailable");
        Check(await icon.HideAsync() is PlatformResult<PlatformUnit>.Unavailable, "hide unavailable");
    }

    [Test, DisplayName("status icon state is bounded and menu IDs are unique")]
    public void Validation()
    {
        DesktopServiceValidation.StatusIcon(State());
        Rejects(State() with { Title = " " }, "blank title");
        Rejects(State() with { Icon = new DesktopIcon() }, "icon without name or image");
        Rejects(State() with { Icon = new DesktopIcon { Images = [new DesktopIconImage(2, 2, [1, 2, 3])] } }, "image size mismatch");
        Rejects(State() with { Menu = [new DesktopMenuCommand("a", "One"), new DesktopMenuCommand("a", "Two")] }, "duplicate IDs");
        Rejects(State() with { Menu = [new DesktopMenuSubmenu("Deep", [new DesktopMenuCommand("a", "One")]), new DesktopMenuCommand("a", "Two")] }, "duplicate nested IDs");
        var deep = ImmutableArray.Create<DesktopMenuItem>(new DesktopMenuCommand("leaf", "Leaf"));
        for (int i = 0; i < 6; i++) deep = [new DesktopMenuSubmenu("Level", deep)];
        Rejects(State() with { Menu = deep }, "too deep");
        Rejects(State() with { Menu = [null!] }, "null entry");
    }

    [Test, Explicit, Category("DBus"), DisplayName("status icon: registration, properties, menu, activation and watcher restart")]
    public async Task StatusNotifierAsync()
    {
        string watcherName = "org.runic.test.StatusNotifierWatcher" + Environment.ProcessId;
        await using var icon = new StatusNotifierIcon(watcher: watcherName);
        var activations = System.Threading.Channels.Channel.CreateUnbounded<DesktopStatusIconActivation>();
        icon.Activated += (_, activation) => activations.Writer.TryWrite(activation);

        // No status area yet: unavailable, then registered once a watcher starts.
        Check(await icon.ShowAsync(State()) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "no watcher is unavailable");
        var watcher = await FakeWatcher.StartAsync(watcherName);
        string item = await watcher.Registered.Task.WaitAsync(Wait);
        Check(item.StartsWith("org.kde.StatusNotifierItem-", StringComparison.Ordinal), "registers its well-known name");
        Check(await icon.ShowAsync(State()) is PlatformResult<PlatformUnit>.Success, "registered with a host is success");
        Check(watcher.Registrations == 1, "an update does not register the item twice");

        using var host = new DBusConnection(DBusAddress.Session!);
        await host.ConnectAsync();
        Check((await Property(host, item, "Title")).GetString() == "Nix Manager", "title");
        Check((await Property(host, item, "Status")).GetString() == "Active", "status");
        Check((await Property(host, item, "IconName")).GetString() == "system-software-update", "icon name");
        Check((await Property(host, item, "AttentionIconName")).GetString() == "software-update-available", "attention icon");
        Check((await Property(host, item, "Menu")).GetObjectPathAsString() == "/MenuBar", "menu path");
        var pixmap = (await Property(host, item, "IconPixmap")).GetItem(0);
        Check(pixmap.GetItem(0).GetInt32() == 1 && pixmap.GetItem(2).GetArray<byte>().SequenceEqual(new byte[] { 0x40, 0x10, 0x20, 0x30 }), "pixmap is ARGB in network order");
        var tooltip = await Property(host, item, "ToolTip");
        Check(tooltip.GetItem(2).GetString() == "Nix Manager" && tooltip.GetItem(3).GetString() == "2 updates", "tooltip title and text");

        var all = await Request(host, item, "/StatusNotifierItem", "org.freedesktop.DBus.Properties", "GetAll", "s",
            (ref MessageWriter writer) => writer.WriteString("org.kde.StatusNotifierItem"), static (message, _) => message.GetBodyReader().ReadDictionaryOfStringToVariantValue());
        Check(all.Count == 16 && all["Title"].Type == VariantValueType.String && all["WindowId"].Type == VariantValueType.Int32
            && all["IconPixmap"].Type == VariantValueType.Array && all["ToolTip"].Type == VariantValueType.Struct
            && all["ItemIsMenu"].Type == VariantValueType.Bool && all["Menu"].Type == VariantValueType.ObjectPath, "GetAll returns every property with its type");

        var (revision, root) = await Layout(host, item);
        Check(root.Children.Select(child => child.Props.GetValueOrDefault("label") ?? child.Props.GetValueOrDefault("type")).SequenceEqual(
            ["Open__dashboard", "separator", "Check daily", "More"]), "layout order, labels and literal underscore");
        Check(root.Children[2].Props["toggle-type"] == "checkmark" && root.Children[2].Props["toggle-state"] == "1", "check item");
        Check(root.Children[3].Props["children-display"] == "submenu" && root.Children[3].Children[0].Props["enabled"] == "False", "submenu with disabled entry");

        await Call(host, item, "/MenuBar", "com.canonical.dbusmenu", "Event", "isvu", (ref MessageWriter writer) =>
        { writer.WriteInt32(root.Children[0].Id); writer.WriteString("clicked"); writer.WriteVariantInt32(0); writer.WriteUInt32(0); });
        var chosen = await activations.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Check(chosen is { Kind: DesktopStatusIconActivationKind.MenuItem, MenuItemId: "open" }, "menu click routes the command ID");
        await Call(host, item, "/MenuBar", "com.canonical.dbusmenu", "Event", "isvu", (ref MessageWriter writer) =>
        { writer.WriteInt32(root.Children[3].Children[0].Id); writer.WriteString("clicked"); writer.WriteVariantInt32(0); writer.WriteUInt32(0); });

        // libdbusmenu-glib (Waybar) reads entries in groups and reports unknown IDs.
        var labels = await Request(host, item, "/MenuBar", "com.canonical.dbusmenu", "GetGroupProperties", "aias", (ref MessageWriter writer) =>
        { writer.WriteArray(new[] { root.Children[0].Id, root.Children[2].Id }); writer.WriteArray(Array.Empty<string>()); }, static (message, _) =>
        {
            var reader = message.GetBodyReader();
            var found = new List<string>();
            var items = reader.ReadArrayStart(DBusType.Struct);
            while (reader.HasNext(items)) { reader.AlignStruct(); _ = reader.ReadInt32(); found.Add(reader.ReadDictionaryOfStringToVariantValue()["label"].GetString()); }
            return found;
        });
        Check(labels.SequenceEqual(["Open__dashboard", "Check daily"]), "group properties");
        var unknown = await Request(host, item, "/MenuBar", "com.canonical.dbusmenu", "EventGroup", "a(isvu)", (ref MessageWriter writer) =>
        {
            var events = writer.WriteArrayStart(DBusType.Struct);
            foreach (int id in new[] { root.Children[0].Id, 9999 })
            { writer.WriteStructureStart(); writer.WriteInt32(id); writer.WriteString("clicked"); writer.WriteVariantInt32(0); writer.WriteUInt32(0); }
            writer.WriteArrayEnd(events);
        }, static (message, _) => message.GetBodyReader().ReadArrayOfInt32());
        Check(unknown.SequenceEqual([9999]), "EventGroup reports unknown IDs");
        Check(await activations.Reader.ReadAsync().AsTask().WaitAsync(Wait) is { MenuItemId: "open" }, "EventGroup clicks route the command ID");

        // libdbusmenu-glib (Waybar) asks before opening; it waits for these replies.
        await Call(host, item, "/MenuBar", "com.canonical.dbusmenu", "AboutToShowGroup", "ai", (ref MessageWriter writer) => writer.WriteArray(RootMenu))
            .WaitAsync(Wait);
        await Call(host, item, "/MenuBar", "com.canonical.dbusmenu", "AboutToShow", "i", (ref MessageWriter writer) => writer.WriteInt32(0)).WaitAsync(Wait);
        await Call(host, item, "/StatusNotifierItem", "org.kde.StatusNotifierItem", "ProvideXdgActivationToken", "s", (ref MessageWriter writer) => writer.WriteString("token-1"));
        await Call(host, item, "/StatusNotifierItem", "org.kde.StatusNotifierItem", "Activate", "ii", (ref MessageWriter writer) => { writer.WriteInt32(0); writer.WriteInt32(0); });
        var primary = await activations.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Check(primary.Kind == DesktopStatusIconActivationKind.Primary && primary.PlatformContext?.ActivationToken == "token-1",
            "a disabled entry does not activate; primary activation carries the activation token");
        await Call(host, item, "/StatusNotifierItem", "org.kde.StatusNotifierItem", "SecondaryActivate", "ii", (ref MessageWriter writer) => { writer.WriteInt32(0); writer.WriteInt32(0); });
        var secondary = await activations.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Check(secondary.Kind == DesktopStatusIconActivationKind.Secondary && secondary.PlatformContext is null, "the token is used once");

        // Updates announce what changed.
        var status = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var layout = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var statusSignal = await host.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = item, Member = "NewStatus" },
            static (message, _) => message.GetBodyReader().ReadString(), notification => { if (notification.HasValue) status.TrySetResult(notification.Value); },
            emitOnCapturedContext: false, flags: ObserverFlags.EmitAll);
        using var layoutSignal = await host.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = item, Member = "LayoutUpdated" },
            static (message, _) => message.GetBodyReader().ReadUInt32(), notification => { if (notification.HasValue) layout.TrySetResult(notification.Value); },
            emitOnCapturedContext: false, flags: ObserverFlags.EmitAll);
        Check(await icon.ShowAsync(State(DesktopStatusIconStatus.NeedsAttention) with
        { Menu = [new DesktopMenuCommand("quit", "Quit"), new DesktopMenuCommand("check", "Check daily") { Checked = false }] }) is PlatformResult<PlatformUnit>.Success, "update");
        Check(await status.Task.WaitAsync(Wait) == "NeedsAttention", "NewStatus carries the status");
        Check(await layout.Task.WaitAsync(Wait) > revision, "LayoutUpdated carries a newer revision");
        var (_, updated) = await Layout(host, item);
        Check(updated.Children.Count == 2 && updated.Children[0].Props["label"] == "Quit", "new menu served");
        Check(updated.Children[1].Id == root.Children[2].Id && updated.Children[0].Id != root.Children[0].Id, "entries keep their IDs; new ones get new IDs");
        try
        {
            await Call(host, item, "/MenuBar", "com.canonical.dbusmenu", "Event", "isvu", (ref MessageWriter writer) =>
            { writer.WriteInt32(root.Children[0].Id); writer.WriteString("clicked"); writer.WriteVariantInt32(0); writer.WriteUInt32(0); });
            throw new InvalidOperationException("a click on a removed entry was accepted");
        }
        catch (DBusErrorReplyException) { }

        // A restarted watcher forgets items; the icon registers again.
        await watcher.DisposeAsync();
        watcher = await FakeWatcher.StartAsync(watcherName);
        Check(await watcher.Registered.Task.WaitAsync(Wait) == item, "registers again after a watcher restart");

        // A watcher without a status area accepts the item but cannot show it.
        watcher.HostRegistered = false;
        Check(await icon.ShowAsync(State()) is PlatformResult<PlatformUnit>.Unavailable, "no host is unavailable");

        Check(!activations.Reader.TryRead(out var stray), $"no other activation arrived: {stray}");
        Check(await icon.HideAsync() is PlatformResult<PlatformUnit>.Success, "hide");
        Check(!await NameHasOwner(host, item), "hiding releases the item's bus name");
        await watcher.DisposeAsync();
    }

    private sealed record Node(int Id, Dictionary<string, string> Props, List<Node> Children);

    private static Node Parse(VariantValue node)
    {
        var props = node.GetItem(1).GetDictionary<string, VariantValue>().ToDictionary(pair => pair.Key, pair => pair.Value.Type switch
        {
            VariantValueType.String => pair.Value.GetString(),
            VariantValueType.Int32 => pair.Value.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            VariantValueType.Bool => pair.Value.GetBool().ToString(),
            _ => pair.Value.ToString(),
        });
        return new Node(node.GetItem(0).GetInt32(), props, node.GetItem(2).GetArray<VariantValue>().Select(Parse).ToList());
    }

    private static Task<(uint Revision, Node Root)> Layout(DBusConnection host, string item)
    {
        var writer = host.GetMessageWriter();
        MessageBuffer call;
        try
        {
            writer.WriteMethodCallHeader(destination: item, path: "/MenuBar", @interface: "com.canonical.dbusmenu", member: "GetLayout", signature: "iias");
            writer.WriteInt32(0); writer.WriteInt32(-1); writer.WriteArray(Array.Empty<string>());
            call = writer.CreateMessage();
        }
        finally { writer.Dispose(); }
        return host.CallMethodAsync(call, static (message, _) =>
        {
            var reader = message.GetBodyReader();
            uint revision = reader.ReadUInt32();
            reader.AlignStruct();
            int id = reader.ReadInt32();
            var props = reader.ReadDictionaryOfStringToVariantValue();
            var children = reader.ReadArrayOfVariantValue();
            return (revision, new Node(id, props.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()), children.Select(Parse).ToList()));
        });
    }

    private static Task<VariantValue> Property(DBusConnection host, string item, string name)
    {
        var writer = host.GetMessageWriter();
        MessageBuffer call;
        try
        {
            writer.WriteMethodCallHeader(destination: item, path: "/StatusNotifierItem", @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss");
            writer.WriteString("org.kde.StatusNotifierItem"); writer.WriteString(name);
            call = writer.CreateMessage();
        }
        finally { writer.Dispose(); }
        return host.CallMethodAsync(call, static (message, _) => message.GetBodyReader().ReadVariantValue());
    }

    private delegate void Body(ref MessageWriter writer);

    private static Task Call(DBusConnection host, string item, string path, string @interface, string member, string signature, Body body)
    {
        var writer = host.GetMessageWriter();
        MessageBuffer call;
        try
        {
            writer.WriteMethodCallHeader(destination: item, path: path, @interface: @interface, member: member, signature: signature);
            body(ref writer);
            call = writer.CreateMessage();
        }
        finally { writer.Dispose(); }
        return host.CallMethodAsync(call);
    }

    private static Task<T> Request<T>(DBusConnection host, string item, string path, string @interface, string member, string signature, Body body,
        MessageValueReader<T> reader)
    {
        var writer = host.GetMessageWriter();
        MessageBuffer call;
        try
        {
            writer.WriteMethodCallHeader(destination: item, path: path, @interface: @interface, member: member, signature: signature);
            body(ref writer);
            call = writer.CreateMessage();
        }
        finally { writer.Dispose(); }
        return host.CallMethodAsync(call, reader);
    }

    private static Task<bool> NameHasOwner(DBusConnection host, string name)
    {
        var writer = host.GetMessageWriter();
        MessageBuffer call;
        try
        {
            writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus", @interface: "org.freedesktop.DBus", member: "NameHasOwner", signature: "s");
            writer.WriteString(name);
            call = writer.CreateMessage();
        }
        finally { writer.Dispose(); }
        return host.CallMethodAsync(call, static (message, _) => message.GetBodyReader().ReadBool());
    }

    private static void Rejects(DesktopStatusIconState state, string what)
    {
        try { DesktopServiceValidation.StatusIcon(state); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException($"accepted {what}");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    // org.kde.StatusNotifierWatcher as Plasma and Waybar provide it, owning a test-specific name.
    private sealed class FakeWatcher : IPathMethodHandler, IAsyncDisposable
    {
        private readonly DBusConnection _connection;
        private FakeWatcher(DBusConnection connection) => _connection = connection;
        public TaskCompletionSource<string> Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Registrations;
        public bool HostRegistered { get; set; } = true;
        public string Path => "/StatusNotifierWatcher";
        public bool HandlesChildPaths => false;

        public static async Task<FakeWatcher> StartAsync(string name)
        {
            var connection = new DBusConnection(DBusAddress.Session!);
            await connection.ConnectAsync();
            var watcher = new FakeWatcher(connection);
            connection.AddMethodHandler(watcher);
            if (!await connection.TryRequestNameAsync(name, RequestNameOptions.None)) throw new InvalidOperationException("watcher name taken");
            return watcher;
        }

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            if (context.Request.MemberAsString == "RegisterStatusNotifierItem")
            {
                string service = reader.ReadString();
                var reply = context.CreateReplyWriter(null);
                try { context.Reply(reply.CreateMessage()); } finally { reply.Dispose(); }
                Interlocked.Increment(ref Registrations);
                Registered.TrySetResult(service);
            }
            else if (context.Request.MemberAsString == "Get")
            {
                _ = reader.ReadString();
                Check(reader.ReadString() == "IsStatusNotifierHostRegistered", "only the host property is queried");
                var reply = context.CreateReplyWriter("v");
                try { reply.WriteVariantBool(HostRegistered); context.Reply(reply.CreateMessage()); } finally { reply.Dispose(); }
            }
            else context.ReplyUnknownMethodError();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() { _connection.Dispose(); return ValueTask.CompletedTask; }
    }
}
