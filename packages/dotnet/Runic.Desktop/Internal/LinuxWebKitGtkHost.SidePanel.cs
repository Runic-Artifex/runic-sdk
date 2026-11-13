using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// sdk#864, P4: the side panel on WebKitGTK 4.1 and GTK 3 (side-panel/DESIGN.md §1.2, §1.7). The window's content is a
// GtkOverlay whose children are the page's view and, while a panel exists, the panel's view; its get-child-position
// handler places both from the overlay's width and SidePanelLayout, so neither view sizes the window. The panel is a
// related view of the page (same web context and web process) with a user content manager of its own, which carries
// the panel's credential and none of the page's scripts or message handlers. It loads outside the content, where the
// overlay clips it, and the window grows only once it loaded: gtk_window_move and gtk_window_resize in one main-loop
// turn, which GTK sends as one configure request. The layout follows at the first allocation with the new width.
// Wayland gives no positions, so the left edge is refused there (D062.4).
internal sealed partial class LinuxWebKitGtkHost
{
    // How long a panel change waits for the window manager to give the window its new bounds before it commits anyway.
    private static readonly TimeSpan PanelCommitTimeout = TimeSpan.FromSeconds(1);
    private static readonly Lazy<PanelApi> PanelNative = new(static () => new());

    // GTK thread only: the overlay that holds the views, the panel that is loading or open, and the layout.
    private nint _overlay;
    private SidePanel? _panel;
    private readonly SidePanelLayout _layout = new();
    private int _commitGeneration;
    // The page's script that hands Tab to the panel, while one is open.
    private nint _pageHandOff;
    // The minimum size DesktopWindowOptions or SetMinimumSizeAsync asked for, zero for none; an open panel adds its width.
    private int _minimumWidth;
    private int _minimumHeight;
    // The bounds a window restores to after its panel closed while it was maximized, full-screen or minimized.
    private DesktopWindowPlacement? _pendingRestore;
    // Serializes the panel's open, move, width change and close, which wait for the window manager.
    private readonly SemaphoreSlim _panelGate = new(1, 1);
    // Any thread: whether a panel is open, and the work area of the window's monitor, for CheckSidePanel.
    private int _panelOpen;
    private StrongBox<ScreenRect>? _workArea;

    public bool SupportsSidePanel => PanelNative.Value.IsAvailable && Api.CanReportState && Volatile.Read(ref _overlay) != 0;

    // From the last reported state, on any thread.
    public DesktopSidePanelRefusal? CheckSidePanel(bool left, int width)
    {
        if (Volatile.Read(ref _panelOpen) != 0) return DesktopSidePanelRefusal.AlreadyOpen;
        if (State is not { Bounds: { } bounds } state || _options?.EphemeralDataStore is not null) return DesktopSidePanelRefusal.Unsupported;
        var activity = Activity;
        var window = new SidePanelWindow(bounds, state.VisibleBounds, Volatile.Read(ref _workArea)?.Value,
            state.IsMaximized, state.IsFullScreen || _options?.Kiosk == true, state.IsSnapped,
            activity is { IsMinimized: true } or { IsVisible: false });
        return SidePanelPlanner.Open(window, left, width).Refusal;
    }

