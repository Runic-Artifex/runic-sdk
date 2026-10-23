using System.Collections.Concurrent;
using System.Collections.Immutable;
using Runic.Desktop.Internal;
using Runic.Platform;
using Runic.Platform.Linux.DBus;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// B10 PR 7 (D036.9): the Linux global menu over com.canonical.dbusmenu with a fake
// com.canonical.AppMenu.Registrar. The DBus cases need a private session bus (dbus-run-session); they never touch the
// user's. The GTK smokes check the X11 properties and the windows' bars.
public sealed class GlobalMenuTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const uint Xid = 0x4200007;
    private const string Menu = "com.canonical.dbusmenu";

    internal static readonly IReadOnlyList<DesktopMenu> Bar =
    [
        new("Help", [new DesktopMenuItem("About Runic", "help.about")]) { Role = DesktopMenuRole.Help },
        new("View",
        [
            new DesktopMenuItem("Word Wrap", "view.wrap") { IsChecked = true },
            new DesktopMenuSeparator(),
            new DesktopMenuItem("Small", "view.zoom") { RadioGroup = "zoom", IsChecked = false, CommandParameter = "0.8" },
            new DesktopMenuItem("Large", "view.zoom") { RadioGroup = "zoom", IsChecked = true, CommandParameter = "1.5" },
            new DesktopMenuItem("Locked", "view.wrap") { IsEnabled = false },
            new DesktopMenu("Disabled More", [new DesktopMenuItem("Inside", "view.wrap")]) { IsEnabled = false },
        ]) { AccessKey = 'V' },
        new("File",
        [
            new DesktopMenuItem("Save", "file.save") { Shortcut = DesktopShortcut.Parse("Primary+S"), AccessKey = 'S' },
            new DesktopMenuItem("Save As…", "file.saveAs") { Shortcut = DesktopShortcut.Parse("Primary+Shift+S") },
            // Ctrl+C stays the focused control's: no shortcut.
            new DesktopMenuItem("Copy Special", "file.copy") { Shortcut = DesktopShortcut.Parse("Primary+C") },
            new DesktopMenuItem("Open_dashboard", "file.dashboard"),
        ]) { Role = DesktopMenuRole.File, AccessKey = 'F' },
        new("Tools", [new DesktopMenuItem("Run", "tools.run"), new DesktopMenuItem("Later", "tools.later")]),
    ];

    private static ImmutableArray<PlannedMenuEntry> Plan(IReadOnlyList<DesktopMenu> bar) => DesktopMenuPlanner.PlanBar(bar, MenuPlatform.Linux);

    [Test]
    public void TheGlobalMenuTakesTheNativeBarOfAWindowThatIsNoKiosk()
    {
        Assert.True(GtkMenus.UsesGlobalMenu(DesktopMenuBarPresentation.Native, kiosk: false, globalMenu: true));
        Assert.False(GtkMenus.UsesGlobalMenu(DesktopMenuBarPresentation.Native, kiosk: false, globalMenu: false));
        Assert.False(GtkMenus.UsesGlobalMenu(DesktopMenuBarPresentation.Native, kiosk: true, globalMenu: true));
        Assert.False(GtkMenus.UsesGlobalMenu(DesktopMenuBarPresentation.Page, kiosk: false, globalMenu: true));
        Assert.False(GtkMenus.UsesGlobalMenu(DesktopMenuBarPresentation.None, kiosk: false, globalMenu: true));
        // A frameless window has no bar of its own but keeps the global menu.
        Assert.Equal(DesktopMenuBarPlacement.Global, GtkMenus.Placement(DesktopMenuBarPresentation.Native, frameless: true, kiosk: false, global: true));
        Assert.Equal(DesktopMenuBarPlacement.Global, GtkMenus.Placement(DesktopMenuBarPresentation.Native, frameless: false, kiosk: false, global: true));
        Assert.Equal(DesktopMenuBarPlacement.Page, GtkMenus.Placement(DesktopMenuBarPresentation.Page, frameless: false, kiosk: false, global: true));
        Assert.Equal(DesktopMenuBarPlacement.Window, GtkMenus.Placement(DesktopMenuBarPresentation.Native, frameless: false, kiosk: false, global: false));
        Assert.True(LinuxGlobalMenu.IsFlatpakOnPlasma(true, "KDE"));
        Assert.True(LinuxGlobalMenu.IsFlatpakOnPlasma(true, "foo:kde"));
        Assert.False(LinuxGlobalMenu.IsFlatpakOnPlasma(false, "KDE"));
        Assert.False(LinuxGlobalMenu.IsFlatpakOnPlasma(true, "GNOME"));
        Assert.False(LinuxGlobalMenu.IsFlatpakOnPlasma(true, null));
    }

    [Test]
    public void PlannedEntriesBecomeDbusmenuProperties()
    {
        var plan = Plan(Bar);
        var file = plan[0];
        Assert.Equal("_File", Value(LinuxGlobalMenu.Describe(file, true), "label").GetString());
        Assert.Equal("submenu", Value(LinuxGlobalMenu.Describe(file, true), "children-display").GetString());
        var save = LinuxGlobalMenu.Describe(file.Children[0], true);
        Assert.Equal("_Save", Value(save, "label").GetString());
        Assert.Equal([["Control", "S"]], Shortcut(save));
        Assert.Null(Find(save, "enabled"));
        Assert.Equal([["Control", "Shift", "S"]], Shortcut(LinuxGlobalMenu.Describe(file.Children[1], true)));
        Assert.Null(Find(LinuxGlobalMenu.Describe(file.Children[2], true), "shortcut"));
        Assert.Equal("Open__dashboard", Value(LinuxGlobalMenu.Describe(file.Children[3], false), "label").GetString());
        Assert.False(Value(LinuxGlobalMenu.Describe(file.Children[3], false), "enabled").GetBoolean());
        var view = plan[1];
        var wrap = LinuxGlobalMenu.Describe(view.Children[0], true);
        Assert.Equal("checkmark", Value(wrap, "toggle-type").GetString());
        Assert.Equal(1, Value(wrap, "toggle-state").GetInt32());
        Assert.Equal("separator", Value(LinuxGlobalMenu.Describe(view.Children[1], true), "type").GetString());
        var small = LinuxGlobalMenu.Describe(view.Children[2], true);
        Assert.Equal("radio", Value(small, "toggle-type").GetString());
        Assert.Equal(0, Value(small, "toggle-state").GetInt32());
        Assert.Equal(1, Value(LinuxGlobalMenu.Describe(view.Children[3], true), "toggle-state").GetInt32());
    }

    [Test, Explicit, Category("DBus")]
    public async Task TheBarIsRegisteredServedAndRunsItsCommands()
    {
        await using var registrar = await FakeRegistrar.StartAsync();
        var window = new FakeWindow("file.save", "file.saveAs", "view.wrap", "view.zoom");
        var menu = await window.StartAsync(Plan(Bar));
        await using (menu)
        {
            var (sender, xid, path) = await registrar.Registrations.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            Assert.Equal(menu.Connection!.UniqueName, sender);
            Assert.Equal(Xid, xid);
            Assert.Equal(menu.ObjectPath, path);
            Assert.StartsWith(LinuxGlobalMenu.PathPrefix, path, StringComparison.Ordinal);
            await window.WaitForAsync(global: true);
            Assert.Equal((menu.Connection.UniqueName, path), window.Annotation);

            await using var client = await ConnectAsync();
            var root = await LayoutAsync(client, menu);
            Assert.Equal(["_File", "_View", "Tools", "Help"], root.Children.Select(static menu => menu.Label));
            var file = root.Children[0].Children;
            Assert.Equal(["_Save", "Save As…", "Copy Special", "Open__dashboard"], file.Select(static item => item.Label));
            Assert.Equal([["Control", "S"]], file[0].Shortcut);
            Assert.Equal([["Control", "Shift", "S"]], file[1].Shortcut);
            Assert.Null(file[2].Shortcut);
            // Availability is the declaration and CanExecute: Copy Special and the dashboard have no target.
            Assert.Equal([true, true, false, false], file.Select(static item => item.Enabled));
            var view = root.Children[1].Children;
            Assert.Equal(("checkmark", 1), (view[0].ToggleType, view[0].ToggleState));
            Assert.Equal("separator", view[1].Type);
            Assert.Equal(("radio", 0), (view[2].ToggleType, view[2].ToggleState));
            Assert.Equal(("radio", 1), (view[3].ToggleType, view[3].ToggleState));
            Assert.False(view[4].Enabled);
            Assert.True(view[5] is { ChildrenDisplay: "submenu", Enabled: false } && !view[5].Children[0].Enabled);

            // A click runs the item on the window's thread, with its parameter; a disabled one runs nothing.
            await EventAsync(client, menu, file[0].Id, "hovered");
            await EventAsync(client, menu, view[2].Id, "clicked");
            await EventAsync(client, menu, view[4].Id, "clicked");
            await EventAsync(client, menu, view[5].Children[0].Id, "clicked");
            await EventAsync(client, menu, file[0].Id, "clicked");
            Assert.Equal(("view.zoom", "0.8"), await window.Executed.Reader.ReadAsync().AsTask().WaitAsync(Wait));
            Assert.Equal(("file.save", (string?)null), await window.Executed.Reader.ReadAsync().AsTask().WaitAsync(Wait));
            await Task.Delay(200);
            Assert.False(window.Executed.Reader.TryRead(out var stray), $"nothing else ran: {stray}");
            var unknown = await CallAsync(client, menu, "EventGroup", DBusValue.Array("(isvu)",
            [
                DBusValue.Struct(DBusValue.From(file[1].Id), DBusValue.From("clicked"), DBusValue.Variant(DBusValue.From(0)), DBusValue.From(0u)),
                DBusValue.Struct(DBusValue.From(99999), DBusValue.From("clicked"), DBusValue.Variant(DBusValue.From(0)), DBusValue.From(0u)),
            ]));
            Assert.Equal([99999], unknown[0].GetItems().Select(static id => id.GetInt32()));
            Assert.Equal(("file.saveAs", (string?)null), await window.Executed.Reader.ReadAsync().AsTask().WaitAsync(Wait));

            // CanExecute is asked again before a submenu opens; changed entries are announced.
            await using var updates = await client.SubscribeAsync(new DBusSignalFilter
            {
                Sender = menu.Connection.UniqueName, Path = new DBusObjectPath(path), Interface = Menu, Member = "ItemsPropertiesUpdated",
            });
            var tools = root.Children[2];
            Assert.Equal([false, false], tools.Children.Select(static item => item.Enabled));
            window.Allow("tools.run");
            Assert.True((await CallAsync(client, menu, "AboutToShow", DBusValue.From(tools.Id)))[0].GetBoolean());
            await using (var signals = updates.GetAsyncEnumerator())
            {
                Assert.True(await signals.MoveNextAsync().AsTask().WaitAsync(Wait));
                var updated = signals.Current.Arguments[0].GetItems();
                Assert.Single(updated);
                Assert.Equal(tools.Children[0].Id, updated[0].GetFields()[0].GetInt32());
                Assert.DoesNotContain("enabled", updated[0].GetFields()[1].GetVariantDictionary().Keys);
                Assert.Contains("enabled", signals.Current.Arguments[1].GetItems()[0].GetFields()[1].GetStringArray());
            }
            Assert.False((await CallAsync(client, menu, "AboutToShow", DBusValue.From(tools.Id)))[0].GetBoolean());
            Assert.Equal([true, false], (await LayoutAsync(client, menu)).Children[2].Children.Select(static item => item.Enabled));
            var group = await CallAsync(client, menu, "AboutToShowGroup", DBusValue.Array("i", [DBusValue.From(tools.Id), DBusValue.From(99999)]));
            Assert.Empty(group[0].GetItems());
            Assert.Equal([99999], group[1].GetItems().Select(static id => id.GetInt32()));

            // SetMenus replaces the layout; an entry whose toggle kind changed gets a new id, as Plasma's importer needs.
            await using var layouts = await client.SubscribeAsync(new DBusSignalFilter
            {
                Sender = menu.Connection.UniqueName, Path = new DBusObjectPath(path), Interface = Menu, Member = "LayoutUpdated",
            });
            var revision = (await CallAsync(client, menu, "GetLayout", DBusValue.From(0), DBusValue.From(-1), DBusValue.StringArray([])))[0].GetUInt32();
            await window.InvokeAsync(() => menu.SetMenus(Plan([.. Bar.Take(1), new DesktopMenu("View", [new DesktopMenuItem("Word Wrap", "view.wrap")]) { AccessKey = 'V' }, .. Bar.Skip(2)])), default);
            await using (var signals = layouts.GetAsyncEnumerator())
            {
                Assert.True(await signals.MoveNextAsync().AsTask().WaitAsync(Wait));
                Assert.True(signals.Current.Arguments[0].GetUInt32() > revision);
            }
            var replaced = await LayoutAsync(client, menu);
            Assert.Equal(root.Children[1].Id, replaced.Children[1].Id);
            Assert.NotEqual(view[0].Id, replaced.Children[1].Children[0].Id);
            Assert.Null(replaced.Children[1].Children[0].ToggleType);
            Assert.Equal(file[0].Id, replaced.Children[0].Children[0].Id);
            // An id of the old layout chooses nothing.
            await Assert.ThrowsAsync<DBusException>(() => EventAsync(client, menu, view[0].Id, "clicked"));

            // Any session-bus peer can call the menu: id lists are bounded.
            var error = await Assert.ThrowsAsync<DBusException>(() => CallAsync(client, menu, "GetGroupProperties",
                DBusValue.Array("i", Enumerable.Repeat(DBusValue.From(file[0].Id), Runic.Platform.Linux.DBus.Menus.DBusMenuServer.MenuIdLimit + 1)), DBusValue.StringArray([])));
            Assert.Equal(DBusException.LimitsExceeded, error.ErrorName);
            Assert.Equal("ltr", (await client.CallMethodAsync(new DBusMethodCall(menu.Connection.UniqueName, new DBusObjectPath(path), "org.freedesktop.DBus.Properties", "Get")
            {
                Arguments = [DBusValue.From(Menu), DBusValue.From("TextDirection")],
            }))[0].GetVariant().GetString());
        }
        // Closing the window unregisters it.
        Assert.Equal(Xid, await registrar.Unregistrations.Reader.ReadAsync().AsTask().WaitAsync(Wait));
    }

    [Test, Explicit, Category("DBus")]
    public async Task TheBarFollowsTheRegistrarOwner()
    {
        var window = new FakeWindow("file.save");
        // No registrar: the bar stays in the window.
        await using var menu = await window.StartAsync(Plan(Bar));
        await window.WaitForAnnotationAsync();
        await Task.Delay(300);
        Assert.Empty(window.Placements);

        // A registrar appears: the window registers and its bar goes global.
        var registrar = await FakeRegistrar.StartAsync();
        Assert.Equal(Xid, (await registrar.Registrations.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Xid);
        await window.WaitForAsync(global: true);

        // It goes away: the bar comes back into the window.
        await registrar.DisposeAsync();
        await window.WaitForAsync(global: false);

        // A new owner knows no window: the window registers again.
        registrar = await FakeRegistrar.StartAsync();
        await using (registrar)
        {
            Assert.Equal(menu.ObjectPath, (await registrar.Registrations.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Path);
            await window.WaitForAsync(global: true);
            Assert.Equal([true, false, true], window.Placements);
        }
        await window.WaitForAsync(global: false);
        Assert.Empty(window.Diagnostics);
    }

    [Test, Explicit, Category("DBus")]
    public async Task ARegistrarThatRefusesTheWindowKeepsTheBarInTheWindow()
    {
        await using var registrar = await FakeRegistrar.StartAsync(refuse: true);
        var window = new FakeWindow();
        await using var menu = await window.StartAsync(Plan(Bar));
        await registrar.Registrations.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        await WaitUntilAsync(() => !window.Diagnostics.IsEmpty);
        await Task.Delay(300);
        Assert.Empty(window.Placements);
        var diagnostic = Assert.Single(window.Diagnostics);
        Assert.Equal(LinuxGlobalMenu.FailedCode, diagnostic.Code);
        Assert.Equal(DesktopDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("org.runic.Test.Refused", diagnostic.Message, StringComparison.Ordinal);
    }

    [Test, Explicit, Category("DBus")]
    public async Task AWaylandWindowFollowsTheRegistrarWithoutRegistering()
    {
        // KWin offers org_kde_kwin_appmenu: the registrar's owner alone decides, and nothing is registered with it.
        var registrar = await FakeRegistrar.StartAsync();
        var window = new FakeWindow("file.save") { Wayland = true };
        await using (var menu = await window.StartAsync(Plan(Bar)))
        {
            await window.WaitForAsync(global: true);
            Assert.Equal((menu.Connection!.UniqueName, menu.ObjectPath!), window.Annotation);
            await registrar.DisposeAsync();
            await window.WaitForAsync(global: false);
            registrar = await FakeRegistrar.StartAsync();
            await window.WaitForAsync(global: true);
            Assert.Equal([true, false, true], window.Placements);
            Assert.False(registrar.Registrations.Reader.TryRead(out _));
        }
        await Task.Delay(300);
        Assert.False(registrar.Unregistrations.Reader.TryRead(out _));
        await registrar.DisposeAsync();
        Assert.Empty(window.Diagnostics);
    }

    [Test, Explicit, Category("DBus")]
    public async Task AWaylandWindowWithoutKdeAppmenuKeepsItsBar()
    {
        // GNOME or wlroots with a registrar owned by an extension: no org_kde_kwin_appmenu, so the bar stays.
        await using var registrar = await FakeRegistrar.StartAsync();
        var window = new FakeWindow { Wayland = true, AppMenu = false };
        await using var menu = await window.StartAsync(Plan(Bar));
        await window.WaitForAnnotationAsync();
        await Task.Delay(300);
        Assert.Empty(window.Placements);
        Assert.Empty(window.Diagnostics);
        Assert.False(registrar.Registrations.Reader.TryRead(out _));
    }

    // The descriptors match what wayland-scanner generates from the pinned appmenu.xml: interface names and versions,
    // and per request its name, its signature ("since" prefix, one code per argument) and the interface of each
    // object and new_id argument.
    [Test]
    public unsafe void TheAppmenuDescriptorsMatchThePinnedProtocol()
    {
        var protocolDirectory = Path.Combine(AppContext.BaseDirectory, "wayland");
        var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(protocolDirectory, "upstream.json"))).RootElement;
        Assert.Matches("^[0-9a-f]{40}$", manifest.GetProperty("revision").GetString()!);
        var pinned = manifest.GetProperty("files")[0];
        var bytes = File.ReadAllBytes(Path.Combine(protocolDirectory, pinned.GetProperty("file").GetString()!));
        Assert.Equal(pinned.GetProperty("sha256").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));

        var surfaceName = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8("wl_surface");
        var surface = new WlInterface { Name = (byte*)surfaceName, Version = 6 };
        var protocol = AppMenuProtocol.Build((nint)(&surface));
        var actual = new[] { AppMenuProtocol.Describe(protocol.Manager), AppMenuProtocol.Describe(protocol.AppMenu) };
        var xml = System.Xml.Linq.XDocument.Load(new MemoryStream(bytes));
        var expected = xml.Root!.Elements("interface").Select(static element => (
            Name: (string)element.Attribute("name")!,
            Version: (int)element.Attribute("version")!,
            Requests: element.Elements("request").Select(static request => (
                Name: (string)request.Attribute("name")!,
                Signature: Signature(request),
                Types: request.Elements("arg").Select(static arg => (string?)arg.Attribute("interface")).ToArray())).ToArray(),
            Events: element.Elements("event").Count())).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Name, actual[index].Name);
            Assert.Equal(expected[index].Version, actual[index].Version);
            Assert.Equal(expected[index].Version, AppMenuProtocol.Version);
            Assert.Equal(expected[index].Events, actual[index].Events);
            Assert.Equal(expected[index].Requests.Select(static request => (request.Name, request.Signature)),
                actual[index].Requests.Select(static request => (request.Name, request.Signature)));
            for (var request = 0; request < expected[index].Requests.Length; request++)
                Assert.Equal(expected[index].Requests[request].Types, actual[index].Requests[request].Types);
        }
        System.Runtime.InteropServices.Marshal.FreeCoTaskMem(surfaceName);

        // wayland-scanner's signature: the since version, then i u f s o n a h per argument, ? before a nullable one.
        static string Signature(System.Xml.Linq.XElement request)
        {
            var codes = request.Elements("arg").Select(static arg =>
                ((string?)arg.Attribute("allow-null") == "true" ? "?" : "") + (string)arg.Attribute("type")! switch
                {
                    "int" => "i", "uint" => "u", "fixed" => "f", "string" => "s", "object" => "o", "new_id" => "n", "array" => "a", "fd" => "h",
                    var type => throw new InvalidOperationException("Unknown argument type " + type),
                });
            return (request.Attribute("since") is { } since && since.Value != "1" ? since.Value : "") + string.Concat(codes);
        }
    }

    private static async Task<DBusConnection> ConnectAsync() =>
        await DBusConnection.ConnectAsync(DBusBus.Session) is PlatformResult<DBusConnection>.Success(var connection)
            ? connection
            : throw new InvalidOperationException("Run the DBus cases on a private session bus: dbus-run-session -- dotnet run ...");

    private static Task<ImmutableArray<DBusValue>> CallAsync(DBusConnection client, LinuxGlobalMenu menu, string member, params DBusValue[] arguments) =>
        client.CallMethodAsync(new DBusMethodCall(menu.Connection!.UniqueName, new DBusObjectPath(menu.ObjectPath!), Menu, member)
        {
            Arguments = [.. arguments],
            Timeout = Wait,
        }).AsTask();

    private static Task<ImmutableArray<DBusValue>> EventAsync(DBusConnection client, LinuxGlobalMenu menu, int id, string kind) =>
        CallAsync(client, menu, "Event", DBusValue.From(id), DBusValue.From(kind), DBusValue.Variant(DBusValue.From(0)), DBusValue.From(0u));

    private static async Task<Node> LayoutAsync(DBusConnection client, LinuxGlobalMenu menu) =>
        Node.Parse((await CallAsync(client, menu, "GetLayout", DBusValue.From(0), DBusValue.From(-1), DBusValue.StringArray([])))[1]);

    private static DBusValue Value(ImmutableArray<KeyValuePair<string, DBusValue>> properties, string name) =>
        Find(properties, name) ?? throw new InvalidOperationException($"no {name}");

    private static DBusValue? Find(ImmutableArray<KeyValuePair<string, DBusValue>> properties, string name) =>
        properties.Where(property => property.Key == name).Select(static property => (DBusValue?)property.Value).FirstOrDefault();

    private static string[][] Shortcut(ImmutableArray<KeyValuePair<string, DBusValue>> properties) =>
        [.. Value(properties, "shortcut").GetItems().Select(static keys => keys.GetStringArray().ToArray())];

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > Wait) throw new TimeoutException("The condition did not hold in time.");
            await Task.Delay(20);
        }
    }

    private sealed record Node(int Id, string? Label, string? Type, bool Enabled, string? ToggleType, int ToggleState, string? ChildrenDisplay,
        string[][]? Shortcut, List<Node> Children)
    {
        internal static Node Parse(DBusValue node)
        {
            var fields = node.GetFields();
            var properties = fields[1].GetVariantDictionary();
            string? Text(string name) => properties.TryGetValue(name, out var value) ? value.GetString() : null;
            return new Node(fields[0].GetInt32(), Text("label"), Text("type"),
                !properties.TryGetValue("enabled", out var enabled) || enabled.GetBoolean(), Text("toggle-type"),
                properties.TryGetValue("toggle-state", out var state) ? state.GetInt32() : 0, Text("children-display"),
                properties.TryGetValue("shortcut", out var shortcut) ? [.. shortcut.GetItems().Select(static keys => keys.GetStringArray().ToArray())] : null,
                [.. fields[2].GetItems().Select(static child => Parse(child.GetVariant()))]);
        }
    }

    // A window whose GTK thread is one serial queue; it records what the global menu asked of it.
