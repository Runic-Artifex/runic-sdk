using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Runic.Desktop;
using Runic.Platform;
using WebView2Host = Runic.Desktop.Internal.WindowsWebView2Host;

// B10 PR 2 on the Windows runners: the declared menu bar in the WebView2 window. The bar holds the menus in Windows
// order with access keys, shortcut text, check, radio and disabled states; a popup follows its commands when it opens
// (WM_INITMENUPOPUP); WM_COMMAND runs the window's target, then the application's, with the item's parameter. Keys go
// to the page first: a shortcut it leaves alone runs the item once, also one the browser would take (F5), and one it
// cancels runs nothing. An unhandled Alt opens the bar (WebView2 forwards SC_KEYMENU) and Alt with a menu's access key
// opens that menu (the host sends SC_KEYMENU). Context menus return the chosen item, null when dismissed or replaced,
// and cancel; a right-to-left window mirrors its menus. A Page window has no bar but keeps the shortcuts and leaves Alt
// to the page; a frameless one has no bar. The arm64 runner's desktop delivers no SendInput, so the keyboard cases are
// unverified there, as the file drop is. Full screen hides the bar.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static partial class WindowsMenuSmoke
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    internal static readonly IReadOnlyList<DesktopMenu> Bar =
    [
        new("Help", [new DesktopMenuItem("Runic Help", "help.show")]) { Role = DesktopMenuRole.Help, AccessKey = 'H' },
        new("Tools",
        [
            new DesktopMenuItem("Run", "tools.run") { Shortcut = DesktopShortcut.Parse("Primary+Shift+R"), AccessKey = 'R' },
            new DesktopMenuSeparator(),
            new DesktopMenuItem("Reload Data", "tools.reload") { Shortcut = DesktopShortcut.Parse("F5") },
            new DesktopMenuItem("Copy Special", "tools.copy") { Shortcut = DesktopShortcut.Parse("Primary+C") },
            new DesktopMenu("More", [new DesktopMenuItem("Nothing", "tools.nothing")]),
            new DesktopMenuItem("Locked", "tools.run") { IsEnabled = false },
            new DesktopMenuItem("Word Wrap", "tools.run") { IsChecked = true },
            new DesktopMenuItem("Small", "tools.zoom") { RadioGroup = "zoom", IsChecked = false, CommandParameter = "0.8" },
            new DesktopMenuItem("Large", "tools.zoom") { RadioGroup = "zoom", IsChecked = true, CommandParameter = "1.5" },
            new DesktopMenu("Disabled More", [new DesktopMenuItem("Inside", "tools.run")]) { IsEnabled = false },
        ]) { AccessKey = 'T' },
        new("Archive", [new DesktopMenuItem("Restore", "tools.run")]) { IsEnabled = false },
        new("Fichier & co", [new DesktopMenuItem("Enregistrer", "file.save") { Shortcut = DesktopShortcut.Parse("Primary+S"), AccessKey = 'S' }])
        {
            Role = DesktopMenuRole.File,
            AccessKey = 'F',
        },
    ];

    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic menus</title></head>
        <body><input id="field" autofocus><script>
        window.__keys = [];
        window.__block = false;
        addEventListener("keydown", event => {
          window.__keys.push(event.key);
          if (window.__block) event.preventDefault();
        });
        </script></body></html>
        """;

    internal sealed class Target(params string[] handled) : IDesktopMenuCommandTarget
    {
        private readonly HashSet<string> _handled = [.. handled];
        internal bool Enabled { get; set; } = true;
        internal ConcurrentQueue<(string Id, string? Parameter)> Executed { get; } = new();
        public bool CanExecute(DesktopMenuItem item) => Enabled && _handled.Contains(item.CommandId);
        public void Execute(DesktopMenuItem item) => Executed.Enqueue((item.CommandId, item.CommandParameter));
    }

    internal static async Task RunAsync(DesktopHostOptions options)
    {
        await using var host = await DesktopHost.StartAsync(options with { MenuBar = Bar });
        var validation = host.Validate(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
        Require(!validation.Diagnostics.Any(static item => item.Code == "menu-bar-unsupported"), "Validate no longer reports menu-bar-unsupported on Windows");
        Require(validation.Diagnostics.Any(static item => item.Code == "menu-shortcut-reserved" && item.Message.Contains("Copy Special", StringComparison.Ordinal)),
            "Validate reports Ctrl+C as kept by Windows");
        var applicationTarget = new Target("help.show", "tools.run");
        host.MenuCommands = applicationTarget;

        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 640, Height = 420, X = 200, Y = 150 }))
        {
            var windowTarget = new Target("file.save", "tools.run", "tools.zoom", "tools.reload");
            window.MenuCommands = windowTarget;
            Require(window.Capabilities.HasFlag(DesktopWindowCapabilities.MenuBar | DesktopWindowCapabilities.ContextMenu),
                $"the window reports MenuBar and ContextMenu, not {window.Capabilities}");
            Require(window.MenuBarPlacement == DesktopMenuBarPlacement.Window, $"the placement is Window, not {window.MenuBarPlacement}");
            var bar = await WaitForAsync(window, static handle => GetMenu(handle) is not 0 and var menu ? menu : null, "the menu bar");
            var ids = await CheckBarAsync(host, window, bar, windowTarget, applicationTarget);
            // SetMenuBar rebuilt the bar.
            bar = await WaitForAsync(window, static handle => GetMenu(handle) is not 0 and var menu ? menu : null, "the rebuilt menu bar");
            Console.WriteLine("PASS menu bar: Windows order, access keys, shortcut text, check, radio and disabled states, availability on WM_INITMENUPOPUP, WM_COMMAND with parameters.");

            windowTarget.Executed.Clear();
            Require(await window.TryExecuteMenuItemAsync(new DesktopMenuItem("Large", "tools.zoom") { CommandParameter = "1.5" })
                && windowTarget.Executed.TryDequeue(out var executed) && executed == ("tools.zoom", "1.5"),
                "TryExecuteMenuItemAsync runs the window's command with its parameter");

            if (await KeyboardAsync(surface, window))
            {
                await CheckShortcutsAsync(surface, window, windowTarget);
                await CheckAltAsync(surface, window, bar, ids.File);
                Console.WriteLine("PASS keyboard: unhandled shortcuts run once (also F5, which the browser keeps off), cancelled ones never; Alt opens the bar and Alt+F its File menu.");
            }
            await CheckContextMenuAsync(window);
            Console.WriteLine("PASS context menu: chosen item, Escape, replaced by a later menu, cancelled, at the page point.");
            // Full screen shows no bar; it returns when the window leaves full screen.
            await window.SetFullScreenAsync(true);
            nint hidden = -1;
            await window.InvokeNativeAsync(handle => hidden = GetMenu(handle));
            await window.SetFullScreenAsync(false);
            nint shown = 0;
            await window.InvokeNativeAsync(handle => shown = GetMenu(handle));
            Require(hidden == 0 && shown != 0, "full screen hides the bar and leaving it shows the bar again");
            window.MenuCommands = null;
        }

        // Right to left: the frame is mirrored and the menus cascade and align right to left.
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded, Width = 640, Height = 420, X = 200, Y = 150, LayoutDirection = DesktopLayoutDirection.RightToLeft,
        }))
        {
            var bar = await WaitForAsync(window, static handle => GetMenu(handle) is not 0 and var menu ? menu : null, "the right-to-left menu bar");
            string? failure = null;
            await window.InvokeNativeAsync(handle =>
            {
                if ((GetWindowLongPtr(handle, -20) & 0x00400000) == 0) { failure = "the window is not WS_EX_LAYOUTRTL"; return; }
                var tools = GetSubMenu(bar, 1);
                for (var position = 0; position < GetMenuItemCount(tools); position++)
                {
                    if ((Read(tools, position).Type & 0x6000) != 0x6000) { failure = $"Tools item {position} lacks MFT_RIGHTORDER | MFT_RIGHTJUSTIFY"; return; }
                }
            });
            Require(failure is null, failure ?? "");
            await CheckContextChoiceAsync(window, "right to left");
            Console.WriteLine("PASS right to left: mirrored frame, menus cascade and align right to left, the context menu chooses its item.");
        }

        // Page: no bar in the window, the shortcuts stay, and an unhandled Alt is the page's.
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded, Width = 640, Height = 420, X = 200, Y = 150, MenuBarPresentation = DesktopMenuBarPresentation.Page,
        }))
        {
            var windowTarget = new Target("tools.run");
            window.MenuCommands = windowTarget;
            await Task.Delay(500);
            nint menu = -1;
            await window.InvokeNativeAsync(handle => menu = GetMenu(handle));
            Require(menu == 0, "a Page window has no menu bar");
            Require(window.MenuBarPlacement == DesktopMenuBarPlacement.None, $"a Page window's placement is None until the in-page bar, not {window.MenuBarPlacement}");
            if (await KeyboardAsync(surface, window))
            {
                await PressAsync([(VkControl, false), (VkShift, false), ('R', false), ('R', true), (VkShift, true), (VkControl, true)]);
                await WaitForAsync(() => windowTarget.Executed.Count == 1, "Ctrl+Shift+R in the Page window");
                await PressAsync([(VkMenu, false), (VkMenu, true)]);
                await Task.Delay(700);
                Require((MenuFlags(window.NativeHandle) & GuiInMenuMode) == 0, "an unhandled Alt in a Page window enters no menu mode");
                Console.WriteLine("PASS Page: no bar, Ctrl+Shift+R runs once, Alt stays the page's.");
            }
            window.MenuCommands = null;
        }

        // Frameless: no frame for a bar.
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded, Width = 400, Height = 300, X = 200, Y = 150, Frameless = true,
        }))
        {
            await Task.Delay(500);
            nint menu = -1;
            await window.InvokeNativeAsync(handle => menu = GetMenu(handle));
            Require(menu == 0 && !window.Capabilities.HasFlag(DesktopWindowCapabilities.MenuBar) && window.MenuBarPlacement == DesktopMenuBarPlacement.None,
                "a frameless window has no bar and does not report MenuBar");
            Require(window.Capabilities.HasFlag(DesktopWindowCapabilities.ContextMenu), "a frameless window still has context menus");
        }
        host.MenuCommands = null;
        Console.WriteLine("PASS Page and frameless windows.");
    }

    private sealed record BarIds(uint Run, uint Small, uint Locked, uint File);

    private static async Task<BarIds> CheckBarAsync(DesktopHost host, DesktopWindow window, nint bar, Target windowTarget, Target applicationTarget)
    {
        BarIds? ids = null;
        string? failure = null;
        await window.InvokeNativeAsync(handle =>
        {
            try { (ids, failure) = ReadBar(bar); }
            catch (Exception error) { failure = error.ToString(); }
        });
        Require(failure is null && ids is not null, "menu bar: " + failure);
        var tools = GetSubMenu(bar, 1);
        // WM_INITMENUPOPUP asks the commands, as AppKit validates its items: sent from this thread, it runs on the window's.
        uint State(uint id) => GetMenuState(tools, id, 0);
        void Open() => SendMessage(window.NativeHandle, 0x0117, tools, 1);
        Open();
        var nothing = GetMenuItemId(GetSubMenu(tools, 4), 0);
        Require((State(ids!.Run) & MfGrayed) == 0 && (GetMenuState(GetSubMenu(tools, 4), nothing, 0) & MfGrayed) == 0,
            "Run is enabled; the More submenu waits for its own popup");
        SendMessage(window.NativeHandle, 0x0117, GetSubMenu(tools, 4), 4);
        Require((GetMenuState(GetSubMenu(tools, 4), nothing, 0) & MfGrayed) != 0, "Nothing, which no target runs, is grayed");
        windowTarget.Enabled = false;
        Open();
        Require((State(ids.Run) & MfGrayed) == 0, "Run falls back to the application's command");
        applicationTarget.Enabled = false;
        Open();
        Require((State(ids.Run) & MfGrayed) != 0, "Run is grayed while no target can run it");
        windowTarget.Enabled = true;
        applicationTarget.Enabled = true;
        Open();
        Require((State(ids.Locked) & MfGrayed) != 0, "the declared-disabled Locked stays grayed while its command can run");

        // WM_COMMAND runs the window's target with the item's parameter; a disabled item runs nothing.
        SendMessage(window.NativeHandle, 0x0111, (nint)ids.Small, 0);
        Require(windowTarget.Executed.TryDequeue(out var small) && small == ("tools.zoom", "0.8"), "choosing Small runs tools.zoom with 0.8");
        SendMessage(window.NativeHandle, 0x0111, (nint)ids.Locked, 0);
        Require(windowTarget.Executed.IsEmpty && applicationTarget.Executed.IsEmpty, "choosing the disabled Locked runs nothing");
        SendMessage(window.NativeHandle, 0x0111, (nint)ids.Run, 0);
        Require(windowTarget.Executed.TryDequeue(out var run) && run.Id == "tools.run", "choosing Run runs the window's command");
        // A removed item's id never reaches another item.
        host.SetMenuBar([.. Bar.Where(static menu => menu.Title != "Archive")]);
        await Task.Delay(300);
        SendMessage(window.NativeHandle, 0x0111, (nint)ids.Run, 0);
        Require(windowTarget.Executed.TryDequeue(out run) && run.Id == "tools.run", "Run keeps its id after SetMenuBar");
        host.SetMenuBar(Bar);
        await Task.Delay(300);
        return ids;
    }

    // Window thread: the bar as InsertMenuItemW built it.
    private static (BarIds?, string?) ReadBar(nint bar)
    {
        var titles = Enumerable.Range(0, GetMenuItemCount(bar)).Select(position => Read(bar, position).Label).ToArray();
        string[] expected = ["&Fichier && co", "&Tools", "Archive", "&Help"];
        if (!titles.SequenceEqual(expected)) return (null, $"the menus are [{string.Join(", ", titles)}], not [{string.Join(", ", expected)}]");
        if ((Read(bar, 2).State & MfsDisabled) != MfsDisabled) return (null, "the disabled Archive menu is not grayed");
        var file = GetSubMenu(bar, 0);
        var save = Read(file, 0);
        if (save.Label != "Enregi&strer\tCtrl+S") return (null, $"Enregistrer reads '{save.Label}'");
        var tools = GetSubMenu(bar, 1);
        var entries = Enumerable.Range(0, GetMenuItemCount(tools)).Select(position => Read(tools, position)).ToArray();
        if (entries.Length != 10) return (null, $"Tools has {entries.Length} entries");
        if (entries[0].Label != "&Run\tCtrl+Shift+R") return (null, $"Run reads '{entries[0].Label}'");
        if ((entries[1].Type & 0x800) == 0) return (null, "the separator is missing");
        if (entries[2].Label != "Reload Data\tF5") return (null, $"Reload Data reads '{entries[2].Label}'");
        if (entries[3].Label != "Copy Special") return (null, "Copy Special shows Ctrl+C, which Windows keeps for Copy");
        if (entries[4].SubMenu == 0) return (null, "More is not a submenu");
        if ((entries[5].State & MfsDisabled) != MfsDisabled) return (null, "Locked is not grayed");
        if ((entries[6].State & MfsChecked) == 0 || (entries[6].Type & MftRadioCheck) != 0) return (null, "Word Wrap is not a checked check item");
        if ((entries[7].Type & MftRadioCheck) == 0 || (entries[7].State & MfsChecked) != 0) return (null, "Small is not an unchecked radio item");
        if ((entries[8].Type & MftRadioCheck) == 0 || (entries[8].State & MfsChecked) == 0) return (null, "Large is not a checked radio item");
        if (entries[9].SubMenu == 0 || (entries[9].State & MfsDisabled) != MfsDisabled
            || (Read(entries[9].SubMenu, 0).State & MfsDisabled) != MfsDisabled)
            return (null, "the disabled submenu or its item is not grayed");
        return (new BarIds(entries[0].Id, entries[7].Id, entries[5].Id, save.Id), null);
    }

    // Whether this desktop delivers SendInput to the window: a key reaches the page.
    private static async Task<bool> KeyboardAsync(DesktopSurface surface, DesktopWindow window)
    {
        await FocusPageAsync(surface, window);
        await PressAsync([(VkEnd, false), (VkEnd, true)]);
        await Task.Delay(500);
        if (await surface.ExecuteJavaScriptAsync("return window.__keys.join(',');") is { Length: > 0 }) return true;
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 || Environment.GetEnvironmentVariable("CI") != "true")
        {
            Console.WriteLine("Menus: keyboard cases unverified: this desktop delivers no SendInput to the window.");
            return false;
        }
        throw new InvalidOperationException("Menus: SendInput did not reach the page on this runner.");
    }

    private static async Task FocusPageAsync(DesktopSurface surface, DesktopWindow window)
    {
        await window.FocusAsync();
        await Task.Delay(300);
        await surface.ExecuteJavaScriptAsync("window.__keys = []; window.__block = false; document.getElementById('field').focus(); return '';");
    }

    private static async Task CheckShortcutsAsync(DesktopSurface surface, DesktopWindow window, Target target)
    {
        target.Executed.Clear();
        await FocusPageAsync(surface, window);
        await PressAsync([(VkControl, false), ('S', false), ('S', true), (VkControl, true)]);
        await WaitForAsync(() => !target.Executed.IsEmpty, "Ctrl+S");
        await Task.Delay(500);
        Require(target.Executed.Count == 1 && target.Executed.TryDequeue(out var save) && save.Id == "file.save", "Ctrl+S runs Enregistrer once");
        Require((await surface.ExecuteJavaScriptAsync("return window.__keys.join(',');")).Contains('s', StringComparison.OrdinalIgnoreCase),
            "the page saw Ctrl+S first");
        // F5 is the browser's reload, which the host keeps off for the declared item.
        await surface.ExecuteJavaScriptAsync("window.__marker = 'kept'; return '';");
        await PressAsync([(VkF5, false), (VkF5, true)]);
        await WaitForAsync(() => !target.Executed.IsEmpty, "F5");
        await Task.Delay(1000);
        Require(target.Executed.TryDequeue(out var reload) && reload.Id == "tools.reload" && target.Executed.IsEmpty, "F5 runs Reload Data once");
        Require(await surface.ExecuteJavaScriptAsync("return String(window.__marker) + ':' + window.__keys.includes('F5');") == "kept:true",
            "the page saw F5 and did not reload");
        // A key the page cancels is the page's.
        await surface.ExecuteJavaScriptAsync("window.__block = true; return '';");
        await PressAsync([(VkControl, false), ('S', false), ('S', true), (VkControl, true)]);
        await Task.Delay(1000);
        Require(target.Executed.IsEmpty, "Ctrl+S that the page cancelled runs nothing");
        await surface.ExecuteJavaScriptAsync("window.__block = false; return '';");
    }

    private static async Task CheckAltAsync(DesktopSurface surface, DesktopWindow window, nint bar, uint saveId)
    {
        var handle = window.NativeHandle;
        await FocusPageAsync(surface, window);
        // An unhandled Alt alone reaches the window as SC_KEYMENU from WebView2.
        await PressAsync([(VkMenu, false), (VkMenu, true)]);
        await WaitForAsync(() => (MenuFlags(handle) & GuiInMenuMode) != 0, "menu mode after Alt");
        await window.InvokeNativeAsync(_ => EndMenu());
        await WaitForAsync(() => (MenuFlags(handle) & GuiInMenuMode) == 0, "menu mode to end");
        // Alt and F, the File menu's access key, open that menu: WebView2 forwards none of it, the host sends SC_KEYMENU.
        await FocusPageAsync(surface, window);
        await PressAsync([(VkMenu, false), ('F', false), ('F', true), (VkMenu, true)]);
        await WaitForAsync(() => (MenuFlags(handle) & GuiInMenuMode) != 0 && FindMenuWindow(handle) != 0, "the File menu after Alt+F");
        var popup = SendMessage(FindMenuWindow(handle), 0x01E1, 0, 0); // MN_GETHMENU
        Require(popup == GetSubMenu(bar, 0), "Alt+F opened the File menu");
        await window.InvokeNativeAsync(_ => EndMenu());
        await WaitForAsync(() => (MenuFlags(handle) & GuiInMenuMode) == 0, "menu mode to end");
        // The page that cancels Alt keeps it.
        await FocusPageAsync(surface, window);
        await surface.ExecuteJavaScriptAsync("window.__block = true; return '';");
        await PressAsync([(VkMenu, false), ('F', false), ('F', true), (VkMenu, true)]);
        await Task.Delay(700);
        Require((MenuFlags(handle) & GuiInMenuMode) == 0, "Alt+F that the page cancelled opens no menu");
        await surface.ExecuteJavaScriptAsync("window.__block = false; return '';");
        _ = saveId;
    }

    private static async Task CheckContextMenuAsync(DesktopWindow window)
    {
        ImmutableArray<DesktopMenuEntry> items =
        [
            new DesktopMenuItem("Rename", "row.rename") { CommandParameter = "row-7", Shortcut = DesktopShortcut.Parse("F2") },
            new DesktopMenuSeparator(),
            new DesktopMenu("More", [new DesktopMenuItem("Delete", "row.delete") { IsEnabled = false }]),
        ];
        await CheckContextChoiceAsync(window, "left to right", items, checkPosition: true);

        // Escape dismisses the menu: null.
        var escaped = window.ShowContextMenuAsync(items).AsTask();
        var menu = await TrackingAsync(0);
        PostMessage(menu, WmKeyDown, VkEscape, 0);
        Require(await escaped.WaitAsync(Deadline) is null, "Escape returns null");

        // A later menu dismisses the open one, which returns null; cancelling closes the later one.
        Idle.Clear();
        var first = window.ShowContextMenuAsync(items).AsTask();
        var firstMenu = await TrackingAsync(0);
        using var cancel = new CancellationTokenSource();
        var second = window.ShowContextMenuAsync(items, cancellationToken: cancel.Token).AsTask();
        Require(await first.WaitAsync(Deadline) is null, "a menu replaced by a later one returns null");
        await TrackingAsync(firstMenu, allowSame: true);
        cancel.Cancel();
        try
        {
            await second.WaitAsync(Deadline);
            throw new InvalidOperationException("Context menu: a cancelled menu did not throw OperationCanceledException.");
        }
        catch (OperationCanceledException) { }
        await WaitForAsync(() => FindMenuWindow(window.NativeHandle) == 0, "no menu open after the last one closed");
    }

    // Opens a context menu at a page point and chooses its first item with the keyboard, from the menu's idle loop.
    private static async Task CheckContextChoiceAsync(DesktopWindow window, string name,
        ImmutableArray<DesktopMenuEntry>? entries = null, bool checkPosition = false)
    {
        ImmutableArray<DesktopMenuEntry> items = entries ?? [new DesktopMenuItem("Rename", "row.rename") { CommandParameter = "row-7" }];
        Idle.Clear();
        var chosen = window.ShowContextMenuAsync(items, new DesktopContextMenuOptions { X = 40, Y = 30 }).AsTask();
        var menu = await TrackingAsync(0);
        if (checkPosition)
        {
            // The menu opens at the page point: the client area's corner plus CSS pixels times the DPI scale.
            var expected = new Point { X = 0, Y = 0 };
            ClientToScreen(window.NativeHandle, ref expected);
            var scale = GetDpiForWindow(window.NativeHandle) / 96d;
            GetWindowRect(menu, out var bounds);
            var x = expected.X + (int)Math.Round(40 * scale);
            var y = expected.Y + (int)Math.Round(30 * scale);
            var edge = GetSystemMetrics(40) != 0 ? bounds.Right : bounds.Left; // SM_MENUDROPALIGNMENT
            Require(Math.Abs(edge - x) <= 4 && Math.Abs(bounds.Top - y) <= 4,
                $"the context menu opens at the page point {x},{y}, not at {bounds.Left},{bounds.Top}-{bounds.Right}");
        }
        PostMessage(menu, WmKeyDown, VkDown, 0);
        PostMessage(menu, WmKeyDown, VkReturn, 0);
        var result = await chosen.WaitAsync(Deadline);
        Require(ReferenceEquals(result, items[0]), $"context menu ({name}): returned {result?.Title ?? "null"}, not {((DesktopMenuItem)items[0]).Title}");
    }

    // The menu windows the host reported from WM_ENTERIDLE (WindowsWebView2Host.ContextMenuIdle).
    private static readonly ConcurrentQueue<nint> Idle = new();

    static WindowsMenuSmoke() => WebView2Host.ContextMenuIdle = static menu => Idle.Enqueue(menu);

    private static async Task<nint> TrackingAsync(nint previous, bool allowSame = false)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < Deadline)
        {
            while (Idle.TryDequeue(out var menu))
            {
                if (menu != 0 && (menu != previous || allowSame) && IsWindowVisible(menu))
                {
                    // Let the menu settle before it is driven.
                    await Task.Delay(200);
                    Idle.Clear();
                    return menu;
                }
            }
            await Task.Delay(20);
        }
        throw new InvalidOperationException("Context menu: no menu was idle within 10 seconds.");
    }

    private static nint FindMenuWindow(nint owner)
    {
        var thread = GetWindowThreadProcessId(owner, out _);
        nint found = 0;
        for (var candidate = FindWindowEx(0, 0, "#32768", null); candidate != 0; candidate = FindWindowEx(0, candidate, "#32768", null))
        {
            if (IsWindowVisible(candidate) && GetWindowThreadProcessId(candidate, out _) == thread) found = candidate;
        }
        return found;
    }

    private static uint MenuFlags(nint window)
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(GetWindowThreadProcessId(window, out _), ref info) ? info.Flags : 0;
    }

    private static async Task<nint> WaitForAsync(DesktopWindow window, Func<nint, nint?> read, string what)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < Deadline)
        {
            nint? value = null;
            await window.InvokeNativeAsync(handle => value = read(handle));
            if (value is { } found) return found;
            await Task.Delay(50);
        }
        throw new InvalidOperationException($"Menus: no {what} within 10 seconds.");
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var started = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - started > Deadline) throw new InvalidOperationException($"Menus: waited 10 seconds for {what}.");
            await Task.Delay(50);
        }
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Menus: " + what);
    }

    private record struct MenuItem(uint Type, uint State, uint Id, nint SubMenu, string Label);

    private static unsafe MenuItem Read(nint menu, int position)
    {
        var buffer = stackalloc char[256];
        var info = new MenuItemInfo
        {
            Size = (uint)sizeof(MenuItemInfo),
            Mask = 0x1 | 0x2 | 0x4 | 0x40 | 0x100, // STATE, ID, SUBMENU, STRING, FTYPE
            TypeData = (nint)buffer,
            Count = 256,
        };
        if (!GetMenuItemInfo(menu, (uint)position, true, ref info)) throw new InvalidOperationException($"GetMenuItemInfo failed for item {position}.");
        return new MenuItem(info.Type, info.State, info.Id, info.SubMenu, new string(buffer, 0, (int)info.Count));
    }

    private static async Task PressAsync((int Key, bool Up)[] keys)
    {
        foreach (var (key, up) in keys)
        {
            Key((ushort)key, up);
            await Task.Delay(60);
        }
    }

    private static unsafe void Key(ushort key, bool up)
    {
        var input = new Input { Type = 1, Keyboard = new KeyboardInput { Key = key, Flags = up ? 0x0002u : 0 } };
        _ = SendInput(1, &input, sizeof(Input));
    }

    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkEnd = 0x23, VkF5 = 0x74;
    private const nuint VkEscape = 0x1B, VkDown = 0x28, VkReturn = 0x0D;
    private const uint WmKeyDown = 0x0100;
    private const uint MfGrayed = 0x1, MfsDisabled = 0x3, MfsChecked = 0x8, MftRadioCheck = 0x200;
    private const uint GuiInMenuMode = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort Key;
        internal ushort Scan;
        internal uint Flags;
        internal uint Time;
        internal nuint Extra;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MenuItemInfo
    {
        internal uint Size;
        internal uint Mask;
        internal uint Type;
        internal uint State;
        internal uint Id;
        internal nint SubMenu;
        internal nint Checked;
        internal nint Unchecked;
        internal nuint Data;
        internal nint TypeData;
        internal uint Count;
        internal nint Item;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        internal uint Size;
        internal uint Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [LibraryImport("user32.dll", SetLastError = true)] private static unsafe partial uint SendInput(uint count, Input* inputs, int size);
    [LibraryImport("user32.dll")] private static partial nint GetMenu(nint window);
    [LibraryImport("user32.dll")] private static partial nint GetSubMenu(nint menu, int position);
    [LibraryImport("user32.dll")] private static partial int GetMenuItemCount(nint menu);
    [LibraryImport("user32.dll")] private static partial uint GetMenuItemId(nint menu, int position);
    [LibraryImport("user32.dll")] private static partial uint GetMenuState(nint menu, uint id, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMenuItemInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMenuItemInfo(nint menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfo info);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")] private static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndMenu();
    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowEx(nint parent, nint after, string className, string? title);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint window, out uint process);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static partial nint GetWindowLongPtr(nint window, int index);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref Point point);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect bounds);
    [LibraryImport("user32.dll")] private static partial uint GetDpiForWindow(nint window);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
}
