using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Desktop;
using Runic.Desktop.Gtk4;

// --placement [--window-manager] (#663) under X11: a restored DesktopWindowPlacement opens the GTK 4 window at its saved
// position, which the window manager's own placement of a new window does not replace, and the window then reports the
// same position, so saving and restoring it again does not move the window. A hidden window takes it when first shown,
// and a maximized placement restores to its saved bounds. --window-manager waits for an EWMH window manager and requires
// the maximized checks, which are skipped without one.
[SupportedOSPlatform("linux")]
internal static partial class PlacementSmoke
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    // Long enough for a window manager to have placed and decorated a new window.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);

    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic placement</title></head>
        <body>placement</body></html>
        """;

    internal static int Run(bool windowManager) =>
        DesktopEventLoop.Run(new DesktopHostOptions().WithGtk4() with { WaitForConnection = true }, host => RunAsync(host, windowManager));

    private static async Task<int> RunAsync(DesktopHost host, bool windowManager)
    {
        // Once GTK runs, which loaded Xlib.
        if (windowManager) WaitForWindowManager();
        DesktopWindowPlacement saved;
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        {
            var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded,
                Placement = new DesktopWindowPlacement { X = 150, Y = 110, Width = 420, Height = 300 },
            });
            if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.Move))
                throw new InvalidOperationException($"Placement: the GTK 4 window does not position windows (X11?): {window.Capabilities}.");
            await HoldsAsync(window, static state => state is { X: 150, Y: 110, Placement: { X: 150, Y: 110, Width: 420, Height: 300 } },
                "the restored position 150,110");
            await window.CloseAsync();
            saved = window.State?.Placement ?? throw new InvalidOperationException("Placement: the closed window kept no state.");
        }

        // The placement the window reported opens it at the same place again.
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        {
            var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Placement = saved });
            await HoldsAsync(window, static state => state is { X: 150, Y: 110, Placement: { X: 150, Y: 110 } },
                "the position 150,110 from the saved placement");
            await window.CloseAsync();
        }

        // A hidden window takes the restored position when it is first shown.
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        {
            var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded,
                Hidden = true,
                Placement = new DesktopWindowPlacement { X = 210, Y = 160, Width = 380, Height = 260 },
            });
            await Task.Delay(200);
            await window.ShowAsync();
            await HoldsAsync(window, static state => state is { X: 210, Y: 160 }, "the restored position 210,160 of a window shown later");
            await window.CloseAsync();
        }

        // A maximized placement opens maximized and restores to its saved bounds; maximizing needs a window manager.
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        {
            var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded,
                Placement = new DesktopWindowPlacement { X = 170, Y = 130, Width = 440, Height = 320, Maximized = true },
            });
            // Without a window manager GDK reports the requested maximized state but keeps the size.
            if (await TryWaitForAsync(window, static state => state is { IsMaximized: true, Width: > 440 }, windowManager ? Deadline : TimeSpan.FromSeconds(3))
                is not { } maximized)
            {
                Require(!windowManager, $"the window manager to maximize the window; it reported {window.State}");
                Console.WriteLine("Placement: no window manager maximized the window; maximized checks skipped.");
            }
            else
            {
                Console.WriteLine($"Placement: opened maximized: {maximized}.");
                Require(maximized.Placement is { X: 170, Y: 130, Width: 440, Height: 320, Maximized: true },
                    $"the saved bounds in the maximized window's placement, not {maximized.Placement}");
                await window.ToggleMaximizedAsync();
                await HoldsAsync(window, static state => state is { IsMaximized: false, X: 170, Y: 130, Width: 440, Height: 320 },
                    "the saved bounds 170,130 440x320 after leaving maximized");
            }
            await window.CloseAsync();
        }
        Console.WriteLine($"PASS GTK 4 X11 placement{(windowManager ? " under a window manager" : "")}: restored position, its round trip, deferred show and maximized restore.");
        return 0;
    }

    // Waits for the state, then requires it to last, so a late placement by the window manager fails the check.
    private static async Task HoldsAsync(DesktopWindow window, Func<DesktopWindowState, bool> condition, string expected)
    {
        var reached = await TryWaitForAsync(window, condition, Deadline)
            ?? throw new TimeoutException($"Placement: the window did not report {expected}; last state: {window.State?.ToString() ?? "<none>"}.");
        await Task.Delay(Settle);
        Require(window.State is { } state && condition(state), $"{expected} to last; it reported {reached}, then {window.State}");
        Console.WriteLine($"Placement: {expected}: {window.State}.");
    }

    private static async Task<DesktopWindowState?> TryWaitForAsync(DesktopWindow window, Func<DesktopWindowState, bool> condition, TimeSpan timeout)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (window.State is { } state && condition(state)) return state;
            await Task.Delay(50);
        }
        return null;
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Placement: expected " + what + ".");
    }

    // An EWMH window manager announces itself with _NET_SUPPORTING_WM_CHECK on the root window once it manages the screen.
    private static unsafe void WaitForWindowManager()
    {
        var display = XOpenDisplay(0);
        if (display == 0) throw new InvalidOperationException("Placement: cannot open the X display.");
        try
        {
            var atom = XInternAtom(display, "_NET_SUPPORTING_WM_CHECK", 0);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (elapsed.Elapsed < Deadline)
            {
                byte* data = null;
                if (XGetWindowProperty(display, XDefaultRootWindow(display), atom, 0, 1, 0, XaWindow,
                        out _, out var format, out var count, out _, &data) == 0 && data is not null)
                {
                    var found = format == 32 && count == 1 && *(nuint*)data != 0;
                    _ = XFree((nint)data);
                    if (found)
                    {
                        Console.WriteLine($"Placement: a window manager runs after {elapsed.ElapsedMilliseconds} ms.");
                        return;
                    }
                }
                Thread.Sleep(50);
            }
            throw new TimeoutException("Placement: no window manager set _NET_SUPPORTING_WM_CHECK.");
        }
        finally
        {
            _ = XCloseDisplay(display);
        }
    }

    private const nuint XaWindow = 33;

    [LibraryImport("libX11.so.6")]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nuint XInternAtom(nint display, string name, int onlyIfExists);

    [LibraryImport("libX11.so.6")]
    private static partial nuint XDefaultRootWindow(nint display);

    [LibraryImport("libX11.so.6")]
    private static unsafe partial int XGetWindowProperty(nint display, nuint window, nuint property, nint offset, nint length, int delete,
        nuint requestedType, out nuint actualType, out int actualFormat, out nuint count, out nuint remaining, byte** data);

    [LibraryImport("libX11.so.6")]
    private static partial int XFree(nint data);
}
