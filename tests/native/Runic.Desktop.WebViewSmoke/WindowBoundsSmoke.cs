using System.Runtime.InteropServices;
using Runic.Desktop;

// #864 B1, --window-bounds: the window building blocks in a real native window. Linked into Runic.Desktop.Gtk4.Smoke
// for GTK 4.
// - Exact bounds: DesktopWindowState.Bounds round-trips, and SetBoundsAsync grows the window on its left edge (moving it
//   and resizing it) in one step: the page sees one resize, by the expected width, and shrinking back by the same
//   length restores the exact bounds.
// - Visible bounds: Windows reports the frame without its invisible resize borders, inside the window rectangle; macOS
//   the frame itself; X11 the window manager's frame at the window's position. A second window placed with them
//   touches the first one, without a gap.
// - Run-time minimum size: SetMinimumSizeAsync grows a smaller window, and a resize from outside Runic, as the user's
//   would be, stays at the minimum: SetWindowPos on Windows (WM_GETMINMAXINFO), the window manager's size hints on X11
//   (and, with --window-manager, an XResizeWindow from another client), contentMinSize on macOS. Removing it lets the
//   window shrink again.
// - Refusals and state: a normal window is not snapped, and a maximized one refuses SetBoundsAsync.
internal static class WindowBoundsSmoke
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(800);
    private const int Growth = 120;

    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic window bounds</title>
        <script>window.__resizes = 0; addEventListener('resize', () => { window.__resizes++; });</script>
        </head><body style="margin:0">bounds</body></html>
        """;

    internal static int Run(DesktopHostOptions hostOptions, bool windowManager, bool gtk4 = false) =>
        DesktopEventLoop.Run(hostOptions, host => RunAsync(host, windowManager, gtk4));

    private static async Task<int> RunAsync(DesktopHost host, bool windowManager, bool gtk4)
    {
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded, Width = 520, Height = 380, X = 260, Y = 140,
        });
        try
        {
            Require(window.Capabilities.HasFlag(DesktopWindowCapabilities.Bounds | DesktopWindowCapabilities.MinimumSize),
                $"the Bounds and MinimumSize capabilities, not {window.Capabilities}");
            await surface.ExecuteJavaScriptAsync("return document.readyState;", Deadline);
            await WaitForAsync(window, static state => state.Bounds is not null, "its bounds");
            await Task.Delay(Settle);
            var opened = window.State!;
            Console.WriteLine($"Window bounds: opened with {opened.Bounds}, visible {opened.VisibleBounds?.ToString() ?? "<none>"}.");
            Require(!opened.IsSnapped, $"a window of its own size not to be snapped: {opened}");

            CheckVisibleBounds(opened);
            await ExerciseOneStepAsync(surface, window, opened.Bounds!);
            if (opened.VisibleBounds is not null && opened.Bounds!.X is not null) await ExerciseAdjacentAsync(host, window);
            await ExerciseMinimumSizeAsync(window, windowManager, gtk4);
            await ExerciseMaximizedRefusalAsync(window, windowManager);
        }
        finally { if (window.IsOpen) await window.CloseAsync(); }
        Console.WriteLine("PASS window bounds: exact bounds in one step, visible frame, run-time minimum size, refusals.");
        return 0;
    }

    private static void CheckVisibleBounds(DesktopWindowState state)
    {
        var bounds = state.Bounds!;
        if (state.VisibleBounds is not { } visible)
        {
            Require(bounds.X is null, $"visible bounds where the platform reports a position: {state}");
            Console.WriteLine("Window bounds: no visible bounds (Wayland).");
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            // DWMWA_EXTENDED_FRAME_BOUNDS: inside the window rectangle, without the invisible borders left, right and below.
            Require(visible.X > bounds.X && visible.X + visible.Width < bounds.X + bounds.Width
                && visible.Y >= bounds.Y && visible.Y + visible.Height < bounds.Y + bounds.Height,
                $"the visible frame {visible} inside the window rectangle {bounds}, without the invisible borders");
            Require(bounds.Scale > 0 && visible.Scale == bounds.Scale, $"the window's scale in both bounds: {bounds.Scale}, {visible.Scale}");
        }
        else if (OperatingSystem.IsMacOS())
        {
            Require(visible == bounds, $"the visible frame {visible} to be the frame {bounds}");
        }
        else
        {
            // The window manager's frame at the frame's position, its decorations added to the content size.
            Require(visible.X == bounds.X && visible.Y == bounds.Y && visible.Width >= bounds.Width && visible.Height >= bounds.Height,
                $"the visible frame {visible} at the frame position of {bounds}, around its content");
        }
        Console.WriteLine($"Window bounds: visible frame {visible} next to the window rectangle {bounds}.");
    }

    // SetBoundsAsync moves the left edge and resizes in one step: the page is resized once, and the round trip is exact.
    private static async Task ExerciseOneStepAsync(DesktopSurface surface, DesktopWindow window, DesktopWindowBounds start)
    {
        var edge = start.X is null ? DesktopWindowEdge.Right : DesktopWindowEdge.Left;
        var before = await ReadPageAsync(surface);
        var grown = start.Expand(edge, Growth);
        await window.SetBoundsAsync(grown);
        var reached = await WaitForAsync(window, state => SameFrame(state.Bounds, grown), $"the grown bounds {grown}");
        if (edge == DesktopWindowEdge.Left)
            Require(reached.Bounds!.X + reached.Bounds.Width == start.X + start.Width, $"the right edge to stay: {start} → {reached.Bounds}");
        await Task.Delay(Settle);
        var after = await ReadPageAsync(surface);
        Require(after.Resizes - before.Resizes == 1, $"one page resize for one SetBoundsAsync, not {after.Resizes - before.Resizes}");
        // About Growth CSS pixels: a page zoom, such as WebKitGTK's for the screen's DPI, scales them.
        Require(Math.Abs(after.Width - before.Width - Growth) <= Growth / 10, $"the page about {Growth} CSS pixels wider: {before.Width} → {after.Width}");
        Require(after.Height == before.Height, $"the page's height to stay: {before.Height} → {after.Height}");
        Console.WriteLine($"Window bounds: {edge} edge grown in one step: {start} → {reached.Bounds}, page {before.Width} → {after.Width}, one resize.");

        await window.SetBoundsAsync(window.State!.Bounds!.Expand(edge, -Growth));
        var restored = await WaitForAsync(window, state => SameFrame(state.Bounds, start), $"the exact start bounds {start}");
        await Task.Delay(Settle);
        var back = await ReadPageAsync(surface);
        Require(back.Resizes - after.Resizes == 1, $"one page resize for shrinking back, not {back.Resizes - after.Resizes}");
        Require(back.Width == before.Width, $"the page's width back to {before.Width}, not {back.Width}");
        Console.WriteLine($"Window bounds: shrunk back to exactly {restored.Bounds}.");
    }

    // A second window placed with the visible bounds touches the first one, without the gap of invisible borders.
    private static async Task ExerciseAdjacentAsync(DesktopHost host, DesktopWindow first)
    {
        var anchor = first.State!.VisibleBounds!;
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        await using var second = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded, Width = 300, Height = 380, X = 40, Y = 40,
        });
        try
        {
            await surface.ExecuteJavaScriptAsync("return document.readyState;", Deadline);
            await WaitForAsync(second, static state => state.VisibleBounds is not null, "the second window's visible bounds");
            await Task.Delay(Settle);
            var state = second.State!;
            var (bounds, visible) = (state.Bounds!, state.VisibleBounds!);
            // The visible left edge at the first window's visible right edge, the tops aligned.
            var target = bounds with
            {
                X = anchor.X + anchor.Width + (bounds.X - visible.X),
                Y = anchor.Y + (bounds.Y - visible.Y),
            };
            await second.SetBoundsAsync(target);
            var placed = await WaitForAsync(second, state => state.VisibleBounds is { } placed && placed.X == anchor.X + anchor.Width,
                $"the second window's visible left edge at {anchor.X + anchor.Width}");
            Require(placed.VisibleBounds!.Y == anchor.Y, $"the visible tops aligned: {anchor} and {placed.VisibleBounds}");
            Console.WriteLine($"Window bounds: a second window placed flush: {anchor} | {placed.VisibleBounds}.");
        }
        finally { if (second.IsOpen) await second.CloseAsync(); }
    }

    private static async Task ExerciseMinimumSizeAsync(DesktopWindow window, bool windowManager, bool gtk4)
    {
        var current = window.State!;
        var (minimumWidth, minimumHeight) = (current.Width + 60, current.Height + 40);
        await window.SetMinimumSizeAsync(minimumWidth, minimumHeight);
        await WaitForAsync(window, state => state.Width >= minimumWidth - 1 && state.Height >= minimumHeight - 1,
            $"the window grown to the minimum {minimumWidth}x{minimumHeight}");
        Console.WriteLine($"Window bounds: a run-time minimum {minimumWidth}x{minimumHeight} grew the window to {window.State}.");

        if (OperatingSystem.IsWindows())
        {
            // As a drag of the frame would: SetWindowPos goes through WM_WINDOWPOSCHANGING, which asks WM_GETMINMAXINFO.
            await window.InvokeNativeAsync(static handle => WindowsNative.SetWindowPos(handle, 0, 0, 0, 200, 150, 0x0002 | 0x0004 | 0x0010));
            await Task.Delay(Settle);
            var bounds = window.State!.Bounds!;
            var physical = (int)Math.Round(minimumWidth * bounds.Scale);
            Require(bounds.Width >= physical - 1, $"a resize to 200x150 from outside Runic to stay at the minimum {physical} px wide: {bounds}");
        }
        else if (OperatingSystem.IsMacOS())
        {
            CGSize minimum = default;
            await window.InvokeNativeAsync(handle => minimum = MacNative.ContentMinSize(handle));
            Require(minimum.Width == minimumWidth && minimum.Height == minimumHeight,
                $"contentMinSize {minimumWidth}x{minimumHeight}, not {minimum.Width}x{minimum.Height}");
        }
        else if (window.State!.Bounds!.X is not null)
        {
            nuint xid = 0;
            await window.InvokeNativeAsync(handle => xid = X11Native.Xid(handle, gtk4));
            var hints = X11Native.MinimumSizeHint(xid);
            Require(hints is { } hint && hint.Width >= minimumWidth && hint.Height >= minimumHeight,
                $"WM_NORMAL_HINTS with a minimum of at least {minimumWidth}x{minimumHeight}, not {hints?.ToString() ?? "none"}");
            if (windowManager)
            {
                // As a drag of the frame would, through the window manager, which applies the hints.
                X11Native.Resize(xid, 200, 150);
                await Task.Delay(Settle * 2);
                Require(window.State!.Width >= minimumWidth && window.State.Height >= minimumHeight,
                    $"a resize to 200x150 from another X client to stay at the minimum: {window.State}");
            }
        }

        // Without the minimum the window can shrink again.
        await window.SetMinimumSizeAsync(0, 0);
        var (smallWidth, smallHeight) = (Math.Max(200, minimumWidth - 160), Math.Max(150, minimumHeight - 120));
        await window.ResizeAsync(smallWidth, smallHeight);
        await WaitForAsync(window, state => Math.Abs(state.Width - smallWidth) <= 2 && Math.Abs(state.Height - smallHeight) <= 2,
            $"the size {smallWidth}x{smallHeight} after removing the minimum");
        Console.WriteLine("Window bounds: the minimum held against a resize from outside Runic, and removing it let the window shrink.");
    }

    private static async Task ExerciseMaximizedRefusalAsync(DesktopWindow window, bool windowManager)
    {
        await window.ToggleMaximizedAsync();
        // Without a window manager, as under Xvfb, X11 has nobody to maximize the window.
        if (await TryWaitForAsync(window, static state => state.IsMaximized,
                OperatingSystem.IsLinux() && !windowManager ? TimeSpan.FromSeconds(3) : Deadline) is null)
        {
            Require(OperatingSystem.IsLinux() && !windowManager, $"the window to maximize: {window.State}");
            Console.WriteLine("Window bounds: no window manager maximized the window; the refusal check is skipped.");
            return;
        }
        Require(!window.State!.IsSnapped, "a maximized window not to count as snapped");
        var bounds = window.State.Bounds! with { Width = 400, Height = 300 };
        try
        {
            await window.SetBoundsAsync(bounds);
            throw new InvalidOperationException("Window bounds: expected SetBoundsAsync to refuse a maximized window.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("maximized", StringComparison.Ordinal)
            && !error.Message.StartsWith("Window bounds:", StringComparison.Ordinal))
        {
            Console.WriteLine($"Window bounds: a maximized window refused SetBoundsAsync: {error.Message}");
        }
        await window.ToggleMaximizedAsync();
        await WaitForAsync(window, static state => !state.IsMaximized, "the restored window");
    }

    private static bool SameFrame(DesktopWindowBounds? actual, DesktopWindowBounds expected) =>
        actual is { } bounds && bounds.X == expected.X && bounds.Y == expected.Y && bounds.Width == expected.Width && bounds.Height == expected.Height;

    private static async Task<(int Resizes, int Width, int Height)> ReadPageAsync(DesktopSurface surface)
    {
        var values = (await surface.ExecuteJavaScriptAsync("return window.__resizes + ' ' + innerWidth + ' ' + innerHeight;", Deadline))
            .Trim('"').Split(' ');
        return (int.Parse(values[0], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(values[1], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(values[2], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<DesktopWindowState> WaitForAsync(DesktopWindow window, Func<DesktopWindowState, bool> condition, string expected)
    {
        var wait = TestTimeouts.Start(Deadline);
        var state = await TryWaitForAsync(window, condition, wait.Limit)
            ?? throw new TimeoutException($"Window bounds: the window did not report {expected}; last state: {window.State?.ToString() ?? "<none>"}.");
        wait.Succeeded();
        return state;
    }

    private static async Task<DesktopWindowState?> TryWaitForAsync(DesktopWindow window, Func<DesktopWindowState, bool> condition, TimeSpan timeout)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (window.State is { } state && condition(state)) return state;
            await Task.Delay(20);
        }
        return null;
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Window bounds: expected " + what + ".");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        internal double Width;
        internal double Height;
    }

    private static class WindowsNative
    {
        internal static unsafe void SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags)
        {
            var user32 = NativeLibrary.Load("user32.dll");
            _ = ((delegate* unmanaged[Stdcall]<nint, nint, int, int, int, int, uint, int>)NativeLibrary.GetExport(user32, "SetWindowPos"))(
                window, after, x, y, width, height, flags);
        }
    }

    private static class MacNative
    {
        // contentMinSize returns an NSSize, two doubles, in registers on arm64 and x64.
        internal static unsafe CGSize ContentMinSize(nint window)
        {
            var objc = NativeLibrary.Load("/usr/lib/libobjc.A.dylib");
            var register = (delegate* unmanaged[Cdecl]<byte*, nint>)NativeLibrary.GetExport(objc, "sel_registerName");
            var send = (delegate* unmanaged[Cdecl]<nint, nint, CGSize>)NativeLibrary.GetExport(objc, "objc_msgSend");
            fixed (byte* name = "contentMinSize\0"u8) return send(window, register(name));
        }
    }

    // The X window of a GTK window and its WM_NORMAL_HINTS, read and resized through a connection of our own.
    internal static unsafe class X11Native
    {
        private static nint X11 => NativeLibrary.TryLoad(Environment.GetEnvironmentVariable("RUNIC_LIBX11") ?? "libX11.so.6", out var x11)
            ? x11 : NativeLibrary.Load("libX11.so.6");

        // GTK thread.
        internal static nuint Xid(nint gtkWindow, bool gtk4)
        {
            if (gtk4)
            {
                var gtk = NativeLibrary.Load("libgtk-4.so.1");
                var surface = ((delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(gtk, "gtk_native_get_surface"))(gtkWindow);
                return ((delegate* unmanaged[Cdecl]<nint, nuint>)NativeLibrary.GetExport(gtk, "gdk_x11_surface_get_xid"))(surface);
            }
            var gtk3 = NativeLibrary.Load("libgtk-3.so.0");
            var gdk3 = NativeLibrary.Load("libgdk-3.so.0");
            var window = ((delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(gtk3, "gtk_widget_get_window"))(gtkWindow);
            return ((delegate* unmanaged[Cdecl]<nint, nuint>)NativeLibrary.GetExport(gdk3, "gdk_x11_window_get_xid"))(window);
        }

        internal static (int Width, int Height)? MinimumSizeHint(nuint xid)
        {
            var x11 = X11;
            var display = ((delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(x11, "XOpenDisplay"))(0);
            if (display == 0) return null;
            try
            {
                SizeHints hints = default;
                nint supplied;
                var read = ((delegate* unmanaged[Cdecl]<nint, nuint, SizeHints*, nint*, int>)NativeLibrary.GetExport(x11, "XGetWMNormalHints"))(
                    display, xid, &hints, &supplied);
                // PMinSize.
                return read != 0 && (hints.Flags & (1 << 4)) != 0 ? (hints.MinWidth, hints.MinHeight) : null;
            }
            finally { _ = ((delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(x11, "XCloseDisplay"))(display); }
        }

        internal static void Resize(nuint xid, uint width, uint height)
        {
            var x11 = X11;
            var display = ((delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(x11, "XOpenDisplay"))(0);
            if (display == 0) throw new InvalidOperationException("Window bounds: cannot open the X display.");
            try
            {
                _ = ((delegate* unmanaged[Cdecl]<nint, nuint, uint, uint, int>)NativeLibrary.GetExport(x11, "XResizeWindow"))(display, xid, width, height);
                _ = ((delegate* unmanaged[Cdecl]<nint, int, int>)NativeLibrary.GetExport(x11, "XSync"))(display, 0);
            }
            finally { _ = ((delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(x11, "XCloseDisplay"))(display); }
        }

        // Xutil.h's XSizeHints: a C long of flags, then ints.
        [StructLayout(LayoutKind.Sequential)]
        private struct SizeHints
        {
            public nint Flags;
            public int X, Y, Width, Height;
            public int MinWidth, MinHeight, MaxWidth, MaxHeight;
            public int WidthIncrement, HeightIncrement;
            public int MinAspectX, MinAspectY, MaxAspectX, MaxAspectY;
            public int BaseWidth, BaseHeight;
            public int WinGravity;
        }
    }
}
