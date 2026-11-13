using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// sdk#864, P3: the side panel on WKWebView (side-panel/DESIGN.md §1.2, Option A). A second WKWebView with the page's
// website data store, in the window's content view beside the page's. It loads just outside the content view's bounds,
// where the window clips it but WebKit renders it, and only after its first navigation finished does the window grow,
// in one setFrame:display:, inside which both views get their frames (ApplyPanelBounds): the page keeps its width and
// its place on the screen. Afterwards the autoresizing masks give a user resize, zoom or full screen to the page, and
// the panel keeps its width (D062.5).
//
// - Focus: as WebView2's MoveFocusRequested, Tab past a document's last tab stop focuses the other view's first, and
//   Shift+Tab before its first the other's last. WebKit hands focus out of a WKWebView only through the private
//   _webView:takeFocus: (its key-view fallback leaves no element focused, osx-arm64), so FocusScript, in a content world
//   of its own in both views, reports a Tab that left its document, and MoveFocusOut moves the first responder and
//   focuses the tab stop. The views are also each other's next key view, for AppKit's own key-view loop.
// - Accessibility: the content view's children are listed in subview order, so the panel is below the page on the left
//   edge and above it on the right: the reading order is the visual order.
// - The panel's view shares the window's delegates through the WebViews map; PanelViews tells them it is the panel.
//   Its title does not reach the window (no title observer), its drag regions are its own, drops from outside it are
//   refused, window.close() closes it, and its own web content process ends with a reload once and a close the second
//   time (D062.6, §3.1).
internal sealed partial class MacOsWkWebViewHost
{
    private const ulong ViewMinXMargin = 1;
    private const ulong ViewMaxXMargin = 4;
    private const long WindowBelow = -1;
    private const long WindowAbove = 1;
    // NSURLErrorCancelled: a navigation that another one replaced, not a failure of the panel's page.
    private const long UrlErrorCancelled = -999;

    private static readonly ConcurrentDictionary<nint, SidePanel> PanelViews = new();

    // Main thread only: the panel that is loading or open; the page's minimum content size without a panel; the frame a
    // zoomed or full-screen window restores to, as it was when it was zoomed or entered full screen; and after a panel of
    // a maximized, full-screen or minimized window closed, the bounds that window restores to with the panel and the
    // bounds without it, which it gets when it is restored there (ShrinkRestoredFrame).
    private SidePanel? _panel;
    private (double Width, double Height)? _pageMinimum;
    private DesktopWindowBounds? _restoredFrame;
    private (DesktopWindowBounds Expected, DesktopWindowBounds Shrunk)? _restoreShrink;
    // Any thread: whether a panel is open, and the visible frame of the window's screen in Runic's coordinates, for
    // CheckSidePanel.
    private int _panelOpen;
    private StrongBox<ScreenRect>? _workArea;

    public bool SupportsSidePanel => true;

    // From the last reported state, on any thread.
    public DesktopSidePanelRefusal? CheckSidePanel(bool left, int width)
    {
        if (Volatile.Read(ref _panelOpen) != 0) return DesktopSidePanelRefusal.AlreadyOpen;
        if (State is not { Bounds: { } bounds } state || Volatile.Read(ref _ephemeralDataStore) is not null) return DesktopSidePanelRefusal.Unsupported;
        var window = new SidePanelWindow(bounds, state.VisibleBounds, Volatile.Read(ref _workArea)?.Value,
            state.IsMaximized, state.IsFullScreen || _kiosk, state.IsSnapped, Activity is { IsMinimized: true } or { IsVisible: false });
        return SidePanelPlanner.Open(window, left, width).Refusal;
    }

