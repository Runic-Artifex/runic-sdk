using System.ComponentModel;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// sdk#864, P2: the side panel on WebView2 (side-panel/DESIGN.md §1.2, Option A). A second WebView2 controller from the
// page controller's environment, in a Runic-owned child window docked on one edge of the client area. The window's
// client area is laid out from its size and the panel's state (LayoutViews, from WM_SIZE): the page's controller keeps
// its width and screen position, and the panel's container fills the strip. Opening, closing, moving and resizing the
// panel are one SetWindowPos each (SidePanelPlanner), inside which WM_SIZE lays the views out; WM_NCCALCSIZE keeps the
// page's pixels where they are when the window's left edge moves (R2, R3).
internal sealed partial class WindowsWebView2Host
{
    private const uint WmNcCalcSize = 0x0083;
    private const nint WvrValidRects = 0x0400;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsClipSiblings = 0x04000000;
    private const nint HwndTop = 0;
    private const nint HwndBottom = 1;
    // COREWEBVIEW2_PROCESS_FAILED_KIND_BROWSER_PROCESS_EXITED and _RENDER_PROCESS_EXITED.
    private const int BrowserProcessExited = 0;
    private const int RenderProcessExitedKind = 1;

    private static readonly string PanelClassName = $"Runic.Desktop.WebView2.SidePanel.{Environment.ProcessId}";
    private static ushort _panelClass;

    // Window thread only: the panel that is loading or open, and while one SetWindowPos of a panel change runs, the page's
    // rectangle in screen coordinates, which it keeps (WM_NCCALCSIZE).
    private SidePanel? _panel;
    private Rect? _keptPage;
    private bool _pageFocusRegistered;
    // Any thread: whether a panel is open, and the work area of the window's monitor in physical pixels, for CheckSidePanel.
    private int _panelOpen;
    private StrongBox<ScreenRect>? _workArea;

    public bool SupportsSidePanel => true;

    // From the last reported state, on any thread.
    public DesktopSidePanelRefusal? CheckSidePanel(bool left, int width)
    {
        if (Volatile.Read(ref _panelOpen) != 0) return DesktopSidePanelRefusal.AlreadyOpen;
        if (State is not { Bounds: { } bounds } state || _options?.EphemeralDataStore is not null) return DesktopSidePanelRefusal.Unsupported;
        var activity = Activity;
        var window = new SidePanelWindow(bounds, state.VisibleBounds, Volatile.Read(ref _workArea)?.Value,
            state.IsMaximized, state.IsFullScreen, state.IsSnapped, activity is { IsMinimized: true } or { IsVisible: false });
        return SidePanelPlanner.Open(window, left, width).Refusal;
    }

    public async ValueTask<WebUiSidePanelOpening> OpenSidePanelAsync(WebUiSidePanelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Task<WebUiSidePanelOpening>? opening = null;
        // Started on the window thread, whose synchronization context its awaits return to.
        await InvokeAsync(() => opening = OpenPanelAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);
        // A window that closes meanwhile stops its thread, which then never resumes the open.
        if (await Task.WhenAny(opening!, _closed.Task).ConfigureAwait(false) != opening)
            throw new InvalidOperationException("The embedded WebView window closed while the side panel opened.");
        return await opening.ConfigureAwait(false);
    }

