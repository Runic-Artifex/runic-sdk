using System.Globalization;
using System.Runtime.InteropServices;
using Runic.Desktop;

// sdk#864, P2 and P4, --side-panel: the side panel on WebView2 and WebKitGTK, GTK 3 and GTK 4 (DesktopWindow.OpenSidePanelAsync,
// RUNICDESK104). Linked into Runic.Desktop.Gtk4.Smoke for GTK 4.
// - Open, close, move and resize on both edges: the window's bounds change by the panel's exact native width, the page
//   is never resized (its resize events stay at zero, its innerWidth is unchanged), closing restores the exact bounds,
//   the panel's page connects as a second connection of the window and disconnects when it closes.
// - Frame capture: each DWM frame (DwmFlush) one row of the screen across the page's left edge, which the page marks
//   with a magenta bar, while the panel opens and closes on the left edge and moves between edges. The page's edge may
//   be out of place in at most one frame (side-panel/DESIGN.md §4); the count is printed. A baseline search before any
//   side-panel operation decides whether this desktop can be captured: where GDI does not show the marker then, as on
//   the arm64 runner (nothing, garbage or a blank white row), frame capture is unverified and the transitions run with
//   their other checks. A marker found at the baseline and missing or out of place later fails.
// - Tab from the page's last element focuses the panel's first, Shift+Tab hands focus back (SendInput, or XTEST on X11;
//   a desktop that delivers no input to the window, as the arm64 runner's, leaves it unverified).
// - Linux runs under a window manager (openbox) for maximize. Frame capture is Windows only; on X11 the page's resize
//   count and width carry the check. On Wayland the left edge is refused with PositionUnavailable (D062.4).
// - The placement excludes the panel; a maximized window keeps the panel and the page takes the rest; refusals
//   (no room, already open, maximized); the minimum width includes the panel; closing the window closes the panel.
// On macOS the panel is refused with Unsupported until its engine has one (P3).
internal static class SidePanelSmoke
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
          // A panel page that closes itself: window.close() closes the panel, never the window.
          // It waits until the smoke allows it, once the panel is open.
          if (location.hash === '#side-panel-close') {
            const poll = () => fetch('close-now').then(r => r.text()).then(t => t === '1' ? window.close() : setTimeout(poll, 100));
            poll();
            return;
          }
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

    internal static int Run(DesktopHostOptions hostOptions, bool gtk4 = false) =>
        DesktopEventLoop.Run(hostOptions, host => RunAsync(host, gtk4));

    private static async Task<int> RunAsync(DesktopHost host, bool gtk4)
    {
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        CloseNow = false;
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Html(Page),
            Resources = (request, _) =>
            {
                if (request.Path == "/close-now") return ValueTask.FromResult<ContentResponse?>(ContentResponse.Text(Volatile.Read(ref CloseNow) ? "1" : "0", "text/plain"));
                if (!request.Path.StartsWith("/report/", StringComparison.Ordinal)) return ValueTask.FromResult<ContentResponse?>(null);
                reports.Enqueue(request.Path["/report/".Length..]);
                return ValueTask.FromResult<ContentResponse?>(ContentResponse.Text("ok", "text/plain"));
            },
        });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded, Width = 440, Height = 360, X = 280, Y = 120,
        });
        LastWindow = window;
        try
        {
            await surface.ExecuteJavaScriptAsync("return document.readyState;", Deadline);
            await WaitForAsync(() => window.State?.Bounds is not null, "the window's bounds");
            await Task.Delay(Settle);
            if (OperatingSystem.IsMacOS())
            {
                Require(window.CheckSidePanel(DesktopWindowEdge.End, PanelWidth) == DesktopSidePanelRefusal.Unsupported,
                    "macOS to refuse the side panel until P3");
                var refused = await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Width = PanelWidth, Path = "#side-panel" });
                Require(refused is DesktopSidePanelResult.Refused { Reason: DesktopSidePanelRefusal.Unsupported }, $"Unsupported, not {refused}");
                Console.WriteLine("PASS side panel: refused with Unsupported on this platform.");
                return 0;
            }

            var start = window.State!;
            var scale = start.Bounds!.Scale;
            var native = (int)Math.Round(PanelWidth * scale, MidpointRounding.AwayFromZero);
            var page = await ReadPageAsync(surface);
            Console.WriteLine($"Side panel: window {start.Bounds} at scale {scale}, page {page.Width} CSS px, work area check for {PanelWidth}.");

            // The baseline, before any side-panel operation, with the window as opened: where GDI cannot find the page's
            // marker now (the arm64 runner's desktop reads garbage or blank white), frame capture is unverified, and the
            // transitions run with every other check. Found here, it must be found in place later.
            // GDI frame capture is Windows only; X11 checks the page's resize count and width instead.
            var marker = OperatingSystem.IsWindows() ? FindBaselineMarker(start.VisibleBounds!) : null;

            await ExerciseRefusalsAsync(window);
            var right = await OpenAsync(window, surface, reports, DesktopWindowEdge.Right, start, native, page);
            Require(window.State!.Placement!.Width == start.Placement!.Width && window.State.Placement.X == start.Placement.X,
                $"the placement {window.State.Placement} to exclude the panel, as {start.Placement}");
            await ExerciseFocusAsync(window, surface, reports);
            Require(await right.SetWidthAsync(PanelWidth + 20) is null, "the panel to widen");
            var wider = (int)Math.Round((PanelWidth + 20) * scale, MidpointRounding.AwayFromZero);
            await WaitForAsync(() => window.State!.Bounds!.Width == start.Bounds.Width + wider, $"the window {wider} px wider than at the start");
            Require(await right.SetWidthAsync(PanelWidth) is null, "the panel to narrow");
            await WaitForAsync(() => window.State!.Bounds!.Width == start.Bounds.Width + native, "the window back at the panel's width");
            await RequirePageAsync(surface, page, "after the width changes");
            await CloseAsync(window, surface, right, start);

            if (start.Bounds!.X is null)
            {
                // Wayland: no positions, so no left edge (D062.4).
                Require(window.CheckSidePanel(DesktopWindowEdge.Left, PanelWidth) == DesktopSidePanelRefusal.PositionUnavailable,
                    "PositionUnavailable for the left edge without positions");
                Console.WriteLine("Side panel: the left edge was refused with PositionUnavailable on Wayland.");
            }
            else if (OperatingSystem.IsWindows())
            {
                await ExerciseLeftEdgeWithFramesAsync(window, surface, reports, start, native, page, marker);
            }
            else
            {
                nint pageView = 0;
                await window.InvokeNativeAsync(handle => pageView = Accessibility.OnlyView(handle, gtk4));
                var left = await OpenAsync(window, surface, reports, DesktopWindowEdge.Left, start, native, page);
                await RequireReadingOrderAsync(window, gtk4, pageView, panelFirst: true);
                Require(await left.MoveToEdgeAsync(DesktopWindowEdge.Right) is null, "the panel to move to the right edge");
                await WaitForAsync(() => window.State!.Bounds!.X == start.Bounds.X, "the window back at its start position");
                Require(left.Edge == DesktopWindowEdge.Right, "the panel on the right edge after the move");
                await RequireReadingOrderAsync(window, gtk4, pageView, panelFirst: false);
                Require(await left.MoveToEdgeAsync(DesktopWindowEdge.Left) is null, "the panel to move to the left edge");
                await WaitForAsync(() => window.State!.Bounds!.X == start.Bounds.X - native, "the window moved left by the panel");
                await RequireReadingOrderAsync(window, gtk4, pageView, panelFirst: true);
                Console.WriteLine($"Side panel: {(gtk4 ? "GtkAccessible" : "ATK")} lists the panel before the page on the left edge and after it on the right.");
                await RequirePageAsync(surface, page, "after the moves");
                await CloseAsync(window, surface, left, start);
                Console.WriteLine("Side panel: opened, moved and closed on the left edge with the page unchanged (frame capture is Windows only).");
            }

            if (!OperatingSystem.IsWindows()) await ExerciseWindowCloseInPanelAsync(window, surface, reports, start);
            await ExerciseMaximizedAsync(window, surface, start, page.Width);
            await ExerciseMinimumAndWindowCloseAsync(window, surface, start, native, gtk4);
        }
        finally { if (window.IsOpen) await window.CloseAsync(); }
        Console.WriteLine("PASS side panel: both edges, exact bounds, page unchanged, frames, focus, refusals, maximize, minimum, window close.");
        return 0;
    }

    // Windows: the left edge with frame capture; the page's left edge stays where it is on the screen.
    private static async Task ExerciseLeftEdgeWithFramesAsync(DesktopWindow window, DesktopSurface surface,
        System.Collections.Concurrent.ConcurrentQueue<string> reports, DesktopWindowState start, int native,
        (int Resizes, int Width, int Height) page, (int X, int Y)? marker)
    {
        // The window is back at its start bounds, so a marker the baseline found is where it found it; without one
        // (the arm64 runner's desktop), the transitions run without frame capture and every other check stays.
        if (marker is { } baseline) RequireMarker(baseline);
        DesktopSidePanel? left = null;
        await CaptureAsync("open on the left edge", marker,
            async () => left = await OpenAsync(window, surface, reports, DesktopWindowEdge.Left, start, native, page));
        await CaptureAsync("move to the right edge", marker, async () =>
        {
            Require(await left!.MoveToEdgeAsync(DesktopWindowEdge.Right) is null, "the panel to move to the right edge");
            await WaitForAsync(() => window.State!.Bounds!.X == start.Bounds!.X, "the window back at its start position");
        });
        await CaptureAsync("move to the left edge", marker, async () =>
        {
            Require(await left!.MoveToEdgeAsync(DesktopWindowEdge.Left) is null, "the panel to move to the left edge");
            await WaitForAsync(() => window.State!.Bounds!.X == start.Bounds!.X - native, "the window moved left by the panel");
        });
        await RequirePageAsync(surface, page, "after the moves");
        await CaptureAsync("close on the left edge", marker, () => CloseAsync(window, surface, left!, start));
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
        await surface.ExecuteJavaScriptAsync("document.getElementById('last').focus(); return document.activeElement.id;", Deadline);
        await Task.Delay(Settle);
        reports.Clear();
        Keyboard.Press(tab: true, shift: false);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!reports.Contains("focus-panel-first") && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
        if (!reports.Contains("focus-panel-first"))
        {
            var focus = await surface.ExecuteJavaScriptAsync("return document.hasFocus() + ' ' + document.activeElement.id;", Deadline);
            Require(focus.Trim('"') == "true last", $"Tab past the page's last element to focus the panel's first, but the page has '{focus}'");
            Console.WriteLine("Side panel: Tab unverified on this runner: its desktop does not deliver SendInput to the window.");
            return;
        }
        Keyboard.Press(tab: true, shift: true);
        await WaitForAsync(async () => (await surface.ExecuteJavaScriptAsync(
            "return document.hasFocus() + ' ' + document.activeElement.id;", Deadline)).Trim('"') == "true last",
            "Shift+Tab from the panel's first element to focus the page's last");
        Console.WriteLine("Side panel: Tab moved from the page to the panel and Shift+Tab back.");
    }

    // window.close() in the panel's page closes the panel with Closed, and the window shrinks back.
    private static async Task ExerciseWindowCloseInPanelAsync(DesktopWindow window, DesktopSurface surface,
        System.Collections.Concurrent.ConcurrentQueue<string> reports, DesktopWindowState start)
    {
        reports.Clear();
        var result = await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Width = PanelWidth, Path = "#side-panel-close" });
        var panel = (result as DesktopSidePanelResult.Opened)?.Panel ?? throw new InvalidOperationException($"Side panel: expected it to open, not {result}.");
        var reason = new TaskCompletionSource<DesktopSidePanelCloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        panel.Closed += (_, args) => reason.TrySetResult(args.Reason);
        Volatile.Write(ref CloseNow, true);
        Require(await reason.Task.WaitAsync(Deadline) == DesktopSidePanelCloseReason.Closed, "Closed when the panel's page calls window.close()");
        Require(window.IsOpen && window.SidePanel is null, "the window open without its panel");
        await WaitForAsync(() => SameFrame(window.State!.Bounds!, start.Bounds!), $"exactly the start bounds {start.Bounds}");
        await WaitForAsync(() => surface.ConnectionCount == 1, "the panel's page to disconnect");
        Console.WriteLine("Side panel: window.close() in the panel's page closed the panel, not the window.");
    }

    private static async Task ExerciseMaximizedAsync(DesktopWindow window, DesktopSurface surface, DesktopWindowState start, int startPageWidth)
    {
        var panel = await OpenQuietlyAsync(window);
        var opened = window.State!.Bounds!;
        await window.ToggleMaximizedAsync();
        await WaitForAsync(() => window.State!.IsMaximized, "the window maximized");
        await Task.Delay(Settle);
        Require(panel.IsOpen, "the panel to stay open in a maximized window");
        var page = await ReadPageAsync(surface);
        if (OperatingSystem.IsWindows())
        {
            var visible = window.State!.VisibleBounds!;
            var client = visible.Width / visible.Scale;
            Require(page.Width + PanelWidth <= client + 1 && page.Width + PanelWidth >= client - 24,
                $"the page ({page.Width}) to take the maximized window ({client:0}) beside the panel ({PanelWidth})");
        }
        else
        {
            // GTK's bounds are the content, which the page and the panel fill. The page's CSS pixels relate to the view's
            // logical pixels as at the start (WebKitGTK zooms by the display's DPI).
            var content = window.State!.Bounds!.Width;
            var ratio = start.Bounds!.Width / (double)startPageWidth;
            await WaitForAsync(async () => Math.Abs((await ReadPageAsync(surface)).Width * ratio + PanelWidth - content) <= 3,
                $"the page to take the maximized window ({window.State.Bounds}) beside the panel ({PanelWidth}), at {ratio:0.###} px per CSS px");
        }
        Require(await panel.SetWidthAsync(PanelWidth + 10) == DesktopSidePanelRefusal.Maximized, "a width change refused while maximized");
        Require(window.CheckSidePanel(DesktopWindowEdge.Left, PanelWidth) == DesktopSidePanelRefusal.AlreadyOpen, "AlreadyOpen while a panel is open");
        await window.ToggleMaximizedAsync();
        // GTK 4 reports the restored state before the restored size.
        await WaitForAsync(() => !window.State!.IsMaximized && SameFrame(window.State.Bounds!, opened), $"the window restored to {opened}");
        await panel.CloseAsync();
        await WaitForAsync(() => SameFrame(window.State!.Bounds!, start.Bounds!), "the start bounds after maximize, restore and close");
        Require(window.CheckSidePanel(DesktopWindowEdge.Right, PanelWidth) is null, "a panel to fit again");
        await window.ToggleMaximizedAsync();
        await WaitForAsync(() => window.State!.IsMaximized, "the window maximized again");
        Require(window.CheckSidePanel(DesktopWindowEdge.Right, PanelWidth) == DesktopSidePanelRefusal.Maximized, "Maximized for a maximized window");
        var refused = await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Width = PanelWidth, Path = "#side-panel" });
        Require(refused is DesktopSidePanelResult.Refused { Reason: DesktopSidePanelRefusal.Maximized }, $"Maximized, not {refused}");
        await window.ToggleMaximizedAsync();
        await WaitForAsync(() => !window.State!.IsMaximized && SameFrame(window.State.Bounds!, start.Bounds!), "the window restored again");
        Console.WriteLine("Side panel: a maximized window kept the panel with the page beside it, and refused a new one.");
        if (OperatingSystem.IsWindows()) return;

        // Closing the panel of a maximized window: the restored size shrinks by the panel (D062.5), as
        // SidePanelEdgeSmoke (--side-panel-edges) checks on every platform.
        panel = await OpenQuietlyAsync(window);
        await WaitForAsync(() => SameFrame(window.State!.Bounds!, opened), $"the grown bounds {opened}");
        await window.ToggleMaximizedAsync();
        await WaitForAsync(() => window.State!.IsMaximized, "the window maximized with the panel");
        await Task.Delay(Settle);
        await panel.CloseAsync();
        Require(window.State!.IsMaximized && window.SidePanel is null, "the window still maximized after the panel closed");
        await window.ToggleMaximizedAsync();
        await WaitForAsync(() => !window.State!.IsMaximized && SameFrame(window.State.Bounds!, start.Bounds!),
            $"the window to restore to exactly the start bounds {start.Bounds}: its restored size shrinks by the panel");
        Console.WriteLine("Side panel: closing a maximized window's panel shrank its restored size; it restored to the start bounds.");
    }

    // The minimum width includes the panel; closing the window closes the panel with WindowClosed.
    private static async Task ExerciseMinimumAndWindowCloseAsync(DesktopWindow window, DesktopSurface surface, DesktopWindowState start,
        int native, bool gtk4)
    {
        var panel = await OpenQuietlyAsync(window);
        if (OperatingSystem.IsWindows())
        {
            await window.InvokeNativeAsync(static handle => Win32.SetWindowPos(handle, 0, 0, 0, 100, 150, 0x0002 | 0x0004 | 0x0010));
        }
        else
        {
            // As a drag of the frame would, through the window manager, which applies the window's size hints.
            nuint xid = 0;
            await window.InvokeNativeAsync(handle => xid = WindowBoundsSmoke.X11Native.Xid(handle, gtk4));
            WindowBoundsSmoke.X11Native.Resize(xid, 100, 150);
            await Task.Delay(Settle);
        }
        await Task.Delay(Settle);
        var bounds = window.State!.Bounds!;
        // Windows keeps its system minimum for the page; GTK, without a minimum of the application's, at least a pixel.
        var page = OperatingSystem.IsWindows() ? 60 : 0;
        Require(bounds.Width > native + page, $"a window {bounds.Width} px wide, resized to 100 px from outside Runic, to stay wider than the panel ({native} px)");
        Console.WriteLine($"Side panel: the minimum width with the panel is {bounds.Width} px.");

        var reason = new TaskCompletionSource<DesktopSidePanelCloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        panel.Closed += (_, args) => reason.TrySetResult(args.Reason);
        await window.CloseAsync();
        Require(await reason.Task.WaitAsync(Deadline) == DesktopSidePanelCloseReason.WindowClosed, "WindowClosed when the window closes");
        Require(!panel.IsOpen && window.SidePanel is null, "no panel after the window closed");
        await WaitForAsync(() => surface.ConnectionCount == 0, "both pages to disconnect");
        Console.WriteLine("Side panel: closing the window closed the panel (WindowClosed).");
    }

    // AT-SPI lists the content's children in the order the toolkit's accessibility tree has them (ATK on GTK 3,
    // GtkAccessible on GTK 4): the panel first on the left edge, last on the right.
    private static async Task RequireReadingOrderAsync(DesktopWindow window, bool gtk4, nint pageView, bool panelFirst)
    {
        bool? first = null;
        await window.InvokeNativeAsync(handle => first = Accessibility.FirstIsPanel(handle, gtk4, pageView));
        Require(first == panelFirst, $"the accessibility tree to list the panel {(panelFirst ? "before" : "after")} the page");
    }

    // GTK thread: the window's content overlay (in the menu bar's box, if any) and its accessible children.
    private static unsafe class Accessibility
    {
        internal static nint OnlyView(nint window, bool gtk4)
        {
            var overlay = Overlay(window, gtk4);
            return gtk4 ? Gtk4("gtk_widget_get_first_child")(overlay) : Gtk3Children(overlay).Single();
        }

        internal static bool? FirstIsPanel(nint window, bool gtk4, nint pageView)
        {
            var overlay = Overlay(window, gtk4);
            if (gtk4)
            {
                // GTK 4.10: the overlay's first accessible child, a reference.
                var first = Gtk4("gtk_accessible_get_first_accessible_child")(overlay);
                if (first == 0) return null;
                ((delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(NativeLibrary.Load("libgobject-2.0.so.0"), "g_object_unref"))(first);
                return first != pageView;
            }
            var atk = NativeLibrary.Load("libatk-1.0.so.0");
            var accessible = Gtk3("gtk_widget_get_accessible")(overlay);
            if (((delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(atk, "atk_object_get_n_accessible_children"))(accessible) != 2) return null;
            var child = ((delegate* unmanaged[Cdecl]<nint, int, nint>)NativeLibrary.GetExport(atk, "atk_object_ref_accessible_child"))(accessible, 0);
            ((delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(NativeLibrary.Load("libgobject-2.0.so.0"), "g_object_unref"))(child);
            return child != Gtk3("gtk_widget_get_accessible")(pageView);
        }

        private static nint Overlay(nint window, bool gtk4)
        {
            if (gtk4)
            {
                var child = Gtk4("gtk_window_get_child")(window);
                return IsA(child, Gtk4Type("gtk_box_get_type"), "libgtk-4.so.1") ? Gtk4("gtk_widget_get_last_child")(child) : child;
            }
            var content = Gtk3("gtk_bin_get_child")(window);
            return IsA(content, Gtk3Type("gtk_box_get_type"), "libgtk-3.so.0") ? Gtk3Children(content)[^1] : content;
        }

        private static List<nint> Gtk3Children(nint container)
        {
            var gtk = NativeLibrary.Load("libgtk-3.so.0");
            var list = ((delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(gtk, "gtk_container_get_children"))(container);
            List<nint> children = [];
            for (var node = list; node != 0; node = ((nint*)node)[1]) children.Add(((nint*)node)[0]);
            ((delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(gtk, "g_list_free"))(list);
            return children;
        }

        private static delegate* unmanaged[Cdecl]<nint, nint> Gtk3(string name) =>
            (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(NativeLibrary.Load("libgtk-3.so.0"), name);

        private static delegate* unmanaged[Cdecl]<nint, nint> Gtk4(string name) =>
            (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(NativeLibrary.Load("libgtk-4.so.1"), name);

        private static nuint Gtk3Type(string name) => ((delegate* unmanaged[Cdecl]<nuint>)NativeLibrary.GetExport(NativeLibrary.Load("libgtk-3.so.0"), name))();

        private static nuint Gtk4Type(string name) => ((delegate* unmanaged[Cdecl]<nuint>)NativeLibrary.GetExport(NativeLibrary.Load("libgtk-4.so.1"), name))();

        private static bool IsA(nint instance, nuint type, string _) => instance != 0 &&
            ((delegate* unmanaged[Cdecl]<nint, nuint, int>)NativeLibrary.GetExport(NativeLibrary.Load("libgobject-2.0.so.0"), "g_type_check_instance_is_a"))(instance, type) != 0;
    }

    private static async Task<DesktopSidePanel> OpenQuietlyAsync(DesktopWindow window)
    {
        var result = await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Width = PanelWidth, Path = "#side-panel" });
        return (result as DesktopSidePanelResult.Opened)?.Panel ?? throw new InvalidOperationException($"Side panel: expected it to open, not {result}.");
    }

    // Samples the row through the page's left edge once per DWM frame while operation runs, and counts the frames in which
    // the edge was not at its place.
    private static async Task CaptureAsync(string what, (int X, int Y)? found, Func<Task> operation)
    {
        if (found is not { } marker)
        {
            await operation();
            await Task.Delay(Settle);
            Console.WriteLine($"Side panel: {what} without frame capture (unverified on this runner).");
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

    // The baseline: the page's magenta marker in the row through the middle of the window's visible frame, read before
    // any side-panel operation, or null when GDI does not show it there: then this runner's desktop cannot be captured
    // (the arm64 runner's reads nothing, garbage or a blank white row), and frame capture is unverified.
    private static (int X, int Y)? FindBaselineMarker(DesktopWindowBounds visible)
    {
        var y = visible.Y!.Value + visible.Height / 2;
        var row = Screen.Row(visible.X!.Value, y, visible.Width);
        var colors = row?.Select(static pixel => pixel & 0xFFFFFF).Distinct().Count() ?? 0;
        if (row is not null && colors > 1 && Screen.MarkerIn(row) is { } x)
        {
            Console.WriteLine($"Side panel: baseline: the page's left edge is at screen x {visible.X.Value + x}, row {y}; frame capture is on.");
            return (visible.X.Value + x, y);
        }
        Console.WriteLine($"Side panel: frame capture unverified on this runner: before any side-panel operation GDI reads " +
            $"{(row is null ? "nothing (BitBlt failed)" : $"{colors} colour(s) and no marker")} in the row {y} of the window {visible}.");
        return null;
    }

    // Once the baseline found the marker, it must be in the same place whenever the window is back at its start bounds.
    private static void RequireMarker((int X, int Y) baseline)
    {
        var found = Screen.MarkerIn(baseline.X - 40, baseline.Y, 80) is { } x ? baseline.X - 40 + x : (int?)null;
        Require(found == baseline.X,
            $"the page's left-edge marker at screen x {baseline.X} in the row {baseline.Y}, where the baseline found it, not {found?.ToString(CultureInfo.InvariantCulture) ?? "missing"}");
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

    // Whether the panel page that closes itself may call window.close().
    private static bool CloseNow;

    // The window whose state a timeout reports.
    private static DesktopWindow? LastWindow;

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
        throw new TimeoutException($"Side panel: expected {expected}{(LastWindow?.State is { } state ? $"; the window's last state is {state}" : "")}.");
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Side panel: expected " + what + ".");
    }

    // Tab through SendInput on Windows, through XTEST on X11 (the test display's keyboard, never the host's).
    private static class Keyboard
    {
        private const ushort Tab = 0x09;
        private const ushort Shift = 0x10;

        internal static void Press(bool tab, bool shift = false)
        {
            if (!tab) return;
            if (!OperatingSystem.IsWindows())
            {
                XTest.Press(0xff09, shift);
                return;
            }
            if (shift) Send(Shift, up: false);
            Send(Tab, up: false);
            Send(Tab, up: true);
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

    private static unsafe class XTest
    {
        internal static void Press(uint keysym, bool shift)
        {
            var x11 = NativeLibrary.TryLoad(Environment.GetEnvironmentVariable("RUNIC_LIBX11") ?? "libX11.so.6", out var loaded) ? loaded : NativeLibrary.Load("libX11.so.6");
            var xtest = NativeLibrary.TryLoad(Environment.GetEnvironmentVariable("RUNIC_LIBXTST") ?? "libXtst.so.6", out var test) ? test : NativeLibrary.Load("libXtst.so.6");
            var display = ((delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(x11, "XOpenDisplay"))(0);
            if (display == 0) throw new InvalidOperationException("Side panel: cannot open the X display.");
            try
            {
                var toKeycode = (delegate* unmanaged[Cdecl]<nint, nuint, byte>)NativeLibrary.GetExport(x11, "XKeysymToKeycode");
                var fake = (delegate* unmanaged[Cdecl]<nint, uint, int, nuint, int>)NativeLibrary.GetExport(xtest, "XTestFakeKeyEvent");
                var key = toKeycode(display, keysym);
                var shiftKey = toKeycode(display, 0xffe1);
                if (shift) _ = fake(display, shiftKey, 1, 0);
                _ = fake(display, key, 1, 0);
                _ = fake(display, key, 0, 0);
                if (shift) _ = fake(display, shiftKey, 0, 0);
                _ = ((delegate* unmanaged[Cdecl]<nint, int, int>)NativeLibrary.GetExport(x11, "XSync"))(display, 0);
            }
            finally { _ = ((delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(x11, "XCloseDisplay"))(display); }
        }
    }

    // One row of the composed screen through GDI, which reads what DWM presented.
    private static unsafe class Screen
    {
        // The offset of the first magenta pixel in the row of width pixels from (x, y), or null.
        internal static int? MarkerIn(int x, int y, int width) => Row(x, y, width) is { } row ? MarkerIn(row) : null;

        internal static int? MarkerIn(uint[] pixels)
        {
            for (var i = 0; i < pixels.Length; i++)
            {
                var (red, green, blue) = ((pixels[i] >> 16) & 0xFF, (pixels[i] >> 8) & 0xFF, pixels[i] & 0xFF);
                if (red > 200 && green < 80 && blue > 200) return i;
            }
            return null;
        }

        // The row of width screen pixels from (x, y) as 0x00RRGGBB, or null when GDI cannot copy it.
        internal static uint[]? Row(int x, int y, int width)
        {
            var screen = Win32.GetDC(0);
            var memory = Win32.CreateCompatibleDC(screen);
            var bitmap = Win32.CreateCompatibleBitmap(screen, width, 1);
            var previous = Win32.SelectObject(memory, bitmap);
            try
            {
                // SRCCOPY | CAPTUREBLT.
                if (screen == 0 || memory == 0 || bitmap == 0
                    || Win32.BitBlt(memory, 0, 0, width, 1, screen, x, y, 0x00CC0020 | 0x40000000) == 0) return null;
                var pixels = new uint[width];
                var header = new BitmapInfoHeader { Size = (uint)sizeof(BitmapInfoHeader), Width = width, Height = -1, Planes = 1, BitCount = 32 };
                fixed (uint* data = pixels)
                {
                    _ = Win32.SelectObject(memory, previous);
                    if (Win32.GetDIBits(memory, bitmap, 0, 1, data, &header, 0) == 0) return null;
                }
                return pixels;
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
