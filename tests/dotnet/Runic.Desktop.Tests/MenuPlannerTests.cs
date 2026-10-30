using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// B10: the shared menu planner every renderer uses, and the bar's per-platform validation.
public sealed class DesktopMenuPlannerTests
{
    private static DesktopMenuItem Item(string title, string id, string? shortcut = null) =>
        new(title, id) { Shortcut = shortcut is null ? null : DesktopShortcut.Parse(shortcut) };

    private static readonly IReadOnlyList<DesktopMenu> Bar =
    [
        new("Help", [Item("About", "help.about")]) { Role = DesktopMenuRole.Help },
        new("Settings", [Item("Settings…", "app.settings", "Primary+,")]) { Role = DesktopMenuRole.Application },
        new("Tools", [Item("Run", "tools.run")]),
        new("File", [Item("Save", "file.save", "Primary+S"), Item("Save", "file.save")]) { Role = DesktopMenuRole.File, AccessKey = 'F' },
    ];

    [Test]
    public void WindowsAndLinuxPutFileFirstAndHelpLastWhileMacOSKeepsTheDeclaration()
    {
        foreach (var platform in new[] { MenuPlatform.Windows, MenuPlatform.Linux })
        {
            Assert.Equal(["File", "Settings", "Tools", "Help"], DesktopMenuPlanner.PlanBar(Bar, platform).Select(static menu => menu.Title));
        }
        Assert.Equal(["Help", "Settings", "Tools", "File"], DesktopMenuPlanner.PlanBar(Bar, MenuPlatform.MacOS).Select(static menu => menu.Title));
    }

    [Test]
    public void KeysFollowThePathAndKindSoRepeatedIdsAndChangedTogglesDiffer()
    {
        var plan = DesktopMenuPlanner.PlanBar(Bar, MenuPlatform.Windows);
        var file = plan[0];
        Assert.Equal("3m", file.Key);
        Assert.Equal(["3m/0i", "3m/1i"], file.Children.Select(static item => item.Key));
        // The same bar plans the same keys again; a plain item that becomes a check or radio item gets a new key.
        Assert.Equal(file.Children.Select(static item => item.Key),
            DesktopMenuPlanner.PlanBar(Bar, MenuPlatform.Windows)[0].Children.Select(static item => item.Key));
        IReadOnlyList<DesktopMenu> toggled =
        [
            new("View", [new DesktopMenuItem("Wrap", "view.wrap") { IsChecked = true }, new DesktopMenuSeparator(),
                new DesktopMenuItem("Small", "view.zoom") { RadioGroup = "zoom", IsChecked = false, CommandParameter = "0.8" },
                new DesktopMenu("More", [new DesktopMenuItem("Wrap", "view.wrap")]) { IsEnabled = false }]),
        ];
        var view = Assert.Single(DesktopMenuPlanner.PlanBar(toggled, MenuPlatform.Linux));
        Assert.Equal(["0m/0c", "0m/1s", "0m/2r", "0m/3m"], view.Children.Select(static entry => entry.Key));
        Assert.Equal([PlannedMenuKind.Check, PlannedMenuKind.Separator, PlannedMenuKind.Radio, PlannedMenuKind.Menu],
            view.Children.Select(static entry => entry.Kind));
        // A disabled menu disables its entries.
        var more = view.Children[3];
        Assert.False(more.Enabled);
        Assert.False(Assert.Single(more.Children).Enabled);
        Assert.Equal("0m/3m/0i", more.Children[0].Key);
        // Context menus have keys of their own.
        Assert.Equal(["c/0i", "c/1s"], DesktopMenuPlanner.PlanEntries([Item("Cut", "cut"), new DesktopMenuSeparator()], MenuPlatform.Windows)
            .Select(static entry => entry.Key));
    }

