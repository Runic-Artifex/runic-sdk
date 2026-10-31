using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Runic.Desktop;
using Runic.Desktop.Internal;
using Runic.Platform;

// D053.27 (H2) on a real WebView2: a start that outlasts the grace period, forced with the host's test delay before the
// WebView2 environment is created, shows the window before its page with the loading surface (the color scheme's
// background and the "Loading…" title), with the bounds, maximized state and DPI of a window shown when ready, then swaps
// the WebView in and connects. Closing the window during loading returns it closed, without asking the close guard and
// without a fallback browser. A hidden window stays hidden, a transparent one waits for WebView2, and a start within the
// grace period never shows the surface.
internal static partial class WindowsLoadingSurfaceSmoke
{
    private static readonly TimeSpan SlowStart = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    internal static async Task RunAsync(DesktopHost host)
    {
        var events = new ConcurrentQueue<(nint Window, bool Showing, long Time)>();
        WindowsWebView2Host.LoadingSurfaceChanged = (window, showing) => events.Enqueue((window, showing, Stopwatch.GetTimestamp()));
        try
        {
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
            var options = new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded,
                Width = 640,
                Height = 480,
                X = 120,
                Y = 90,
                ColorScheme = DesktopColorScheme.Dark,
            };

            // Shown when ready: the grace period cannot end first.
            WindowsWebView2Host.LoadingSurfaceDelay = TimeSpan.FromMinutes(10);
            Geometry reference;
            await using (var window = await surface.OpenWindowAsync(options))
            {
                reference = Measure(window.NativeHandle);
                await window.CloseAsync();
            }
            Expect(events.IsEmpty, "a window shown when ready showed the loading surface");
            WindowsWebView2Host.LoadingSurfaceDelay = WindowsLoadingSurface.GracePeriod;

            // A slow start: the window shows its loading surface after the grace period, then the WebView.
            WindowsWebView2Host.BeforeEnvironmentCreation = () => Task.Delay(SlowStart);
            var started = Stopwatch.GetTimestamp();
            var opening = surface.OpenWindowAsync(options).AsTask();
            var (loadingWindow, shownAt) = await ShownAsync(events);
            var shownAfter = Stopwatch.GetElapsedTime(started, shownAt);
            Expect(shownAfter >= WindowsLoadingSurface.GracePeriod - TimeSpan.FromMilliseconds(50),
                $"the loading surface showed {shownAfter.TotalMilliseconds:0} ms after the open, before the grace period");
            Expect(!opening.IsCompleted, "the window opened before the loading surface showed");
            await Task.Delay(300);
            Expect(IsWindowVisible(loadingWindow), "the loading window is not visible");
            Expect(Title(loadingWindow) == WindowsLoadingSurface.Title, $"the loading window is titled '{Title(loadingWindow)}'");
            var loading = Measure(loadingWindow);
            Expect(loading == reference, $"the loading window has {loading}, the window shown when ready {reference}");
            var background = ClientPixel(loadingWindow);
            var dark = WindowsLoadingSurface.PaletteFor(DesktopColorScheme.Dark, false, false, static _ => 0).Background;
            Expect(background == dark, string.Create(CultureInfo.InvariantCulture,
                $"the dark loading surface is 0x{background:X6}, expected 0x{dark:X6}"));
            await using (var window = await opening.WaitAsync(Deadline))
            {
                Expect(window.IsOpen && window.NativeHandle == loadingWindow, "the WebView did not open in the loading window");
                Expect(events.Count(item => item.Window == loadingWindow && !item.Showing) == 1, "the loading surface did not end once");
                Expect(Measure(window.NativeHandle) == reference, $"the open window has {Measure(window.NativeHandle)}, expected {reference}");
                await UntilAsync(() => Title(loadingWindow) == PageTitle, $"the page did not title the window: '{Title(loadingWindow)}'");
                await window.CloseAsync();
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Loading surface: shown {shownAfter.TotalMilliseconds:0} ms after the open, {loading}, then the WebView connected."));

            // A maximized placement shows maximized during loading too.
            events.Clear();
            var maximized = options with
            {
                Placement = new DesktopWindowPlacement { X = 120, Y = 90, Width = 640, Height = 480, Maximized = true },
            };
            opening = surface.OpenWindowAsync(maximized).AsTask();
            (loadingWindow, _) = await ShownAsync(events);
            Expect(IsZoomed(loadingWindow), "a maximized placement is not maximized during loading");
            await using (var window = await opening.WaitAsync(Deadline))
            {
                Expect(window.State?.IsMaximized == true, "a maximized placement is not maximized once open");
                await window.CloseAsync();
            }

            // Closing during loading: the window is returned closed, the guard is not asked and no fallback opens.
            events.Clear();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            WindowsWebView2Host.BeforeEnvironmentCreation = () => release.Task;
            var asked = 0;
            opening = surface.OpenWindowAsync(options with
            {
                PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
                Browser = BrowserKind.Embedded,
                ConfirmCloseAsync = _ => { Interlocked.Increment(ref asked); return ValueTask.FromResult(false); },
            }).AsTask();
            (loadingWindow, _) = await ShownAsync(events);
            PostMessage(loadingWindow, 0x0010, 0, 0); // WM_CLOSE, as the close button sends it.
            await using (var window = await opening.WaitAsync(TestTimeouts.Scale(TimeSpan.FromSeconds(15))))
            {
                Expect(!window.IsOpen, "a window closed during loading opened");
                Expect(window.Browser == BrowserKind.Embedded && !window.IsFallback, $"closing during loading opened {window.Browser}");
                Expect(Volatile.Read(ref asked) == 0, "closing during loading asked the close guard");
                Expect(!IsWindow(loadingWindow), "the loading window still exists");
                await window.WaitForCloseAsync().AsTask().WaitAsync(TestTimeouts.Scale(TimeSpan.FromSeconds(10)));
            }
            release.SetResult();

            // A hidden window stays hidden while it starts.
            events.Clear();
            WindowsWebView2Host.BeforeEnvironmentCreation = () => Task.Delay(TimeSpan.FromSeconds(3));
            await using (var window = await surface.OpenWindowAsync(options with { Hidden = true }).AsTask().WaitAsync(Deadline))
            {
                Expect(events.IsEmpty, "a hidden window showed the loading surface");
                Expect(!IsWindowVisible(window.NativeHandle), "a hidden window became visible");
                await window.CloseAsync();
            }
            // A transparent window waits for WebView2: its page would show the surface's last frame through.
            await using (var window = await surface.OpenWindowAsync(options with { Transparent = true }).AsTask().WaitAsync(Deadline))
            {
                Expect(events.IsEmpty, "a transparent window showed the loading surface");
                Expect(IsWindowVisible(window.NativeHandle), "a transparent window is not visible once open");
                await window.CloseAsync();
            }
            Console.WriteLine("PASS WebView2 loading surface: shown after the grace period with the ready placement, swapped, "
                + "maximized, closed during loading without a fallback, hidden and transparent windows wait.");
        }
        finally
        {
            WindowsWebView2Host.BeforeEnvironmentCreation = null;
            WindowsWebView2Host.LoadingSurfaceDelay = WindowsLoadingSurface.GracePeriod;
            WindowsWebView2Host.LoadingSurfaceChanged = null;
        }
    }