    public async ValueTask<WebUiSidePanelOpening> OpenSidePanelAsync(WebUiSidePanelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _panelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await OpenPanelAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _panelGate.Release();
        }
    }

    private async Task<WebUiSidePanelOpening> OpenPanelAsync(WebUiSidePanelRequest request, CancellationToken cancellationToken)
    {
        SidePanel? panel = null;
        DesktopSidePanelRefusal? refusal = null;
        await InvokeWindowAsync(() =>
        {
            if (_panel is not null) refusal = DesktopSidePanelRefusal.AlreadyOpen;
            else if (_overlay == 0 || _options is not { EphemeralDataStore: null }) refusal = DesktopSidePanelRefusal.Unsupported;
            else if (SidePanelPlanner.Open(ReadPanelWindow(), request.Left, request.Width).Refusal is { } early) refusal = early;
            else panel = CreatePanel(request);
        }, cancellationToken).ConfigureAwait(false);
        if (refusal is { } refused) return WebUiSidePanelOpening.Refused(refused);
        var opening = panel!;

        bool loaded;
        try
        {
            loaded = await WhileOpenAsync(opening.Loaded.Task.WaitAsync(request.LoadTimeout, cancellationToken)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            loaded = false;
        }
        catch (OperationCanceledException)
        {
            await DiscardPanelAsync(opening).ConfigureAwait(false);
            throw;
        }
        Task? committed = null;
        await InvokeWindowAsync(() =>
        {
            if (opening.IsClosed) throw new InvalidOperationException("The embedded WebView window closed while the side panel opened.");
            if (!loaded)
            {
                refusal = DesktopSidePanelRefusal.LoadFailed;
                DiscardPanel(opening);
                return;
            }
            // The window may have changed while the page loaded.
            var window = ReadPanelWindow();
            var plan = SidePanelPlanner.Open(window, request.Left, request.Width);
            if (plan.Refusal is { } late)
            {
                refusal = late;
                DiscardPanel(opening);
                return;
            }
            opening.Before = window.Bounds;
            opening.NativeWidth = plan.NativeWidth;
            committed = _layout.BeginOpen(Api.WidgetAllocatedWidth(_overlay), request.Left, plan.NativeWidth);
            ApplyPanelBounds(plan.Target!);
        }, CancellationToken.None).ConfigureAwait(false);
        if (refusal is { } after) return WebUiSidePanelOpening.Refused(after);

        await WhileOpenAsync(committed!).ConfigureAwait(false);
        await InvokeWindowAsync(() =>
        {
            if (opening.IsClosed) throw new InvalidOperationException("The embedded WebView window closed while the side panel opened.");
            opening.IsShown = true;
            opening.Opened = CurrentBounds();
            OrderPanel(opening);
            SetPageHandOff(true);
            ApplyMinimumSize();
            UpdateState();
            Volatile.Write(ref _panelOpen, 1);
        }, CancellationToken.None).ConfigureAwait(false);
        return new WebUiSidePanelOpening(opening, null);
    }

    // GTK thread: the panel's view beside the page's, loading its page outside the content (SidePanelLayout).
    private unsafe SidePanel CreatePanel(WebUiSidePanelRequest request)
    {
        var options = _options!;
        var view = PanelNative.Value.NewRelatedView(_webView);
        if (view == 0) throw new InvalidOperationException("WebKitGTK could not create the side panel's view.");
        var panel = new SidePanel(this, request, view);
        _panel = panel;
        try
        {
            // The panel credential and the keyboard script, in the panel's own user content manager; the panel always
            // hands Tab to the page.
            Api.AddDocumentStartScript(view, request.DocumentStartScript);
            var manager = Api.AddKeyboardScript(view);
            PanelNative.Value.AddKeyboardWorldScript(manager, HandOffScript(true));
            Api.Connect(manager, "script-message-received::" + WebKitGtkKeyboard.ZoomMessageHandler,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnPanelZoomMessage, this);
            // The menu bar's shortcuts and the zoom keys; the panel's zoom is its own.
            Api.Connect(view, "key-press-event", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnUnhandledKeyPress, this, after: true);
            Api.Connect(view, "web-process-terminated", (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&OnPanelProcessTerminated, this);
            Api.Connect(view, "close", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnPanelCloseRequested, this);
            // Before the web content's load-failed, which can stop the emission.
            Api.Connect(view, "load-failed", (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, nint, nint, int>)&OnPanelLoadFailed, this);
            Api.Connect(view, "load-changed", (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&OnPanelLoadChanged, this);
            // Drops from outside the panel are refused in this version (D062.6); drags inside its page still work.
            Api.Connect(view, "drag-motion", (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, int, uint, nint, int>)&OnPanelDragMotion, this);
            Api.Connect(view, "drag-drop", (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, int, uint, nint, int>)&OnPanelDragDrop, this);
            // The window's web content policy, with the panel's own pending navigations. No title, permission or media
            // frame handlers: the title is the page's, and WebKit denies the requests no handler allows.
            panel.WebContent = new WebKitGtkWebContent(new WebContentRules(options.WebContent, request.Url, options.AdditionalOrigins),
                Api.CanIdentifyMainFrame);
            Api.SetDeveloperExtras(view, panel.WebContent.Rules.DevToolsEnabled);
            Api.Connect(view, "decide-policy", (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, nint, int>)&OnDecidePolicy, this);
            Api.Connect(view, "create", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)&OnCreate, this);
            Api.Connect(view, "load-failed", (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, nint, nint, int>)&OnLoadFailed, this);
            if (LayoutDirection != 0) Api.GtkWidgetSetDirection(view, LayoutDirection);
            _layout.BeginLoading(request.Left, request.Width);
            PanelNative.Value.OverlayAddOverlay(_overlay, view);
            PanelNative.Value.WidgetShow(view);
            PanelNative.Value.QueueResize(_overlay);
            Api.LoadUri(view, request.Url.AbsoluteUri);
            return panel;
        }
        catch
        {
            DiscardPanel(panel);
            throw;
        }
    }

    // GTK thread, from Create: the overlay that holds the page's view, which fills it while no panel shows. Without the
    // functions the panel needs, and in a transparent window, whose view draws on the window's RGBA visual, the view stays
    // the window's content as before and the window refuses the panel (Unsupported).
    private unsafe nint CreateOverlay()
    {
        if (!PanelNative.Value.IsAvailable || !Api.CanReportState || _options?.Transparent == true) return _webView;
        var overlay = PanelNative.Value.OverlayNew();
        if (overlay == 0) return _webView;
        PanelNative.Value.OverlayAddOverlay(overlay, _webView);
        Api.Connect(overlay, "get-child-position",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, PanelApi.GdkRectangle*, nint, int>)&OnOverlayChildPosition, this);
        Volatile.Write(ref _overlay, overlay);
        return overlay;
    }

    // Waits for task unless the window closes first, which then fails the operation.
    private async Task<T> WhileOpenAsync<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, _closed.Task).ConfigureAwait(false) != task)
            throw new InvalidOperationException("The embedded WebView window closed while the side panel changed.");
        return await task.ConfigureAwait(false);
    }

    private async Task WhileOpenAsync(Task task)
    {
        if (await Task.WhenAny(task, _closed.Task).ConfigureAwait(false) != task)
            throw new InvalidOperationException("The embedded WebView window closed while the side panel changed.");
        await task.ConfigureAwait(false);
    }

    // GTK thread: the window as SidePanelPlanner reads it, from the window itself.
    private SidePanelWindow ReadPanelWindow()
    {
        UpdateState();
        var state = Volatile.Read(ref _state) ?? throw new InvalidOperationException("The embedded WebView window reports no state.");
        var flags = Api.GetWindowStateFlags(_window);
        var minimized = (flags & GdkWindowStateIconified) != 0 || !Api.GtkWidgetGetVisible(_window);
        return new SidePanelWindow(state.Bounds!, state.VisibleBounds, Volatile.Read(ref _workArea)?.Value,
            state.IsMaximized, state.IsFullScreen || _options?.Kiosk == true, state.IsSnapped, minimized);
    }

    // GTK thread: the window's bounds now, in logical pixels.
    private DesktopWindowBounds CurrentBounds()
    {
        var (width, height) = Api.GtkWindowGetSize(_window);
        int? x = null, y = null;
        if (!Api.IsWaylandDisplay()) (x, y) = Api.GtkWindowGetPosition(_window);
        return new DesktopWindowBounds { X = x, Y = y, Width = Math.Max(1, width), Height = Math.Max(1, height), Scale = 1 };
    }

    // GTK thread, from UpdateState: the work area of the window's monitor.
    private void RecordWorkArea()
    {
        if (Api.MonitorWorkArea(Api.MonitorOfWindow(_window)) is not { } work) return;
        if (Volatile.Read(ref _workArea)?.Value != work) Volatile.Write(ref _workArea, new StrongBox<ScreenRect>(work));
    }

    // GTK thread: the bounds of a panel change in one main-loop turn, which GTK sends as one configure request, and the
    // commit after PanelCommitTimeout when the window manager does not give them.
    private void ApplyPanelBounds(DesktopWindowBounds target)
    {
        if (target is { X: { } x, Y: { } y }) Api.GtkWindowMove(_window, x, y);
        Api.GtkWindowResize(_window, target.Width, target.Height);
        var generation = ++_commitGeneration;
        _ = Task.Delay(PanelCommitTimeout).ContinueWith(_ =>
        {
            if (!IsOpen) return;
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (generation != _commitGeneration || !_layout.IsPending || _overlay == 0) return;
                if (_panel is { } moving) moving.Moving = null;
                _layout.Commit();
                PanelNative.Value.QueueResize(_overlay);
            }, CancellationToken.None);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // GTK thread, from configure-event: a move of the window to the other edge commits once the window is there.
    private void OnPanelConfigure()
    {
        if (_panel is not { Moving: { } target } panel || Api.IsWaylandDisplay()) return;
        if (Api.GtkWindowGetPosition(_window).X != target) return;
        panel.Moving = null;
        _layout.CommitMove();
        PanelNative.Value.QueueResize(_overlay);
    }

    // AT-SPI lists the overlay's children in their order, so the panel comes before the page on the left edge and after it
    // on the right: the reading order is the visual order.
    private void OrderPanel(SidePanel panel) => PanelNative.Value.OverlayReorder(_overlay, panel.View, panel.IsLeft ? 0 : -1);

    // GTK thread: while a panel is open, Tab that leaves the page goes to the panel (OnWindowFocus) instead of wrapping in
    // the page; a script of the page's user content manager keeps this across the page's loads.
    private void SetPageHandOff(bool on)
    {
        var manager = Api.WebKitWebViewGetUserContentManager(_webView);
        if (on && _pageHandOff == 0) _pageHandOff = PanelNative.Value.AddKeyboardWorldScript(manager, HandOffScript(true), keep: true);
        if (!on && _pageHandOff != 0)
        {
            PanelNative.Value.RemoveScript(manager, _pageHandOff);
            _pageHandOff = 0;
        }
        PanelNative.Value.RunInKeyboardWorld(_webView, HandOffScript(on));
    }

    private static string HandOffScript(bool on) => $"runicDesktopKeyboard.setHandOff({(on ? "true" : "false")});";

    // GTK thread, from OnWindowFocus: Tab that leaves one view focuses the other one's first control, Shift+Tab its last.
    private bool HandFocus(nint focused, bool backward)
    {
        if (_panel is not { IsShown: true } panel) return false;
        var target = focused == _webView ? panel.View : focused == panel.View ? _webView : 0;
        if (target == 0) return false;
        Api.GtkWidgetGrabFocus(target);
        PanelNative.Value.RunInKeyboardWorld(target, $"runicDesktopKeyboard.enter({(backward ? "true" : "false")});");
        return true;
    }

    // The window's size request: the minimum it was given, and with an open panel the panel's width beside the page.
    private void ApplyMinimumSize()
    {
        if (_window == 0) return;
        var panel = _panel is { IsShown: true } && _layout.IsShown ? _layout.Width : 0;
        var width = _minimumWidth > 0 ? _minimumWidth + panel : panel > 0 ? panel + 1 : -1;
        Api.GtkWidgetSetSizeRequest(_window, width, _minimumHeight > 0 ? _minimumHeight : -1);
    }

    // The placement applications save excludes an open panel (side-panel/DESIGN.md §3.1).
    private DesktopWindowPlacement ExcludePanel(DesktopWindowPlacement current)
    {
        if (_panel is not { IsShown: true } panel || !_layout.IsShown) return current;
        var bounds = new DesktopWindowBounds { X = current.X, Y = current.Y, Width = current.Width, Height = current.Height, Scale = 1 };
        var excluded = SidePanelPlanner.ExcludePanel(bounds, panel.IsLeft, _layout.Width);
        return current with { X = excluded.X, Width = excluded.Width };
    }

    // Closes the panel and shrinks the window back (SidePanelPlanner.Close), or for a maximized, minimized or full-screen
    // window the size it restores to. Reports the close once.
    private async Task ClosePanelAsync(SidePanel panel, DesktopSidePanelCloseReason reason)
    {
        await _panelGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsOpen) return;
            Task? committed = null;
            var closing = false;
            await InvokeWindowAsync(() =>
            {
                if (!ReferenceEquals(_panel, panel) || panel.IsClosed) return;
                closing = true;
                if (!panel.IsShown) return;
                SetPageHandOff(false);
                var flags = Api.GetWindowStateFlags(_window);
                if ((flags & (GdkWindowStateIconified | GdkWindowStateMaximized | GdkWindowStateFullscreen)) != 0
                    || Api.GtkWindowIsMaximized(_window) || _options?.Kiosk == true)
                {
                    ShrinkRestoredBounds();
                    _layout.CloseNow();
                    PanelNative.Value.QueueResize(_overlay);
                    return;
                }
                var target = SidePanelPlanner.Close(CurrentBounds(), panel.Opened!, panel.Before!, panel.IsLeft, panel.NativeWidth);
                committed = _layout.BeginClose(Api.WidgetAllocatedWidth(_overlay));
                ApplyPanelBounds(target);
            }, CancellationToken.None).ConfigureAwait(false);
            if (!closing) return;
            if (committed is not null) await WhileOpenAsync(committed).ConfigureAwait(false);
            await InvokeWindowAsync(() =>
            {
                if (panel.IsClosed) return;
                var shown = panel.IsShown;
                DiscardPanel(panel);
                ApplyMinimumSize();
                UpdateState();
                if (shown) panel.Request.Closed(reason);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (!IsOpen)
        {
            // The window closed meanwhile, and reported the panel's close (ClosePanelWithWindow).
        }
        finally
        {
            _panelGate.Release();
        }
    }

    // The size a maximized or full-screen window restores to loses the panel's width: the placement already excludes it.
    // The window manager keeps the geometry a maximized or full-screen window restores to and ignores a resize requested
    // meanwhile, so the bounds apply when the window is restored (ApplyPendingRestore, D062.5).
    private void ShrinkRestoredBounds() => _pendingRestore = _normal;

    // GTK thread, from UpdateState of a window that is neither maximized, full-screen nor minimized: a panel closed while
    // it was gives it the restored bounds without the panel, in one configure request. True when it changed the window,
    // whose next configure-event reports the new state.
    private bool ApplyPendingRestore(DesktopWindowPlacement current)
    {
        if (_pendingRestore is not { } restore) return false;
        _pendingRestore = null;
        var wayland = Api.IsWaylandDisplay();
        var moves = !wayland && restore is { X: not null, Y: not null } && (restore.X != current.X || restore.Y != current.Y);
        if (!moves && restore.Width == current.Width && restore.Height == current.Height) return false;
        if (moves) Api.GtkWindowMove(_window, restore.X!.Value, restore.Y!.Value);
        Api.GtkWindowResize(_window, restore.Width, restore.Height);
        return true;
    }

    private async Task DiscardPanelAsync(SidePanel panel)
    {
        try { await Dispatcher.InvokeAsync(() => DiscardPanel(panel), CancellationToken.None).ConfigureAwait(false); }
        catch (InvalidOperationException) { }
    }

    // GTK thread: destroys the panel's view and forgets it, without changing the window or reporting.
    private void DiscardPanel(SidePanel panel)
    {
        var shown = panel.IsShown;
        panel.IsShown = false;
        panel.IsClosed = true;
        panel.Loaded.TrySetResult(false);
        if (ReferenceEquals(_panel, panel))
        {
            _panel = null;
            Volatile.Write(ref _panelOpen, 0);
            _layout.Discard();
            if (shown && _webView != 0) SetPageHandOff(false);
        }
        if (panel.View != 0)
        {
            // As for the page's view (HandleDestroyed): its children first, while it still lists them.
            Api.DestroyWebViewChildren(panel.View);
            Api.GtkWidgetDestroy(panel.View);
            panel.View = 0;
        }
        if (_overlay != 0) PanelNative.Value.QueueResize(_overlay);
    }

    // GTK thread, when the window is destroyed: the panel goes with it.
    private void ClosePanelWithWindow()
    {
        if (_panel is not { } panel) return;
        var shown = panel.IsShown;
        panel.IsShown = false;
        panel.IsClosed = true;
        panel.Loaded.TrySetResult(false);
        _panel = null;
        Volatile.Write(ref _panelOpen, 0);
        _layout.Discard();
        if (panel.View != 0) Api.DestroyWebViewChildren(panel.View);
        panel.View = 0;
        if (shown) panel.Request.Closed(DesktopSidePanelCloseReason.WindowClosed);
    }

    // The overlay places the page and the panel (SidePanelLayout).
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnOverlayChildPosition(nint overlay, nint widget, PanelApi.GdkRectangle* allocation, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not LinuxWebKitGtkHost host) return 0;
            var width = Api.WidgetAllocatedWidth(overlay);
            var height = Math.Max(1, Api.WidgetAllocatedHeight(overlay));
            var (pageX, pageWidth, panelX, panelWidth) = host._layout.Allocate(width);
            if (widget == host._webView)
            {
                *allocation = new PanelApi.GdkRectangle { X = pageX, Y = 0, Width = Math.Max(1, pageWidth), Height = height };
                return 1;
            }
            if (host._panel is { } panel && widget == panel.View)
            {
                *allocation = new PanelApi.GdkRectangle { X = panelX, Y = 0, Width = Math.Max(1, panelWidth), Height = height };
                return 1;
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPanelZoomMessage(nint manager, nint message, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost { _panel: { View: not 0 } panel })
            {
                var command = WebKitGtkKeyboard.GetWheelZoomCommand(Api.GetMessageInt32(message));
                if (command != PageZoomCommand.None) Api.SetZoomLevel(panel.View, WebKitGtkKeyboard.Apply(command, Api.GetZoomLevel(panel.View)));
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    // A crashed panel page reloads once; a second crash closes the panel. The panel shares the page's web process, so a
    // crash usually takes both, and the page reloads as before (OnWebProcessTerminated).
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPanelProcessTerminated(nint webView, int reason, nint context)
    {
        const int TerminatedByApi = 2;
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not LinuxWebKitGtkHost { IsOpen: true } host
                || host._panel is not { IsClosed: false } panel || panel.View != webView || reason == TerminatedByApi) return;
            if (!panel.IsShown)
            {
                panel.Loaded.TrySetResult(false);
            }
            else if (++panel.Crashes > 1)
            {
                _ = host.ClosePanelAsync(panel, DesktopSidePanelCloseReason.Crashed);
            }
            else
            {
                Api.Reload(webView);
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    // window.close() in the panel's page closes the panel, never the window.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPanelCloseRequested(nint webView, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost { IsOpen: true } host
                && host._panel is { IsShown: true } panel && panel.View == webView)
            {
                _ = host.ClosePanelAsync(panel, DesktopSidePanelCloseReason.Closed);
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPanelLoadFailed(nint webView, int loadEvent, nint failingUri, nint error, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost { _panel: { } panel } && panel.View == webView) panel.LoadFailed = true;
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
        return 0;
    }

    // The panel's first load decides whether it opens.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPanelLoadChanged(nint webView, int loadEvent, nint context)
    {
        const int LoadFinished = 3;
        try
        {
            if (loadEvent == LoadFinished && GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost { _panel: { } panel }
                && panel.View == webView)
            {
                panel.Loaded.TrySetResult(!panel.LoadFailed);
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    // A drag from outside the panel's view gets no drop action, and WebKit's handlers do not run.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPanelDragMotion(nint widget, nint context, int x, int y, uint time, nint userData)
    {
        try
        {
            if (PanelNative.Value.DragSourceWidget(context) == widget) return 0;
            PanelNative.Value.DragStatus(context, time);
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPanelDragDrop(nint widget, nint context, int x, int y, uint time, nint userData)
    {
        try
        {
            if (PanelNative.Value.DragSourceWidget(context) == widget) return 0;
            PanelNative.Value.DragFinish(context, time);
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    // The web content policy of the view a signal came from: the panel's, or the page's.
    private WebKitGtkWebContent? WebContentOf(nint webView) =>
        _panel is { WebContent: { } panel, View: var view } && view == webView ? panel : _webContent;

    // A panel from its request until it closed. GTK thread only, except the read-only members of IWebUiSidePanel.
    private sealed class SidePanel(LinuxWebKitGtkHost host, WebUiSidePanelRequest request, nint view) : IWebUiSidePanel
    {
        private int _width = request.Width;
        private int _left = request.Left ? 1 : 0;

        internal WebUiSidePanelRequest Request { get; } = request;
        internal nint View { get; set; } = view;
        internal WebKitGtkWebContent? WebContent { get; set; }
        internal TaskCompletionSource<bool> Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool LoadFailed { get; set; }
        // Logical pixels; GTK has no other unit.
        internal int NativeWidth { get; set; } = request.Width;
        internal bool IsShown { get; set; }
        internal bool IsClosed { get; set; }
        internal int Crashes { get; set; }
        // The x a move to the other edge waits for (OnPanelConfigure).
        internal int? Moving { get; set; }
        // The window's bounds without the panel, and the bounds the last open, move or width change gave it.
        internal DesktopWindowBounds? Before { get; set; }
        internal DesktopWindowBounds? Opened { get; set; }

        public bool IsLeft
        {
            get => Volatile.Read(ref _left) != 0;
            private set => Volatile.Write(ref _left, value ? 1 : 0);
        }

        public int Width
        {
            get => Volatile.Read(ref _width);
            private set => Volatile.Write(ref _width, value);
        }

        public ValueTask<DesktopSidePanelRefusal?> MoveAsync(bool left, CancellationToken cancellationToken) =>
            ChangeAsync(window =>
            {
                if (left == IsLeft) return (null, null, null);
                var plan = SidePanelPlanner.Move(window, left, NativeWidth);
                if (plan.Refusal is { } refused) return (refused, null, null);
                Rebase(window.Bounds);
                Moving = plan.Target!.X;
                var moved = host._layout.BeginMove(left);
                host.ApplyPanelBounds(plan.Target);
                return (null, () => IsLeft = left, moved);
            }, cancellationToken);

        public ValueTask<DesktopSidePanelRefusal?> SetWidthAsync(int width, CancellationToken cancellationToken) =>
            ChangeAsync(window =>
            {
                var plan = SidePanelPlanner.Resize(window, IsLeft, NativeWidth, width);
                if (plan.Refusal is { } refused) return (refused, null, null);
                Rebase(window.Bounds);
                var resized = host._layout.BeginResize(Api.WidgetAllocatedWidth(host._overlay), plan.NativeWidth);
                host.ApplyPanelBounds(plan.Target!);
                return (null, () =>
                {
                    NativeWidth = plan.NativeWidth;
                    Width = width;
                }, resized);
            }, cancellationToken);

        public ValueTask FocusAsync(CancellationToken cancellationToken) =>
            host.InvokeWindowAsync(() =>
            {
                RequireOpen();
                Api.GtkWidgetGrabFocus(View);
            }, cancellationToken);

        public ValueTask CloseAsync(CancellationToken cancellationToken) =>
            host.IsOpen ? new ValueTask(host.ClosePanelAsync(this, DesktopSidePanelCloseReason.Closed).WaitAsync(cancellationToken)) : ValueTask.CompletedTask;

        // A move or width change: plans it on the GTK thread, waits for the window manager, then records the new bounds.
        private async ValueTask<DesktopSidePanelRefusal?> ChangeAsync(
            Func<SidePanelWindow, (DesktopSidePanelRefusal? Refusal, Action? Applied, Task? Committed)> change,
            CancellationToken cancellationToken)
        {
            await host._panelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                (DesktopSidePanelRefusal? Refusal, Action? Applied, Task? Committed) result = default;
                await host.InvokeWindowAsync(() =>
                {
                    RequireOpen();
                    result = change(host.ReadPanelWindow());
                }, cancellationToken).ConfigureAwait(false);
                if (result.Committed is { } committed)
                {
                    await host.WhileOpenAsync(committed).ConfigureAwait(false);
                    await host.InvokeWindowAsync(() =>
                    {
                        Moving = null;
                        result.Applied?.Invoke();
                        Opened = host.CurrentBounds();
                        host.OrderPanel(this);
                        host.ApplyMinimumSize();
                        host.UpdateState();
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                return result.Refusal;
            }
            finally
            {
                host._panelGate.Release();
            }
        }

        private void RequireOpen() => ObjectDisposedException.ThrowIf(IsClosed || !IsShown, typeof(DesktopSidePanel));

        // The user moved or resized the window since the panel last changed it: the bounds without the panel follow.
        private void Rebase(DesktopWindowBounds current)
        {
            if (Opened is { } opened && SidePanelPlanner.SameFrame(current, opened)) return;
            Before = SidePanelPlanner.ExcludePanel(current, IsLeft, NativeWidth);
        }
    }

    // The GTK 3 and WebKitGTK functions only the side panel uses. Available when every one of them is.
    internal sealed unsafe class PanelApi
    {
        private readonly nint _overlayNew, _overlayAddOverlay, _overlayReorder, _queueResize, _widgetShow;
        private readonly nint _dragGetSourceWidget, _dragStatus, _dragFinish;
        private readonly nint _objectNewWithProperties, _valueInit, _valueSetObject, _valueUnset, _objectUnref;
        private readonly nint _webViewGetType, _contentManagerGetType, _contentManagerNew, _contentManagerAddScript, _contentManagerRemoveScript;
        private readonly nint _userScriptNewForWorld, _userScriptUnref;
        // WebKitGTK 2.40, and the older call it replaced.
        private readonly nint _evaluateJavascript, _runJavascriptInWorld;

        internal PanelApi()
        {
            if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libgtk-3.so.0", out var gtk)
                || !NativeLibrary.TryLoad("libgobject-2.0.so.0", out var gobject)
                || !NativeLibrary.TryLoad("libwebkit2gtk-4.1.so.0", out var webkit)) return;
            _overlayNew = Export(gtk, "gtk_overlay_new");
            _overlayAddOverlay = Export(gtk, "gtk_overlay_add_overlay");
            _overlayReorder = Export(gtk, "gtk_overlay_reorder_overlay");
            _queueResize = Export(gtk, "gtk_widget_queue_resize");
            _widgetShow = Export(gtk, "gtk_widget_show");
            _dragGetSourceWidget = Export(gtk, "gtk_drag_get_source_widget");
            _dragStatus = Export(gtk, "gdk_drag_status");
            _dragFinish = Export(gtk, "gtk_drag_finish");
            _objectNewWithProperties = Export(gobject, "g_object_new_with_properties");
            _valueInit = Export(gobject, "g_value_init");
            _valueSetObject = Export(gobject, "g_value_set_object");
            _valueUnset = Export(gobject, "g_value_unset");
            _objectUnref = Export(gobject, "g_object_unref");
            _webViewGetType = Export(webkit, "webkit_web_view_get_type");
            _contentManagerGetType = Export(webkit, "webkit_user_content_manager_get_type");
            _contentManagerNew = Export(webkit, "webkit_user_content_manager_new");
            _contentManagerAddScript = Export(webkit, "webkit_user_content_manager_add_script");
            _contentManagerRemoveScript = Export(webkit, "webkit_user_content_manager_remove_script");
            _userScriptNewForWorld = Export(webkit, "webkit_user_script_new_for_world");
            _userScriptUnref = Export(webkit, "webkit_user_script_unref");
            _evaluateJavascript = Export(webkit, "webkit_web_view_evaluate_javascript");
            _runJavascriptInWorld = Export(webkit, "webkit_web_view_run_javascript_in_world");
            IsAvailable = _overlayNew != 0 && _overlayAddOverlay != 0 && _overlayReorder != 0 && _queueResize != 0 && _widgetShow != 0
                && _dragGetSourceWidget != 0 && _dragStatus != 0 && _dragFinish != 0 && _objectNewWithProperties != 0 && _valueInit != 0
                && _valueSetObject != 0 && _valueUnset != 0 && _objectUnref != 0 && _webViewGetType != 0 && _contentManagerGetType != 0
                && _contentManagerNew != 0 && _contentManagerAddScript != 0 && _contentManagerRemoveScript != 0 && _userScriptNewForWorld != 0
                && _userScriptUnref != 0 && (_evaluateJavascript != 0 || _runJavascriptInWorld != 0);
        }

        internal bool IsAvailable { get; }

        private static nint Export(nint library, string name) => NativeLibrary.TryGetExport(library, name, out var address) ? address : 0;

        internal nint OverlayNew() => ((delegate* unmanaged[Cdecl]<nint>)_overlayNew)();

        internal void OverlayAddOverlay(nint overlay, nint widget) => ((delegate* unmanaged[Cdecl]<nint, nint, void>)_overlayAddOverlay)(overlay, widget);

        internal void OverlayReorder(nint overlay, nint widget, int index)
        {
            if (overlay != 0 && widget != 0) ((delegate* unmanaged[Cdecl]<nint, nint, int, void>)_overlayReorder)(overlay, widget, index);
        }

        internal void QueueResize(nint widget)
        {
            if (widget != 0) ((delegate* unmanaged[Cdecl]<nint, void>)_queueResize)(widget);
        }

        internal void WidgetShow(nint widget) => ((delegate* unmanaged[Cdecl]<nint, void>)_widgetShow)(widget);

        internal nint DragSourceWidget(nint context) => ((delegate* unmanaged[Cdecl]<nint, nint>)_dragGetSourceWidget)(context);

        // No action: the drag cannot drop here.
        internal void DragStatus(nint context, uint time) => ((delegate* unmanaged[Cdecl]<nint, int, uint, void>)_dragStatus)(context, 0, time);

        internal void DragFinish(nint context, uint time) => ((delegate* unmanaged[Cdecl]<nint, int, int, uint, void>)_dragFinish)(context, 0, 0, time);

        // A view related to page, sharing its web context and web process, with a user content manager of its own:
        // webkit_web_view_new_with_related_view would share the page's, and with it the page's credential and handlers.
        internal nint NewRelatedView(nint page)
        {
            var manager = ((delegate* unmanaged[Cdecl]<nint>)_contentManagerNew)();
            if (manager == 0) return 0;
            using var relatedName = new Utf8("related-view");
            using var managerName = new Utf8("user-content-manager");
            var names = stackalloc nint[2];
            names[0] = (nint)relatedName.Pointer;
            names[1] = (nint)managerName.Pointer;
            var values = stackalloc GValue[2];
            new Span<GValue>(values, 2).Clear();
            var viewType = ((delegate* unmanaged[Cdecl]<nuint>)_webViewGetType)();
            ((delegate* unmanaged[Cdecl]<GValue*, nuint, GValue*>)_valueInit)(&values[0], viewType);
            ((delegate* unmanaged[Cdecl]<GValue*, nint, void>)_valueSetObject)(&values[0], page);
            ((delegate* unmanaged[Cdecl]<GValue*, nuint, GValue*>)_valueInit)(&values[1], ((delegate* unmanaged[Cdecl]<nuint>)_contentManagerGetType)());
            ((delegate* unmanaged[Cdecl]<GValue*, nint, void>)_valueSetObject)(&values[1], manager);
            try
            {
                return ((delegate* unmanaged[Cdecl]<nuint, uint, nint*, GValue*, nint>)_objectNewWithProperties)(viewType, 2, names, values);
            }
            finally
            {
                ((delegate* unmanaged[Cdecl]<GValue*, void>)_valueUnset)(&values[0]);
                ((delegate* unmanaged[Cdecl]<GValue*, void>)_valueUnset)(&values[1]);
                // The view holds the manager.
                ((delegate* unmanaged[Cdecl]<nint, void>)_objectUnref)(manager);
            }
        }

        // A top-frame script at document start in the keyboard script's world, after the scripts added before it. With
        // keep, returns the script for RemoveScript, which releases it; otherwise 0.
        internal nint AddKeyboardWorldScript(nint manager, string source, bool keep = false)
        {
            using var text = new Utf8(source);
            using var world = new Utf8(WebKitGtkKeyboard.ScriptWorld);
            // WEBKIT_USER_CONTENT_INJECT_TOP_FRAME (1), WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START (0).
            var script = ((delegate* unmanaged[Cdecl]<byte*, int, int, byte*, nint, nint, nint>)_userScriptNewForWorld)(
                text.Pointer, 1, 0, world.Pointer, 0, 0);
            if (script == 0) throw new InvalidOperationException("WebKitGTK could not create the side panel's keyboard script.");
            ((delegate* unmanaged[Cdecl]<nint, nint, void>)_contentManagerAddScript)(manager, script);
            if (keep) return script;
            ((delegate* unmanaged[Cdecl]<nint, void>)_userScriptUnref)(script);
            return 0;
        }

        internal void RemoveScript(nint manager, nint script)
        {
            if (manager != 0) ((delegate* unmanaged[Cdecl]<nint, nint, void>)_contentManagerRemoveScript)(manager, script);
            ((delegate* unmanaged[Cdecl]<nint, void>)_userScriptUnref)(script);
        }

        // Runs source in the view's document, in the keyboard script's world, without waiting for its result.
        internal void RunInKeyboardWorld(nint webView, string source)
        {
            if (webView == 0) return;
            using var text = new Utf8(source);
            using var world = new Utf8(WebKitGtkKeyboard.ScriptWorld);
            if (_evaluateJavascript != 0)
            {
                ((delegate* unmanaged[Cdecl]<nint, byte*, nint, byte*, nint, nint, nint, nint, void>)_evaluateJavascript)(
                    webView, text.Pointer, -1, world.Pointer, 0, 0, 0, 0);
            }
            else
            {
                ((delegate* unmanaged[Cdecl]<nint, byte*, byte*, nint, nint, nint, void>)_runJavascriptInWorld)(
                    webView, text.Pointer, world.Pointer, 0, 0, 0);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct GdkRectangle
        {
            internal int X;
            internal int Y;
            internal int Width;
            internal int Height;
        }

        // GType g_type; union { gint64 v_int64; gpointer v_pointer; ... } data[2].
        [StructLayout(LayoutKind.Sequential)]
        private struct GValue
        {
            internal nuint Type;
            internal long Data0;
            internal long Data1;
        }

        private sealed class Utf8(string value) : IDisposable
        {
            private readonly nint _memory = Marshal.StringToCoTaskMemUTF8(value);

            internal byte* Pointer => (byte*)_memory;

            public void Dispose() => Marshal.FreeCoTaskMem(_memory);
        }
    }
}