    [Test]
    public void AccessKeysAreMarkedAndLiteralMarkersEscaped()
    {
        Assert.Equal(("Save & Close", 0), DesktopMenuPlanner.PlaceAccessKey("Save & Close", 's'));
        Assert.Equal(("Enregistrer (K)", 13), DesktopMenuPlanner.PlaceAccessKey("Enregistrer", 'k'));
        Assert.Equal(("Enregistrer", 6), DesktopMenuPlanner.PlaceAccessKey("Enregistrer", 'S'));
        Assert.Equal(("Plain", -1), DesktopMenuPlanner.PlaceAccessKey("Plain", null));
        Assert.Equal("&Save && Close", DesktopMenuPlanner.Mnemonic("Save & Close", 0, '&'));
        Assert.Equal("Open__dash_board", DesktopMenuPlanner.Mnemonic("Open_dashboard", 9, '_'));
        Assert.Equal("Enregistrer (_K)", DesktopMenuPlanner.Mnemonic("Enregistrer (K)", 13, '_'));
        var file = DesktopMenuPlanner.PlanBar(Bar, MenuPlatform.Windows)[0];
        Assert.Equal("&File", file.Win32Label);
        Assert.Equal("_File", file.MnemonicLabel);
        Assert.Equal("Save\tCtrl+S", file.Children[0].Win32Label);
        Assert.Equal("Save", file.Children[1].Win32Label);
    }

    [Test]
    public void ShortcutsMapToEachNativeForm()
    {
        var save = DesktopMenuPlanner.MapShortcut(DesktopShortcut.Parse("Primary+Shift+S"), MenuPlatform.Windows);
        Assert.Equal("Ctrl+Shift+S", save.DisplayText);
        Assert.Equal("Control+Shift+S", save.AriaKeyShortcuts);
        Assert.Equal((ushort)'S', save.VirtualKey);
        Assert.Equal(DesktopMenuPlanner.FVirtKey | DesktopMenuPlanner.FControl | DesktopMenuPlanner.FShift, save.Win32Modifiers);
        Assert.Equal((uint)'s', save.GdkKeyval);
        Assert.Equal(DesktopMenuPlanner.GdkControlMask | DesktopMenuPlanner.GdkShiftMask, save.GdkModifiers);
        Assert.Equal(["Control", "Shift", "S"], save.DBusMenu.ToArray());
        Assert.Equal("Shift+Ctrl+S", DesktopMenuPlanner.MapShortcut(DesktopShortcut.Parse("Primary+Shift+S"), MenuPlatform.Linux).DisplayText);
        var mac = DesktopMenuPlanner.MapShortcut(DesktopShortcut.Parse("Primary+Shift+S"), MenuPlatform.MacOS);
        Assert.Equal("⇧⌘S", mac.DisplayText);
        Assert.Equal("Meta+Shift+S", mac.AriaKeyShortcuts);
        Assert.Equal("Control+Meta+K", DesktopMenuPlanner.MapShortcut(DesktopShortcut.Parse("Control+Primary+K"), MenuPlatform.MacOS).AriaKeyShortcuts);

        var page = DesktopMenuPlanner.MapShortcut(DesktopShortcut.Parse("Alt+PageDown"), MenuPlatform.Linux);
        Assert.Equal((ushort)0x22, page.VirtualKey);
        Assert.Equal(0xFF56u, page.GdkKeyval);
        Assert.Equal(DesktopMenuPlanner.GdkAltMask, page.GdkModifiers);
        Assert.Equal(["Alt", "PgDown"], page.DBusMenu.ToArray());
        Assert.Equal("Alt+Page Down", page.DisplayText);

        Assert.Equal((ushort)0x74, DesktopMenuPlanner.VirtualKey("F5"));
        Assert.Equal((ushort)0x87, DesktopMenuPlanner.VirtualKey("F24"));
        Assert.Equal(0xFFC2u, DesktopMenuPlanner.GdkKeyval("F5"));
        Assert.Equal((ushort)0xBC, DesktopMenuPlanner.VirtualKey(","));
        Assert.Equal((uint)',', DesktopMenuPlanner.GdkKeyval(","));
        Assert.Equal((ushort)0xBB, DesktopMenuPlanner.VirtualKey("Plus"));
        Assert.Equal(0x2Bu, DesktopMenuPlanner.GdkKeyval("Plus"));
        Assert.Equal((ushort)0x0D, DesktopMenuPlanner.VirtualKey("Enter"));
        Assert.Equal(0xFF0Du, DesktopMenuPlanner.GdkKeyval("Enter"));
        Assert.Equal("Return", DesktopMenuPlanner.QtKeyName("Enter"));
        Assert.Equal("+", DesktopMenuPlanner.QtKeyName("Plus"));
        Assert.Equal((ushort)'7', DesktopMenuPlanner.VirtualKey("7"));
        // A character without a layout-independent key code is mapped by the host at run time.
        Assert.Null(DesktopMenuPlanner.VirtualKey("§"));
        Assert.Null(DesktopMenuPlanner.VirtualKey("!"));
        Assert.Equal((uint)'!', DesktopMenuPlanner.GdkKeyval("!"));
    }

