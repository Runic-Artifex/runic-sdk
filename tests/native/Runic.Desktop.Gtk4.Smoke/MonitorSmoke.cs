using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Desktop;
using Runic.Desktop.Gtk4;
using Runic.Platform;

// --monitors gtk3|gtk4 (B12, I-066, I-073) under Xvfb: the host lists and watches GDK's monitors, a window opens on and
// moves to a monitor and reports it, enters and leaves full screen and restores a full-screen placement, and a window
// with LayoutDirection and Language has GTK's direction and the page's navigator.language, under the C locale.
// RUNIC_TEST_MONITORS=2 requires two monitors, as Xvfb gives GTK 3 with +xinerama and two screens.
[SupportedOSPlatform("linux")]
internal static class MonitorSmoke
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic monitors</title></head>
        <body>monitors</body></html>
        """;

    internal static int Run(string toolkit)
    {
        var options = toolkit switch
        {
            "gtk3" => new DesktopHostOptions { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } },
            "gtk4" => new DesktopHostOptions().WithGtk4(),
            _ => throw new ArgumentException($"Unknown toolkit '{toolkit}'; use gtk3 or gtk4.", nameof(toolkit)),
        };
        options = options with { WaitForConnection = true };
        return DesktopEventLoop.Run(options, host => RunAsync(host, toolkit));
    }

    private static async Task<int> RunAsync(DesktopHost host, string toolkit)
    {
        var result = await host.Monitors.ListAsync();
        if (result is not PlatformResult<ImmutableArray<DesktopMonitor>>.Success { Value: var monitors })
            throw new InvalidOperationException($"The {toolkit} host did not list its monitors: {result}.");
        foreach (var monitor in monitors) Console.WriteLine($"Monitors ({toolkit}): {monitor}.");
        Require(monitors.Length > 0, "at least one monitor");
        if (Environment.GetEnvironmentVariable("RUNIC_TEST_MONITORS") is { } expected)
            Require(monitors.Length == int.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), $"{expected} monitors");
        Require(monitors.Select(static monitor => monitor.Id).Distinct().Count() == monitors.Length, "distinct monitor identifiers");
        Require(monitors.All(static monitor => monitor.Scale >= 1 && monitor.Bounds.Width > 0 && monitor.WorkArea.Width > 0), "sizes and scales");

        using (var cancellation = TestTimeouts.Cancellation(TimeSpan.FromSeconds(30)))
        {
            await using var watch = host.Monitors.WatchAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            Require(await watch.MoveNextAsync() && watch.Current is PlatformResult<ImmutableArray<DesktopMonitor>>.Success { Value: var first }
                && first.SequenceEqual(monitors), "the watch starts with the current monitors");
        }

        var last = monitors[^1];
        DesktopWindowPlacement saved;
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        {
            var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded, Width = 360, Height = 240, MonitorId = last.Id,
                LayoutDirection = DesktopLayoutDirection.RightToLeft, Language = "ar-EG",
            });
            Require(window.Capabilities.HasFlag(DesktopWindowCapabilities.FullScreen | DesktopWindowCapabilities.MonitorPlacement),
                $"FullScreen and MonitorPlacement on X11, not {window.Capabilities}");
            await WaitForAsync(window, state => state.MonitorId == last.Id, $"the window on {last.Id}");
            Console.WriteLine($"Monitors ({toolkit}): opened on {last.Id}: {window.State}.");
            await window.MoveToMonitorAsync(monitors[0].Id);
            await WaitForAsync(window, state => state.MonitorId == monitors[0].Id, $"the window moved to {monitors[0].Id}");
            Console.WriteLine($"Monitors ({toolkit}): moved to {monitors[0].Id}: {window.State}.");
            try
            {
                await window.MoveToMonitorAsync("runic-no-such-monitor");
                throw new InvalidOperationException("Monitors: an unknown monitor did not throw ArgumentException.");
            }
            catch (ArgumentException) { }

            var direction = 0;
            await window.InvokeNativeAsync(handle => direction = toolkit == "gtk3" ? Gtk3Direction(handle) : Gtk4Direction(handle));
            Require(direction == 2, $"GTK's direction Rtl (2) on the window, got {direction}");
            var language = await surface.ExecuteJavaScriptAsync("return navigator.language;", TimeSpan.FromSeconds(10));
            Require(string.Equals(language, "ar-EG", StringComparison.OrdinalIgnoreCase), $"navigator.language ar-EG, got {language}");
            Console.WriteLine($"PASS {toolkit} direction and language: GTK direction Rtl, navigator.language {language} under the C locale.");

            // X11 full screen is a window manager's (_NET_WM_STATE_FULLSCREEN); Xvfb alone has none.
            await window.SetFullScreenAsync(true);
            if (await TryWaitForAsync(window, static state => state.IsFullScreen, TimeSpan.FromSeconds(3)) is not { } full)
            {
                Console.WriteLine($"Monitors ({toolkit}): no window manager made the window full-screen; full-screen checks skipped.");
                await window.CloseAsync();
                Console.WriteLine($"PASS {toolkit} monitors: list, watch, open on and move to a monitor.");
                return 0;
            }
            Require(full.Placement.FullScreen, "a full-screen placement");
            await window.SetFullScreenAsync(false);
            await WaitForAsync(window, static state => !state.IsFullScreen, "leaving full screen");
            await window.SetFullScreenAsync(true);
            saved = (await WaitForAsync(window, static state => state.IsFullScreen, "full screen again")).Placement;
            await window.CloseAsync();
        }
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Placement = saved }))
        {
            await WaitForAsync(window, static state => state.IsFullScreen, "the full-screen placement restored full-screen");
        }
        Console.WriteLine($"PASS {toolkit} monitors: list, watch, open on and move to a monitor, full screen and its restore.");
        return 0;
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Monitors: expected " + what + ".");
    }

    private static async Task<DesktopWindowState> WaitForAsync(DesktopWindow window, Func<DesktopWindowState, bool> condition, string expected)
    {
        var wait = TestTimeouts.Start(Deadline);
        var state = await TryWaitForAsync(window, condition, wait.Limit)
            ?? throw new TimeoutException($"Monitors: the window did not report {expected}; last state: {window.State?.ToString() ?? "<none>"}.");
        wait.Succeeded();
        return state;
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

    [DllImport("libgtk-3.so.0", EntryPoint = "gtk_widget_get_direction")]
    private static extern int Gtk3Direction(nint widget);

    [DllImport("libgtk-4.so.1", EntryPoint = "gtk_widget_get_direction")]
    private static extern int Gtk4Direction(nint widget);
}
