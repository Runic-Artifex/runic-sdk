using Runic.Desktop;

// #1189 (D077.2): drops onto the side panel, after each toolkit's file drop smoke: WebKitGTK on GTK 3 (FileDropSmoke),
// GTK 4 (FileDropGtk4Smoke, linked) and WebView2 (WindowsFileDropSmoke). A real drag of files onto the panel's page
// reaches the window's FilesDropped with the native paths, the panel page's label and InSidePanel; the panel's page
// receives its own drop event and does not navigate. A drag onto the page beside it is still the page's. Where the
// toolkit's drag source can offer text, a text drag reaches the panel page's drop event and reports no files.
internal static class SidePanelDropSmoke
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    internal const string Text = "Runic panel text";

    // One document: the page, or with #side-panel the panel's page, whose drop target has its own label. The panel's
    // page reports its drop events to the surface's Resources handler, since ExecuteJavaScriptAsync reaches the page only.
    private const string Page =
        """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic side panel drop</title>
        <style>html,body{margin:0;height:100%} #target{position:fixed;inset:0;background:#cde}</style></head>
        <body><div id="target" data-runic-drop-target="folder-7">drop here</div>
        <script>
        window.__pageDrops = 0;
        if (location.hash === "#side-panel") {
          document.title = "Side panel";
          document.getElementById("target").setAttribute("data-runic-drop-target", "panel-3");
          document.getElementById("target").style.background = "#edc";
          // A page that takes text drops says so on dragover, as an application would.
          addEventListener("dragover", event => event.preventDefault());
          addEventListener("drop", event => {
            event.preventDefault();
            const types = Array.from(event.dataTransfer.types);
            // WebKitGTK lists a file drag as text/uri-list.
            const files = types.includes("Files") || types.includes("text/uri-list");
            const kind = files ? "files" : "text:" + event.dataTransfer.getData("text/plain");
            fetch("report/panel-drop/" + encodeURIComponent(kind));
          });
          fetch("report/panel-loaded");
        } else {
          addEventListener("drop", () => window.__pageDrops++);
        }
        </script></body></html>
        """;

    // How a toolkit drags onto its window: Drag performs one drag of the paths (or, with paths null, of Text) onto a
    // point of the toolkit's choice, and returns false when the desktop delivers no input (Windows runners).
    internal sealed record Toolkit
    {
        internal required string Name { get; init; }
        internal required DesktopWindowOptions Window { get; init; }
        internal required DesktopWindowEdge Edge { get; init; }
        internal required int Width { get; init; }
        // The points Drag takes once the panel is open: on the panel's page and on the window's page.
        internal required Func<DesktopWindow, (int X, int Y)> PanelPoint { get; init; }
        internal required Func<DesktopWindow, (int X, int Y)> PagePoint { get; init; }
        internal required Func<DesktopWindow, string[]?, (int X, int Y), Task<bool>> Drag { get; init; }
        internal bool DragsText { get; init; }
    }

    internal static async Task RunAsync(DesktopHost host, string[] paths, Toolkit toolkit)
    {
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Html(Page),
            Resources = (request, _) =>
            {
                if (!request.Path.StartsWith("/report/", StringComparison.Ordinal)) return ValueTask.FromResult<ContentResponse?>(null);
                reports.Enqueue(Uri.UnescapeDataString(request.Path["/report/".Length..]));
                return ValueTask.FromResult<ContentResponse?>(ContentResponse.Text("ok", "text/plain"));
            },
        });
        await using var window = await surface.OpenWindowAsync(toolkit.Window);
        await window.FocusAsync();
        var before = await surface.ExecuteJavaScriptAsync("return location.href;", TimeSpan.FromSeconds(10));
        await WaitForAsync(() => window.State?.Bounds is not null, "the window's bounds");
        await Task.Delay(500);
        var result = await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Edge = toolkit.Edge, Width = toolkit.Width, Path = "#side-panel" });
        await using var panel = (result as DesktopSidePanelResult.Opened)?.Panel
            ?? throw new InvalidOperationException($"{toolkit.Name} side panel drop: the panel did not open on the {toolkit.Edge} edge: {result}.");
        await WaitForAsync(() => reports.Contains("panel-loaded"), "the panel's page to load", reports);
        await Task.Delay(700);
        var drops = System.Threading.Channels.Channel.CreateUnbounded<DesktopFileDropEventArgs>();
        window.FilesDropped += (_, drop) => drops.Writer.TryWrite(drop);

        if (!await toolkit.Drag(window, paths, toolkit.PanelPoint(window)))
        {
            Console.WriteLine($"{toolkit.Name} side panel drop: unverified on this runner: its desktop does not deliver input to a window of this process.");
            return;
        }
        var onPanel = await drops.Reader.ReadAsync().AsTask().WaitScaledAsync(Deadline);
        if (!onPanel.InSidePanel || onPanel.Target != "panel-3" || !SamePaths(onPanel.Paths, paths))
            throw new InvalidOperationException($"{toolkit.Name} side panel drop: the panel's drop reported InSidePanel {onPanel.InSidePanel}, target '{onPanel.Target}' and [{string.Join(", ", onPanel.Paths)}].");
        await WaitForAsync(() => reports.Contains("panel-drop/files"), "the panel's page to see its drop event", reports);
        // A panel page that navigated or reloaded would report loading again.
        await Task.Delay(500);
        if (reports.Count(static report => report == "panel-loaded") != 1)
            throw new InvalidOperationException($"{toolkit.Name} side panel drop: the panel's page loaded again after the drop.");
        Console.WriteLine($"{toolkit.Name} side panel drop: the panel's drop reached FilesDropped with InSidePanel, target 'panel-3' and the native paths; its page saw the drop and stayed.");

        if (!await toolkit.Drag(window, paths, toolkit.PagePoint(window)))
            throw new InvalidOperationException($"{toolkit.Name} side panel drop: the drag onto the page found no desktop input after the panel's did.");
        var onPage = await drops.Reader.ReadAsync().AsTask().WaitScaledAsync(Deadline);
        if (onPage.InSidePanel || onPage.Target != "folder-7" || !SamePaths(onPage.Paths, paths))
            throw new InvalidOperationException($"{toolkit.Name} side panel drop: the page's drop beside the panel reported InSidePanel {onPage.InSidePanel} and target '{onPage.Target}'.");
        var pageDrops = await surface.ExecuteJavaScriptAsync("return String(window.__pageDrops);");
        var after = await surface.ExecuteJavaScriptAsync("return location.href;");
        if (pageDrops != "1" || after != before)
            throw new InvalidOperationException($"{toolkit.Name} side panel drop: the page saw {pageDrops} drop events and moved from {before} to {after}.");
        Console.WriteLine($"{toolkit.Name} side panel drop: the page's drop beside the panel stayed the page's.");

        if (toolkit.DragsText)
        {
            if (!await toolkit.Drag(window, null, toolkit.PanelPoint(window)))
                throw new InvalidOperationException($"{toolkit.Name} side panel drop: the text drag found no desktop input.");
            await WaitForAsync(() => reports.Contains("panel-drop/text:" + Text), "the panel's page to receive the dropped text", reports);
            await Task.Delay(800);
            if (drops.Reader.TryRead(out var stray))
                throw new InvalidOperationException($"{toolkit.Name} side panel drop: a text drop was reported as files [{string.Join(", ", stray.Paths)}].");
            Console.WriteLine($"{toolkit.Name} side panel drop: dropped text reached the panel's page and reported no files.");
        }
        await panel.CloseAsync();
        await window.CloseAsync();
    }

    private static bool SamePaths(IReadOnlyList<string> actual, string[] expected) =>
        actual.SequenceEqual(expected, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static async Task WaitForAsync(Func<bool> condition, string expected, IEnumerable<string>? reports = null)
    {
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(Deadline);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Side panel drop: timed out waiting for {expected}"
                    + (reports is null ? "." : $"; the panel reported [{string.Join(", ", reports)}]."));
            }
            await Task.Delay(50);
        }
    }
}