    [Test]
    public void EachPlatformReservesItsStandardShortcuts()
    {
        foreach (var platform in new[] { MenuPlatform.Windows, MenuPlatform.Linux })
        {
            foreach (var reserved in new[] { "Primary+Z", "Primary+Y", "Primary+Shift+Z", "Primary+X", "Primary+C", "Primary+V", "Primary+A", "Alt+F4", "F10", "Control+C" })
                Assert.True(DesktopMenuPlanner.IsReserved(DesktopShortcut.Parse(reserved), platform), $"{reserved} on {platform}");
            foreach (var free in new[] { "Primary+Q", "Primary+W", "Primary+S", "F5", "Primary+F10", "Primary+Shift+C" })
                Assert.False(DesktopMenuPlanner.IsReserved(DesktopShortcut.Parse(free), platform), $"{free} on {platform}");
        }
        Assert.True(DesktopMenuPlanner.IsReserved(DesktopShortcut.Parse("Primary+Q"), MenuPlatform.MacOS));
        Assert.False(DesktopMenuPlanner.IsReserved(DesktopShortcut.Parse("Primary+Y"), MenuPlatform.MacOS));
        Assert.False(DesktopMenuPlanner.IsReserved(DesktopShortcut.Parse("Control+C"), MenuPlatform.MacOS));
        // A reserved shortcut is planned without it.
        var copy = Assert.Single(Assert.Single(DesktopMenuPlanner.PlanBar([new("Edit", [Item("Copy Special", "copy", "Primary+C")])], MenuPlatform.Windows)).Children);
        Assert.Null(copy.Shortcut);
        Assert.Equal("Copy Special", copy.Win32Label);
    }

    [Test]
    public void ValidationReportsPerPlatformRadioGroupsAndPresentations()
    {
        IReadOnlyList<DesktopMenu> bar =
        [
            new("Edit", [Item("Copy Special", "copy.special", "Primary+C"), Item("Quit", "app.quit", "Primary+Q"), Item("Grab", "grab", "Control+G"),
                Item("Grab Again", "grab", "Primary+G")]),
            new("View",
            [
                new DesktopMenuItem("Small", "view.zoom") { RadioGroup = "zoom", IsChecked = true, CommandParameter = "0.8" },
                new DesktopMenuItem("Large", "view.zoom") { RadioGroup = "zoom", IsChecked = true, CommandParameter = "1.5" },
                new DesktopMenu("Theme", [new DesktopMenuItem("Dark", "theme") { RadioGroup = "zoom", IsChecked = true }]),
            ]),
        ];
        var windows = DesktopMenuBar.Check(bar, MenuPlatform.Windows, DesktopMenuBarPresentation.Page, nativeBar: false);
        var reserved = Assert.Single(windows, static item => item.Code == DesktopMenuBar.ReservedCode);
        Assert.Contains("Edit ▸ Copy Special uses Primary+C, which Windows keeps for Copy", reserved.Message);
        // Control+G and Primary+G are both Ctrl+G on Windows.
        Assert.Single(windows, static item => item.Code == DesktopMenuBar.DuplicateCode);
        // A group lives in one menu level: the submenu's Dark is another group.
        var radio = Assert.Single(windows, static item => item.Code == DesktopMenuBar.RadioCheckedCode);
        Assert.Contains("'zoom' in 'View'", radio.Message);
        Assert.DoesNotContain(windows, static item => item.Code is DesktopMenuBar.UnsupportedCode or DesktopMenuBar.PageNativeCode);

        var mac = DesktopMenuBar.Check(bar, MenuPlatform.MacOS, DesktopMenuBarPresentation.Native, nativeBar: true);
        Assert.Equal(["Edit ▸ Copy Special", "Edit ▸ Quit"],
            mac.Where(static item => item.Code == DesktopMenuBar.ReservedCode).Select(static item => item.Message.Split(" uses ")[0]));
        Assert.Contains(mac, static item => item.Message.Contains("the shortcut of Quit on macOS", StringComparison.Ordinal));
        Assert.DoesNotContain(mac, static item => item.Code == DesktopMenuBar.DuplicateCode);

        // Native without a bar: unsupported, and no reserved shortcuts of a bar that is not shown.
        var unsupported = DesktopMenuBar.Check(bar, MenuPlatform.Linux, DesktopMenuBarPresentation.Native, nativeBar: false);
        Assert.Single(unsupported, static item => item.Code == DesktopMenuBar.UnsupportedCode);
        Assert.DoesNotContain(unsupported, static item => item.Code == DesktopMenuBar.ReservedCode);
        Assert.DoesNotContain(DesktopMenuBar.Check(bar, MenuPlatform.Linux, DesktopMenuBarPresentation.Native, nativeBar: true),
            static item => item.Code == DesktopMenuBar.UnsupportedCode);
        Assert.DoesNotContain(DesktopMenuBar.Check(bar, MenuPlatform.Windows, DesktopMenuBarPresentation.None, nativeBar: false),
            static item => item.Code == DesktopMenuBar.UnsupportedCode);
        // macOS keeps the declared menus in the application bar for Page and None.
        foreach (var presentation in new[] { DesktopMenuBarPresentation.Page, DesktopMenuBarPresentation.None })
        {
            var kept = Assert.Single(DesktopMenuBar.Check(bar, MenuPlatform.MacOS, presentation, nativeBar: true),
                static item => item.Code == DesktopMenuBar.PageNativeCode);
            Assert.Equal(DesktopDiagnosticSeverity.Warning, kept.Severity);
            Assert.Equal("DesktopWindowOptions.MenuBarPresentation", kept.Option);
        }
        Assert.Empty(DesktopMenuBar.Check([], MenuPlatform.MacOS, DesktopMenuBarPresentation.Page, nativeBar: true));
        Assert.Throws<ArgumentException>(() => DesktopMenuBar.Check([new("View", [new DesktopMenuItem("Small", "zoom") { RadioGroup = "zoom" }])], macOS: true));
    }