    // Window thread.
    private async Task<WebUiSidePanelOpening> OpenPanelAsync(WebUiSidePanelRequest request, CancellationToken cancellationToken)
    {
        if (_panel is not null) return WebUiSidePanelOpening.Refused(DesktopSidePanelRefusal.AlreadyOpen);
        if (_controller is not { } page || _options is not { EphemeralDataStore: null } options)
            return WebUiSidePanelOpening.Refused(DesktopSidePanelRefusal.Unsupported);
        var plan = SidePanelPlanner.Open(ReadPanelWindow(), request.Left, request.Width);
        if (plan.Refusal is { } early) return WebUiSidePanelOpening.Refused(early);

        var panel = new SidePanel(this, request, plan.NativeWidth);
        _panel = panel;
        try
        {
            EnsurePanelClass();
            var client = ClientBounds();
            // Outside the client area while it loads, where it renders but does not show (LayoutViews).
            panel.Container = Native.CreateWindowEx(0, PanelClassName, string.Empty, WsChild | WsVisible | WsClipSiblings,
                client.Width, 0, panel.NativeWidth, client.Height, _window, 0, Native.GetModuleHandle(null), 0);
            if (panel.Container == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The side panel's window could not be created.");
            var controller = await page.CreateSiblingAsync(panel.Container);
            if (panel.IsClosed)
            {
                // The window closed while WebView2 created the controller (ClosePanelWithWindow).
                controller.Dispose();
                throw new InvalidOperationException("The embedded WebView window closed while the side panel opened.");
            }
            panel.Controller = controller;
            await ConfigurePanelAsync(panel, controller, options);
            LayoutViews();
            var loaded = controller.NextNavigationCompletedAsync();
            controller.Navigate(request.Url.AbsoluteUri);
            bool succeeded;
            try { succeeded = await loaded.WaitAsync(request.LoadTimeout, cancellationToken); }
            catch (TimeoutException) { succeeded = false; }
            if (panel.IsClosed) throw new InvalidOperationException("The embedded WebView window closed while the side panel opened.");
            if (!succeeded)
            {
                DiscardPanel(panel);
                return WebUiSidePanelOpening.Refused(DesktopSidePanelRefusal.LoadFailed);
            }

            // The window may have changed while the page loaded.
            var window = ReadPanelWindow();
            plan = SidePanelPlanner.Open(window, request.Left, request.Width);
            if (plan.Refusal is { } late)
            {
                DiscardPanel(panel);
                return WebUiSidePanelOpening.Refused(late);
            }
            panel.Before = window.Bounds;
            ApplyPanelBounds(plan.Target!, () =>
            {
                panel.NativeWidth = plan.NativeWidth;
                panel.IsShown = true;
            });
            panel.Opened = CurrentBounds();
            OrderPanel(panel);
            RegisterPageFocus(page);
            Volatile.Write(ref _panelOpen, 1);
            return new WebUiSidePanelOpening(panel, null);
        }
        catch
        {
            DiscardPanel(panel);
            throw;
        }
    }

    // Window thread: the panel controller's registrations, the window's for its page with the panel's address. The title
    // is not the window's, window.close() closes the panel, and drops from outside are refused (D062.6).
    private async Task ConfigurePanelAsync(SidePanel panel, WindowsWebView2Controller controller, WebUiEmbeddedHostOptions options)
    {
        var url = panel.Request.Url;
        controller.Bounds = new Rectangle(0, 0, panel.NativeWidth, ClientBounds().Height);
        controller.IsVisible = true;
        controller.RefuseExternalDrops();
        controller.RegisterEvents(
            static _ => { },
            () => PostToWindow(() =>
            {
                if (panel.IsShown) ClosePanel(panel, DesktopSidePanelCloseReason.Closed);
            }),
            options.AllowedPermissions,
            url,
            options.RegionFrames);
        controller.ApplyWebContent(
            new WebContentRules(options.WebContent, url, options.AdditionalOrigins),
            target => WindowsWebView2Interop.OpenExternally(target),
            target => PostToWindow(() => panel.Controller?.Navigate(target)));
        controller.RegisterProcessFailed(kind => PostToWindow(() => OnPanelProcessFailed(panel, kind)));
        controller.RegisterMoveFocusRequested(reason =>
        {
            if (!panel.IsShown || _controller is not { } page) return false;
            page.MoveFocus(reason);
            return true;
        });
        controller.RegisterAcceleratorKeys(IsMenuShortcut);
        var documentStart = controller.AddDocumentStartScriptAsync(panel.Request.DocumentStartScript);
        var menuKeys = controller.EnableMenuKeysAsync(url, OnMenuKey, options.RegionFrames);
        await Task.WhenAll(documentStart, menuKeys);
    }

    // Window thread: a crashed panel page reloads once; a second crash, or the browser process's end, closes the panel.
    private void OnPanelProcessFailed(SidePanel panel, int kind)
    {
        if (!ReferenceEquals(_panel, panel) || !panel.IsShown) return;
        if (kind == BrowserProcessExited || (kind == RenderProcessExitedKind && ++panel.Crashes > 1))
        {
            ClosePanel(panel, DesktopSidePanelCloseReason.Crashed);
        }
        else if (kind == RenderProcessExitedKind)
        {
            panel.Controller?.Navigate(panel.Request.Url.AbsoluteUri);
        }
    }

    // Tab past the page's last tab stop focuses the panel's first, Shift+Tab before its first the panel's last.
    private void RegisterPageFocus(WindowsWebView2Controller page)
    {
        if (_pageFocusRegistered) return;
        _pageFocusRegistered = true;
        page.RegisterMoveFocusRequested(reason =>
        {
            if (_panel is not { IsShown: true, Controller: { } panel }) return false;
            panel.MoveFocus(reason);
            return true;
        });
    }

    // Window thread: the window as SidePanelPlanner reads it, from the window itself.
    private SidePanelWindow ReadPanelWindow()
    {
        UpdateState();
        var state = Volatile.Read(ref _state) ?? throw new InvalidOperationException("The embedded WebView window reports no state.");
        var minimized = Native.IsIconic(_window) || !Native.IsWindowVisible(_window);
        return new SidePanelWindow(state.Bounds!, state.VisibleBounds, Volatile.Read(ref _workArea)?.Value,
            state.IsMaximized, state.IsFullScreen || _options?.Kiosk == true, state.IsSnapped, minimized);
    }

    // Window thread: the window rectangle in physical pixels.
    private DesktopWindowBounds CurrentBounds()
    {
        Native.GetWindowRect(_window, out var frame);
        var dpi = Native.GetDpiForWindow(_window);
        return new DesktopWindowBounds
        {
            X = frame.Left, Y = frame.Top, Width = frame.Right - frame.Left, Height = frame.Bottom - frame.Top,
            Scale = (dpi == 0 ? 96 : dpi) / 96d,
        };
    }

    // Window thread, from UpdateState: the work area of the window's monitor.
    private void RecordWorkArea()
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!Native.GetMonitorInfo(Native.MonitorFromWindow(_window, MonitorDefaultToNearest), ref info)) return;
        var work = new ScreenRect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        if (Volatile.Read(ref _workArea)?.Value != work) Volatile.Write(ref _workArea, new StrongBox<ScreenRect>(work));
    }

    // Window thread: one SetWindowPos to target, after change updated the panel's layout state, so the WM_SIZE inside it
    // lays the views out for the new size. The page keeps its screen rectangle meanwhile (WM_NCCALCSIZE).
    private void ApplyPanelBounds(DesktopWindowBounds target, Action change)
    {
        _keptPage = PageScreenRect();
        change();
        try
        {
            var flags = SwpNoZOrder | SwpNoActivate;
            if (target.X is null) flags |= SwpNoMove;
            Native.SetWindowPos(_window, 0, target.X ?? 0, target.Y ?? 0, target.Width, target.Height, flags);
        }
        finally { _keptPage = null; }
        LayoutViews();
        UpdateState();
    }

    // Window thread: the page's rectangle in screen coordinates, as LayoutViews places it now.
    private Rect PageScreenRect()
    {
        var client = ClientBounds();
        var (x, width) = _panel is { IsShown: true } panel
            ? SidePanelPlanner.Layout(client.Width, panel.Left, panel.NativeWidth) is var layout ? (layout.PageX, layout.PageWidth) : default
            : (0, client.Width);
        var origin = new Point();
        Native.ClientToScreen(_window, ref origin);
        return new Rect { Left = origin.X + x, Top = origin.Y, Right = origin.X + x + width, Bottom = origin.Y + client.Height };
    }

    // Window thread, from WM_SIZE and after every panel change: the page and the panel from the client area's size.
    private void LayoutViews()
    {
        if (_controller is not { } page) return;
        var client = ClientBounds();
        if (_panel is not { Container: not 0 } panel)
        {
            page.Bounds = client;
            return;
        }
        if (!panel.IsShown)
        {
            // Loading or closing: just outside the client area, where the parent clips it.
            Native.SetWindowPos(panel.Container, 0, client.Width, 0, panel.NativeWidth, client.Height, SwpNoZOrder | SwpNoActivate);
            page.Bounds = client;
            return;
        }
        var layout = SidePanelPlanner.Layout(client.Width, panel.Left, panel.NativeWidth);
        page.Bounds = new Rectangle(layout.PageX, 0, layout.PageWidth, client.Height);
        Native.SetWindowPos(panel.Container, 0, layout.PanelX, 0, layout.PanelWidth, client.Height, SwpNoZOrder | SwpNoActivate);
        if (panel.Controller is { } controller) controller.Bounds = new Rectangle(0, 0, layout.PanelWidth, client.Height);
    }

    // UI Automation lists sibling windows in z-order, so the panel's container comes before the page's view on the left
    // edge and after it on the right: the reading order is the visual order.
    private static void OrderPanel(SidePanel panel) =>
        Native.SetWindowPos(panel.Container, panel.Left ? HwndTop : HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

    // WM_NCCALCSIZE while ApplyPanelBounds sets the window's bounds: by default Windows keeps the client area's old pixels
    // at its top-left corner, which on the left edge would show the page shifted by the panel's width until it repaints.
    // The page's rectangle, which it keeps on the screen, is valid instead.
    private unsafe bool KeepPagePixels(nint window, nuint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (wParam == 0 || _keptPage is not { } page) return false;
        result = Native.DefWindowProc(window, WmNcCalcSize, wParam, lParam);
        var parameters = (NcCalcSizeParameters*)lParam;
        var client = parameters->NewClient;
        var valid = new Rect
        {
            Left = Math.Max(page.Left, client.Left), Top = Math.Max(page.Top, client.Top),
            Right = Math.Min(page.Right, client.Right), Bottom = Math.Min(page.Bottom, client.Bottom),
        };
        if (valid.Right <= valid.Left || valid.Bottom <= valid.Top) return true;
        parameters->ValidDestination = valid;
        parameters->ValidSource = valid;
        result = WvrValidRects;
        return true;
    }

    // WM_DPICHANGED with an open panel: both views keep their device-independent widths exactly at the new scale, so the
    // window's width is computed rather than scaled (the suggested rectangle would round the page's width by ±1).
    private unsafe Rect SuggestedDpiRect(Rect suggested, uint dpi)
    {
        if (_panel is not { IsShown: true } panel || _fullScreen || Native.IsZoomed(_window) || dpi == 0) return suggested;
        var client = ClientBounds();
        var oldScale = panel.Scale;
        var newScale = dpi / 96d;
        var pageWidth = SidePanelPlanner.Layout(client.Width, panel.Left, panel.NativeWidth).PageWidth;
        var newPage = (int)Math.Round(pageWidth / oldScale * newScale, MidpointRounding.AwayFromZero);
        var newPanel = (int)Math.Round(panel.Width * newScale, MidpointRounding.AwayFromZero);
        var frame = new Rect();
        var style = (uint)Native.GetWindowLongPtr(_window, GwlStyle);
        var extendedStyle = (uint)Native.GetWindowLongPtr(_window, GwlExStyle);
        if (!Native.AdjustWindowRectExForDpi(ref frame, style, false, extendedStyle, dpi)) return suggested;
        panel.NativeWidth = newPanel;
        panel.Scale = newScale;
        return suggested with { Right = suggested.Left + (frame.Right - frame.Left) + newPage + newPanel };
    }

    // Window thread: closes the panel and shrinks the window back (SidePanelPlanner.Close), or for a maximized, minimized
    // or full-screen window, the bounds it restores to. Reports the close once.
    private void ClosePanel(SidePanel panel, DesktopSidePanelCloseReason reason)
    {
        if (!ReferenceEquals(_panel, panel) || panel.IsClosed) return;
        if (panel.IsShown && _window != 0)
        {
            if (_fullScreen || _options?.Kiosk == true || Native.IsZoomed(_window) || Native.IsIconic(_window))
            {
                ShrinkRestoredBounds(panel);
                HidePanel(panel);
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

    // Window thread: out of the layout; the page takes the client area at the next WM_SIZE.
    private static void HidePanel(SidePanel panel)
    {
        panel.IsShown = false;
        if (panel.Container != 0) Native.ShowWindow(panel.Container, SwHide);
    }

    // The bounds a maximized, minimized or full-screen window restores to shrink by the panel's width on its edge.
    private void ShrinkRestoredBounds(SidePanel panel)
    {
        if (_fullScreen)
        {
            _windowedPlacement.NormalPosition = Shrink(_windowedPlacement.NormalPosition);
            return;
        }
        var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        if (!Native.GetWindowPlacement(_window, ref placement)) return;
        placement.NormalPosition = Shrink(placement.NormalPosition);
        Native.SetWindowPlacement(_window, ref placement);

        Rect Shrink(Rect normal)
        {
            var shrunk = SidePanelPlanner.ShrinkRestored(
                new ScreenRect(normal.Left, normal.Top, normal.Right - normal.Left, normal.Bottom - normal.Top), panel.Left, panel.NativeWidth);
            return new Rect { Left = shrunk.X, Top = shrunk.Y, Right = shrunk.X + shrunk.Width, Bottom = shrunk.Y + shrunk.Height };
        }
    }

    // Window thread: releases the panel's views and forgets it, without changing the window or reporting.
    private void DiscardPanel(SidePanel panel)
    {
        panel.IsShown = false;
        panel.IsClosed = true;
        try { panel.Controller?.Dispose(); }
        catch (Exception error) when (error is COMException or InvalidOperationException) { }
        panel.Controller = null;
        if (panel.Container != 0)
        {
            Native.DestroyWindow(panel.Container);
            panel.Container = 0;
        }
        if (ReferenceEquals(_panel, panel))
        {
            _panel = null;
            Volatile.Write(ref _panelOpen, 0);
            LayoutViews();
        }
    }

    // WM_DESTROY: the panel goes with the window, before the page's controller.
    private void ClosePanelWithWindow()
    {
        if (_panel is not { } panel) return;
        var opened = panel.IsShown;
        DiscardPanel(panel);
        if (opened) panel.Request.Closed(DesktopSidePanelCloseReason.WindowClosed);
    }

    // From a WebView2 event: runs action on the window thread once the event has returned.
    private static void PostToWindow(Action action) =>
        SynchronizationContext.Current?.Post(static state => ((Action)state!)(), action);

    private static unsafe void EnsurePanelClass()
    {
        lock (ClassGate)
        {
            if (_panelClass != 0) return;
            // A plain container: DefWindowProc is its window procedure, and the WebView2 controller inside it handles its input.
            var windowClass = new WindowClass
            {
                Size = checked((uint)Marshal.SizeOf<WindowClass>()),
                WindowProcedure = NativeLibrary.GetExport(NativeLibrary.Load("user32.dll"), "DefWindowProcW"),
                Instance = Native.GetModuleHandle(null),
                Cursor = Native.LoadCursor(0, 32512),
                ClassName = Marshal.StringToHGlobalUni(PanelClassName),
            };
            try
            {
                _panelClass = Native.RegisterClassEx(in windowClass);
                if (_panelClass == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The side panel's window class could not be registered.");
            }
            finally { Marshal.FreeHGlobal(windowClass.ClassName); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NcCalcSizeParameters
    {
        internal Rect NewClient;
        internal Rect ValidDestination;
        internal Rect ValidSource;
        internal nint WindowPosition;
    }

    // A panel from its request until it closed. Window thread only, except the read-only members of IWebUiSidePanel.
    private sealed class SidePanel(WindowsWebView2Host host, WebUiSidePanelRequest request, int nativeWidth) : IWebUiSidePanel
    {
        private int _width = request.Width;
        private int _left = request.Left ? 1 : 0;

        internal WebUiSidePanelRequest Request { get; } = request;
        internal nint Container { get; set; }
        internal WindowsWebView2Controller? Controller { get; set; }
        // In physical pixels at Scale, the window's scale when it was last laid out for the panel.
        internal int NativeWidth { get; set; } = nativeWidth;
        internal double Scale { get; set; } = host.CurrentScale();
        internal bool IsShown { get; set; }
        internal bool IsClosed { get; set; }
        internal int Crashes { get; set; }
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
            await host.InvokeAsync(() =>
            {
                RequireOpen();
                if (left == IsLeft) return;
                var window = host.ReadPanelWindow();
                var plan = SidePanelPlanner.Move(window, left, NativeWidth);
                if (plan.Refusal is { } refused)
                {
                    refusal = refused;
                    return;
                }
                Rebase(window.Bounds);
                host.ApplyPanelBounds(plan.Target!, () => IsLeft = left);
                Opened = host.CurrentBounds();
                OrderPanel(this);
            }, cancellationToken).ConfigureAwait(false);
            return refusal;
        }

        public async ValueTask<DesktopSidePanelRefusal?> SetWidthAsync(int width, CancellationToken cancellationToken)
        {
            DesktopSidePanelRefusal? refusal = null;
            await host.InvokeAsync(() =>
            {
                RequireOpen();
                var window = host.ReadPanelWindow();
                var plan = SidePanelPlanner.Resize(window, IsLeft, NativeWidth, width);
                if (plan.Refusal is { } refused)
                {
                    refusal = refused;
                    return;
                }
                Rebase(window.Bounds);
                host.ApplyPanelBounds(plan.Target!, () =>
                {
                    NativeWidth = plan.NativeWidth;
                    Scale = window.Bounds.Scale;
                    Width = width;
                });
                Opened = host.CurrentBounds();
            }, cancellationToken).ConfigureAwait(false);
            return refusal;
        }

        public ValueTask FocusAsync(CancellationToken cancellationToken) =>
            host.InvokeAsync(() =>
            {
                RequireOpen();
                Controller?.MoveFocus();
            }, cancellationToken);

        public ValueTask CloseAsync(CancellationToken cancellationToken) =>
            host.IsOpen
                ? host.InvokeAsync(() => host.ClosePanel(this, DesktopSidePanelCloseReason.Closed), cancellationToken)
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

    private double CurrentScale()
    {
        var dpi = _window == 0 ? 0 : Native.GetDpiForWindow(_window);
        return (dpi == 0 ? 96 : dpi) / 96d;
    }

    private static partial class Native
    {
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AdjustWindowRectExForDpi(ref Rect rectangle, uint style,
            [MarshalAs(UnmanagedType.Bool)] bool menu, uint extendedStyle, uint dpi);
    }
}
