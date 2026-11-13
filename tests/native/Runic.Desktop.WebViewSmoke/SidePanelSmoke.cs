using System.Globalization;
using System.Runtime.InteropServices;
using Runic.Desktop;

// sdk#864, P2, --side-panel: the side panel on WebView2 (DesktopWindow.OpenSidePanelAsync, RUNICDESK104).
// - Open, close, move and resize on both edges: the window's bounds change by the panel's exact native width, the page
//   is never resized (its resize events stay at zero, its innerWidth is unchanged), closing restores the exact bounds,
//   the panel's page connects as a second connection of the window and disconnects when it closes.
// - Frame capture: each DWM frame (DwmFlush) one row of the screen across the page's left edge, which the page marks
//   with a magenta bar, while the panel opens and closes on the left edge and moves between edges. The page's edge may
//   be out of place in at most one frame (side-panel/DESIGN.md §4); the count is printed.
// - Tab from the page's last element focuses the panel's first, Shift+Tab hands focus back (SendInput; a desktop that
//   delivers no input to the window, as the arm64 runner's, leaves it unverified).
// - The placement excludes the panel; a maximized window keeps the panel and the page takes the rest; refusals
//   (no room, already open, maximized); the minimum width includes the panel; closing the window closes the panel.
// P3, on macOS (WKWebView) the same, except: no frame capture, but the page's rectangle on the screen read before and
// after each change (SidePanelSmoke.MacOS.cs); Tab posted to the application's event queue; the accessibility order of
// the views; the minimum content size; and closing the panel of a zoomed window, which shrinks it once it is restored.
// On Linux the panel is refused with Unsupported until its engine has one (P4).
internal static partial class SidePanelSmoke
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(600);
    private const int PanelWidth = 240;

    // One document: the page, or with #side-panel the panel's page. The panel reports its focus to the surface's
    // Resources handler, since ExecuteJavaScriptAsync reaches the page only.
    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic side panel</title>
        <style>html,body{margin:0;height:100%;background:#fff}#marker{position:fixed;left:0;top:0;bottom:0;width:8px;background:#f0f}
        main{padding:24px}</style>
        <script>
        window.__resizes = 0; addEventListener('resize', () => { window.__resizes++; });
        addEventListener('DOMContentLoaded', () => {
          if (location.hash !== '#side-panel') return;
          document.title = 'Side panel';
          document.body.innerHTML = '<aside role="complementary" aria-label="Side panel" style="padding:16px">' +
            '<button id="panel-first">Panel first</button> <button id="panel-last">Panel last</button></aside>';
          for (const button of document.querySelectorAll('button'))
            button.addEventListener('focus', () => fetch('report/focus-' + button.id));
          fetch('report/loaded');
        });
        </script></head>
        <body><div id="marker"></div><main><button id="first">First</button> <button id="last">Last</button></main></body></html>
        """;

    internal static int Run(DesktopHostOptions hostOptions) => DesktopEventLoop.Run(hostOptions, RunAsync);

    private static async Task<int> RunAsync(DesktopHost host)
    {
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Html(Page),
            Resources = (request, _) =>
            {
                if (!request.Path.StartsWith("/report/", StringComparison.Ordinal)) return ValueTask.FromResult<ContentResponse?>(null);
                reports.Enqueue(request.Path["/report/".Length..]);
                return ValueTask.FromResult<ContentResponse?>(ContentResponse.Text("ok", "text/plain"));
            },
        });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded, Width = 440, Height = 360, X = 280, Y = 120,
        });
        try
        {
            await surface.ExecuteJavaScriptAsync("return document.readyState;", Deadline);
            await WaitForAsync(() => window.State?.Bounds is not null, "the window's bounds");
            await Task.Delay(Settle);
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            {
                Require(window.CheckSidePanel(DesktopWindowEdge.End, PanelWidth) == DesktopSidePanelRefusal.Unsupported,
                    "Linux to refuse the side panel until P4");
                var refused = await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Width = PanelWidth, Path = "#side-panel" });
                Require(refused is DesktopSidePanelResult.Refused { Reason: DesktopSidePanelRefusal.Unsupported }, $"Unsupported, not {refused}");
                Console.WriteLine("PASS side panel: refused with Unsupported on this platform.");
                return 0;
            }

            if (OperatingSystem.IsMacOS()) await MacOS.FindPageAsync(window);
            var start = window.State!;
            var scale = start.Bounds!.Scale;
            var native = (int)Math.Round(PanelWidth * scale, MidpointRounding.AwayFromZero);
            var page = await ReadPageAsync(surface);
            Console.WriteLine($"Side panel: window {start.Bounds} at scale {scale}, page {page.Width} CSS px, work area check for {PanelWidth}.");

            await ExerciseRefusalsAsync(window);
            var right = await OpenAsync(window, surface, reports, DesktopWindowEdge.Right, start, native, page);
            Require(window.State!.Placement!.Width == start.Placement!.Width && window.State.Placement.X == start.Placement.X,
                $"the placement {window.State.Placement} to exclude the panel, as {start.Placement}");
            if (OperatingSystem.IsMacOS()) await MacOS.RequireReadingOrderAsync(window, "the right edge");
            await ExerciseFocusAsync(window, surface, reports);
            Require(await right.SetWidthAsync(PanelWidth + 20) is null, "the panel to widen");
            var wider = (int)Math.Round((PanelWidth + 20) * scale, MidpointRounding.AwayFromZero);
            await WaitForAsync(() => window.State!.Bounds!.Width == start.Bounds.Width + wider, $"the window {wider} px wider than at the start");
            Require(await right.SetWidthAsync(PanelWidth) is null, "the panel to narrow");
            await WaitForAsync(() => window.State!.Bounds!.Width == start.Bounds.Width + native, "the window back at the panel's width");
            await RequirePageAsync(surface, page, "after the width changes");
            await CloseAsync(window, surface, right, start);

            // The left edge with frame capture: the page's left edge stays where it is on the screen.
            var marker = OperatingSystem.IsWindows() ? FindMarker(window.State!.VisibleBounds!) : default;
            if (OperatingSystem.IsWindows()) Console.WriteLine($"Side panel: the page's left edge is at screen x {marker.X}, row {marker.Y}.");
            DesktopSidePanel? left = null;
            await CaptureAsync("open on the left edge", marker,
                async () => left = await OpenAsync(window, surface, reports, DesktopWindowEdge.Left, start, native, page));
            await CaptureAsync("move to the right edge", marker, async () =>
            {
                Require(await left!.MoveToEdgeAsync(DesktopWindowEdge.Right) is null, "the panel to move to the right edge");
                await WaitForAsync(() => window.State!.Bounds!.X == start.Bounds.X, "the window back at its start position");
            });
            await CaptureAsync("move to the left edge", marker, async () =>
            {
                Require(await left!.MoveToEdgeAsync(DesktopWindowEdge.Left) is null, "the panel to move to the left edge");
                await WaitForAsync(() => window.State!.Bounds!.X == start.Bounds.X - native, "the window moved left by the panel");
            });
            if (OperatingSystem.IsMacOS()) await MacOS.RequireReadingOrderAsync(window, "the left edge");
            await RequirePageAsync(surface, page, "after the moves");
            await CaptureAsync("close on the left edge", marker, () => CloseAsync(window, surface, left!, start));

            await ExerciseMaximizedAsync(window, surface, start);
            if (OperatingSystem.IsMacOS()) await MacOS.ExerciseZoomedCloseAsync(window, start, native);
            await ExerciseMinimumAndWindowCloseAsync(window, surface, start, native);
        }
        finally { if (window.IsOpen) await window.CloseAsync(); }
        Console.WriteLine("PASS side panel: both edges, exact bounds, page unchanged, frames, focus, refusals, maximize, minimum, window close.");
        return 0;
    }

    private static async Task<DesktopSidePanel> OpenAsync(DesktopWindow window, DesktopSurface surface,
        System.Collections.Concurrent.ConcurrentQueue<string> reports, DesktopWindowEdge edge, DesktopWindowState start, int native,
        (int Resizes, int Width, int Height) page)
    {
        reports.Clear();
        Require(window.CheckSidePanel(edge, PanelWidth) is null, $"the {edge} edge to have room: {window.CheckSidePanel(edge, PanelWidth)}");
        var result = await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Edge = edge, Width = PanelWidth, Path = "#side-panel" });
        var panel = (result as DesktopSidePanelResult.Opened)?.Panel ?? throw new InvalidOperationException($"Side panel: expected it to open on the {edge} edge, not {result}.");
        Require(panel.Edge == edge && panel.Width == PanelWidth && ReferenceEquals(window.SidePanel, panel), $"the panel on the {edge} edge");
        var expected = start.Bounds! with
        {
            X = edge == DesktopWindowEdge.Left ? start.Bounds.X - native : start.Bounds.X, Width = start.Bounds.Width + native,
        };
        await WaitForAsync(() => SameFrame(window.State!.Bounds!, expected), $"the bounds {expected}");
        await WaitForAsync(() => surface.ConnectionCount == 2, "the panel's page to connect beside the page");
        await WaitForAsync(() => reports.Contains("loaded"), "the panel's page to load from the surface");
        await Task.Delay(Settle);
        await RequirePageAsync(surface, page, $"after opening on the {edge} edge");
        Console.WriteLine($"Side panel: opened on the {edge} edge: {start.Bounds} → {window.State!.Bounds}, 2 connections, the page unchanged.");
        return panel;
    }

    private static async Task CloseAsync(DesktopWindow window, DesktopSurface surface, DesktopSidePanel panel, DesktopWindowState start)
    {
        var reason = new TaskCompletionSource<DesktopSidePanelCloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        panel.Closed += (_, args) => reason.TrySetResult(args.Reason);
        await panel.CloseAsync();
        Require(!panel.IsOpen && window.SidePanel is null, "the panel closed");
        Require(await reason.Task.WaitAsync(Deadline) == DesktopSidePanelCloseReason.Closed, "Closed as the close reason");
        await WaitForAsync(() => SameFrame(window.State!.Bounds!, start.Bounds!), $"exactly the start bounds {start.Bounds}");
        await WaitForAsync(() => surface.ConnectionCount == 1, "the panel's page to disconnect");
        Console.WriteLine($"Side panel: closed; the window is back at exactly {window.State!.Bounds}.");
    }

    private static async Task ExerciseRefusalsAsync(DesktopWindow window)
    {
        // More than half the work area.
        Require(window.CheckSidePanel(DesktopWindowEdge.Right, 4000) == DesktopSidePanelRefusal.NoRoom, "NoRoom for a panel wider than half the work area");
        var refused = await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Width = 4000, Path = "#side-panel" });
        Require(refused is DesktopSidePanelResult.Refused { Reason: DesktopSidePanelRefusal.NoRoom }, $"NoRoom, not {refused}");
        Require(window.SidePanel is null, "no panel after a refusal");
        Console.WriteLine("Side panel: a panel wider than half the work area was refused with NoRoom.");
    }

    // Tab past the page's last element focuses the panel's first; Shift+Tab from there hands focus back to the page's last.
    private static async Task ExerciseFocusAsync(DesktopWindow window, DesktopSurface surface, System.Collections.Concurrent.ConcurrentQueue<string> reports)
    {
        await window.FocusAsync();
        var before = (await surface.ExecuteJavaScriptAsync("document.getElementById('last').focus(); return document.activeElement.id;", Deadline)).Trim('"');
        Require(before == "last", $"the page's last element focused before Tab, not '{before}'");
        await Task.Delay(Settle);
        reports.Clear();
        await PressTabAsync(window, shift: false);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!reports.Contains("focus-panel-first") && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
        if (!reports.Contains("focus-panel-first"))
        {
            var focus = await surface.ExecuteJavaScriptAsync("return document.hasFocus() + ' ' + document.activeElement.id;", Deadline);
            var responder = OperatingSystem.IsMacOS() ? $"; the window's first responder is {await MacOS.DescribeFirstResponderAsync(window)}" : "";
            Require(focus.Trim('"') == "true last", $"Tab past the page's last element to focus the panel's first, but the page has '{focus}'{responder}");
            Console.WriteLine("Side panel: Tab unverified on this runner: its desktop does not deliver the key to the window.");
            return;
        }
        await PressTabAsync(window, shift: true);
        await WaitForAsync(async () => (await surface.ExecuteJavaScriptAsync(
            "return document.hasFocus() + ' ' + document.activeElement.id;", Deadline)).Trim('"') == "true last",
            "Shift+Tab from the panel's first element to focus the page's last");
        Console.WriteLine("Side panel: Tab moved from the page to the panel and Shift+Tab back.");
    }

    private static async Task ExerciseMaximizedAsync(DesktopWindow window, DesktopSurface surface, DesktopWindowState start)
    {
        var panel = await OpenQuietlyAsync(window);
        await window.ToggleMaximizedAsync();
        await WaitForAsync(() => window.State!.IsMaximized, "the window maximized");
        await Task.Delay(Settle);
        Require(panel.IsOpen, "the panel to stay open in a maximized window");
        var page = await ReadPageAsync(surface);
        var visible = window.State!.VisibleBounds!;
        var client = visible.Width / visible.Scale;
        Require(page.Width + PanelWidth <= client + 1 && page.Width + PanelWidth >= client - 24,
            $"the page ({page.Width}) to take the maximized window ({client:0}) beside the panel ({PanelWidth})");
        Require(await panel.SetWidthAsync(PanelWidth + 10) == DesktopSidePanelRefusal.Maximized, "a width change refused while maximized");
        Require(window.CheckSidePanel(DesktopWindowEdge.Left, PanelWidth) == DesktopSidePanelRefusal.AlreadyOpen, "AlreadyOpen while a panel is open");
        await window.ToggleMaximizedAsync();
        await WaitForAsync(() => !window.State!.IsMaximized, "the window restored");
        // AppKit animates the zoom; the panel closes once the window is back where it was.
        if (OperatingSystem.IsMacOS())
        {
            var opened = start.Bounds! with { Width = start.Bounds.Width + PanelWidth };
            await WaitForAsync(() => SameFrame(window.State!.Bounds!, opened), $"the window restored with the panel at {opened}");
        }
        await panel.CloseAsync();
        await WaitForAsync(() => SameFrame(window.State!.Bounds!, start.Bounds!), "the start bounds after maximize, restore and close");
        Require(window.CheckSidePanel(DesktopWindowEdge.Right, PanelWidth) is null, "a panel to fit again");
        await window.ToggleMaximizedAsync();
        await WaitForAsync(() => window.State!.IsMaximized, "the window maximized again");
        Require(window.CheckSidePanel(DesktopWindowEdge.Right, PanelWidth) == DesktopSidePanelRefusal.Maximized, "Maximized for a maximized window");
        var refused = await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Width = PanelWidth, Path = "#side-panel" });
        Require(refused is DesktopSidePanelResult.Refused { Reason: DesktopSidePanelRefusal.Maximized }, $"Maximized, not {refused}");
        await window.ToggleMaximizedAsync();
        await WaitForAsync(() => !window.State!.IsMaximized, "the window restored again");
        Console.WriteLine("Side panel: a maximized window kept the panel with the page beside it, and refused a new one.");
    }

    // The minimum width includes the panel; closing the window closes the panel with WindowClosed.
    private static async Task ExerciseMinimumAndWindowCloseAsync(DesktopWindow window, DesktopSurface surface, DesktopWindowState start, int native)
    {
        var minimum = OperatingSystem.IsMacOS() ? await MacOS.ContentMinimumWidthAsync(window) : 0;
        var panel = await OpenQuietlyAsync(window);
        if (OperatingSystem.IsMacOS())
        {
            // AppKit applies the content minimum to the user's resize only: the minimum itself is checked.
            var withPanel = await MacOS.ContentMinimumWidthAsync(window);
            Require(Math.Abs(withPanel - (minimum + PanelWidth)) < 0.5, $"a minimum content width of {minimum} + {PanelWidth} with the panel, not {withPanel}");
            Console.WriteLine($"Side panel: the minimum content width with the panel is {withPanel} pt ({minimum} pt without).");
        }
        else
        {
            await window.InvokeNativeAsync(static handle => Win32.SetWindowPos(handle, 0, 0, 0, 100, 150, 0x0002 | 0x0004 | 0x0010));
            await Task.Delay(Settle);
            var bounds = window.State!.Bounds!;
            Require(bounds.Width > native + 60, $"a window {bounds.Width} px wide, resized to 100 px from outside Runic, to stay wider than the panel ({native} px)");
            Console.WriteLine($"Side panel: the minimum width with the panel is {bounds.Width} px.");
        }

        var reason = new TaskCompletionSource<DesktopSidePanelCloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        panel.Closed += (_, args) => reason.TrySetResult(args.Reason);
        await window.CloseAsync();
        Require(await reason.Task.WaitAsync(Deadline) == DesktopSidePanelCloseReason.WindowClosed, "WindowClosed when the window closes");
        Require(!panel.IsOpen && window.SidePanel is null, "no panel after the window closed");
        try { await WaitForAsync(() => surface.ConnectionCount == 0, "both pages to disconnect"); }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"{error.Message} Still connected: {surface.DescribeConnectedSlots()}.", error);
        }
        Console.WriteLine("Side panel: closing the window closed the panel (WindowClosed).");
    }

    private static async Task<DesktopSidePanel> OpenQuietlyAsync(DesktopWindow window)
    {
        var result = await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Width = PanelWidth, Path = "#side-panel" });
        return (result as DesktopSidePanelResult.Opened)?.Panel ?? throw new InvalidOperationException($"Side panel: expected it to open, not {result}.");
    }

    // Samples the row through the page's left edge once per DWM frame while operation runs, and counts the frames in which
    // the edge was not at its place.
    private static async Task CaptureAsync(string what, (int X, int Y) marker, Func<Task> operation)
    {
        if (OperatingSystem.IsMacOS())
        {
            await MacOS.KeepsPageAsync(what, operation);
            return;
        }
        var frames = new List<int>();
        using var stop = new CancellationTokenSource();
        var sampler = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                _ = Win32.DwmFlush();
                frames.Add(Screen.MarkerIn(marker.X - 40, marker.Y, 80) is { } x ? marker.X - 40 + x : int.MinValue);
            }
        }) { IsBackground = true, Name = "Side panel frame capture" };
        sampler.Start();
        try
        {
            await operation();
            await Task.Delay(Settle);
        }
        finally
        {
            stop.Cancel();
            sampler.Join();
        }
        var deviating = frames.Count(x => x != marker.X);
        var detail = string.Join(", ", frames.Where(x => x != marker.X).Take(8).Select(x => x == int.MinValue ? "none" : x.ToString(CultureInfo.InvariantCulture)));
        Console.WriteLine($"Side panel: frame capture, {what}: {frames.Count} frames, {deviating} with the page's edge out of place{(deviating == 0 ? "" : $" ({detail})")}.");
        Require(frames.Count >= 5, $"frames captured during '{what}'");
        Require(deviating <= 1, $"at most one frame with the page's left edge out of place during '{what}', not {deviating}");
    }

    // The page's magenta marker in the row through the middle of the window's visible frame.
    private static (int X, int Y) FindMarker(DesktopWindowBounds visible)
    {
        var y = visible.Y!.Value + visible.Height / 2;
        var x = Screen.MarkerIn(visible.X!.Value, y, visible.Width)
            ?? throw new InvalidOperationException($"Side panel: the page's marker is not on the screen in the row {y} of {visible}; is the window covered?");
        return (visible.X.Value + x, y);
    }

    private static async Task RequirePageAsync(DesktopSurface surface, (int Resizes, int Width, int Height) expected, string when)
    {
        var page = await ReadPageAsync(surface);
        Require(page == expected, $"the page unchanged {when}: {expected} (resizes, width, height), not {page}");
    }

    private static async Task<(int Resizes, int Width, int Height)> ReadPageAsync(DesktopSurface surface)
    {
        var values = (await surface.ExecuteJavaScriptAsync("return window.__resizes + ' ' + innerWidth + ' ' + innerHeight;", Deadline))
            .Trim('"').Split(' ');
        return (int.Parse(values[0], CultureInfo.InvariantCulture), int.Parse(values[1], CultureInfo.InvariantCulture),
            int.Parse(values[2], CultureInfo.InvariantCulture));
    }

    private static bool SameFrame(DesktopWindowBounds actual, DesktopWindowBounds expected) =>
        actual.X == expected.X && actual.Y == expected.Y && actual.Width == expected.Width && actual.Height == expected.Height;

    private static Task WaitForAsync(Func<bool> condition, string expected) => WaitForAsync(() => Task.FromResult(condition()), expected);

    private static async Task WaitForAsync(Func<Task<bool>> condition, string expected)
    {
        var wait = TestTimeouts.Start(Deadline);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < wait.Limit)
        {
            if (await condition())
            {
                wait.Succeeded();
                return;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException($"Side panel: expected {expected}.");
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Side panel: expected " + what + ".");
    }

    // SendInput on Windows; on macOS a key event posted to the application's queue, as the window server delivers one.
    private static ValueTask PressTabAsync(DesktopWindow window, bool shift)
    {
        if (OperatingSystem.IsMacOS()) return MacOS.PressTabAsync(window, shift);
        Keyboard.Press(Keyboard.Tab, shift);
        return ValueTask.CompletedTask;
    }

    private static class Keyboard
    {
        internal const ushort Tab = 0x09;
        private const ushort Shift = 0x10;

        internal static void Press(ushort key, bool shift = false)
        {
            if (shift) Send(Shift, up: false);
            Send(key, up: false);
            Send(key, up: true);
            if (shift) Send(Shift, up: true);
        }

        private static unsafe void Send(ushort key, bool up)
        {
            var input = new KeyboardInput { Type = 1, Key = key, Flags = up ? 0x0002u : 0 };
            _ = Win32.SendInput(1, &input, sizeof(KeyboardInput));
        }

        // INPUT with a KEYBDINPUT, as laid out on 64-bit Windows: the union starts at 8.
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct KeyboardInput
        {
            [FieldOffset(0)] internal uint Type;
            [FieldOffset(8)] internal ushort Key;
            [FieldOffset(10)] internal ushort Scan;
            [FieldOffset(12)] internal uint Flags;
        }
    }

    // One row of the composed screen through GDI, which reads what DWM presented.
    private static unsafe class Screen
    {
        // The offset of the first magenta pixel in the row of width pixels from (x, y), or null.
        internal static int? MarkerIn(int x, int y, int width)
        {
            var screen = Win32.GetDC(0);
            var memory = Win32.CreateCompatibleDC(screen);
            var bitmap = Win32.CreateCompatibleBitmap(screen, width, 1);
            var previous = Win32.SelectObject(memory, bitmap);
            try
            {
                // SRCCOPY | CAPTUREBLT.
                _ = Win32.BitBlt(memory, 0, 0, width, 1, screen, x, y, 0x00CC0020 | 0x40000000);
                var pixels = new uint[width];
                var header = new BitmapInfoHeader { Size = (uint)sizeof(BitmapInfoHeader), Width = width, Height = -1, Planes = 1, BitCount = 32 };
                fixed (uint* data = pixels)
                {
                    _ = Win32.SelectObject(memory, previous);
                    _ = Win32.GetDIBits(memory, bitmap, 0, 1, data, &header, 0);
                }
                for (var i = 0; i < pixels.Length; i++)
                {
                    var (red, green, blue) = ((pixels[i] >> 16) & 0xFF, (pixels[i] >> 8) & 0xFF, pixels[i] & 0xFF);
                    if (red > 200 && green < 80 && blue > 200) return i;
                }
                return null;
            }
            finally
            {
                _ = Win32.DeleteObject(bitmap);
                _ = Win32.DeleteDC(memory);
                _ = Win32.ReleaseDC(0, screen);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfoHeader
        {
            internal uint Size;
            internal int Width;
            internal int Height;
            internal ushort Planes;
            internal ushort BitCount;
            internal uint Compression;
            internal uint SizeImage;
            internal int XPelsPerMeter;
            internal int YPelsPerMeter;
            internal uint ClrUsed;
            internal uint ClrImportant;
            // The colour table of BITMAPINFO, unused for 32 bits per pixel.
            internal uint Colors;
        }
    }

    private static unsafe class Win32
    {
        private static readonly nint User32 = NativeLibrary.Load("user32.dll");
        private static readonly nint Gdi32 = NativeLibrary.Load("gdi32.dll");
        private static readonly nint Dwmapi = NativeLibrary.Load("dwmapi.dll");

        internal static void SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags) =>
            _ = ((delegate* unmanaged[Stdcall]<nint, nint, int, int, int, int, uint, int>)NativeLibrary.GetExport(User32, "SetWindowPos"))(
                window, after, x, y, width, height, flags);

        internal static uint SendInput(uint count, void* inputs, int size) =>
            ((delegate* unmanaged[Stdcall]<uint, void*, int, uint>)NativeLibrary.GetExport(User32, "SendInput"))(count, inputs, size);

        internal static int DwmFlush() => ((delegate* unmanaged[Stdcall]<int>)NativeLibrary.GetExport(Dwmapi, "DwmFlush"))();

        internal static nint GetDC(nint window) => ((delegate* unmanaged[Stdcall]<nint, nint>)NativeLibrary.GetExport(User32, "GetDC"))(window);

        internal static int ReleaseDC(nint window, nint dc) =>
            ((delegate* unmanaged[Stdcall]<nint, nint, int>)NativeLibrary.GetExport(User32, "ReleaseDC"))(window, dc);

        internal static nint CreateCompatibleDC(nint dc) => ((delegate* unmanaged[Stdcall]<nint, nint>)NativeLibrary.GetExport(Gdi32, "CreateCompatibleDC"))(dc);

        internal static nint CreateCompatibleBitmap(nint dc, int width, int height) =>
            ((delegate* unmanaged[Stdcall]<nint, int, int, nint>)NativeLibrary.GetExport(Gdi32, "CreateCompatibleBitmap"))(dc, width, height);

        internal static nint SelectObject(nint dc, nint value) =>
            ((delegate* unmanaged[Stdcall]<nint, nint, nint>)NativeLibrary.GetExport(Gdi32, "SelectObject"))(dc, value);

        internal static int BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation) =>
            ((delegate* unmanaged[Stdcall]<nint, int, int, int, int, nint, int, int, uint, int>)NativeLibrary.GetExport(Gdi32, "BitBlt"))(
                target, x, y, width, height, source, sourceX, sourceY, operation);

        internal static int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, Screen.BitmapInfoHeader* info, uint usage) =>
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, void*, Screen.BitmapInfoHeader*, uint, int>)NativeLibrary.GetExport(Gdi32, "GetDIBits"))(
                dc, bitmap, start, lines, bits, info, usage);

        internal static int DeleteObject(nint value) => ((delegate* unmanaged[Stdcall]<nint, int>)NativeLibrary.GetExport(Gdi32, "DeleteObject"))(value);

        internal static int DeleteDC(nint dc) => ((delegate* unmanaged[Stdcall]<nint, int>)NativeLibrary.GetExport(Gdi32, "DeleteDC"))(dc);
    }
}