    private static JsonElement Rules() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "menu-entries.json"))).RootElement;

    private static IEnumerable<string> Values(JsonElement rules, string group, string kind) =>
        rules.GetProperty(group).GetProperty(kind).EnumerateArray().Select(static value => value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Concat(Enumerable.Repeat(value.GetProperty("repeat").GetString(), value.GetProperty("count").GetInt32())));

    // The same vectors as the status icon menu (Runic.Platform.Runtime.Tests, StatusIconMenuRuleTests).
    [Test]
    public void SharedParameterAccessKeyAndRadioRulesApplyToTheBarAndContextMenus()
    {
        var rules = Rules();
        // The context menu's argument checks run before any outcome.
        static void Context(DesktopMenuItem item) =>
            WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable).ShowContextMenuAsync([item]).AsTask().GetAwaiter().GetResult();
        void Accepts(DesktopMenuItem item)
        {
            DesktopMenuBar.Check([new("Tools", [item])], macOS: true);
            Context(item);
        }
        void Refuses(DesktopMenuItem item)
        {
            Assert.Throws<ArgumentException>(() => DesktopMenuBar.Check([new("Tools", [item])], macOS: true));
            Assert.Throws<ArgumentException>(() => Context(item));
        }
        foreach (var parameter in Values(rules, "commandParameters", "valid")) Accepts(new("Run", "id") { CommandParameter = parameter });
        foreach (var parameter in Values(rules, "commandParameters", "invalid")) Refuses(new("Run", "id") { CommandParameter = parameter });
        foreach (var key in Values(rules, "accessKeys", "valid"))
        {
            Accepts(new("Run", "id") { AccessKey = key[0] });
            DesktopMenuBar.Check([new("Tools", []) { AccessKey = key[0] }], macOS: true);
        }
        foreach (var key in Values(rules, "accessKeys", "invalid"))
        {
            Refuses(new("Run", "id") { AccessKey = key[0] });
            Assert.Throws<ArgumentException>(() => DesktopMenuBar.Check([new("Tools", []) { AccessKey = key[0] }], macOS: true));
        }
        foreach (var group in Values(rules, "radioGroups", "valid")) Accepts(new("Small", "id") { RadioGroup = group, IsChecked = false });
        foreach (var group in Values(rules, "radioGroups", "invalid").Concat(Values(rules, "radioGroups", "reserved")))
            Refuses(new("Small", "id") { RadioGroup = group, IsChecked = false });
        Refuses(new("Small", "id") { RadioGroup = "zoom" });
    }

    [Test]
    public void NativeIdsAreStablePerKeyAndNeverReused()
    {
        var ids = new MenuIdAllocator(first: 100, last: 102);
        Assert.Equal(100, ids["0m/0i"]);
        Assert.Equal(101, ids["0m/1c"]);
        Assert.Equal(100, ids["0m/0i"]);
        Assert.Equal(102, ids["0m/1i"]);
        Assert.Equal(3, ids.Count);
        Assert.Throws<InvalidOperationException>(() => ids["0m/2i"]);
    }
}