    private const string PageTitle = "Runic loading surface smoke";

    private static string Page =>
        $"""
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script>
        <title>{PageTitle}</title></head><body>loaded</body></html>
        """;

    private readonly record struct Geometry(int Left, int Top, int Right, int Bottom, bool Maximized, uint Dpi)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"({Left}, {Top})-({Right}, {Bottom}){(Maximized ? " maximized" : "")} at {Dpi} DPI");
    }

    private static Geometry Measure(nint window)
    {
        Expect(GetWindowRect(window, out var frame), "the window has no frame");
        return new Geometry(frame.Left, frame.Top, frame.Right, frame.Bottom, IsZoomed(window), GetDpiForWindow(window));
    }

    private static async Task<(nint Window, long Time)> ShownAsync(ConcurrentQueue<(nint Window, bool Showing, long Time)> events)
    {
        var wait = TestTimeouts.Start(TimeSpan.FromSeconds(10));
        while (true)
        {
            foreach (var item in events)
            {
                if (item.Showing)
                {
                    wait.Succeeded();
                    return (item.Window, item.Time);
                }
            }
            if (wait.IsExpired) throw new InvalidOperationException("The slow-starting window did not show its loading surface.");
            await Task.Delay(20);
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string failure)
    {
        if (!await TestTimeouts.UntilAsync(condition, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50)))
            throw new InvalidOperationException(failure);
    }

    private static void Expect(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException("Loading surface smoke: " + failure + ".");
    }

    private static unsafe string Title(nint window)
    {
        var buffer = stackalloc char[256];
        var length = GetWindowText(window, buffer, 256);
        return new string(buffer, 0, Math.Max(0, length));
    }

    // A pixel near the client area's top-left corner, away from the progress bar, as DWM composes it.
    private static uint ClientPixel(nint window)
    {
        const uint ClientOnlyFullContent = 0x1 | 0x2; // PW_CLIENTONLY | PW_RENDERFULLCONTENT
        Expect(GetClientRect(window, out var client), "the window has no client area");
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, client.Right, client.Bottom);
        var previous = SelectObject(memory, bitmap);
        try
        {
            Expect(PrintWindow(window, memory, ClientOnlyFullContent), "PrintWindow failed");
            return GetPixel(memory, 8, 8) & 0x00FFFFFF;
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            _ = ReleaseDC(0, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint window);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rectangle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out Rect rectangle);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowText(nint window, char* text, int length);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint window, nint deviceContext, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint deviceContext);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint deviceContext);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint deviceContext, nint item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint deviceContext);

    [LibraryImport("gdi32.dll")]
    private static partial uint GetPixel(nint deviceContext, int x, int y);
}
