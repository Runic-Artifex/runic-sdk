using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Desktop.Internal;
using WebKit;

namespace Runic.Desktop.Gtk4;

// sdk#864, P4: the side panel on WebKitGTK 6 and GTK 4 (side-panel/DESIGN.md §1.2), through Runic Desktop's internal
// side panel seam (IDesktopSidePanelWindowHost). As on GTK 3 (LinuxWebKitGtkHost.SidePanel.cs): a GtkOverlay holds the
// page's view and the panel's, and places both from its width and SidePanelLayout; the panel is a related view of the
// page with a user content manager of its own; it loads outside the content and the window grows once it loaded, on X11
// with the default size and one XMoveResizeWindow (as SetBoundsAsync), on Wayland with the default size alone, so the
// left edge is refused there (D062.4). GTK's focus chain moves Tab between the two views, which are both focusable; the
// keyboard scripts hand the focus off instead of wrapping inside their page, and the view that receives the focus after
// a Tab focuses its first control, or its last after Shift+Tab (WatchFocus).
internal sealed partial class Gtk4WindowHost : IDesktopSidePanelWindowHost
{
    private static readonly TimeSpan PanelCommitTimeout = TimeSpan.FromSeconds(1);

    // GTK thread only.
    private Gtk.Overlay? _overlay;
    private Gtk4SidePanel? _panel;
    private readonly SidePanelLayout _layout = new();
    private int _commitGeneration;
    private UserScript? _pageHandOff;
    private int _minimumWidth;
    private int _minimumHeight;
    // The last Tab the window received, and the view that had the focus, for WatchFocus.
    private Gtk.EventControllerKey? _tabKeys;
    private (bool Backward, long At)? _lastTab;
    private nint _focusedView;
    private readonly SemaphoreSlim _panelGate = new(1, 1);
    // Any thread.
    private int _panelOpen;
    private StrongBox<ScreenRect>? _workArea;

    public bool SupportsSidePanel => Volatile.Read(ref _isOpen) != 0 && _overlay is not null;

    public DesktopSidePanelRefusal? CheckSidePanel(bool left, int width)
    {
        if (Volatile.Read(ref _panelOpen) != 0) return DesktopSidePanelRefusal.AlreadyOpen;
        if (State is not { Bounds: { } bounds } state || _options?.EphemeralDataStore is not null) return DesktopSidePanelRefusal.Unsupported;
        var window = new SidePanelWindow(bounds, state.VisibleBounds, Volatile.Read(ref _workArea)?.Value,
            state.IsMaximized, state.IsFullScreen || _options?.Kiosk == true, state.IsSnapped,
            Activity is { IsMinimized: true } or { IsVisible: false });
        return SidePanelPlanner.Open(window, left, width).Refusal;
    }

    public async ValueTask<WebUiSidePanelOpening> OpenSidePanelAsync(WebUiSidePanelRequest request, CancellationToken cancellationToken)
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
        Gtk4SidePanel? created = null;
        DesktopSidePanelRefusal? refusal = null;
        await InvokePanelAsync(() =>
        {
            if (_panel is not null) refusal = DesktopSidePanelRefusal.AlreadyOpen;
            else if (_overlay is null || _options is not { EphemeralDataStore: null }) refusal = DesktopSidePanelRefusal.Unsupported;
            else if (SidePanelPlanner.Open(ReadPanelWindow(), request.Left, request.Width).Refusal is { } early) refusal = early;
            else created = CreatePanel(request);
        }, cancellationToken).ConfigureAwait(false);
        if (refusal is { } refused) return WebUiSidePanelOpening.Refused(refused);
        var panel = created!;