public sealed class MenuCommandChainTests
{
    private sealed class Target(params string[] handled) : IDesktopMenuCommandTarget
    {
        internal List<DesktopMenuItem> Executed { get; } = [];
        internal bool Throws { get; set; }
        public bool CanExecute(DesktopMenuItem item) => Throws ? throw new InvalidOperationException("broken") : handled.Contains(item.CommandId);
        public void Execute(DesktopMenuItem item) => Executed.Add(item);
    }

    private sealed class Logger : ILogger
    {
        internal List<(EventId Id, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((eventId, formatter(state, exception)));
    }

    [Test]
    public void TheWindowRunsFirstThenTheApplicationAndThrowingTargetsAreLogged()
    {
        var window = new Target("file.save");
        var application = new Target("file.save", "file.new");
        var logger = new Logger();
        var chain = new MenuCommandChain(() => application, logger) { Window = window };
        var save = new DesktopMenuItem("Save", "file.save") { CommandParameter = "draft" };
        Assert.True(chain.TryExecute(save));
        Assert.Same(save, Assert.Single(window.Executed));
        Assert.Empty(application.Executed);
        Assert.True(chain.CanExecute(new DesktopMenuItem("New", "file.new")));
        Assert.True(chain.TryExecute(new DesktopMenuItem("New", "file.new")));
        Assert.Single(application.Executed);
        Assert.False(chain.TryExecute(new DesktopMenuItem("Quit", "app.quit")));
        Assert.False(chain.TryExecute(save with { IsEnabled = false }));
        Assert.False(chain.CanExecute(save with { IsEnabled = false }));

        window.Throws = true;
        Assert.True(chain.TryExecute(save));
        Assert.Single(application.Executed, item => ReferenceEquals(item, save));
        var (id, message) = Assert.Single(logger.Entries);
        Assert.Equal(3014, id.Id);
        Assert.Equal("MenuCommandFailed", id.Name);
        Assert.Contains("CanExecute failed for file.save with System.InvalidOperationException", message);
        // The same target as window and application is asked once.
        var both = new Target("x");
        Assert.True(MenuCommandChain.TryExecute(new DesktopMenuItem("X", "x"), both, both, null));
        Assert.Single(both.Executed);
    }
}

public sealed class MenuWindowHostTests
{
    [Test]
    public async Task AMenuHostGetsTheBarTheCommandPathAndTheContextMenus()
    {
        var factory = new MenuFactory();
        IReadOnlyList<DesktopMenu> bar = [new("File", [new DesktopMenuItem("Save", "file.save")]) { Role = DesktopMenuRole.File }];
        var loggers = new ConfigurationValidationTests.RecordingLoggerFactory();
        await using var host = await DesktopHost.StartAsync(new()
        {
            WindowHostFactory = factory, WaitForConnection = false, MenuBar = bar, LoggerFactory = loggers,
            Linux = new() { GlobalMenu = false },
        });
        Assert.DoesNotContain(host.Validate(new() { Browser = BrowserKind.Embedded }).Diagnostics,
            static item => item.Code == DesktopMenuBar.UnsupportedCode);
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new()
        {
            Browser = BrowserKind.Embedded,
            MenuBarPresentation = DesktopMenuBarPresentation.Page,
        });
        var native = Assert.Single(factory.Hosts);
        Assert.Equal(DesktopMenuBarPresentation.Page, native.Options!.MenuBarPresentation);
        // LinuxDesktopOptions.GlobalMenu reaches the window host.
        Assert.False(native.Options.GlobalMenu);
        await WaitAsync(() => native.Bars.Count == 1);
        Assert.Equal(bar, native.Bars[0]);
        IReadOnlyList<DesktopMenu> translated = [new("Fichier", [new DesktopMenuItem("Enregistrer", "file.save")]) { Role = DesktopMenuRole.File }];
        host.SetMenuBar(translated);
        await WaitAsync(() => native.Bars.Count == 2);
        Assert.Equal(translated, native.Bars[1]);

