using System.Collections.Concurrent;
using System.Globalization;
using Runic.Desktop;

// sdk#864, P6, --side-panel-edges: the side panel cases --side-panel (P2–P4) leaves out.
// - window.close() in the panel's page closes the panel (Closed), never the window, and the window shrinks back to
//   exactly its start bounds.
// - Closing the panel of a maximized window gives the page the whole window, and the restored size shrinks: the window
//   restores to exactly its start bounds.
// - A full-screen window keeps the panel and the page takes the rest; width changes and moves are refused while it is
//   full-screen; leaving full screen gives back the grown bounds. Closing the panel there and then leaving full screen
//   gives back exactly the start bounds.
// A platform whose host has no panel yet (CheckSidePanel is Unsupported) reports the cases as not verified: macOS until
// #905 (P3), Linux until #907 (P4). Windows must have it.
internal static class SidePanelEdgeSmoke
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(600);
    private const int PanelWidth = 240;

    // One document: the page, or with #side-panel the panel's page. The panel's page asks the surface's Resources handler
    // what to do, since ExecuteJavaScriptAsync reaches the page only; "close" makes it call window.close().
    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic side panel edges</title>
        <style>html,body{margin:0;height:100%;background:#fff} main{padding:24px}</style>
        <script>
        addEventListener('DOMContentLoaded', () => {
          if (location.hash !== '#side-panel') return;
          document.title = 'Side panel';
          document.body.innerHTML = '<aside aria-label="Side panel" style="padding:16px"><button>Panel</button></aside>';
          fetch('report/loaded');
          const poll = async () => {
            const command = await (await fetch('report/command', { cache: 'no-store' })).text();
            if (command === 'close') { fetch('report/closing'); window.close(); return; }
            setTimeout(poll, 100);
          };
          poll();
        });
        </script></head>
        <body><main><button>Page</button></main></body></html>
        """;

    internal static int Run(DesktopHostOptions hostOptions) => DesktopEventLoop.Run(hostOptions, RunAsync);

    private static async Task<int> RunAsync(DesktopHost host)
    {
        var reports = new ConcurrentQueue<string>();
        var command = "";
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Html(Page),
            Resources = (request, _) =>
            {
                if (!request.Path.StartsWith("/report/", StringComparison.Ordinal)) return ValueTask.FromResult<ContentResponse?>(null);
                var report = request.Path["/report/".Length..];
                if (report == "command") return ValueTask.FromResult<ContentResponse?>(ContentResponse.Text(Volatile.Read(ref command), "text/plain"));
                reports.Enqueue(report);
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
            if (window.CheckSidePanel(DesktopWindowEdge.End, PanelWidth) == DesktopSidePanelRefusal.Unsupported)
            {
                if (OperatingSystem.IsWindows()) throw new InvalidOperationException("Side panel edges: WebView2 refused the panel with Unsupported.");
                Console.WriteLine("NOT VERIFIED side panel edges: this platform's host refuses the panel with Unsupported "
                    + "(macOS until #905, Linux until #907).");
                return 0;
            }
            var start = window.State!;
            var native = (int)Math.Round(PanelWidth * start.Bounds!.Scale, MidpointRounding.AwayFromZero);
            var grown = start.Bounds with { Width = start.Bounds.Width + native };
            Console.WriteLine($"Side panel edges: window {start.Bounds}, panel {PanelWidth} ({native} native).");

            // window.close() in the panel's page.
            reports.Clear();
            var panel = await OpenAsync(window, surface, reports, grown);
            var reason = ClosedReason(panel);
            Volatile.Write(ref command, "close");
            await WaitForAsync(() => reports.Contains("closing"), "the panel's page to call window.close()");
            Require(await reason.WaitAsync(Deadline) == DesktopSidePanelCloseReason.Closed, "Closed after window.close() in the panel");
            Volatile.Write(ref command, "");
            await WaitForAsync(() => SameFrame(window.State!.Bounds!, start.Bounds), $"exactly the start bounds {start.Bounds} after window.close()");
            await WaitForAsync(() => surface.ConnectionCount == 1, "the panel's page to disconnect");
            Require(window.IsOpen && window.SidePanel is null, "the window still open, without a panel");
            Require(await surface.ExecuteJavaScriptAsync("return document.title;", Deadline) is "Runic side panel edges" or "\"Runic side panel edges\"",
                "the page still there");
            Console.WriteLine("Side panel edges: window.close() in the panel closed the panel (Closed), not the window; exact start bounds.");

            // Closing the panel of a maximized window.
            panel = await OpenAsync(window, surface, reports, grown);
            await window.ToggleMaximizedAsync();
            await WaitForAsync(() => window.State!.IsMaximized, "the window maximized");
            await Task.Delay(Settle);
            Require(panel.IsOpen, "the panel to stay open in a maximized window");
            reason = ClosedReason(panel);
            await panel.CloseAsync();
            Require(await reason.WaitAsync(Deadline) == DesktopSidePanelCloseReason.Closed, "Closed as the close reason");
            Require(window.State!.IsMaximized, "the window still maximized after the panel closed");
            await RequirePageFillsAsync(window, surface, 0, "the maximized window without the panel");
            await window.ToggleMaximizedAsync();
            await WaitForAsync(() => !window.State!.IsMaximized && SameFrame(window.State.Bounds!, start.Bounds),
                $"the window to restore to exactly the start bounds {start.Bounds}: its restored size shrinks by the panel");
            Console.WriteLine("Side panel edges: closing a maximized window's panel shrank its restored size; it restored to the start bounds.");

            // A full-screen window keeps the panel.
            panel = await OpenAsync(window, surface, reports, grown);
            await window.SetFullScreenAsync(true);
            await WaitForAsync(() => window.State!.IsFullScreen, "the window full-screen");
            await Task.Delay(Settle);
            Require(panel.IsOpen, "the panel to stay open in a full-screen window");
            await RequirePageFillsAsync(window, surface, PanelWidth, "the full-screen window beside the panel");
            Require(await panel.SetWidthAsync(PanelWidth + 10) == DesktopSidePanelRefusal.FullScreen, "a width change refused while full-screen");
            Require(await panel.MoveToEdgeAsync(DesktopWindowEdge.Left) == DesktopSidePanelRefusal.FullScreen, "a move refused while full-screen");
            Require(window.CheckSidePanel(DesktopWindowEdge.Right, PanelWidth) == DesktopSidePanelRefusal.AlreadyOpen, "AlreadyOpen while a panel is open");
            await window.SetFullScreenAsync(false);
            await WaitForAsync(() => !window.State!.IsFullScreen && SameFrame(window.State.Bounds!, grown),
                $"the window to leave full screen at the grown bounds {grown}");
            Require(panel.IsOpen && panel.Width == PanelWidth && panel.Edge == DesktopWindowEdge.Right, "the panel unchanged after full screen");
            await panel.CloseAsync();
            await WaitForAsync(() => SameFrame(window.State!.Bounds!, start.Bounds), $"exactly the start bounds {start.Bounds}");
            Console.WriteLine("Side panel edges: a full-screen window kept the panel and refused changes; it left full screen at the grown bounds.");

            // Closing the panel of a full-screen window.
            panel = await OpenAsync(window, surface, reports, grown);
            await window.SetFullScreenAsync(true);
            await WaitForAsync(() => window.State!.IsFullScreen, "the window full-screen again");
            await Task.Delay(Settle);
            await panel.CloseAsync();
            Require(window.State!.IsFullScreen, "the window still full-screen after the panel closed");
            await RequirePageFillsAsync(window, surface, 0, "the full-screen window without the panel");
            await window.SetFullScreenAsync(false);
            await WaitForAsync(() => !window.State!.IsFullScreen && SameFrame(window.State.Bounds!, start.Bounds),
                $"the window to leave full screen at exactly the start bounds {start.Bounds}");
            Console.WriteLine("Side panel edges: closing a full-screen window's panel shrank its restored size; it left full screen at the start bounds.");
        }
        finally { if (window.IsOpen) await window.CloseAsync(); }
        Console.WriteLine("PASS side panel edges: window.close() in the panel, closing while maximized, full screen with the panel open and closed.");
        return 0;
    }

    private static async Task<DesktopSidePanel> OpenAsync(DesktopWindow window, DesktopSurface surface, ConcurrentQueue<string> reports,
        DesktopWindowBounds grown)
    {
        reports.Clear();
        var result = await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Edge = DesktopWindowEdge.Right, Width = PanelWidth, Path = "#side-panel" });
        var panel = (result as DesktopSidePanelResult.Opened)?.Panel ?? throw new InvalidOperationException($"Side panel edges: expected it to open, not {result}.");
        await WaitForAsync(() => SameFrame(window.State!.Bounds!, grown), $"the grown bounds {grown}");
        await WaitForAsync(() => surface.ConnectionCount == 2 && reports.Contains("loaded"), "the panel's page to connect beside the page");
        await Task.Delay(Settle);
        return panel;
    }

    private static Task<DesktopSidePanelCloseReason> ClosedReason(DesktopSidePanel panel)
    {
        var reason = new TaskCompletionSource<DesktopSidePanelCloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        panel.Closed += (_, args) => reason.TrySetResult(args.Reason);
        return reason.Task;
    }

    // The page's viewport is the window's visible width less the panel (0 when it is closed), within the frame's width.
    private static async Task RequirePageFillsAsync(DesktopWindow window, DesktopSurface surface, int panel, string what)
    {
        var expected = 0d;
        var width = 0;
        await WaitForAsync(async () =>
        {
            var visible = window.State!.VisibleBounds!;
            expected = visible.Width / visible.Scale - panel;
            width = int.Parse((await surface.ExecuteJavaScriptAsync("return String(innerWidth);", Deadline)).Trim('"'), CultureInfo.InvariantCulture);
            return width <= expected + 1 && width >= expected - 24;
        }, $"the page to take {what}");
        Console.WriteLine($"Side panel edges: the page is {width} CSS px wide in {what} ({expected:0} expected).");
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
        throw new TimeoutException($"Side panel edges: expected {expected}.");
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Side panel edges: expected " + what + ".");
    }
}