        bool loaded;
        try
        {
            loaded = await WhileOpenAsync(panel.Loaded.Task.WaitAsync(request.LoadTimeout, cancellationToken)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            loaded = false;
        }
        catch (OperationCanceledException)
        {
            try { await InvokePanelAsync(() => DiscardPanel(panel), CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { }
            throw;
        }
        Task? committed = null;
        await InvokePanelAsync(() =>
        {
            if (panel.IsClosed) throw new InvalidOperationException("The GTK 4 window closed while the side panel opened.");
            if (!loaded)
            {
                refusal = DesktopSidePanelRefusal.LoadFailed;
                DiscardPanel(panel);
                return;
            }
            var window = ReadPanelWindow();
            var plan = SidePanelPlanner.Open(window, request.Left, request.Width);
            if (plan.Refusal is { } late)
            {
                refusal = late;
                DiscardPanel(panel);
                return;
            }
            panel.Before = window.Bounds;
            panel.NativeWidth = plan.NativeWidth;
            committed = _layout.BeginOpen(_overlay!.GetWidth(), request.Left, plan.NativeWidth);
            ApplyPanelBounds(plan.Target!);
        }, CancellationToken.None).ConfigureAwait(false);
        if (refusal is { } after) return WebUiSidePanelOpening.Refused(after);

        await WhileOpenAsync(committed!).ConfigureAwait(false);
        await InvokePanelAsync(() =>
        {
            if (panel.IsClosed) throw new InvalidOperationException("The GTK 4 window closed while the side panel opened.");
            panel.IsShown = true;
            panel.Opened = CurrentBounds();
            OrderPanel(panel);
            SetPageHandOff(true);
            ApplyMinimumSize();
            UpdateState();
            Volatile.Write(ref _panelOpen, 1);
        }, CancellationToken.None).ConfigureAwait(false);
        return new WebUiSidePanelOpening(panel, null);
    }

    private Task InvokePanelAsync(Action action, CancellationToken cancellationToken)
    {
        if (!IsOpen) throw new InvalidOperationException("The GTK 4 window is not open.");
        return Gtk4Dispatcher.Instance.InvokeAsync(action, cancellationToken);
    }

    private async Task<T> WhileOpenAsync<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, _closed.Task).ConfigureAwait(false) != task)
            throw new InvalidOperationException("The GTK 4 window closed while the side panel changed.");
        return await task.ConfigureAwait(false);
    }

    private async Task WhileOpenAsync(Task task)
    {
        if (await Task.WhenAny(task, _closed.Task).ConfigureAwait(false) != task)
            throw new InvalidOperationException("The GTK 4 window closed while the side panel changed.");
        await task.ConfigureAwait(false);
    }

    // GTK thread, from Create: the overlay that holds the page's view, which fills it while no panel shows.
    private Gtk.Overlay CreateOverlay(WebView webView)
    {
        var overlay = Gtk.Overlay.New();
        overlay.AddOverlay(webView);
        PanelSignals.ConnectChildPosition(this, overlay);
        _overlay = overlay;
        return overlay;
    }

    // GTK thread: the panel's view beside the page's, loading its page outside the content.
    private Gtk4SidePanel CreatePanel(WebUiSidePanelRequest request)
    {
        var options = _options!;
        var page = GetWebView();
        var manager = UserContentManager.New();
        WebView view;
        using (var related = new GObject.Value(page))
        using (var managerValue = new GObject.Value(manager))
        {
            view = WebView.NewWithProperties(
                [new GObject.ConstructArgument("related-view", related), new GObject.ConstructArgument("user-content-manager", managerValue)]);
        }
        var panel = new Gtk4SidePanel(this, request, view, manager);
        _panel = panel;
        try
        {
            _finalization?.Track(view.Handle.DangerousGetHandle());
            using (var credential = UserScript.New(request.DocumentStartScript, UserContentInjectedFrames.AllFrames, UserScriptInjectionTime.Start, null, null))
            {
                manager.AddScript(credential);
            }
            if (!manager.RegisterScriptMessageHandler(WebKitGtkKeyboard.ZoomMessageHandler, WebKitGtkKeyboard.ScriptWorld))
            {
                throw new InvalidOperationException("WebKitGTK could not register the side panel's zoom message handler.");
            }
            UserContentManager.ScriptMessageReceivedSignal.Connect(manager, OnPanelZoomMessage, false, WebKitGtkKeyboard.ZoomMessageHandler);
            foreach (var source in (string[])[WebKitGtkKeyboard.Script, HandOffScript(true)])
            {
                using var script = UserScript.NewForWorld(source, UserContentInjectedFrames.TopFrame, UserScriptInjectionTime.Start,
                    WebKitGtkKeyboard.ScriptWorld, null, null);
                manager.AddScript(script);
            }
            // As the page's view: WebKit's hidden text view takes no focus, the view does (Create).
            for (var child = view.GetFirstChild(); child is not null; child = child.GetNextSibling())
            {
                if (child.GetCssName() == "textview") child.SetCanFocus(false);
            }
            view.SetFocusable(true);
            panel.WebContent = new WebKitGtkWebContent(new WebContentRules(options.WebContent, request.Url, options.AdditionalOrigins),
                WebContentSignals.CanIdentifyMainFrame);
            view.GetSettings().SetEnableDeveloperExtras(panel.WebContent.Rules.DevToolsEnabled);
            PanelSignals.Connect(this, view);
            WebContentSignals.Connect(this, view);
            // Drops are refused in this version (D062.6): without WebKit's drop target the view takes none.
            PanelSignals.RemoveDropTarget(view);
            if (options.LayoutDirection != Runic.Platform.DesktopLayoutDirection.Default)
            {
                view.SetDirection(options.LayoutDirection == Runic.Platform.DesktopLayoutDirection.RightToLeft ? Gtk.TextDirection.Rtl : Gtk.TextDirection.Ltr);
            }
            WatchFocus();
            _layout.BeginLoading(request.Left, request.Width);
            _overlay!.AddOverlay(view);
            _overlay.QueueResize();
            view.LoadUri(request.Url.AbsoluteUri);
            return panel;
        }
        catch
        {
            DiscardPanel(panel);
            throw;
        }
    }

    // GTK thread.
    private SidePanelWindow ReadPanelWindow()
    {
        UpdateState();
        var state = Volatile.Read(ref _state) ?? throw new InvalidOperationException("The GTK 4 window reports no state.");
        UpdateActivity();
        var minimized = Activity is { IsMinimized: true } or { IsVisible: false };
        return new SidePanelWindow(state.Bounds!, state.VisibleBounds, Volatile.Read(ref _workArea)?.Value,
            state.IsMaximized, state.IsFullScreen || _options?.Kiosk == true, state.IsSnapped, minimized);
    }

    // GTK thread: the window's bounds now, as the state reports them.
    private DesktopWindowBounds CurrentBounds()
    {
        var window = GetWindow();
        var position = window.IsVisible() ? Gtk4X11Placement.GetPosition(SurfaceOf(window)) : null;
        return new DesktopWindowBounds
        {
            X = position?.X, Y = position?.Y, Width = Math.Max(1, window.GetWidth()), Height = Math.Max(1, window.GetHeight()), Scale = 1,
        };
    }

    // GTK thread, from UpdateState: the work area of the window's monitor, or on Wayland its geometry.
    private void RecordWorkArea(Gtk.Window window)
    {
        if (window.GetSurface() is not { } surface || Gdk.Display.GetDefault()?.GetMonitorAtSurface(surface) is not { } monitor) return;
        monitor.GetGeometry(out var geometry);
        var work = Gtk4X11Placement.MonitorWorkArea(monitor.Handle.DangerousGetHandle()) is { Width: > 0, Height: > 0 } area
            ? area : new ScreenRect(geometry.X, geometry.Y, geometry.Width, geometry.Height);
        if (Volatile.Read(ref _workArea)?.Value != work) Volatile.Write(ref _workArea, new StrongBox<ScreenRect>(work));
    }

    // GTK thread, from UpdateState: the placement applications save excludes an open panel, and a move to the other edge
    // commits once the window is there.
    private DesktopWindowPlacement PanelPlacement(DesktopWindowPlacement placement, (int X, int Y)? position)
    {
        if (_panel is { Moving: { } target } moving && position?.X == target)
        {
            moving.Moving = null;
            _layout.CommitMove();
            _overlay?.QueueResize();
        }
        if (_panel is not { IsShown: true } panel || !_layout.IsShown) return placement;
        var bounds = new DesktopWindowBounds { X = placement.X, Y = placement.Y, Width = placement.Width, Height = placement.Height, Scale = 1 };
        var excluded = SidePanelPlanner.ExcludePanel(bounds, panel.IsLeft, _layout.Width);
        return placement with { X = excluded.X, Width = excluded.Width };
    }

    // GTK thread: the default size and, on X11, one XMoveResizeWindow; the commit after PanelCommitTimeout when the
    // window manager does not give the bounds.
    private void ApplyPanelBounds(DesktopWindowBounds target)
    {
        var window = GetWindow();
        window.SetDefaultSize(target.Width, target.Height);
        if (target is { X: { } x, Y: { } y }) Gtk4X11Placement.MoveResize(SurfaceOf(window), x, y, target.Width, target.Height);
        QueueStateUpdate();
        var generation = ++_commitGeneration;
        // A reparenting window manager may move the frame after the last event the window gets: read the state again.
        foreach (var delay in (int[])[100, 300, 700])
        {
            _ = Task.Delay(delay).ContinueWith(_ => { if (IsOpen) Gtk4Dispatcher.Instance.Post(QueueStateUpdate); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        _ = Task.Delay(PanelCommitTimeout).ContinueWith(_ =>
        {
            if (!IsOpen) return;
            Gtk4Dispatcher.Instance.Post(() =>
            {
                try
                {
                    if (generation != _commitGeneration || !_layout.IsPending || _overlay is null) return;
                    if (_panel is { } moving) moving.Moving = null;
                    _layout.Commit();
                    _overlay.QueueResize();
                }
                catch
                {
                    // Never unwind through GTK.
                }
            });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // The overlay's children in the visual order, which is the focus order and the order AT-SPI lists them in.
    private void OrderPanel(Gtk4SidePanel panel)
    {
        if (_overlay is not { } overlay || _webView is not { } page) return;
        if (panel.IsLeft) panel.View.InsertBefore(overlay, page);
        else panel.View.InsertAfter(overlay, page);
    }

    private void SetPageHandOff(bool on)
    {
        if (_contentManager is not { } manager || _webView is not { } page) return;
        if (on && _pageHandOff is null)
        {
            _pageHandOff = UserScript.NewForWorld(HandOffScript(true), UserContentInjectedFrames.TopFrame, UserScriptInjectionTime.Start,
                WebKitGtkKeyboard.ScriptWorld, null, null);
            manager.AddScript(_pageHandOff);
        }
        if (!on && _pageHandOff is { } script)
        {
            manager.RemoveScript(script);
            script.Dispose();
            _pageHandOff = null;
        }
        PanelSignals.RunInKeyboardWorld(page, HandOffScript(on));
    }

    // GTK thread: a capture-phase key controller of the window notes each Tab before the page sees it, and the window's
    // focus-widget tells when GTK's focus chain moved the focus from one view to the other.
    private void WatchFocus()
    {
        if (_tabKeys is not null || _window is not { } window) return;
        _tabKeys = Gtk.EventControllerKey.New();
        _tabKeys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        _tabKeys.OnKeyPressed += OnTabPressed;
        window.AddController(_tabKeys);
        _focusedView = window.GetFocus()?.Handle.DangerousGetHandle() ?? 0;
        PanelSignals.ConnectFocusWidget(this, window);
    }

    private bool OnTabPressed(Gtk.EventControllerKey controller, Gtk.EventControllerKey.KeyPressedSignalArgs args)
    {
        // GDK_KEY_Tab and GDK_KEY_ISO_Left_Tab, which Shift+Tab produces.
        if (args.Keyval is 0xff09 or 0xfe20)
            _lastTab = (args.Keyval == 0xfe20 || (args.State & Gdk.ModifierType.ShiftMask) != 0, Environment.TickCount64);
        return false;
    }

    // GTK thread: the focus moved from one view to the other after a Tab: the new view focuses its first control, or its
    // last one after Shift+Tab, as Tab into a document does.
    private void OnFocusWidgetChanged()
    {
        var focus = _window?.GetFocus()?.Handle.DangerousGetHandle() ?? 0;
        var previous = _focusedView;
        if (focus != 0) _focusedView = focus;
        if (_panel is not { IsShown: true } panel || _webView is not { } page || _lastTab is not { } tab) return;
        var pageView = page.Handle.DangerousGetHandle();
        var panelView = panel.View.Handle.DangerousGetHandle();
        var target = focus == panelView && previous == pageView ? panel.View : focus == pageView && previous == panelView ? page : null;
        if (target is null || Environment.TickCount64 - tab.At > 2000) return;
        _lastTab = null;
        PanelSignals.RunInKeyboardWorld(target, $"runicDesktopKeyboard.enter({(tab.Backward ? "true" : "false")});");
    }

    private static string HandOffScript(bool on) => $"runicDesktopKeyboard.setHandOff({(on ? "true" : "false")});";

    // The content's size request: the minimum it was given, and with an open panel the panel's width beside the page.
    private void ApplyMinimumSize()
    {
        if (_overlay is not { } overlay) return;
        var panel = _panel is { IsShown: true } && _layout.IsShown ? _layout.Width : 0;
        var width = _minimumWidth > 0 ? _minimumWidth + panel : panel > 0 ? panel + 1 : -1;
        overlay.SetSizeRequest(width, _minimumHeight > 0 ? _minimumHeight : -1);
    }

    private async Task ClosePanelAsync(Gtk4SidePanel panel, DesktopSidePanelCloseReason reason)
    {
        await _panelGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsOpen) return;
            Task? committed = null;
            var closing = false;
            await InvokePanelAsync(() =>
            {
                if (!ReferenceEquals(_panel, panel) || panel.IsClosed) return;
                closing = true;
                if (!panel.IsShown) return;
                SetPageHandOff(false);
                var window = GetWindow();
                if (window.IsMaximized() || window.IsFullscreen() || _options?.Kiosk == true || Activity is { IsMinimized: true })
                {
                    ShrinkRestoredBounds(window, panel);
                    _layout.CloseNow();
                    _overlay?.QueueResize();
                    return;
                }
                var target = SidePanelPlanner.Close(CurrentBounds(), panel.Opened!, panel.Before!, panel.IsLeft, panel.NativeWidth);
                committed = _layout.BeginClose(_overlay!.GetWidth());
                ApplyPanelBounds(target);
            }, CancellationToken.None).ConfigureAwait(false);
            if (!closing) return;
            if (committed is not null) await WhileOpenAsync(committed).ConfigureAwait(false);
            await InvokePanelAsync(() =>
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

    // GTK 4 keeps the restored size in the default size: it loses the panel's width, and on the left edge the position
    // it restores to moves right by it.
    private void ShrinkRestoredBounds(Gtk.Window window, Gtk4SidePanel panel)
    {
        window.GetDefaultSize(out var width, out var height);
        if (width > 0) window.SetDefaultSize(Math.Max(1, width - panel.NativeWidth), height);
        if (panel.IsLeft && _normalPosition is { } position) _normalPosition = (position.X + panel.NativeWidth, position.Y);
    }

    // GTK thread: removes the panel's view and forgets it, without changing the window or reporting.
    private void DiscardPanel(Gtk4SidePanel panel)
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
            if (shown) SetPageHandOff(false);
        }
        ReleasePanelView(panel);
        _overlay?.QueueResize();
    }

    private void ReleasePanelView(Gtk4SidePanel panel)
    {
        if (panel.Released) return;
        panel.Released = true;
        UserContentManager.ScriptMessageReceivedSignal.Disconnect(panel.Manager, OnPanelZoomMessage);
        panel.Manager.UnregisterScriptMessageHandler(WebKitGtkKeyboard.ZoomMessageHandler, WebKitGtkKeyboard.ScriptWorld);
        if (_overlay is { } overlay && panel.View.GetParent() is not null) overlay.RemoveOverlay(panel.View);
        panel.View.TryClose();
        panel.View.Dispose();
        panel.Manager.Dispose();
    }

    // GTK thread, from CleanupNative: the panel goes with the window.
    private void ClosePanelWithWindow()
    {
        if (_tabKeys is { } watching && _panel is null)
        {
            watching.OnKeyPressed -= OnTabPressed;
            _tabKeys = null;
        }
        if (_panel is not { } panel) return;
        var shown = panel.IsShown;
        panel.IsShown = false;
        panel.IsClosed = true;
        panel.Loaded.TrySetResult(false);
        _panel = null;
        Volatile.Write(ref _panelOpen, 0);
        _layout.Discard();
        ReleasePanelView(panel);
        if (_tabKeys is { } keys)
        {
            keys.OnKeyPressed -= OnTabPressed;
            _tabKeys = null;
        }
        if (_pageHandOff is { } script)
        {
            script.Dispose();
            _pageHandOff = null;
        }
        if (shown) panel.Request.Closed(DesktopSidePanelCloseReason.WindowClosed);
    }

    private void OnPanelZoomMessage(UserContentManager _, UserContentManager.ScriptMessageReceivedSignalArgs args)
    {
        var command = WebKitGtkKeyboard.GetWheelZoomCommand(args.Value.ToInt32());
        if (command != PageZoomCommand.None && _panel?.View is { } view) view.SetZoomLevel(WebKitGtkKeyboard.Apply(command, view.GetZoomLevel()));
    }

    // The view a key the page did not handle zooms: the panel's while it has the focus.
    private WebView? FocusedView(WebView page) =>
        _panel is { IsShown: true, View: var panel } && _window?.GetFocus() is { } focus
            && focus.Handle.DangerousGetHandle() == panel.Handle.DangerousGetHandle() ? panel : page;

    // The view a signal came from: the page's or the panel's, and its web content policy.
    private bool IsOwnView(nint webView, out WebKitGtkWebContent? content)
    {
        content = null;
        if (IsOwnOpenView(webView))
        {
            content = _webContent;
            return true;
        }
        if (IsOpen && _panel is { IsClosed: false, View: var view } panel && view.Handle.DangerousGetHandle() == webView)
        {
            content = panel.WebContent;
            return true;
        }
        return false;
    }

    private WebView? ViewOf(nint webView) =>
        _webView is { } page && page.Handle.DangerousGetHandle() == webView ? page
        : _panel?.View is { } panel && panel.Handle.DangerousGetHandle() == webView ? panel : null;

    // The overlay places the page and the panel: x, y, width and height, or null for another child.
    private (int X, int Y, int Width, int Height)? PositionChild(nint widget)
    {
        if (_overlay is not { } own) return null;
        var width = own.GetWidth();
        var height = Math.Max(1, own.GetHeight());
        var (pageX, pageWidth, panelX, panelWidth) = _layout.Allocate(width);
        if (_webView is { } page && page.Handle.DangerousGetHandle() == widget) return (pageX, 0, Math.Max(1, pageWidth), height);
        if (_panel is { } panel && panel.View.Handle.DangerousGetHandle() == widget) return (panelX, 0, Math.Max(1, panelWidth), height);
        return null;
    }

    private void OnPanelProcessTerminated(nint webView, int reason)
    {
        const int TerminatedByApi = 2;
        if (!IsOpen || _panel is not { IsClosed: false } panel || panel.View.Handle.DangerousGetHandle() != webView || reason == TerminatedByApi) return;
        if (!panel.IsShown) panel.Loaded.TrySetResult(false);
        else if (++panel.Crashes > 1) _ = ClosePanelAsync(panel, DesktopSidePanelCloseReason.Crashed);
        else panel.View.Reload();
    }

    private void OnPanelCloseRequested(nint webView)
    {
        if (IsOpen && _panel is { IsShown: true } panel && panel.View.Handle.DangerousGetHandle() == webView)
            _ = ClosePanelAsync(panel, DesktopSidePanelCloseReason.Closed);
    }

    private void OnPanelLoad(nint webView, bool failed, bool finished)
    {
        if (_panel is not { } panel || panel.View.Handle.DangerousGetHandle() != webView) return;
        if (failed) panel.LoadFailed = true;
        if (finished) panel.Loaded.TrySetResult(!panel.LoadFailed);
    }

    private sealed class Gtk4SidePanel(Gtk4WindowHost host, WebUiSidePanelRequest request, WebView view, UserContentManager manager) : IWebUiSidePanel
    {
        private int _width = request.Width;
        private int _left = request.Left ? 1 : 0;

        internal WebUiSidePanelRequest Request { get; } = request;
        internal WebView View { get; } = view;
        internal UserContentManager Manager { get; } = manager;
        internal WebKitGtkWebContent? WebContent { get; set; }
        internal TaskCompletionSource<bool> Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool LoadFailed { get; set; }
        internal int NativeWidth { get; set; } = request.Width;
        internal bool IsShown { get; set; }
        internal bool IsClosed { get; set; }
        internal bool Released { get; set; }
        internal int Crashes { get; set; }
        internal int? Moving { get; set; }
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
                var resized = host._layout.BeginResize(host._overlay!.GetWidth(), plan.NativeWidth);
                host.ApplyPanelBounds(plan.Target!);
                return (null, () =>
                {
                    NativeWidth = plan.NativeWidth;
                    Width = width;
                }, resized);
            }, cancellationToken);

        public ValueTask FocusAsync(CancellationToken cancellationToken) =>
            new(host.InvokePanelAsync(() =>
            {
                RequireOpen();
                _ = View.GrabFocus();
            }, cancellationToken));

        public ValueTask CloseAsync(CancellationToken cancellationToken) =>
            host.IsOpen ? new ValueTask(host.ClosePanelAsync(this, DesktopSidePanelCloseReason.Closed).WaitAsync(cancellationToken)) : ValueTask.CompletedTask;

        private async ValueTask<DesktopSidePanelRefusal?> ChangeAsync(
            Func<SidePanelWindow, (DesktopSidePanelRefusal? Refusal, Action? Applied, Task? Committed)> change,
            CancellationToken cancellationToken)
        {
            await host._panelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                (DesktopSidePanelRefusal? Refusal, Action? Applied, Task? Committed) result = default;
                await host.InvokePanelAsync(() =>
                {
                    RequireOpen();
                    result = change(host.ReadPanelWindow());
                }, cancellationToken).ConfigureAwait(false);
                if (result.Committed is { } committed)
                {
                    await host.WhileOpenAsync(committed).ConfigureAwait(false);
                    await host.InvokePanelAsync(() =>
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

        private void Rebase(DesktopWindowBounds current)
        {
            if (Opened is { } opened && SidePanelPlanner.SameFrame(current, opened)) return;
            Before = SidePanelPlanner.ExcludePanel(current, IsLeft, NativeWidth);
        }
    }

    // The panel's signals, connected through GObject for the reason WebProcessTerminatedSignal gives; the connections end
    // when the view (or the overlay) is finalized.
    private static unsafe partial class PanelSignals
    {
        private static readonly nint EvaluateJavascript = NativeLibrary.GetExport(WebContentSignals.WebKitLibrary, "webkit_web_view_evaluate_javascript");

        internal static void ConnectChildPosition(Gtk4WindowHost host, Gtk.Overlay overlay) =>
            Connect(host, overlay.Handle.DangerousGetHandle(), "get-child-position",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, int*, nint, int>)&OnChildPosition);

        internal static void Connect(Gtk4WindowHost host, WebView view)
        {
            var native = view.Handle.DangerousGetHandle();
            Connect(host, native, "web-process-terminated", (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&OnTerminated);
            Connect(host, native, "close", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnClose);
            Connect(host, native, "load-failed", (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, nint, nint, int>)&OnLoadFailed);
            Connect(host, native, "load-changed", (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&OnLoadChanged);
        }

        // WebKit's drop target of the view, removed.
        internal static void RemoveDropTarget(WebView view)
        {
            var native = view.Handle.DangerousGetHandle();
            var targetType = DropTargetAsyncGetType();
            var controllers = WidgetObserveControllers(native);
            if (controllers == 0) return;
            List<nint> found = [];
            try
            {
                var count = ListModelGetNItems(controllers);
                for (uint index = 0; index < count; index++)
                {
                    var controller = ListModelGetItem(controllers, index);
                    if (controller == 0) continue;
                    if (TypeCheckInstanceIsA(controller, targetType) != 0) found.Add(controller);
                    else ObjectUnref(controller);
                }
            }
            finally
            {
                ObjectUnref(controllers);
            }
            foreach (var controller in found)
            {
                WidgetRemoveController(native, controller);
                ObjectUnref(controller);
            }
        }

        internal static void ConnectFocusWidget(Gtk4WindowHost host, Gtk.Window window) =>
            Connect(host, window.Handle.DangerousGetHandle(), "notify::focus-widget", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnFocusWidget);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnFocusWidget(nint window, nint property, nint context)
        {
            try { Host(context)?.OnFocusWidgetChanged(); }
            catch { /* Native callbacks cannot unwind through GTK. */ }
        }

        internal static void RunInKeyboardWorld(WebView view, string source)
        {
            var script = Marshal.StringToCoTaskMemUTF8(source);
            var world = Marshal.StringToCoTaskMemUTF8(WebKitGtkKeyboard.ScriptWorld);
            try
            {
                ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, void>)EvaluateJavascript)(
                    view.Handle.DangerousGetHandle(), script, -1, world, 0, 0, 0, 0);
            }
            finally
            {
                Marshal.FreeCoTaskMem(script);
                Marshal.FreeCoTaskMem(world);
            }
        }

        private static void Connect(Gtk4WindowHost host, nint instance, string signal, nint callback)
        {
            var context = GCHandle.Alloc(host);
            if (ConnectData(instance, signal, callback, GCHandle.ToIntPtr(context), (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Release, 0) == 0)
            {
                context.Free();
                throw new InvalidOperationException($"GTK rejected the '{signal}' signal handler.");
            }
        }

        private static Gtk4WindowHost? Host(nint context) => GCHandle.FromIntPtr(context).Target as Gtk4WindowHost;

        // GdkRectangle: x, y, width, height.
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int OnChildPosition(nint overlay, nint widget, int* allocation, nint context)
        {
            try
            {
                if (Host(context)?.PositionChild(widget) is not { } bounds) return 0;
                (allocation[0], allocation[1], allocation[2], allocation[3]) = (bounds.X, bounds.Y, bounds.Width, bounds.Height);
                return 1;
            }
            catch
            {
                return 0;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnTerminated(nint webView, int reason, nint context)
        {
            try { Host(context)?.OnPanelProcessTerminated(webView, reason); }
            catch { /* Native callbacks cannot unwind through GTK. */ }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnClose(nint webView, nint context)
        {
            try { Host(context)?.OnPanelCloseRequested(webView); }
            catch { /* Native callbacks cannot unwind through GTK. */ }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int OnLoadFailed(nint webView, int loadEvent, nint uri, nint error, nint context)
        {
            try { Host(context)?.OnPanelLoad(webView, failed: true, finished: false); }
            catch { /* Native callbacks cannot unwind through GTK. */ }
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnLoadChanged(nint webView, int loadEvent, nint context)
        {
            const int LoadFinished = 3;
            try
            {
                if (loadEvent == LoadFinished) Host(context)?.OnPanelLoad(webView, failed: false, finished: true);
            }
            catch { /* Native callbacks cannot unwind through GTK. */ }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Release(nint context, nint closure) => GCHandle.FromIntPtr(context).Free();

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nuint ConnectData(nint instance, string signal, nint handler, nint data, nint destroy, int flags);

        [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_drop_target_async_get_type")]
        private static partial nuint DropTargetAsyncGetType();

        [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_widget_observe_controllers")]
        private static partial nint WidgetObserveControllers(nint widget);

        [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_widget_remove_controller")]
        private static partial void WidgetRemoveController(nint widget, nint controller);

        [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_list_model_get_n_items")]
        private static partial uint ListModelGetNItems(nint list);

        [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_list_model_get_item")]
        private static partial nint ListModelGetItem(nint list, uint position);

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_type_check_instance_is_a")]
        private static partial int TypeCheckInstanceIsA(nint instance, nuint type);

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")]
        private static partial void ObjectUnref(nint instance);
    }
}