#pragma warning disable CA1001 // The semaphore has no wait handle to release.
    private sealed class FakeWindow(params string[] allowed) : ILinuxGlobalMenuWindow
    {
        // A Wayland window: no XID, and whether the compositor offers org_kde_kwin_appmenu.
        internal bool Wayland { get; init; }
        internal bool AppMenu { get; init; } = true;

        private readonly ConcurrentDictionary<string, bool> _allowed = new(allowed.Select(static id => KeyValuePair.Create(id, true)));
        private readonly SemaphoreSlim _thread = new(1, 1);
        private readonly List<bool> _placements = [];
        internal System.Threading.Channels.Channel<(string, string?)> Executed { get; } = System.Threading.Channels.Channel.CreateUnbounded<(string, string?)>();
        internal ConcurrentQueue<DesktopDiagnostic> Diagnostics { get; } = new();
        internal (string Service, string Path)? Annotation { get; private set; }

        internal List<bool> Placements
        {
            get
            {
                lock (_placements) return [.. _placements];
            }
        }

        internal void Allow(string id) => _allowed[id] = true;

        internal async Task<LinuxGlobalMenu> StartAsync(ImmutableArray<PlannedMenuEntry> plan)
        {
            LinuxGlobalMenu? menu = null;
            await InvokeAsync(() => menu = LinuxGlobalMenu.Start(this, Wayland ? null : Xid, plan), default);
            return menu!;
        }

        internal Task WaitForAsync(bool global) => WaitUntilAsync(() => Placements is [.., var last] && last == global);

        internal Task WaitForAnnotationAsync() => WaitUntilAsync(() => Annotation is not null);

        public async Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            await _thread.WaitAsync(cancellationToken);
            try
            {
                action();
            }
            finally
            {
                _thread.Release();
            }
        }

        public bool CanExecute(DesktopMenuItem item) => _allowed.ContainsKey(item.CommandId);

        public void Execute(DesktopMenuItem item)
        {
            if (CanExecute(item)) Executed.Writer.TryWrite((item.CommandId, item.CommandParameter));
        }

        public void PlacementChanged(bool global)
        {
            lock (_placements) _placements.Add(global);
        }

        public bool Annotate(string serviceName, string objectPath)
        {
            Annotation = (serviceName, objectPath);
            return !Wayland || AppMenu;
        }

        public void Report(DesktopDiagnostic diagnostic) => Diagnostics.Enqueue(diagnostic);
    }