        // The host runs items through Runic's command path: the window's target, then the application's.
        var commands = native.MenuCommands ?? throw new InvalidOperationException("The host got no command path.");
        var application = new Target("file.save", "file.new");
        host.MenuCommands = application;
        var windowTarget = new Target("file.save");
        window.MenuCommands = windowTarget;
        var save = new DesktopMenuItem("Save", "file.save") { CommandParameter = "draft" };
        commands.Execute(save);
        Assert.Same(save, Assert.Single(windowTarget.Executed));
        Assert.True(await window.TryExecuteMenuItemAsync(new DesktopMenuItem("New", "file.new")));
        Assert.Single(application.Executed);
        Assert.False(await window.TryExecuteMenuItemAsync(new DesktopMenuItem("Quit", "app.quit")));
        Assert.False(await window.TryExecuteMenuItemAsync(save with { IsEnabled = false }));
        await Assert.ThrowsAsync<ArgumentException>(async () => await window.TryExecuteMenuItemAsync(new DesktopMenuItem("Run", "runic.x")));

        Assert.Equal(DesktopMenuBarPlacement.Page, window.MenuBarPlacement);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.MenuBarPlacementChanged += (sender, _) => { Assert.Same(window, sender); changed.TrySetResult(); };
        native.Place(DesktopMenuBarPlacement.Global);
        await changed.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DesktopMenuBarPlacement.Global, window.MenuBarPlacement);
        // Each move is logged with the placement before and after it (event 3015).
        var moved = Assert.Single(loggers.Entries, static entry => entry.EventId.Id == 3015);
        Assert.Equal(("MenuBarPlacementChanged", LogLevel.Information), (moved.EventId.Name, moved.Level));
        Assert.Contains("from Page to Global", moved.Message, StringComparison.Ordinal);

