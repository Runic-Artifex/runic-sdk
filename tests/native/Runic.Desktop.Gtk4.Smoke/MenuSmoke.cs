using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Desktop;
using Runic.Desktop.Gtk4;
using Runic.Platform;

// --menus gtk3|gtk4 [--rtl] (B10) under Xvfb with GDK_BACKEND=x11: the in-window menu bar of the GTK 3 and GTK 4 hosts.
// - The declared bar in the platform order with access keys, check and radio items, declared disabled entries,
//   shortcut text and no text for a reserved shortcut, read from the GTK widgets.
// - Availability follows CanExecute when a menu is shown; choosing an item runs the window's command with its
//   parameter, a disabled one runs nothing, and the declared check and radio state stays until SetMenuBar changes it.
// - A shortcut the page leaves alone runs its item once; one the page cancels does not (real keys through XTEST).
// - Context menus: chosen, dismissed with Escape, replaced by a later menu, cancelled, at a page point and at the
//   pointer after a real right-click.
// - A Page window has no bar widget and still answers the shortcut.
// - --rtl: LayoutDirection RightToLeft mirrors the bar and the context menu.
[SupportedOSPlatform("linux")]
internal static partial class MenuSmoke
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic menus</title></head>
        <body style="margin:0;height:100vh">menus
        <script>
        window.prevented = 0;
        window.contextPoints = [];
        addEventListener("keydown", event => {
          // The page keeps Ctrl+Shift+S, so the menu item with it must not run.
          if (event.ctrlKey && event.shiftKey && event.key.toLowerCase() === "s") { event.preventDefault(); window.prevented++; }
        });
        addEventListener("contextmenu", event => { event.preventDefault(); window.contextPoints.push([event.clientX, event.clientY]); });
        </script></body></html>
        """;

    internal static readonly IReadOnlyList<DesktopMenu> Bar =
    [
        new("Help", [new DesktopMenuItem("About Runic", "help.about")]) { Role = DesktopMenuRole.Help },
        new("View",
        [
            new DesktopMenuItem("Word Wrap", "view.wrap") { IsChecked = true },
            new DesktopMenuItem("Ruler", "view.ruler") { IsChecked = false },
            new DesktopMenuSeparator(),
            new DesktopMenuItem("Small", "view.zoom") { RadioGroup = "zoom", IsChecked = false, CommandParameter = "0.8" },
            new DesktopMenuItem("Large", "view.zoom") { RadioGroup = "zoom", IsChecked = true, CommandParameter = "1.5" },
            new DesktopMenuSeparator(),
            // No item of this group is checked, which GTK radio items cannot show: check items drawn as radio items.
            new DesktopMenuItem("Light", "view.theme") { RadioGroup = "theme", IsChecked = false, CommandParameter = "light" },
            new DesktopMenuItem("Dark", "view.theme") { RadioGroup = "theme", IsChecked = false, CommandParameter = "dark" },
            new DesktopMenuItem("Locked", "view.wrap") { IsEnabled = false },
            new DesktopMenu("Disabled More", [new DesktopMenuItem("Inside", "view.wrap")]) { IsEnabled = false },
        ]) { AccessKey = 'V' },
        new("File",
        [
            new DesktopMenuItem("Save", "file.save") { Shortcut = DesktopShortcut.Parse("Primary+S"), AccessKey = 'S' },
            new DesktopMenuItem("Save As…", "file.saveAs") { Shortcut = DesktopShortcut.Parse("Primary+Shift+S") },
            // Ctrl+C stays the page's: the item shows no shortcut.
            new DesktopMenuItem("Copy Special", "file.copy") { Shortcut = DesktopShortcut.Parse("Primary+C") },
            new DesktopMenuItem("Open_dashboard", "file.dashboard"),
        ]) { Role = DesktopMenuRole.File, AccessKey = 'F' },
        new("Tools", [new DesktopMenuItem("Run", "tools.run"), new DesktopMenu("More", [new DesktopMenuItem("Nothing", "tools.nothing")])]),
    ];

    private sealed class Target(params string[] handled) : IDesktopMenuCommandTarget
    {
        private readonly HashSet<string> _handled = [.. handled];
        internal ConcurrentQueue<(string Id, string? Parameter)> Executed { get; } = new();
        public bool CanExecute(DesktopMenuItem item) => _handled.Contains(item.CommandId);
        public void Execute(DesktopMenuItem item) => Executed.Enqueue((item.CommandId, item.CommandParameter));
    }

    internal static int Run(string toolkit, bool rtl)
    {
        var options = toolkit switch
        {
            "gtk3" => new DesktopHostOptions { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } },
            "gtk4" => new DesktopHostOptions().WithGtk4(),
            _ => throw new ArgumentException($"Unknown toolkit '{toolkit}'; use gtk3 or gtk4.", nameof(toolkit)),
        };
        options = options with { WaitForConnection = true, MenuBar = Bar };
        return DesktopEventLoop.Run(options, async host =>
        {
            try
            {
                return await RunAsync(host, toolkit == "gtk3" ? new Gtk3Inspector() : new Gtk4Inspector(), rtl);
            }
            catch (Exception error)
            {
                // Reported before the host closes its windows.
                Console.Error.WriteLine("Menus failed: " + error.Message);
                Console.Error.WriteLine(error);
                throw;
            }
        });
    }

    private static async Task<int> RunAsync(DesktopHost host, Inspector inspector, bool rtl)
    {
        var name = inspector.Name + (rtl ? " rtl" : "");
        Require(!host.Validate(new DesktopWindowOptions { Browser = BrowserKind.Embedded }).Diagnostics.Any(static item => item.Code == "menu-bar-unsupported"),
            "no menu-bar-unsupported for the embedded window");
        using var input = XTest.Open() ?? throw new InvalidOperationException("Menus: XTEST is not available; run under xvfb-run with GDK_BACKEND=x11.");
        var application = new Target("help.about");
        var windowTarget = new Target("file.save", "file.saveAs", "view.wrap", "view.ruler", "view.zoom", "view.theme", "tools.run");
        host.MenuCommands = application;
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        {
            var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded, X = 0, Y = 0, Width = 520, Height = 360,
                LayoutDirection = rtl ? DesktopLayoutDirection.RightToLeft : DesktopLayoutDirection.Default,
            });
            window.MenuCommands = windowTarget;
            Console.WriteLine($"Menus ({name}): the window is open.");
            Require(window.Capabilities.HasFlag(DesktopWindowCapabilities.MenuBar | DesktopWindowCapabilities.ContextMenu),
                $"MenuBar and ContextMenu capabilities, not {window.Capabilities}");
            Require(window.MenuBarPlacement == DesktopMenuBarPlacement.Window, $"placement Window, not {window.MenuBarPlacement}");

            var bar = await WaitForBarAsync(window, inspector, static bar => bar.Menus.Count == 4);
            Require(bar.Menus.Select(static menu => menu.Label).SequenceEqual(["_File", "_View", "Tools", "Help"]),
                "File first, Help last, declaration order between, with access keys: " + string.Join(", ", bar.Menus.Select(static menu => menu.Label)));
            var file = bar.Menus[0].Items;
            Require(file.Select(static item => item.Label).SequenceEqual(["_Save", "Save As…", "Copy Special", "Open__dashboard"]),
                "File items: " + string.Join(", ", file.Select(static item => item.Label)));
            Require(file[0].Accelerator == "Ctrl+S" && file[1].Accelerator == "Shift+Ctrl+S" && file[2].Accelerator is null,
                $"shortcut text Ctrl+S, Shift+Ctrl+S and none for the reserved Ctrl+C: {file[0].Accelerator}, {file[1].Accelerator}, {file[2].Accelerator}");
            var view = bar.Menus[1].Items;
            Require(view.Select(static item => item.Label).SequenceEqual(["Word Wrap", "Ruler", "Small", "Large", "Light", "Dark", "Locked", "Disabled More"]),
                "View items: " + string.Join(", ", view.Select(static item => item.Label)));
            Require(view[0] is { Kind: "check", Checked: true } && view[1] is { Kind: "check", Checked: false },
                $"Word Wrap checked and Ruler unchecked check items: {view[0]}, {view[1]}");
            Require(view[2] is { Kind: "radio", Checked: false } && view[3] is { Kind: "radio", Checked: true },
                $"Small and Large radio items with Large checked: {view[2]}, {view[3]}");
            Require(view[4] is { Kind: "radio", Checked: false } && view[5] is { Kind: "radio", Checked: false },
                $"Light and Dark radio items, neither checked: {view[4]}, {view[5]}");
            Require(!view[6].Sensitive && view[7].Kind == "menu", $"the declared disabled item and a submenu: {view[6]}, {view[7]}");
            // GTK 3 disables the submenu's item; a GMenu cannot, so GTK 4 opens it with every entry disabled.
            var inside = await window.ReadNativeAsync(handle => inspector.ShowSubmenu(handle, 1, 7));
            Require((!view[7].Sensitive || inspector.Name == "gtk4") && inside is [{ Label: "Inside", Sensitive: false }],
                $"the declared disabled submenu: {view[7]}, {string.Join(", ", inside)}");
            Console.WriteLine($"PASS {name} menu bar: platform order, access keys, escaping, shortcut text, check, radio and declared disabled entries.");

            // Availability: the Tools menu asks CanExecute when it is shown. Run has a target; Nothing has none.
            var tools = await window.ReadNativeAsync(handle => inspector.ShowMenu(handle, 2));
            Require(tools[0] is { Label: "Run", Sensitive: true } && tools[1].Kind == "menu", $"Run enabled by its target: {tools[0]}");
            var more = await window.ReadNativeAsync(handle => inspector.ShowSubmenu(handle, 2, 1));
            Require(more is [{ Label: "Nothing", Sensitive: false }], $"Nothing disabled without a target: {string.Join(", ", more)}");

            // Choosing items runs the window's command with the parameter; the declared state stays.
            await ActivateAsync(window, inspector, 1, 2, windowTarget, ("view.zoom", "0.8"));
            await ActivateAsync(window, inspector, 1, 0, windowTarget, ("view.wrap", null));
            await ActivateAsync(window, inspector, 0, 0, windowTarget, ("file.save", null));
            await window.InvokeNativeAsync(handle => inspector.Activate(handle, 1, 6));
            await Task.Delay(300);
            Require(windowTarget.Executed.IsEmpty, "the declared disabled item ran " + string.Join(", ", windowTarget.Executed));
            bar = await WaitForBarAsync(window, inspector, static bar => bar.Menus.Count == 4);
            Require(bar.Menus[1].Items[0].Checked && !bar.Menus[1].Items[2].Checked && bar.Menus[1].Items[3].Checked,
                "the declared check and radio state after the clicks: " + string.Join(", ", bar.Menus[1].Items));
            Require(await window.TryExecuteMenuItemAsync(new DesktopMenuItem("About", "help.about")) && application.Executed.TryDequeue(out _),
                "TryExecuteMenuItemAsync runs the application's command");
            Console.WriteLine($"PASS {name} menu commands: availability on show, parameters, disabled items, declared state, the application's target.");

            // SetMenuBar rebuilds the bar.
            host.SetMenuBar([.. Bar.Take(1), new DesktopMenu("View", [new DesktopMenuItem("Word Wrap", "view.wrap") { IsChecked = false }]), .. Bar.Skip(2)]);
            bar = await WaitForBarAsync(window, inspector, static bar => bar.Menus.Count == 4 && bar.Menus[1].Items.Count == 1);
            Require(bar.Menus[1].Items[0] is { Kind: "check", Checked: false }, "SetMenuBar's unchecked Word Wrap: " + bar.Menus[1].Items[0]);
            Console.WriteLine($"PASS {name} SetMenuBar replaces the bar.");

            if (rtl)
            {
                var direction = await window.ReadNativeAsync(handle => inspector.BarDirection(handle));
                Require(direction.Direction == 2 && direction.FirstIsRight, $"a right-to-left bar with File at the right: {direction}");
                Console.WriteLine($"PASS {name} the bar is right to left.");
            }

            // Shortcuts after the page: real keys through XTEST into the focused page.
            await input.ClickAsync(260, 220, button: 1);
            await Task.Delay(300);
            await input.KeyAsync(XTest.S, XTest.ControlL);
            await WaitForAsync(() => windowTarget.Executed.TryDequeue(out var ran) && ran.Id == "file.save", "Ctrl+S to run Save");
            await input.KeyAsync(XTest.S, XTest.ControlL, XTest.ShiftL);
            await WaitForAsync(async () => await surface.ExecuteJavaScriptAsync("return String(window.prevented);", Deadline) == "1",
                "the page to see and cancel Ctrl+Shift+S");
            await Task.Delay(500);
            Require(windowTarget.Executed.IsEmpty, "a shortcut the page cancelled ran " + string.Join(", ", windowTarget.Executed));
            Console.WriteLine($"PASS {name} shortcuts: Ctrl+S runs Save after the page; Ctrl+Shift+S, which the page cancels, runs nothing.");

            await ContextMenusAsync(window, surface, inspector, input, name, rtl);
            await window.CloseAsync();
        }

        // A Page window has no bar in it and keeps the shortcuts.
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded, X = 0, Y = 0, Width = 520, Height = 360, MenuBarPresentation = DesktopMenuBarPresentation.Page,
        }))
        {
            window.MenuCommands = windowTarget;
            Require(window.MenuBarPlacement == DesktopMenuBarPlacement.Page, $"placement Page, not {window.MenuBarPlacement}");
            await Task.Delay(500);
            Require(!await window.ReadNativeAsync(handle => inspector.HasBar(handle)), "no bar widget in a Page window");
            windowTarget.Executed.Clear();
            await input.ClickAsync(260, 220, button: 1);
            await Task.Delay(300);
            await input.KeyAsync(XTest.S, XTest.ControlL);
            await WaitForAsync(() => windowTarget.Executed.TryDequeue(out var ran) && ran.Id == "file.save", "Ctrl+S to run Save in a Page window");
            Console.WriteLine($"PASS {name} a Page window shows no bar and answers Ctrl+S.");
        }
        Console.WriteLine($"PASS {name} menus.");
        return 0;
    }

    private static async Task ContextMenusAsync(DesktopWindow window, DesktopSurface surface, Inspector inspector, XTest input, string name, bool rtl)
    {
        var copy = new DesktopMenuItem("Copy", "edit.copy") { Shortcut = DesktopShortcut.Parse("Primary+Shift+C") };
        var rename = new DesktopMenuItem("Rename", "edit.rename") { CommandParameter = "row-7" };
        ImmutableArray<DesktopMenuEntry> items =
        [
            copy,
            new DesktopMenuItem("Delete", "edit.delete") { IsEnabled = false },
            new DesktopMenuSeparator(),
            new DesktopMenu("Share", [new DesktopMenuItem("Mail", "share.mail")]),
            rename,
        ];

        // After a real right-click, at the pointer; the chosen item comes back.
        await input.ClickAsync(200, 200, button: 3);
        await WaitForAsync(async () => await surface.ExecuteJavaScriptAsync("return String(window.contextPoints.length);", Deadline) == "1",
            "the page's contextmenu event");
        var chosen = window.ShowContextMenuAsync(items);
        var shown = await WaitForContextMenuAsync(window, inspector);
        Require(shown.Select(static item => item.Label).SequenceEqual(["Copy", "Delete", "Share", "Rename"]),
            "context menu items: " + string.Join(", ", shown));
        Require(shown[0] is { Accelerator: "Shift+Ctrl+C", Sensitive: true } && !shown[1].Sensitive && shown[2].Kind == "menu",
            $"a shortcut hint, a disabled item and a submenu: {shown[0]}, {shown[1]}, {shown[2]}");
        if (rtl) Require(await window.ReadNativeAsync(handle => inspector.ContextMenuDirection(handle)) == 2, "a right-to-left context menu");
        await window.InvokeNativeAsync(handle => inspector.ActivateContextItem(handle, 3));
        var result = await chosen.AsTask().WaitScaledAsync(Deadline);
        Require(ReferenceEquals(result, rename), $"the chosen Rename item, not {result}");
        Console.WriteLine($"PASS {name} context menu at the pointer: items, hints, disabled entries{(rtl ? ", right to left" : "")}, the chosen item.");

        // At a page point; Escape dismisses it.
        var dismissed = window.ShowContextMenuAsync(items, new DesktopContextMenuOptions { X = 40, Y = 60 });
        await WaitForContextMenuAsync(window, inspector);
        await Task.Delay(300);
        await input.KeyAsync(XTest.Escape);
        Require(await dismissed.AsTask().WaitScaledAsync(Deadline) is null, "Escape returns null");

        // A later menu replaces the open one, which returns null; cancelling closes the later one.
        var first = window.ShowContextMenuAsync(items).AsTask();
        await WaitForContextMenuAsync(window, inspector);
        using var cancellation = new CancellationTokenSource();
        var second = window.ShowContextMenuAsync(items, cancellationToken: cancellation.Token).AsTask();
        Require(await first.WaitScaledAsync(Deadline) is null, "a replaced context menu returns null");
        await WaitForContextMenuAsync(window, inspector);
        await cancellation.CancelAsync();
        try
        {
            await second.WaitScaledAsync(Deadline);
            throw new InvalidOperationException("Menus: a cancelled context menu did not throw.");
        }
        catch (OperationCanceledException)
        {
        }
        await WaitForAsync(async () => !await window.ReadNativeAsync(handle => inspector.HasContextMenu(handle)), "the cancelled menu to close");
        Console.WriteLine($"PASS {name} context menus: at a page point, Escape, replaced by a later menu, cancelled.");
    }

    private static async Task ActivateAsync(DesktopWindow window, Inspector inspector, int menu, int item, Target target,
        (string Id, string? Parameter) expected)
    {
        await window.InvokeNativeAsync(handle => inspector.Activate(handle, menu, item));
        await WaitForAsync(() => !target.Executed.IsEmpty, $"{expected.Id} to run");
        Require(target.Executed.TryDequeue(out var ran) && ran == expected && target.Executed.IsEmpty, $"{expected} ran once, not {ran}");
    }

    private static async Task<BarSnapshot> WaitForBarAsync(DesktopWindow window, Inspector inspector, Func<BarSnapshot, bool> condition)
    {
        BarSnapshot? last = null;
        await WaitForAsync(async () =>
        {
            last = await window.ReadNativeAsync(handle => inspector.ReadBar(handle));
            return condition(last);
        }, "the menu bar");
        return last!;
    }

    private static async Task<IReadOnlyList<ItemSnapshot>> WaitForContextMenuAsync(DesktopWindow window, Inspector inspector)
    {
        IReadOnlyList<ItemSnapshot>? items = null;
        await WaitForAsync(async () => (items = await window.ReadNativeAsync(handle => inspector.ReadContextMenu(handle))) is { Count: > 0 },
            "the context menu");
        return items!;
    }

    private static async Task<T> ReadNativeAsync<T>(this DesktopWindow window, Func<nint, T> read)
    {
        T value = default!;
        await window.InvokeNativeAsync(handle => value = read(handle));
        return value;
    }

    private static Task WaitForAsync(Func<bool> condition, string what) => WaitForAsync(() => Task.FromResult(condition()), what);

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition().WaitScaledAsync(Deadline))
        {
            if (elapsed.Elapsed > Deadline) throw new TimeoutException("Menus: timed out waiting for " + what + ".");
            await Task.Delay(50);
        }
    }

    private static void Require(bool condition, string what)
    {
        if (condition) return;
        // Also written here: a failure of the GTK 4 callback reaches the console only when the host has closed.
        Console.Error.WriteLine("Menus: expected " + what + ".");
        throw new InvalidOperationException("Menus: expected " + what + ".");
    }

    internal sealed record BarSnapshot(IReadOnlyList<MenuSnapshot> Menus);

    internal sealed record MenuSnapshot(string Label, IReadOnlyList<ItemSnapshot> Items);

    // Kind: item, check, radio or menu. Accelerator: the shortcut text GTK shows, or null.
    internal sealed record ItemSnapshot(string Label, string Kind, bool Checked, bool Sensitive, string? Accelerator);

    // Reads and drives the toolkit's widgets on the GTK thread, from the window's native handle.
    private abstract class Inspector
    {
        internal abstract string Name { get; }
        internal abstract BarSnapshot ReadBar(nint window);
        internal abstract bool HasBar(nint window);
        internal abstract IReadOnlyList<ItemSnapshot> ShowMenu(nint window, int menu);
        internal abstract IReadOnlyList<ItemSnapshot> ShowSubmenu(nint window, int menu, int item);
        internal abstract void Activate(nint window, int menu, int item);
        internal abstract (int Direction, bool FirstIsRight) BarDirection(nint window);
        internal abstract IReadOnlyList<ItemSnapshot> ReadContextMenu(nint window);
        internal abstract bool HasContextMenu(nint window);
        internal abstract int ContextMenuDirection(nint window);
        internal abstract void ActivateContextItem(nint window, int item);
    }

    private sealed class Gtk3Inspector : Inspector
    {
        internal override string Name => "gtk3";

                private static nint Box(nint window) => Gtk3.BinGetChild(window);

        private static nint MenuBar(nint window) =>
            Gtk3.Is(Box(window), Gtk3.BoxType()) && Gtk3.Children(Box(window)) is [var bar, _] && Gtk3.Is(bar, Gtk3.MenuBarType()) ? bar : 0;

        private static nint WebView(nint window) => Gtk3.Is(Box(window), Gtk3.BoxType()) ? Gtk3.Children(Box(window))[^1] : Box(window);

        internal override bool HasBar(nint window) => MenuBar(window) != 0;

        internal override BarSnapshot ReadBar(nint window) => MenuBar(window) is not 0 and var bar
            ? new(Gtk3.Children(bar).Select(static menu => new MenuSnapshot(Gtk3.Label(menu), Items(Gtk3.MenuItemGetSubmenu(menu)))).ToList())
            : new([]);

        private static List<ItemSnapshot> Items(nint menu) =>
            [.. Gtk3.Children(menu).Where(static item => !Gtk3.Is(item, Gtk3.SeparatorMenuItemType())).Select(Item)];

        private static ItemSnapshot Item(nint item)
        {
            var kind = Gtk3.MenuItemGetSubmenu(item) != 0 ? "menu"
                : Gtk3.Is(item, Gtk3.RadioMenuItemType()) || Gtk3.Is(item, Gtk3.CheckMenuItemType()) && Gtk3.CheckMenuItemGetDrawAsRadio(item) ? "radio"
                : Gtk3.Is(item, Gtk3.CheckMenuItemType()) ? "check"
                : "item";
            var isCheck = Gtk3.Is(item, Gtk3.CheckMenuItemType());
            return new ItemSnapshot(Gtk3.Label(item), kind, isCheck && Gtk3.CheckMenuItemGetActive(item), Gtk3.WidgetGetSensitive(item), Gtk3.Accelerator(item));
        }

        private static nint ItemAt(nint window, int menu, int item) =>
            Gtk3.Children(Gtk3.MenuItemGetSubmenu(Gtk3.Children(MenuBar(window))[menu])).Where(static entry => !Gtk3.Is(entry, Gtk3.SeparatorMenuItemType())).ElementAt(item);

        internal override IReadOnlyList<ItemSnapshot> ShowMenu(nint window, int menu)
        {
            var submenu = Gtk3.MenuItemGetSubmenu(Gtk3.Children(MenuBar(window))[menu]);
            // Showing the menu emits "show", as opening it does; it is not on screen without its popup window.
            Gtk3.WidgetShow(submenu);
            Gtk3.WidgetHide(submenu);
            return Items(submenu);
        }

        internal override IReadOnlyList<ItemSnapshot> ShowSubmenu(nint window, int menu, int item)
        {
            var submenu = Gtk3.MenuItemGetSubmenu(ItemAt(window, menu, item));
            Gtk3.WidgetShow(submenu);
            Gtk3.WidgetHide(submenu);
            return Items(submenu);
        }

        internal override void Activate(nint window, int menu, int item) => Gtk3.MenuItemActivate(ItemAt(window, menu, item));

        internal override (int Direction, bool FirstIsRight) BarDirection(nint window)
        {
            var items = Gtk3.Children(MenuBar(window));
            return (Gtk3.WidgetGetDirection(MenuBar(window)), Gtk3.AllocationX(items[0]) > Gtk3.AllocationX(items[1]));
        }

        private static nint ContextMenu(nint window) => Gtk3.MenuGetForAttachWidget(WebView(window)) is [.., var menu] ? menu : 0;

        internal override bool HasContextMenu(nint window) => ContextMenu(window) is not 0 and var menu && Gtk3.WidgetGetVisible(menu);

        internal override IReadOnlyList<ItemSnapshot> ReadContextMenu(nint window) => HasContextMenu(window) ? Items(ContextMenu(window)) : [];

        internal override int ContextMenuDirection(nint window) => Gtk3.WidgetGetDirection(ContextMenu(window));

        internal override void ActivateContextItem(nint window, int item)
        {
            var menu = ContextMenu(window);
            // As a click does: the menu is deactivated, then the item activated.
            Gtk3.MenuShellActivateItem(menu, Gtk3.Children(menu).Where(static entry => !Gtk3.Is(entry, Gtk3.SeparatorMenuItemType())).ElementAt(item));
        }
    }

    private sealed class Gtk4Inspector : Inspector
    {
        internal override string Name => "gtk4";

        private static Gtk.Window Window(nint window) =>
            (Gtk.Window)GObject.Internal.InstanceWrapper.WrapHandle<Gtk.Window>(window, false);

        private static Gtk.PopoverMenuBar? MenuBar(nint window) =>
            Window(window).GetChild() is Gtk.Box box ? box.GetFirstChild() as Gtk.PopoverMenuBar : null;

        private static Gtk.Widget WebView(nint window) =>
            Window(window).GetChild() is Gtk.Box box ? box.GetLastChild()! : Window(window).GetChild()!;

        internal override bool HasBar(nint window) => MenuBar(window) is not null;

        private static List<Gtk.Widget> BarItems(Gtk.PopoverMenuBar bar) => [.. Children(bar).Where(static child => child.GetCssName() == "item")];

        private static Gtk.PopoverMenu Popover(Gtk.Widget item) => Children(item).OfType<Gtk.PopoverMenu>().Single();

        internal override BarSnapshot ReadBar(nint window) => MenuBar(window) is { } bar
            ? new(BarItems(bar).Select(static item => new MenuSnapshot(Children(item).OfType<Gtk.Label>().First().GetLabel(), Items(Popover(item)))).ToList())
            : new([]);

        // A popover's model buttons, without those of the submenus nested in it as popovers of their own.
        // A sliding popover, such as a context menu's, shows each submenu as another page of its stack: read the main one.
        private static List<Gtk.Widget> Buttons(Gtk.Widget popover)
        {
            var root = Descendants(popover, skipPopovers: true).OfType<Gtk.Stack>().FirstOrDefault() is { } stack
                ? stack.GetChildByName("main") ?? stack.GetVisibleChild() ?? popover
                : popover;
            return [.. Descendants(root, skipPopovers: true).Where(static widget => widget.GetCssName() == "modelbutton")];
        }

        private static List<ItemSnapshot> Items(Gtk.Widget popover) => [.. Buttons(popover).Select(Item)];

        private static ItemSnapshot Item(Gtk.Widget button)
        {
            var labels = Descendants(button, skipPopovers: true).OfType<Gtk.Label>().ToList();
            var label = labels.FirstOrDefault(static label => label.GetCssName() == "label")?.GetLabel() ?? "";
            var accelerator = labels.FirstOrDefault(static label => label.GetCssName() == "accelerator" && label.GetVisible())?.GetText();
            var indicator = Descendants(button, skipPopovers: true).FirstOrDefault(static widget => widget.GetCssName() is "check" or "radio" or "arrow");
            var kind = indicator?.GetCssName() switch { "check" => "check", "radio" => "radio", "arrow" => "menu", _ => "item" };
            var isChecked = indicator is not null && indicator.GetStateFlags().HasFlag(Gtk.StateFlags.Checked);
            return new ItemSnapshot(label, kind, isChecked, button.GetSensitive(), string.IsNullOrEmpty(accelerator) ? null : accelerator);
        }

        private static Gtk.Widget ButtonAt(nint window, int menu, int item) => Buttons(Popover(BarItems(MenuBar(window)!)[menu]))[item];

        internal override IReadOnlyList<ItemSnapshot> ShowMenu(nint window, int menu)
        {
            var popover = Popover(BarItems(MenuBar(window)!)[menu]);
            popover.Popup();
            var items = Items(popover);
            popover.Popdown();
            return items;
        }

        internal override IReadOnlyList<ItemSnapshot> ShowSubmenu(nint window, int menu, int item)
        {
            var popover = Popover(BarItems(MenuBar(window)!)[menu]);
            popover.Popup();
            // The bar nests a submenu as a popover of its item's button.
            var submenu = Children(ButtonAt(window, menu, item)).OfType<Gtk.PopoverMenu>().Single();
            submenu.Popup();
            var items = Items(submenu);
            submenu.Popdown();
            popover.Popdown();
            return items;
        }

        internal override void Activate(nint window, int menu, int item)
        {
            var popover = Popover(BarItems(MenuBar(window)!)[menu]);
            popover.Popup();
            ButtonAt(window, menu, item).Activate();
            popover.Popdown();
        }

        internal override (int Direction, bool FirstIsRight) BarDirection(nint window)
        {
            var bar = MenuBar(window)!;
            var items = BarItems(bar);
            items[0].TranslateCoordinates(bar, 0, 0, out var first, out _);
            items[1].TranslateCoordinates(bar, 0, 0, out var second, out _);
            return ((int)bar.GetDirection(), first > second);
        }

        private static Gtk.PopoverMenu? ContextMenu(nint window) => Children(WebView(window)).OfType<Gtk.PopoverMenu>().LastOrDefault();

        internal override bool HasContextMenu(nint window) => ContextMenu(window) is { } popover && popover.GetVisible();

        internal override IReadOnlyList<ItemSnapshot> ReadContextMenu(nint window) =>
            HasContextMenu(window) ? Items(ContextMenu(window)!) : [];

        internal override int ContextMenuDirection(nint window) => (int)ContextMenu(window)!.GetDirection();

        internal override void ActivateContextItem(nint window, int item) => Buttons(ContextMenu(window)!)[item].Activate();

        private static IEnumerable<Gtk.Widget> Children(Gtk.Widget widget)
        {
            for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling()) yield return child;
        }

        private static IEnumerable<Gtk.Widget> Descendants(Gtk.Widget widget, bool skipPopovers = false)
        {
            foreach (var child in Children(widget))
            {
                if (skipPopovers && child is Gtk.Popover) continue;
                yield return child;
                foreach (var descendant in Descendants(child, skipPopovers)) yield return descendant;
            }
        }
    }

    // GTK 3 through its C API; this smoke's GTK 4 process never loads it.
    private static unsafe partial class Gtk3
    {
        private const string Library = "libgtk-3.so.0";

        internal static List<nint> Children(nint container)
        {
            var list = ContainerGetChildren(container);
            List<nint> children = [];
            for (var node = list; node != 0; node = ((nint*)node)[1]) children.Add(((nint*)node)[0]);
            ListFree(list);
            return children;
        }

        internal static List<nint> MenuGetForAttachWidget(nint widget)
        {
            List<nint> menus = [];
            for (var node = MenuGetForAttachWidgetNative(widget); node != 0; node = ((nint*)node)[1]) menus.Add(((nint*)node)[0]);
            return menus;
        }

        internal static bool Is(nint instance, nuint type) => instance != 0 && TypeCheckInstanceIsA(instance, type) != 0;

        internal static string Label(nint item) => Marshal.PtrToStringUTF8(MenuItemGetLabel(item)) ?? "";

        // The text GTK 3 shows for the item's accelerator label, as gtk_accelerator_get_label writes it.
        internal static string? Accelerator(nint item)
        {
            var label = BinGetChild(item);
            if (label == 0) return null;
            uint key = 0, modifiers = 0;
            AccelLabelGetAccel(label, &key, &modifiers);
            if (key == 0) return null;
            var text = AcceleratorGetLabel(key, modifiers);
            try
            {
                return Marshal.PtrToStringUTF8(text);
            }
            finally
            {
                Free(text);
            }
        }

        internal static int AllocationX(nint widget)
        {
            var allocation = stackalloc int[4];
            WidgetGetAllocation(widget, allocation);
            return allocation[0];
        }

        [LibraryImport(Library, EntryPoint = "gtk_bin_get_child")] internal static partial nint BinGetChild(nint bin);
        [LibraryImport(Library, EntryPoint = "gtk_container_get_children")] private static partial nint ContainerGetChildren(nint container);
        [LibraryImport(Library, EntryPoint = "g_list_free")] private static partial void ListFree(nint list);
        [LibraryImport(Library, EntryPoint = "g_free")] private static partial void Free(nint memory);
        [LibraryImport(Library, EntryPoint = "g_type_check_instance_is_a")] private static partial int TypeCheckInstanceIsA(nint instance, nuint type);
        [LibraryImport(Library, EntryPoint = "gtk_box_get_type")] internal static partial nuint BoxType();
        [LibraryImport(Library, EntryPoint = "gtk_menu_bar_get_type")] internal static partial nuint MenuBarType();
        [LibraryImport(Library, EntryPoint = "gtk_check_menu_item_get_type")] internal static partial nuint CheckMenuItemType();
        [LibraryImport(Library, EntryPoint = "gtk_radio_menu_item_get_type")] internal static partial nuint RadioMenuItemType();
        [LibraryImport(Library, EntryPoint = "gtk_separator_menu_item_get_type")] internal static partial nuint SeparatorMenuItemType();
        [LibraryImport(Library, EntryPoint = "gtk_menu_item_get_label")] private static partial nint MenuItemGetLabel(nint item);
        [LibraryImport(Library, EntryPoint = "gtk_menu_item_get_submenu")] internal static partial nint MenuItemGetSubmenu(nint item);
        [LibraryImport(Library, EntryPoint = "gtk_menu_item_activate")] internal static partial void MenuItemActivate(nint item);
        [LibraryImport(Library, EntryPoint = "gtk_menu_shell_activate_item")] private static partial void MenuShellActivateItem(nint shell, nint item, int forceDeactivate);
        internal static void MenuShellActivateItem(nint shell, nint item) => MenuShellActivateItem(shell, item, 1);
        [LibraryImport(Library, EntryPoint = "gtk_check_menu_item_get_active")] private static partial int CheckMenuItemGetActiveNative(nint item);
        internal static bool CheckMenuItemGetActive(nint item) => CheckMenuItemGetActiveNative(item) != 0;
        [LibraryImport(Library, EntryPoint = "gtk_check_menu_item_get_draw_as_radio")] private static partial int CheckMenuItemGetDrawAsRadioNative(nint item);
        internal static bool CheckMenuItemGetDrawAsRadio(nint item) => CheckMenuItemGetDrawAsRadioNative(item) != 0;
        [LibraryImport(Library, EntryPoint = "gtk_widget_get_sensitive")] private static partial int WidgetGetSensitiveNative(nint widget);
        internal static bool WidgetGetSensitive(nint widget) => WidgetGetSensitiveNative(widget) != 0;
        [LibraryImport(Library, EntryPoint = "gtk_widget_get_visible")] private static partial int WidgetGetVisibleNative(nint widget);
        internal static bool WidgetGetVisible(nint widget) => WidgetGetVisibleNative(widget) != 0;
        [LibraryImport(Library, EntryPoint = "gtk_widget_get_direction")] internal static partial int WidgetGetDirection(nint widget);
        [LibraryImport(Library, EntryPoint = "gtk_widget_show")] internal static partial void WidgetShow(nint widget);
        [LibraryImport(Library, EntryPoint = "gtk_widget_hide")] internal static partial void WidgetHide(nint widget);
        [LibraryImport(Library, EntryPoint = "gtk_widget_get_allocation")] private static partial void WidgetGetAllocation(nint widget, int* allocation);
        [LibraryImport(Library, EntryPoint = "gtk_accel_label_get_accel")] private static partial void AccelLabelGetAccel(nint label, uint* key, uint* modifiers);
        [LibraryImport(Library, EntryPoint = "gtk_accelerator_get_label")] private static partial nint AcceleratorGetLabel(uint key, uint modifiers);
        [LibraryImport(Library, EntryPoint = "gtk_menu_get_for_attach_widget")] private static partial nint MenuGetForAttachWidgetNative(nint widget);
    }

    // Keys and buttons of the test display through XTEST: never the host's input. Without a window manager the X
    // focus follows the pointer, so a click into the page first gives it the keys.
    private sealed class XTest : IDisposable
    {
        internal const uint S = 0x73, Escape = 0xff1b, ControlL = 0xffe3, ShiftL = 0xffe1;

        private readonly nint _x11;
        private readonly nint _xtest;
        private readonly nint _display;

        private XTest(nint x11, nint xtest, nint display) => (_x11, _xtest, _display) = (x11, xtest, display);

        internal static unsafe XTest? Open()
        {
            if (!NativeLibrary.TryLoad(Environment.GetEnvironmentVariable("RUNIC_LIBX11") ?? "libX11.so.6", out var x11)
                && !NativeLibrary.TryLoad("libX11.so.6", out x11))
                return null;
            if (!NativeLibrary.TryLoad(Environment.GetEnvironmentVariable("RUNIC_LIBXTST") ?? "libXtst.so.6", out var xtest)
                && !NativeLibrary.TryLoad("libXtst.so.6", out xtest))
                return null;
            var display = ((delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(x11, "XOpenDisplay"))(0);
            return display == 0 ? null : new XTest(x11, xtest, display);
        }

        internal async Task ClickAsync(int x, int y, uint button)
        {
            Move(x, y);
            await Task.Delay(150);
            Button(button, true);
            await Task.Delay(50);
            Button(button, false);
        }

        // Presses the modifiers, then the key, and releases them in reverse order.
        internal async Task KeyAsync(uint keysym, params uint[] modifiers)
        {
            foreach (var modifier in modifiers) Key(modifier, true);
            Key(keysym, true);
            await Task.Delay(30);
            Key(keysym, false);
            foreach (var modifier in modifiers.Reverse()) Key(modifier, false);
            await Task.Delay(30);
        }

        private unsafe void Move(int x, int y)
        {
            ((delegate* unmanaged[Cdecl]<nint, int, int, int, nuint, int>)NativeLibrary.GetExport(_xtest, "XTestFakeMotionEvent"))(_display, -1, x, y, 0);
            Flush();
        }

        private unsafe void Button(uint button, bool pressed)
        {
            ((delegate* unmanaged[Cdecl]<nint, uint, int, nuint, int>)NativeLibrary.GetExport(_xtest, "XTestFakeButtonEvent"))(_display, button, pressed ? 1 : 0, 0);
            Flush();
        }

        private unsafe void Key(uint keysym, bool pressed)
        {
            var keycode = ((delegate* unmanaged[Cdecl]<nint, nuint, byte>)NativeLibrary.GetExport(_x11, "XKeysymToKeycode"))(_display, keysym);
            ((delegate* unmanaged[Cdecl]<nint, uint, int, nuint, int>)NativeLibrary.GetExport(_xtest, "XTestFakeKeyEvent"))(_display, keycode, pressed ? 1 : 0, 0);
            Flush();
        }

        private unsafe void Flush() => ((delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(_x11, "XFlush"))(_display);

        public unsafe void Dispose() => ((delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(_x11, "XCloseDisplay"))(_display);
    }
}