#pragma warning restore CA1001

    // com.canonical.AppMenu.Registrar as kded's appmenu module answers it: RegisterWindow takes the caller as the menu's
    // service.
    private sealed class FakeRegistrar : DBusObject, IAsyncDisposable
    {
        private readonly bool _refuse;
        private DBusConnection? _connection;

        private FakeRegistrar(bool refuse) => _refuse = refuse;

        internal System.Threading.Channels.Channel<(string Sender, uint Xid, string Path)> Registrations { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<(string, uint, string)>();

        internal System.Threading.Channels.Channel<uint> Unregistrations { get; } = System.Threading.Channels.Channel.CreateUnbounded<uint>();

        internal static async Task<FakeRegistrar> StartAsync(bool refuse = false)
        {
            var registrar = new FakeRegistrar(refuse);
            var connection = await ConnectAsync();
            registrar._connection = connection;
            connection.Export(new DBusObjectPath(LinuxGlobalMenu.RegistrarPath), registrar);
            if (await connection.RequestNameAsync(LinuxGlobalMenu.RegistrarName, DBusRequestNameOptions.DoNotQueue) != DBusRequestNameReply.PrimaryOwner)
                throw new InvalidOperationException("Another registrar owns the name on this bus.");
            return registrar;
        }

        protected override ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context)
        {
            switch (context.Member)
            {
                case "RegisterWindow" when context.Signature.Value == "uo":
                    Registrations.Writer.TryWrite((context.Sender ?? "", context.Arguments[0].GetUInt32(), context.Arguments[1].GetObjectPath().Value));
                    if (_refuse) throw new DBusException("org.runic.Test.Refused", "This registrar takes no windows.");
                    return ValueTask.FromResult(ImmutableArray<DBusValue>.Empty);
                case "UnregisterWindow" when context.Signature.Value == "u":
                    Unregistrations.Writer.TryWrite(context.Arguments[0].GetUInt32());
                    return ValueTask.FromResult(ImmutableArray<DBusValue>.Empty);
                default:
                    throw new DBusException(DBusException.UnknownMethod, context.Member);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_connection is { } connection) await connection.DisposeAsync();
            _connection = null;
        }
    }
}