        ImmutableArray<DesktopMenuEntry> menu = [new DesktopMenuItem("Rename", "row.rename") { CommandParameter = "row-7" }];
        var chosen = await window.ShowContextMenuAsync(menu);
        Assert.Same(menu[0], chosen);
        Assert.Null(native.ContextOptions!.X);
        Assert.Null(await window.ShowContextMenuAsync(menu, new DesktopContextMenuOptions { X = 4, Y = 8 }));
        Assert.Equal(8, native.ContextOptions!.Y);
        await Assert.ThrowsAsync<ArgumentException>(async () => await window.ShowContextMenuAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(async () => await window.ShowContextMenuAsync(menu, new DesktopContextMenuOptions { Y = 3 }));

        await window.CloseAsync();
        Assert.Equal(DesktopMenuBarPlacement.None, window.MenuBarPlacement);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await window.TryExecuteMenuItemAsync(save));
    }

    [Test]
    public async Task AWindowWithoutContextMenusReportsTheCapability()
    {
        var factory = new MenuFactory { ContextMenus = false };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        Assert.False(window.Capabilities.HasFlag(DesktopWindowCapabilities.ContextMenu));
        var error = await Assert.ThrowsAsync<DesktopException>(async () => await window.ShowContextMenuAsync([new DesktopMenuItem("Cut", "cut")]));
        Assert.Equal("window-capability-unavailable", error.Code);
        // The factory reports a native bar, so validation finds nothing missing.
        var diagnostics = host.Validate(new() { Browser = BrowserKind.Embedded }).Diagnostics;
        Assert.DoesNotContain(diagnostics, static item => item.Code == DesktopMenuBar.UnsupportedCode);
    }

    [Test]
    public async Task ValidationNamesTheMissingBarForTheNativePresentationOnly()
    {
        if (OperatingSystem.IsMacOS()) return;
        IReadOnlyList<DesktopMenu> bar = [new("Tools", [new DesktopMenuItem("Run", "tools.run")])];
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = new PlainFactory(), MenuBar = bar });
        Assert.Contains(host.Validate(new() { Browser = BrowserKind.Embedded }).Diagnostics, static item => item.Code == DesktopMenuBar.UnsupportedCode);
        Assert.DoesNotContain(host.Validate(new() { Browser = BrowserKind.Embedded, MenuBarPresentation = DesktopMenuBarPresentation.Page }).Diagnostics,
            static item => item.Code == DesktopMenuBar.UnsupportedCode);
        Assert.DoesNotContain(host.Validate(new() { Browser = BrowserKind.Embedded, MenuBarPresentation = DesktopMenuBarPresentation.None }).Diagnostics,
            static item => item.Code == DesktopMenuBar.UnsupportedCode);
        Assert.Contains(host.Validate(new() { MenuBarPresentation = (DesktopMenuBarPresentation)9 }).Diagnostics,
            static item => item.Message.Contains("MenuBarPresentation", StringComparison.Ordinal));
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met.");
            await Task.Delay(10);
        }
    }

    private sealed class Target(params string[] handled) : IDesktopMenuCommandTarget
    {
        internal List<DesktopMenuItem> Executed { get; } = [];
        public bool CanExecute(DesktopMenuItem item) => handled.Contains(item.CommandId);
        public void Execute(DesktopMenuItem item) => Executed.Add(item);
    }

    private sealed class MenuFactory : IDesktopWindowHostFactory
    {
        internal List<MenuHost> Hosts { get; } = [];
        internal bool ContextMenus { get; init; } = true;
        public bool IsSupported => true;
        public DesktopWindowCapabilities Capabilities => DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.MenuBar |
            (ContextMenus ? DesktopWindowCapabilities.ContextMenu : DesktopWindowCapabilities.None);
        public IDesktopWindowHost Create()
        {
            var host = new MenuHost(Capabilities);
            Hosts.Add(host);
            return host;
        }
    }

    private sealed class PlainFactory : IDesktopWindowHostFactory
    {
        public bool IsSupported => true;
        public IDesktopWindowHost Create() => new MenuHost(DesktopWindowCapabilities.NativeHandle);
    }

    private sealed class MenuHost(DesktopWindowCapabilities capabilities) : IDesktopMenuWindowHost
    {
        private DesktopMenuBarPlacement _placement;
        public event EventHandler? Closed;
        public event EventHandler? MenuBarPlacementChanged;
        public DesktopWindowCapabilities Capabilities => capabilities;
        public bool IsOpen { get; private set; }
        public nint NativeHandle => IsOpen ? 1 : 0;
        internal DesktopWindowHostOptions? Options { get; private set; }
        internal List<IReadOnlyList<DesktopMenu>> Bars { get; } = [];
        internal DesktopContextMenuOptions? ContextOptions { get; private set; }
        public IDesktopMenuCommandTarget? MenuCommands { get; set; }
        public DesktopMenuBarPlacement MenuBarPlacement => IsOpen ? _placement : DesktopMenuBarPlacement.None;

        internal void Place(DesktopMenuBarPlacement placement)
        {
            _placement = placement;
            MenuBarPlacementChanged?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask SetMenuBarAsync(IReadOnlyList<DesktopMenu> menus, CancellationToken cancellationToken = default)
        {
            lock (Bars) Bars.Add(menus);
            return ValueTask.CompletedTask;
        }

        // Chooses the first entry of a menu at the pointer and dismisses one at a point.
        public ValueTask<DesktopMenuItem?> ShowContextMenuAsync(ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions options,
            CancellationToken cancellationToken = default)
        {
            ContextOptions = options;
            return ValueTask.FromResult(options.X is null ? items[0] as DesktopMenuItem : null);
        }

        public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
        {
            Options = options;
            _placement = options.MenuBarPresentation == DesktopMenuBarPresentation.Page ? DesktopMenuBarPlacement.Page : DesktopMenuBarPlacement.Window;
            IsOpen = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            if (IsOpen) { IsOpen = false; Closed?.Invoke(this, EventArgs.Empty); }
            return ValueTask.CompletedTask;
        }
        public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }
}
