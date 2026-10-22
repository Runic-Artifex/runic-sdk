using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Runic.Desktop;
using Runic.Platform;

// B12 (I-066, I-073) on the Windows and macOS runners: the host lists its monitors and watches them, a window opens on
// and moves to a monitor, enters and leaves full screen, and a placement saved full-screen opens full-screen. On
// Windows a right-to-left window has a mirrored frame (WS_EX_LAYOUTRTL) while WebView2 still renders and hit-tests
// the page where it is shown (#389), and the web view takes the window's Language. On macOS the standard menus take
// DesktopHostOptions.StandardMenuTitles. The runners have one monitor; two monitors need a VM (I-099).
internal static partial class MonitorSmoke
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    // German titles, as an application would take them from its translations.
    internal static readonly DesktopStandardMenuTitles GermanTitles = new()
    {
        About = "Über {0}", Hide = "{0} ausblenden", HideOthers = "Andere ausblenden", ShowAll = "Alle einblenden",
        Quit = "{0} beenden", File = "Ablage", Close = "Schließen", Edit = "Bearbeiten", Undo = "Widerrufen",
        Redo = "Wiederholen", Cut = "Ausschneiden", Copy = "Kopieren", Paste = "Einsetzen", SelectAll = "Alles auswählen",
        View = "Darstellung", ActualSize = "Originalgröße", ZoomIn = "Vergrößern", ZoomOut = "Verkleinern", Window = "Fenster",
        Minimize = "Im Dock ablegen", Zoom = "Zoomen", BringAllToFront = "Alle nach vorne bringen",
    };

    private const string Page = """
        <!doctype html><html dir="rtl" lang="ar"><head><meta charset="utf-8"><script src="webui.js"></script>
        <title>Runic monitors</title><style>html,body{margin:0;height:100%;background:#fff}
        #target{position:absolute;left:40px;top:40px;width:160px;height:100px;border:0;background:rgb(255,0,0)}</style></head>
        <body><button id="target" type="button">هدف</button>
        <script>window.clicks = 0; document.getElementById('target').addEventListener('click', () => window.clicks++);</script></body></html>
        """;

    internal static async Task<int> RunAsync(DesktopHost host)
    {
        var monitors = await CheckMonitorsAsync(host);
        var primary = monitors[0];
        await CheckWatchAsync(host, monitors);
        DesktopWindowPlacement saved;
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        {
            var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded, Width = 520, Height = 360, Monitor = primary.Id,
            });
            Require(window.Capabilities.HasFlag(DesktopWindowCapabilities.FullScreen | DesktopWindowCapabilities.MonitorPlacement),
                $"the window advertises FullScreen and MonitorPlacement, not {window.Capabilities}");
            var opened = await WaitForAsync(window, state => state.MonitorId == primary.Id, $"the monitor {primary.Id}");
            RequireInside(opened, primary.WorkArea, "opened on the chosen monitor");
            Console.WriteLine($"Monitors: opened on {primary.DisplayName} at {opened.X},{opened.Y}.");

            await window.MoveAsync(primary.WorkArea.X + 10, OperatingSystem.IsMacOS() ? primary.WorkArea.Y + 10 : primary.WorkArea.Y + 10);
            await window.MoveToMonitorAsync(primary.Id);
            var centered = await WaitForAsync(window, state => state.X != primary.WorkArea.X + 10, "the window centered on the monitor");
            RequireInside(centered, primary.WorkArea, "moved to the monitor");
            await RequireThrowsAsync<ArgumentException>(() => window.MoveToMonitorAsync("runic-no-such-monitor").AsTask(), "an unknown monitor");

            var windowed = window.State!;
            await window.SetFullScreenAsync(true);
            var full = await WaitForAsync(window, static state => state.IsFullScreen, "full screen");
            Require(full.Placement.FullScreen, "the placement of a full-screen window records FullScreen");
            Require(full.Placement with { FullScreen = false } == windowed.Placement with { FullScreen = false },
                $"full screen kept the windowed placement {windowed.Placement}, got {full.Placement}");
            if (OperatingSystem.IsWindows()) Windows.RequireCovers(window.NativeHandle, primary.Bounds, captioned: false);
            Console.WriteLine($"Monitors: full screen {full}.");
            await window.SetFullScreenAsync(true); // Already full-screen: nothing changes.
            await window.SetFullScreenAsync(false);
            var restored = await WaitForAsync(window, static state => !state.IsFullScreen, "leaving full screen");
            Require(Near(restored.Width, windowed.Width) && Near(restored.Height, windowed.Height),
                $"leaving full screen restored {windowed}, got {restored}");
            if (OperatingSystem.IsWindows()) Windows.RequireCaptioned(window.NativeHandle);

            await window.SetFullScreenAsync(true);
            saved = (await WaitForAsync(window, static state => state.IsFullScreen, "full screen again")).Placement;
            await window.CloseAsync();
            Require(window.State?.Placement.FullScreen == true, "a window closed full-screen saves a full-screen placement");
        }

        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Placement = saved }))
        {
            await WaitForAsync(window, static state => state.IsFullScreen, "the full-screen placement restored full-screen");
            await window.SetFullScreenAsync(false);
            var windowed = await WaitForAsync(window, static state => !state.IsFullScreen, "leaving the restored full screen");
            Require(Near(windowed.Width, saved.Width) && Near(windowed.Height, saved.Height),
                $"the restored window left full screen to {saved}, got {windowed}");
            Console.WriteLine("PASS full-screen restore: a placement saved full-screen opened full-screen and left it to its bounds.");
        }

        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        await using (var kiosk = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Kiosk = true }))
        {
            await RequireThrowsAsync<InvalidOperationException>(() => kiosk.SetFullScreenAsync(false).AsTask(), "a kiosk window leaving full screen");
            var state = await WaitForAsync(kiosk, static state => state.IsFullScreen, "the kiosk window full-screen");
            Require(!state.Placement.FullScreen, "a kiosk window does not save a full-screen placement");
        }

        if (OperatingSystem.IsWindows())
        {
            await Windows.RightToLeftAsync(host);
        }
        if (OperatingSystem.IsMacOS())
        {
            await MacOS.CheckTitlesAsync(host);
        }
        Console.WriteLine("PASS monitors: list, watch, open and move on a monitor, full screen and its restore.");
        return 0;
    }

    private static async Task<ImmutableArray<DesktopMonitor>> CheckMonitorsAsync(DesktopHost host)
    {
        var result = await host.Monitors.GetMonitorsAsync();
        if (result is not PlatformResult<ImmutableArray<DesktopMonitor>>.Success { Value: var monitors })
            throw new InvalidOperationException($"The host did not list its monitors: {result}.");
        foreach (var monitor in monitors) Console.WriteLine($"Monitors: {monitor}.");
        Require(monitors.Length > 0, "at least one monitor");
        Require(monitors[0].IsPrimary && monitors.Count(static monitor => monitor.IsPrimary) == 1, "one primary monitor, listed first");
        Require(monitors.Select(static monitor => monitor.Id).Distinct().Count() == monitors.Length
            && monitors.All(static monitor => monitor.Id.Length > 0 && monitor.DisplayName.Length > 0), "distinct identifiers and names");
        foreach (var monitor in monitors)
        {
            Require(monitor.Scale > 0 && monitor.Bounds.Width > 0 && monitor.Bounds.Height > 0, $"{monitor.Id} has a size and scale");
            Require(monitor.WorkArea.X >= monitor.Bounds.X && monitor.WorkArea.Y >= monitor.Bounds.Y
                && monitor.WorkArea.X + monitor.WorkArea.Width <= monitor.Bounds.X + monitor.Bounds.Width
                && monitor.WorkArea.Y + monitor.WorkArea.Height <= monitor.Bounds.Y + monitor.Bounds.Height,
                $"{monitor.Id}'s work area lies inside its bounds");
        }
        if (OperatingSystem.IsWindows()) Windows.RequireMatchesSystem(monitors);
        return monitors;
    }

    private static async Task CheckWatchAsync(DesktopHost host, ImmutableArray<DesktopMonitor> monitors)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var watch = host.Monitors.WatchAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Require(await watch.MoveNextAsync() && watch.Current is PlatformResult<ImmutableArray<DesktopMonitor>>.Success { Value: var first }
            && first.SequenceEqual(monitors), "the watch starts with the current monitors");
        // A resolution change is a monitor change; it alters the session's display, so only on a disposable runner.
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("RUNIC_TEST_DISPLAY_SCALING") == "1")
        {
            var resolution = WindowsDpiSmoke.CurrentResolution();
            var (width, height) = resolution.Width == 1024 ? (1280, 1024) : (1024, 768);
            try
            {
                Require(WindowsDpiSmoke.SetResolution(width, height) == 0, $"the display accepts {width}x{height}");
                Require(await watch.MoveNextAsync() && watch.Current is PlatformResult<ImmutableArray<DesktopMonitor>>.Success { Value: var changed }
                    && changed[0].Bounds.Width == width && changed[0].Bounds.Height == height,
                    $"the watch reports the primary monitor at {width}x{height}: {watch.Current}");
                Console.WriteLine($"Monitors: the watch reported the resolution change to {width}x{height}.");
            }
            finally
            {
                _ = WindowsDpiSmoke.SetResolution(resolution.Width, resolution.Height);
            }
            Require(await watch.MoveNextAsync(), "the watch reports the resolution restored");
        }
        Console.WriteLine("PASS monitors watch.");
    }

    private static void RequireInside(DesktopWindowState state, DesktopRect area, string what)
    {
        Require(state.X is { } x && state.Y is { } y && x >= area.X - 16 && y >= area.Y - 16
            && x < area.X + area.Width && y < area.Y + area.Height, $"{what}: {state} inside {area}");
    }

    private static bool Near(int actual, int expected) => Math.Abs((long)actual - expected) <= 2;

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Monitors: expected " + what + ".");
    }

    private static async Task RequireThrowsAsync<T>(Func<Task> action, string what) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Monitors: {what} did not throw {typeof(T).Name}.");
    }

    private static async Task<DesktopWindowState> WaitForAsync(DesktopWindow window, Func<DesktopWindowState, bool> condition, string expected)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < Deadline)
        {
            if (window.State is { } state && condition(state)) return state;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Monitors: the window did not report {expected}; last state: {window.State?.ToString() ?? "<none>"}.");
    }

    private static async Task<string?> ScriptAsync(DesktopSurface surface, string script) =>
        await surface.ExecuteJavaScriptAsync(script, TimeSpan.FromSeconds(15));

    private static partial class Windows
    {
        private const int GwlStyle = -16, GwlExStyle = -20;
        private const long WsCaption = 0x00C00000, WsExLayoutRtl = 0x00400000;

        internal static void RequireMatchesSystem(ImmutableArray<DesktopMonitor> monitors) =>
            Require(GetSystemMetrics(80) == monitors.Length, $"{GetSystemMetrics(80)} monitors (SM_CMONITORS), as listed");

        internal static void RequireCovers(nint window, DesktopRect bounds, bool captioned)
        {
            using var aware = new PerMonitorAware();
            Require(GetWindowRect(window, out var rect) != 0 && rect.Left == bounds.X && rect.Top == bounds.Y
                && rect.Right - rect.Left == bounds.Width && rect.Bottom - rect.Top == bounds.Height,
                $"the full-screen frame covers {bounds}, got {rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}");
            Require(((long)GetWindowLongPtr(window, GwlStyle) & WsCaption) == 0 == !captioned, "a full-screen window without its caption");
        }

        internal static void RequireCaptioned(nint window) =>
            Require(((long)GetWindowLongPtr(window, GwlStyle) & WsCaption) == WsCaption, "the caption back after full screen");

        // #389: a mirrored frame (WS_EX_LAYOUTRTL) whose WebView2 child keeps its layout, renders the page where it is
        // shown and hit-tests a click where the user sees the page's element.
        internal static async Task RightToLeftAsync(DesktopHost host)
        {
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded, Width = 520, Height = 360, Centered = true,
                LayoutDirection = DesktopLayoutDirection.RightToLeft, Language = "ar-EG",
            });
            var handle = window.NativeHandle;
            Require(((long)GetWindowLongPtr(handle, GwlExStyle) & WsExLayoutRtl) != 0, "WS_EX_LAYOUTRTL on the right-to-left window");
            var child = GetWindow(handle, 5); // GW_CHILD
            Require(child != 0, "the WebView2 child window");
            Require(((long)GetWindowLongPtr(child, GwlExStyle) & WsExLayoutRtl) == 0, "a WebView2 child without WS_EX_LAYOUTRTL");
            var language = await ScriptAsync(surface, "return navigator.language;");
            Require(language == "ar-EG", $"navigator.language ar-EG, got {language}");
            var accept = await ScriptAsync(surface, "return JSON.stringify(navigator.languages);");
            Console.WriteLine($"Right to left: navigator.languages {accept}.");

            await window.FocusAsync();
            await Task.Delay(500);
            var geometry = JsonDocument.Parse(await ScriptAsync(surface,
                "const r = document.getElementById('target').getBoundingClientRect(); return JSON.stringify({ x: r.left + r.width / 2, y: r.top + r.height / 2, ratio: devicePixelRatio });")
                ?? "{}").RootElement;
            double pageX = geometry.GetProperty("x").GetDouble(), pageY = geometry.GetProperty("y").GetDouble();
            double ratio = geometry.GetProperty("ratio").GetDouble();
            int screenX, screenY;
            uint pixel;
            using (new PerMonitorAware())
            {
                Require(GetWindowRect(child, out var view) != 0, "the WebView2 child's rectangle");
                screenX = view.Left + (int)Math.Round(pageX * ratio);
                screenY = view.Top + (int)Math.Round(pageY * ratio);
                var screen = GetDC(0);
                pixel = GetPixel(screen, screenX, screenY);
                _ = ReleaseDC(0, screen);
                Console.WriteLine($"Right to left: the button's center at {screenX},{screenY} in the WebView2 child at {view.Left},{view.Top} shows 0x{pixel:X6}.");
                // The red button where the page placed it: WebView2 did not render a mirrored page.
                Require((pixel & 0xFF) > 200 && ((pixel >> 8) & 0xFF) < 80 && ((pixel >> 16) & 0xFF) < 80,
                    $"the page's red button rendered at its own position, got 0x{pixel:X6}");
                Require(SetCursorPos(screenX, screenY) != 0, "the pointer on the button");
                mouse_event(0x0002, 0, 0, 0, 0); // MOUSEEVENTF_LEFTDOWN
                mouse_event(0x0004, 0, 0, 0, 0); // MOUSEEVENTF_LEFTUP
            }
            string? clicks = null;
            for (var waited = 0; waited < 5000 && clicks != "1"; waited += 100)
            {
                await Task.Delay(100);
                clicks = await ScriptAsync(surface, "return String(window.clicks);");
            }
            Require(clicks == "1", $"the click at the button's screen position reached it, got {clicks} clicks");
            Console.WriteLine("PASS right to left: mirrored frame, WebView2 renders and hit-tests the page unmirrored, Language reaches navigator.language.");
        }

        private readonly struct PerMonitorAware : IDisposable
        {
            private readonly nint _previous;
            public PerMonitorAware() => _previous = SetThreadDpiAwarenessContext(-4);
            public void Dispose() => _ = SetThreadDpiAwarenessContext(_previous);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static partial nint GetWindowLongPtr(nint window, int index);
        [LibraryImport("user32.dll")] private static partial nint GetWindow(nint window, uint command);
        [LibraryImport("user32.dll")] private static partial int GetWindowRect(nint window, out Rect rect);
        [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
        [LibraryImport("user32.dll")] private static partial nint SetThreadDpiAwarenessContext(nint context);
        [LibraryImport("user32.dll")] private static partial int SetCursorPos(int x, int y);
        [LibraryImport("user32.dll")] private static partial void mouse_event(uint flags, int dx, int dy, int data, nint extra);
        [LibraryImport("user32.dll")] private static partial nint GetDC(nint window);
        [LibraryImport("user32.dll")] private static partial int ReleaseDC(nint window, nint context);
        [LibraryImport("gdi32.dll")] private static partial uint GetPixel(nint context, int x, int y);
    }

    private static partial class MacOS
    {
        // The standard menus Runic completed carry the application's titles.
        internal static async Task CheckTitlesAsync(DesktopHost host)
        {
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
            string? failure = null;
            List<string> found = [];
            await window.InvokeNativeAsync(_ =>
            {
                try
                {
                    var mainMenu = Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("mainMenu"));
                    foreach (var item in Items(mainMenu))
                    {
                        var submenu = Send(item, Sel("submenu"));
                        found.Add(Text(Send(item, Sel("title"))) + ": " + string.Join(", ", Items(submenu).Select(static entry => Text(Send(entry, Sel("title"))))));
                    }
                }
                catch (Exception error) { failure = error.ToString(); }
            });
            if (failure is not null) throw new InvalidOperationException("Menu titles: " + failure);
            foreach (var line in found) Console.WriteLine($"Menu titles: {line}");
            string[] expected = ["Bearbeiten: Widerrufen, Wiederholen, , Ausschneiden, Kopieren, Einsetzen, Alles auswählen",
                "Ablage: Schließen", "Fenster: Im Dock ablegen, Zoomen, , Alle nach vorne bringen"];
            foreach (var menu in expected)
                Require(found.Contains(menu), $"the menu \"{menu}\"");
            Require(found.Any(static line => line.StartsWith("Darstellung: Originalgröße, Vergrößern", StringComparison.Ordinal)), "the Darstellung menu");
            Require(found.Any(static line => line.Contains("ausblenden, Andere ausblenden, Alle einblenden", StringComparison.Ordinal)
                && line.Contains(" beenden", StringComparison.Ordinal) && line.Contains("Über ", StringComparison.Ordinal)),
                "the application menu with the name in place of {0}");
            Console.WriteLine("PASS macOS menu titles: the standard menus took DesktopHostOptions.StandardMenuTitles.");
        }

        private static IEnumerable<nint> Items(nint menu)
        {
            if (menu == 0) yield break;
            for (nint index = 0, count = Send(menu, Sel("numberOfItems")); index < count; index++)
                yield return Arg(menu, Sel("itemAtIndex:"), index);
        }

        private static string Text(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"))) ?? "";

        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
        [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);
        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Arg(nint target, nint selector, nint value);
    }
}