    public async ValueTask<WebUiSidePanelOpening> OpenSidePanelAsync(WebUiSidePanelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        SidePanel? panel = null;
        DesktopSidePanelRefusal? refusal = null;
        await InvokeAsync(() => panel = BeginPanel(request, out refusal), cancellationToken).ConfigureAwait(false);
        if (panel is null) return WebUiSidePanelOpening.Refused(refusal ?? DesktopSidePanelRefusal.Unsupported);

        bool loaded;
        try
        {
            var load = panel.Loaded.Task.WaitAsync(request.LoadTimeout, cancellationToken);
            // A window that closes meanwhile discarded the panel (ClosePanelWithWindow).
            if (await Task.WhenAny(load, _closed.Task).ConfigureAwait(false) != load)
                throw new InvalidOperationException("The embedded WebView window closed while the side panel opened.");
            loaded = await load.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            loaded = false;
        }
        catch
        {
            await DiscardOnMainAsync(panel).ConfigureAwait(false);
            throw;
        }

        WebUiSidePanelOpening opening = default;
        try
        {
            await InvokeAsync(() => opening = FinishPanel(panel, loaded), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await DiscardOnMainAsync(panel).ConfigureAwait(false);
            throw;
        }
        return opening;
    }

    // Main thread: the panel's view, outside the content view's bounds, loading the panel's page; null with the refusal
    // when the window rules a panel out now.
    private SidePanel? BeginPanel(WebUiSidePanelRequest request, out DesktopSidePanelRefusal? refusal)
    {
        refusal = null;
        if (_panel is not null)
        {
            refusal = DesktopSidePanelRefusal.AlreadyOpen;
            return null;
        }
        if (_webView == 0 || _ephemeralDataStore is not null || _webContentDelegate == 0)
        {
            refusal = DesktopSidePanelRefusal.Unsupported;
            return null;
        }
        var plan = SidePanelPlanner.Open(ReadPanelWindow(), request.Left, request.Width);
        if (plan.Refusal is { } early)
        {
            refusal = early;
            return null;
        }

        var panel = new SidePanel(this, request, plan.NativeWidth);
        var content = Api.SendNint(_window, "contentView");
        var bounds = Api.SendRect(content, "bounds");
        // The page's profile: its website data store (macOS 12 shares one web content process pool anyway).
        var store = Api.SendNint(Api.SendNint(_webView, "configuration"), "websiteDataStore");
        var view = Api.AllocInitWebView(new CGRect(bounds.X + bounds.Width, bounds.Y, panel.NativeWidth, bounds.Height),
            request.DocumentStartScript, WebViewClass, fileDropHandler: 0,
            WebViewClass != 0 && _usesDragRegions ? WindowChromeHandler.Value : 0, store, FocusHandler.Value);
        if (view == 0) throw new InvalidOperationException("WebKit could not create the side panel's WKWebView.");
        panel.View = view;
        _panel = panel;
        try
        {
            WebViews[view] = this;
            PanelViews[view] = panel;
            // Without Runic's WKWebView subclass the panel cannot tell drops from outside apart: it takes none.
            if (WebViewClass == 0) Api.SendVoid(view, "unregisterDraggedTypes");
            if (_webContent is { } rules && Api.RespondsToSelector(view, "setInspectable:"))
                Api.SendVoidBool(view, "setInspectable:", rules.DevToolsEnabled);
            Api.SendVoidNint(view, "setNavigationDelegate:", _webContentDelegate);
            Api.SendVoidNint(view, "setUIDelegate:", _webContentDelegate);
            Api.SendVoidBool(view, "setAllowsMagnification:", true);
            Api.SendVoidUlong(view, "setAutoresizingMask:", ViewMinXMargin | ViewHeightSizable);
            Api.SendVoidNint(content, "addSubview:", view);
            Navigate(request.Url, view);
        }
        catch
        {
            DiscardPanel(panel);
            throw;
        }
        return panel;
    }

    // Main thread, once the panel's page loaded or did not: grows the window by the panel, or discards it.
    private WebUiSidePanelOpening FinishPanel(SidePanel panel, bool loaded)
    {
        if (panel.IsClosed || !ReferenceEquals(_panel, panel))
            throw new InvalidOperationException("The embedded WebView window closed while the side panel opened.");
        if (!loaded)
        {
            DiscardPanel(panel);
            return WebUiSidePanelOpening.Refused(DesktopSidePanelRefusal.LoadFailed);
        }
        // The window may have changed while the page loaded.
        var window = ReadPanelWindow();
        var plan = SidePanelPlanner.Open(window, panel.Left, panel.Request.Width);
        if (plan.Refusal is { } late)
        {
            DiscardPanel(panel);
            return WebUiSidePanelOpening.Refused(late);
        }
        panel.Before = window.Bounds;
        _restoreShrink = null;
        ApplyPanelBounds(plan.Target!, () =>
        {
            panel.NativeWidth = plan.NativeWidth;
            panel.IsShown = true;
        });
        panel.Opened = CurrentBounds();
        OrderPanel(panel);
        Api.SendVoidNint(_webView, "setNextKeyView:", panel.View);
        Api.SendVoidNint(panel.View, "setNextKeyView:", _webView);
        Volatile.Write(ref _panelOpen, 1);
        return new WebUiSidePanelOpening(panel, null);
    }

    private async Task DiscardOnMainAsync(SidePanel panel)
    {
        if (!IsOpen) return;
        try { await InvokeAsync(() => DiscardPanel(panel), CancellationToken.None).ConfigureAwait(false); }
        catch (InvalidOperationException) { /* The window closed and discarded it. */ }
    }

    // Main thread: the window as SidePanelPlanner reads it, from the window itself.
    private SidePanelWindow ReadPanelWindow()
    {
        UpdateState();
        var state = Volatile.Read(ref _state) ?? throw new InvalidOperationException("The embedded WebView window reports no state.");
        var minimized = Api.SendBool(_window, "isMiniaturized") || !Api.SendBool(_window, "isVisible");
        return new SidePanelWindow(state.Bounds!, state.VisibleBounds, Volatile.Read(ref _workArea)?.Value,
            state.IsMaximized, state.IsFullScreen || _kiosk || _fullScreenTransition, state.IsSnapped, minimized);
    }

    // Main thread: the frame as bounds.
    private DesktopWindowBounds CurrentBounds() => ReadExactBounds(maximized: false, fullScreen: false).Bounds;

    // Main thread, from UpdateState: the visible frame of the window's screen, without the menu bar and the Dock.
    private void RecordWorkArea()
    {
        var screen = Api.SendNint(_window, "screen");
        if (screen == 0) return;
        var visible = Api.SendRect(screen, "visibleFrame");
        var rect = MacOsCoordinates.ToTopLeft(visible.X, visible.Y, visible.Width, visible.Height, PrimaryScreenHeight());
        var work = new ScreenRect(rect.X, rect.Y, rect.Width, rect.Height);
        if (Volatile.Read(ref _workArea)?.Value != work) Volatile.Write(ref _workArea, new StrongBox<ScreenRect>(work));
    }

    // Main thread, from UpdateState: the placement that applications save excludes an open panel (DESIGN.md §3.1).
    private DesktopWindowPlacement ExcludePanel(DesktopWindowPlacement placement)
    {
        if (_panel is not { IsShown: true } panel) return placement;
        var width = Math.Max(1, placement.Width - panel.NativeWidth);
        var removed = placement.Width - width;
        return panel.Left && placement.X is { } x ? placement with { X = x + removed, Width = width } : placement with { Width = width };
    }

    // Main thread, from UpdateState of a window that is neither maximized nor full-screen: once a window whose panel closed
    // while it was maximized, full-screen or minimized is back at the bounds it had with the panel, the panel's width
    // leaves its docked edge (side-panel/DESIGN.md §3.1: at the next windowDidResize: after leaving zoom). A frame on the
    // way there, as a zoom animation's, does not match.
    private void ShrinkRestoredFrame(DesktopWindowBounds bounds)
    {
        if (_restoreShrink is not { } shrink || !SidePanelPlanner.SameFrame(bounds, shrink.Expected)) return;
        _restoreShrink = null;
        SetFrame(bounds, shrink.Shrunk);
    }

    // Main thread, as the window zooms or enters full screen from a frame of its own: the frame it restores to.
    private void RecordRestoredFrame()
    {
        if (!IsOpen || IsFullScreen || Api.SendBool(_window, "isZoomed")) return;
        _restoredFrame = CurrentBounds();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte WindowShouldZoom(nint self, nint selector, nint window, CGRect frame)
    {
        try
        {
            if (Hosts.TryGetValue(window, out var host)) host.RecordRestoredFrame();
        }
        catch
        {
            // Never unwind through AppKit.
        }
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FullScreenEntering(nint self, nint selector, nint notification)
    {
        try
        {
            if (Hosts.TryGetValue(Api.SendNint(notification, "object"), out var host))
            {
                host.RecordRestoredFrame();
                host._fullScreenTransition = true;
            }
        }
        catch
        {
            // Never unwind through AppKit.
        }
    }

    // Main thread: one setFrame:display: to target, after change updated the panel's state, with both views laid out
    // for the new frame before AppKit draws it. The autoresizing masks are off meanwhile, so they do not distribute the
    // difference.
    private void ApplyPanelBounds(DesktopWindowBounds target, Action change)
    {
        var current = CurrentBounds();
        change();
        ApplyContentMinimum();
        var content = Api.SendNint(_window, "contentView");
        Api.SendVoidBool(content, "setAutoresizesSubviews:", false);
        try
        {
            SetFrame(current, target);
            LayoutViews();
        }
        finally
        {
            Api.SendVoidBool(content, "setAutoresizesSubviews:", true);
        }
        UpdateState();
    }

    // Main thread: the window's frame moved and resized by the difference between current and target bounds.
    private void SetFrame(DesktopWindowBounds current, DesktopWindowBounds target)
    {
        var frame = Api.SendRect(_window, "frame");
        var (x, y, width, height) = MacOsSidePanelLayout.Frame((frame.X, frame.Y, frame.Width, frame.Height), current, target);
        Api.SendVoidRectBool(_window, "setFrame:display:", new CGRect(x, y, width, height), true);
    }

    // Main thread: the page and the panel from the content view's bounds and the panel's state; a loading panel lies
    // just outside the bounds, where the window clips it.
    private void LayoutViews()
    {
        if (_webView == 0 || _window == 0) return;
        var bounds = Api.SendRect(Api.SendNint(_window, "contentView"), "bounds");
        if (_panel is not { View: not 0 } panel || !panel.IsShown)
        {
            Api.SendVoidRect(_webView, "setFrame:", bounds);
            if (_panel is { View: not 0 } loading)
            {
                Api.SendVoidRect(loading.View, "setFrame:", new CGRect(bounds.X + bounds.Width, bounds.Y, loading.NativeWidth, bounds.Height));
                Api.SendVoidUlong(loading.View, "setAutoresizingMask:", ViewMinXMargin | ViewHeightSizable);
            }
            return;
        }
        var layout = MacOsSidePanelLayout.Layout(bounds.X, bounds.Width, panel.Left, panel.NativeWidth);
        Api.SendVoidRect(_webView, "setFrame:", new CGRect(layout.PageX, bounds.Y, layout.PageWidth, bounds.Height));
        Api.SendVoidRect(panel.View, "setFrame:", new CGRect(layout.PanelX, bounds.Y, layout.PanelWidth, bounds.Height));
        // A user resize, zoom or full screen keeps the panel's width on its edge, and the page takes the rest.
        Api.SendVoidUlong(panel.View, "setAutoresizingMask:", (panel.Left ? ViewMaxXMargin : ViewMinXMargin) | ViewHeightSizable);
    }

    // Main thread: the panel's view comes before the page's on the left edge and after it on the right, so accessibility
    // reads them in visual order. The first responder stays.
    private void OrderPanel(SidePanel panel)
    {
        var responder = Api.SendNint(_window, "firstResponder");
        Api.SendVoidNintLongNint(Api.SendNint(_window, "contentView"), "addSubview:positioned:relativeTo:", panel.View,
            panel.Left ? WindowBelow : WindowAbove, _webView);
        if (responder != 0 && Api.SendNint(_window, "firstResponder") != responder) Api.SendNintNint(_window, "makeFirstResponder:", responder);
    }

    // Main thread: the content view's minimum size is the page's and, while a panel is shown, its width beside it.
    private void ApplyContentMinimum()
    {
        if (_window == 0) return;
        var page = _pageMinimum ??= ReadContentMinimum();
        var panel = _panel is { IsShown: true } shown ? shown.NativeWidth : 0;
        Api.SendVoidSize(_window, "setContentMinSize:", new CGSize(page.Width + panel, page.Height));
    }

    private (double Width, double Height) ReadContentMinimum()
    {
        var size = Api.SendSize(_window, "contentMinSize");
        return (size.Width, size.Height);
    }

    // Main thread: closes the panel and shrinks the window back (SidePanelPlanner.Close); a maximized, full-screen or
    // minimized window keeps its frame and shrinks once it is restored (ShrinkRestoredFrame). Reports the close once.
    private void ClosePanel(SidePanel panel, DesktopSidePanelCloseReason reason)
    {
        if (!ReferenceEquals(_panel, panel) || panel.IsClosed) return;
        if (panel.IsShown && _window != 0 && IsOpen)
        {
            var zoomedOrFullScreen = IsFullScreen || _fullScreenTransition || _kiosk || Api.SendBool(_window, "isZoomed");
            if (zoomedOrFullScreen || Api.SendBool(_window, "isMiniaturized"))
            {
                // A minimized window restores to its frame; a zoomed or full-screen one to the frame it had before.
                var restored = zoomedOrFullScreen ? _restoredFrame : CurrentBounds();
                if (restored is not null && !_kiosk)
                    _restoreShrink = (restored, SidePanelPlanner.ExcludePanel(restored, panel.Left, panel.NativeWidth));
                HidePanel(panel);
                ApplyContentMinimum();
                LayoutViews();
            }
            else
            {
                var current = CurrentBounds();
                var target = SidePanelPlanner.Close(current, panel.Opened!, panel.Before!, panel.Left, panel.NativeWidth);
                ApplyPanelBounds(target, () => HidePanel(panel));
            }
        }
        DiscardPanel(panel);
        UpdateState();
        panel.Request.Closed(reason);
    }

    // Main thread: out of the layout; the page takes the content view at the next layout.
    private static void HidePanel(SidePanel panel)
    {
        panel.IsShown = false;
        if (panel.View != 0) Api.SendVoidBool(panel.View, "setHidden:", true);
    }

    // Main thread: releases the panel's view and forgets it, without changing the window's frame or reporting.
    private void DiscardPanel(SidePanel panel)
    {
        panel.IsShown = false;
        panel.IsClosed = true;
        panel.Loaded.TrySetResult(false);
        if (panel.View is var view and not 0)
        {
            panel.View = 0;
            PanelViews.TryRemove(view, out _);
            WebViews.TryRemove(view, out _);
            if (_webView != 0 && Api.SendNint(_webView, "nextKeyView") == view) Api.SendVoidNint(_webView, "setNextKeyView:", 0);
            if (_window != 0 && Api.SendNint(_window, "firstResponder") == view && _webView != 0) Api.SendNintNint(_window, "makeFirstResponder:", _webView);
            Api.SendVoidNint(view, "setNextKeyView:", 0);
            // WebKit holds its delegates weakly; the window's object outlives the view, but the view stops asking it.
            Api.SendVoidNint(view, "setNavigationDelegate:", 0);
            Api.SendVoidNint(view, "setUIDelegate:", 0);
            Api.SendVoid(view, "stopLoading");
            Api.SendVoid(view, "removeFromSuperview");
            Api.SendVoid(view, "release");
        }
        if (ReferenceEquals(_panel, panel))
        {
            _panel = null;
            Volatile.Write(ref _panelOpen, 0);
            ApplyContentMinimum();
            LayoutViews();
        }
    }

    // Main thread, as the window closes (HandleClosed): the panel goes with it.
    private void ClosePanelWithWindow()
    {
        _restoreShrink = null;
        if (_panel is not { } panel) return;
        var opened = panel.IsShown;
        DiscardPanel(panel);
        if (opened) panel.Request.Closed(DesktopSidePanelCloseReason.WindowClosed);
    }

    // The panel's web view whose drop should be refused: one that comes from outside the panel's own page.
    private static bool RefusesDrop(nint view, nint draggingInfo) =>
        PanelViews.ContainsKey(view) && (draggingInfo == 0 || Api.SendNint(draggingInfo, "draggingSource") != view);

    // Main thread: the drag regions the panel's page reported, or the page's.
    private MacOsWindowChrome.DragRegions DragRegionsOf(nint webView) =>
        PanelViews.TryGetValue(webView, out var panel) ? Volatile.Read(ref panel.DragRegions) : _dragRegions;

    // From the window chrome script of webView's main frame.
    private void ReportDragRegions(nint webView, MacOsWindowChrome.DragRegions regions)
    {
        if (PanelViews.TryGetValue(webView, out var panel)) Volatile.Write(ref panel.DragRegions, regions);
        else Volatile.Write(ref _dragRegions, regions);
    }

    // WKNavigationDelegate, main thread: the panel's page finished or failed its load.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void NavigationFinished(nint self, nint selector, nint webView, nint navigation)
    {
        try
        {
            if (PanelViews.TryGetValue(webView, out var panel)) panel.Loaded.TrySetResult(true);
        }
        catch
        {
            // Never unwind through WebKit.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void NavigationFailed(nint self, nint selector, nint webView, nint navigation, nint error)
    {
        try
        {
            if (PanelViews.TryGetValue(webView, out var panel) && (error == 0 || (long)Api.SendNint(error, "code") != UrlErrorCancelled))
                panel.Loaded.TrySetResult(false);
        }
        catch
        {
            // Never unwind through WebKit.
        }
    }

    // WKUIDelegate: window.close() in the panel's page closes the panel, after WebKit's callback returned. The page's
    // window.close() does nothing, as before.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WebViewDidClose(nint self, nint selector, nint webView)
    {
        try
        {
            if (PanelViews.TryGetValue(webView, out var panel))
                MacOsMainThread.Post(() =>
                {
                    if (panel.IsShown) panel.Host.ClosePanel(panel, DesktopSidePanelCloseReason.Closed);
                });
        }
        catch
        {
            // Never unwind through WebKit.
        }
    }

    // webViewWebContentProcessDidTerminate: of the panel's view: a crashed page reloads once, a second crash closes the
    // panel with Crashed, after WebKit's callback returned.
    private static bool PanelProcessTerminated(nint webView)
    {
        if (!PanelViews.TryGetValue(webView, out var panel)) return false;
        MacOsMainThread.Post(() =>
        {
            if (panel.IsClosed || !ReferenceEquals(panel.Host._panel, panel)) return;
            if (!panel.IsShown)
            {
                // Still loading: the open fails.
                panel.Loaded.TrySetResult(false);
            }
            else if (++panel.Crashes > 1)
            {
                panel.Host.ClosePanel(panel, DesktopSidePanelCloseReason.Crashed);
            }
            else
            {
                _ = Api.SendNint(panel.View, "reload");
            }
        });
        return true;
    }

    private const string FocusMessageHandler = "runicSidePanelFocus";
    private const string FocusContentWorld = "runic-desktop-side-panel-focus";
    private static readonly Lazy<nint> FocusHandler = new(CreateFocusHandler);

    // Runs at document start in the main frame of the page's and the panel's view, in its own content world. After a
    // trusted Tab or Shift+Tab that the page did not cancel, a document whose focus left it (WebKit clears the focused
    // element when Tab passes the last tab stop or Shift+Tab the first) reports "next" or "previous". Focus moving into
    // a frame of the page leaves the frame element active, which is not reported.
    internal const string FocusScript = """
        (() => {
          "use strict";
          const handler = globalThis.webkit?.messageHandlers?.runicSidePanelFocus;
          if (!handler) return;
          addEventListener("keydown", event => {
            if (event.key !== "Tab" || !event.isTrusted || event.altKey || event.ctrlKey || event.metaKey) return;
            const direction = event.shiftKey ? "previous" : "next";
            setTimeout(() => {
              if (event.defaultPrevented) return;
              const active = document.activeElement;
              if (active && active !== document.body && active !== document.documentElement) return;
              handler.postMessage(direction);
            }, 0);
          }, true);
        })();
        """;

    // The first (forward) or last tab stop of a document, in tab order: positive tabindex values first, then document
    // order; it is focused.
    private static string FocusEdgeScript(bool forward) => $$"""
        (() => {
          const candidates = [...document.querySelectorAll('a[href],area[href],button,input,select,textarea,iframe,summary,audio[controls],video[controls],[tabindex],[contenteditable]:not([contenteditable="false"])')]
            .filter(element => element.tabIndex >= 0 && !element.disabled && element.type !== "hidden" && element.getClientRects().length > 0
              && !element.closest("[inert]"));
          const ordered = [...candidates.filter(element => element.tabIndex > 0).sort((a, b) => a.tabIndex - b.tabIndex),
            ...candidates.filter(element => element.tabIndex === 0)];
          const target = {{(forward ? "ordered[0]" : "ordered[ordered.length - 1]")}};
          if (target) target.focus();
          return target ? target.id || target.tagName : "";
        })();
        """;

    // Main thread: Tab left view's document forward or backward; the other view of the window takes the focus at its
    // first or last tab stop. Without a shown panel nothing changes, as before.
    private void MoveFocusOut(nint view, bool forward)
    {
        if (!IsOpen || _panel is not { IsShown: true, View: not 0 } panel) return;
        var target = view == panel.View ? _webView : view == _webView ? panel.View : 0;
        if (target == 0) return;
        Api.SendNintNint(_window, "makeFirstResponder:", target);
        var script = Api.CreateString(FocusEdgeScript(forward));
        try
        {
            Api.SendVoidNintNint(target, "evaluateJavaScript:completionHandler:", script, 0);
        }
        finally
        {
            Api.SendVoid(script, "release");
        }
    }

    private static unsafe nint CreateFocusHandler()
    {
        if (!OperatingSystem.IsMacOS() || !Api.IsAvailable) return 0;
        var type = Api.GetClass("RunicDesktopSidePanelFocusHandler");
        if (type == 0)
        {
            type = Api.AllocateClassPair(Api.GetClass("NSObject"), "RunicDesktopSidePanelFocusHandler");
            if (type == 0) return 0;
            Api.AddMethod(type, "userContentController:didReceiveScriptMessage:",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&DidReceiveFocusMessage, "v@:@@");
            Api.RegisterClassPair(type);
        }
        return Api.SendNint(Api.SendNint(type, "alloc"), "init");
    }

    // The focus script's report, from a main frame; the focus moves after WebKit's callback returned.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidReceiveFocusMessage(nint self, nint selector, nint controller, nint message)
    {
        try
        {
            var webView = Api.SendNint(message, "webView");
            if (!WebViews.TryGetValue(webView, out var host) || !host.IsOpen) return;
            var frame = Api.SendNint(message, "frameInfo");
            if (frame != 0 && !Api.SendBool(frame, "isMainFrame")) return;
            var body = Api.SendNint(message, "body");
            var direction = Api.IsString(body) ? Api.Text(body) : null;
            if (direction is not ("next" or "previous")) return;
            MacOsMainThread.Post(() => host.MoveFocusOut(webView, direction == "next"));
        }
        catch
        {
            // Never unwind through WebKit.
        }
    }

    // A panel from its request until it closed. Main thread only, except the read-only members of IWebUiSidePanel.
    private sealed class SidePanel(MacOsWkWebViewHost host, WebUiSidePanelRequest request, int nativeWidth) : IWebUiSidePanel
    {
        private int _width = request.Width;
        private int _left = request.Left ? 1 : 0;

        internal MacOsWkWebViewHost Host { get; } = host;
        internal WebUiSidePanelRequest Request { get; } = request;
        internal TaskCompletionSource<bool> Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal nint View { get; set; }
        // In points: macOS never scales the panel's width.
        internal int NativeWidth { get; set; } = nativeWidth;
        internal bool IsShown { get; set; }
        internal bool IsClosed { get; set; }
        internal int Crashes { get; set; }
        internal MacOsWindowChrome.DragRegions DragRegions = MacOsWindowChrome.DragRegions.Empty;
        // The window's bounds without the panel, and the bounds the last open, move or width change gave it.
        internal DesktopWindowBounds? Before { get; set; }
        internal DesktopWindowBounds? Opened { get; set; }

        public bool IsLeft
        {
            get => Volatile.Read(ref _left) != 0;
            private set => Volatile.Write(ref _left, value ? 1 : 0);
        }

        internal bool Left => IsLeft;

        public int Width
        {
            get => Volatile.Read(ref _width);
            private set => Volatile.Write(ref _width, value);
        }

        public async ValueTask<DesktopSidePanelRefusal?> MoveAsync(bool left, CancellationToken cancellationToken)
        {
            DesktopSidePanelRefusal? refusal = null;
            await Host.InvokeAsync(() =>
            {
                RequireOpen();
                if (left == IsLeft) return;
                var window = Host.ReadPanelWindow();
                var plan = SidePanelPlanner.Move(window, left, NativeWidth);
                if (plan.Refusal is { } refused)
                {
                    refusal = refused;
                    return;
                }
                Rebase(window.Bounds);
                Host.ApplyPanelBounds(plan.Target!, () => IsLeft = left);
                Opened = Host.CurrentBounds();
                Host.OrderPanel(this);
            }, cancellationToken).ConfigureAwait(false);
            return refusal;
        }

        public async ValueTask<DesktopSidePanelRefusal?> SetWidthAsync(int width, CancellationToken cancellationToken)
        {
            DesktopSidePanelRefusal? refusal = null;
            await Host.InvokeAsync(() =>
            {
                RequireOpen();
                var window = Host.ReadPanelWindow();
                var plan = SidePanelPlanner.Resize(window, IsLeft, NativeWidth, width);
                if (plan.Refusal is { } refused)
                {
                    refusal = refused;
                    return;
                }
                Rebase(window.Bounds);
                Host.ApplyPanelBounds(plan.Target!, () =>
                {
                    NativeWidth = plan.NativeWidth;
                    Width = width;
                });
                Opened = Host.CurrentBounds();
            }, cancellationToken).ConfigureAwait(false);
            return refusal;
        }

        public ValueTask FocusAsync(CancellationToken cancellationToken) =>
            Host.InvokeAsync(() =>
            {
                RequireOpen();
                Api.SendNintNint(Host._window, "makeFirstResponder:", View);
            }, cancellationToken);

        public ValueTask CloseAsync(CancellationToken cancellationToken) =>
            Host.IsOpen
                ? Host.InvokeAsync(() => Host.ClosePanel(this, DesktopSidePanelCloseReason.Closed), cancellationToken)
                : ValueTask.CompletedTask;

        private void RequireOpen()
        {
            ObjectDisposedException.ThrowIf(IsClosed || !IsShown, typeof(DesktopSidePanel));
        }

        // The user moved or resized the window since the panel last changed it: the bounds without the panel follow.
        private void Rebase(DesktopWindowBounds current)
        {
            if (Opened is { } opened && SidePanelPlanner.SameFrame(current, opened)) return;
            Before = SidePanelPlanner.ExcludePanel(current, IsLeft, NativeWidth);
        }
    }

    private sealed unsafe partial class ObjC
    {
        internal CGSize SendSize(nint receiver, string selector) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, CGSize>)_messageSend)(receiver, Selector(selector));

        internal void SendVoidNintLongNint(nint receiver, string selector, nint first, long second, nint third) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, long, nint, void>)_messageSend)(receiver, Selector(selector), first, second, third);
    }
}
