using System.Collections.Concurrent;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

internal sealed partial class WindowsWebView2Host : IWebUiEmbeddedHost
{
    private const uint WmAppDispatch = 0x8001;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmGetObject = 0x003D;
    private const uint WmSize = 0x0005;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmNcCreate = 0x0081;
    private const uint WmWindowPosChanged = 0x0047;
    private const int GwlpUserData = -21;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwMinimize = 6;
    private const int SwMaximize = 3;
    private const int SwRestore = 9;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsPopup = 0x80000000;
    private const uint WsThickFrame = 0x00040000;
    private const uint WsMinimizeBox = 0x00020000;
    private const uint WsMaximizeBox = 0x00010000;
    private const uint WsSysMenu = 0x00080000;
    private const uint WsCaption = 0x00C00000;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const uint WmSetIcon = 0x0080;
    private const uint WmNcLeftButtonDown = 0x00A1;
    private const nuint HitCaption = 2;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int GwlStyle = -16;
    private const uint WmDisplayChange = 0x007E;
    private const uint WsExLayoutRtl = 0x00400000;
    private const uint WsExNoInheritLayout = 0x00100000;
    private const uint SwShowNormal = 1;
    private const uint SwShowMaximized = 3;

    private static readonly string WindowClassName = $"Runic.Desktop.WebView2.{Environment.ProcessId}";
    private static readonly object ClassGate = new();
    private static ushort _windowClass;

    private readonly TaskCompletionSource _ready = NewCompletionSource();
    private readonly TaskCompletionSource _closed = NewCompletionSource();
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _dispatchQueue = new();
    private readonly ConcurrentQueue<NativeDispatchWork> _nativeWork = new();
    private WindowsWebView2Controller? _controller;
    private WebUiEmbeddedHostOptions? _options;
    private Thread? _thread;
    private GCHandle _selfHandle;
    private nint _window;
    private nint _provider;
    private nint _icon;
    private readonly CancellationTokenSource _nativeShutdown = new();
    private int _isOpen;
    private int _disposed;
    private DesktopWindowState? _state;
    private DesktopWindowPlacement? _normal;
    private bool _maximizeWhenShown;
    // Full screen through SetFullScreenAsync or a restored placement (Raymond Chen's toggle): the style and placement
    // to return to. Window thread only.
    private bool _fullScreen;
    private nint _windowedStyle;
    private WindowPlacement _windowedPlacement;
    private bool _windowedMaximized;

    public bool SupportsCloseConfirmation => true;
    // Known before WebView2 loads; DesktopPlatform.GetAvailability advertises the same set.
    internal const DesktopWindowCapabilities KnownCapabilities = DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.Focus |
        DesktopWindowCapabilities.Minimize | DesktopWindowCapabilities.Maximize |
        DesktopWindowCapabilities.Resize | DesktopWindowCapabilities.Move | DesktopWindowCapabilities.Announcements |
        DesktopWindowCapabilities.State | DesktopWindowCapabilities.FileDrop |
        DesktopWindowCapabilities.FullScreen | DesktopWindowCapabilities.MonitorPlacement |
        DesktopWindowCapabilities.MenuBar | DesktopWindowCapabilities.ContextMenu | DesktopWindowCapabilities.FileDrag;
    // A frameless or kiosk window has no frame for a menu bar.
    public DesktopWindowCapabilities Capabilities => _options is { Frameless: true } or { Kiosk: true }
        ? KnownCapabilities & ~DesktopWindowCapabilities.MenuBar
        : KnownCapabilities;

    public DesktopWindowState? State => Volatile.Read(ref _state);

    public event EventHandler<DesktopFileDropEventArgs>? FilesDropped;

    public event EventHandler? StateChanged;
    public bool SupportsDocumentStartScript => true;
    public bool SupportsNativeDispatch => true;
    public bool CheckNativeAccess() => ReferenceEquals(Thread.CurrentThread, _thread);
    public async ValueTask InvokeNativeAsync(Action action, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _nativeShutdown.Token);
        await InvokeAsync(action, linked.Token).ConfigureAwait(false);
    }

    public event EventHandler? Closed;

    internal static bool IsSupported
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
            try
            {
                return WindowsWebView2Interop.IsAvailable();
            }
            catch (Exception exception) when (exception is COMException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    public bool IsOpen => Volatile.Read(ref _isOpen) != 0;

    public nint NativeHandle => Volatile.Read(ref _window);

    public async ValueTask ShowAsync(
        Uri url,
        WebUiEmbeddedHostOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WebView2 embedded hosts are available only on Windows.");
        }
        if (IsOpen)
        {
            await NavigateAsync(url, cancellationToken).ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _options = options;
        _selfHandle = GCHandle.Alloc(this);
        _thread = new Thread(() => Run(url))
        {
            IsBackground = true,
            Name = "Runic Desktop WebView2",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        // Initialization still settles on the window thread. A window that becomes
        // ready after cancellation destroys itself instead of remaining orphaned.
        using (cancellationToken.UnsafeRegister(static (state, token) => ((TaskCompletionSource)state!).TrySetCanceled(token), _ready))
        {
            await _ready.Task.ConfigureAwait(false);
        }
    }

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        return InvokeAsync(() => _controller!.Navigate(url.AbsoluteUri), cancellationToken);
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
        {
            return ValueTask.CompletedTask;
        }
        return new ValueTask(CloseCoreAsync(cancellationToken));
    }

    public ValueTask FocusAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            Native.ShowWindow(_window, SwShow);
            Native.SetForegroundWindow(_window);
            Native.SetFocus(_window);
            _controller!.MoveFocus();
        }, cancellationToken);

    public ValueTask MinimizeAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(() => Native.ShowWindow(_window, SwMinimize), cancellationToken);

    // A UI Automation notification event from the window's provider (WindowsAnnouncementProvider), which Narrator and
    // NVDA speak. Polite messages are queued (NotificationProcessing_All); assertive ones replace those not yet spoken
    // (ImportantMostRecent).
    public ValueTask AnnounceAsync(string message, DesktopAnnouncementPriority priority, CancellationToken cancellationToken = default)
        => InvokeAsync(() => RaiseNotification(_provider, message, priority == DesktopAnnouncementPriority.Assertive), cancellationToken);

    // Whether the keyboard focus is in the page, that is in a window of WebView2's browser process inside this window,
    // rather than in this window itself or in another application. Read on any thread: the focus of the foreground
    // thread, which is the browser's while the page has the focus, else that of the window's own thread.
    internal bool IsWebContentFocused => IsWebContentFocusedIn(_window);

    // The same for any window that hosts a WebView2 controller, such as the child window of a WPF RunicWebView (I-203):
    // the focus is in a window of another process inside it.
    internal static bool IsWebContentFocusedIn(nint window)
    {
        if (window == 0) return false;
        var thread = Native.GetWindowThreadProcessId(window, out var process);
        return thread != 0 && (IsPageFocus(0) || IsPageFocus(thread));

        bool IsPageFocus(uint focusThread)
        {
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            if (!Native.GetGUIThreadInfo(focusThread, ref info) || info.Focus == 0 || !Native.IsChild(window, info.Focus)) return false;
            return Native.GetWindowThreadProcessId(info.Focus, out var focusProcess) != 0 && focusProcess != process;
        }
    }

    // An OLE drag on the window thread while the primary button is held (WindowsDragSource); the call completes when the
    // drag's modal loop ends.
    public async ValueTask<Runic.Platform.DesktopDragEffects> StartDragAsync(Runic.Platform.DesktopDragData data, CancellationToken cancellationToken = default)
    {
        var effect = Runic.Platform.DesktopDragEffects.None;
        await InvokeAsync(() => effect = WindowsDragSource.Run(_window, data), cancellationToken).ConfigureAwait(false);
        return effect;
    }

    private static void RaiseNotification(nint provider, string message, bool assertive)
    {
        const int NotificationKindOther = 4;
        const int NotificationProcessingImportantMostRecent = 1;
        const int NotificationProcessingAll = 2;
        if (provider == 0)
        {
            throw new InvalidOperationException("The window is closed.");
        }
        int result;
        try
        {
            var text = Native.SysAllocString(message);
            var activity = Native.SysAllocString("Runic.Desktop.Announcement");
            try
            {
                result = Native.UiaRaiseNotificationEvent(provider, NotificationKindOther,
                    assertive ? NotificationProcessingImportantMostRecent : NotificationProcessingAll, text, activity);
            }
            finally
            {
                Native.SysFreeString(activity);
                Native.SysFreeString(text);
            }
        }
        catch (EntryPointNotFoundException error)
        {
            throw new NotSupportedException("Windows 10 version 1709 or later is required for announcements.", error);
        }
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    // Ask the window: the user can also maximize or restore it from its frame. A full-screen window leaves full screen first.
    public ValueTask MaximizeAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            if (_fullScreen) LeaveFullScreen();
            Native.ShowWindow(_window, Native.IsZoomed(_window) ? SwRestore : SwMaximize);
        }, cancellationToken);

    public ValueTask SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            if (_options?.Kiosk == true || fullScreen == _fullScreen) return;
            if (fullScreen) EnterFullScreen();
            else LeaveFullScreen();
        }, cancellationToken);

    public ValueTask MoveToMonitorAsync(string monitorId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(monitorId);
        return InvokeAsync(() => MoveToMonitor(monitorId), cancellationToken);
    }

    // Window thread only. Drops the frame and covers the window's monitor; the saved style and placement come back on leaving.
    private void EnterFullScreen()
    {
        var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        if (!Native.GetWindowPlacement(_window, ref placement)) return;
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!Native.GetMonitorInfo(Native.MonitorFromWindow(_window, MonitorDefaultToNearest), ref info)) return;
        _windowedPlacement = placement;
        _windowedMaximized = Native.IsZoomed(_window) || _maximizeWhenShown;
        _maximizeWhenShown = false;
        _windowedStyle = Native.GetWindowLongPtr(_window, GwlStyle);
        _fullScreen = true;
        HideBarForFullScreen();
        Native.SetWindowLongPtr(_window, GwlStyle, _windowedStyle & ~(nint)(WsCaption | WsThickFrame));
        var monitor = info.Monitor;
        Native.SetWindowPos(_window, 0, monitor.Left, monitor.Top, monitor.Right - monitor.Left, monitor.Bottom - monitor.Top,
            SwpNoOwnerZOrder | SwpFrameChanged | SwpNoActivate);
        UpdateState();
    }

    // Window thread only.
    private void LeaveFullScreen()
    {
        if (!_fullScreen) return;
        var visible = Native.IsWindowVisible(_window);
        Native.SetWindowLongPtr(_window, GwlStyle, _windowedStyle);
        _fullScreen = false;
        ShowBarAfterFullScreen();
        var placement = _windowedPlacement;
        // A hidden window keeps hidden and maximizes when it is shown.
        placement.ShowCommand = !visible ? (uint)SwHide : _windowedMaximized ? SwShowMaximized : SwShowNormal;
        if (!visible) _maximizeWhenShown = _windowedMaximized;
        Native.SetWindowPlacement(_window, ref placement);
        Native.SetWindowPos(_window, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoOwnerZOrder | SwpFrameChanged | SwpNoActivate);
        UpdateState();
    }

    // Window thread only: per-monitor DPI aware, so the monitors are in physical pixels.
    private void MoveToMonitor(string monitorId)
    {
        if (WindowsMonitors.Find(monitorId) is not { } target)
        {
            throw new ArgumentException($"No connected monitor has the identifier '{monitorId}'.", nameof(monitorId));
        }
        var work = target.WorkArea;
        if (_fullScreen)
        {
            // The window covers the new monitor and leaves full screen centered on it.
            _windowedPlacement.NormalPosition = Centered(_windowedPlacement.NormalPosition, work, target.Dpi);
            Native.SetWindowPos(_window, 0, target.Bounds.X, target.Bounds.Y, target.Bounds.Width, target.Bounds.Height,
                SwpNoOwnerZOrder | SwpNoZOrder | SwpNoActivate);
            return;
        }
        if (Native.IsZoomed(_window))
        {
            // A maximized window maximizes on the monitor of its restored bounds.
            var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
            if (!Native.GetWindowPlacement(_window, ref placement)) return;
            placement.NormalPosition = Centered(placement.NormalPosition, work, target.Dpi);
            placement.ShowCommand = SwShowMaximized;
            Native.SetWindowPlacement(_window, ref placement);
            return;
        }
        // Moving onto the monitor first lets WM_DPICHANGED scale the window for it; then it fits and centers.
        Native.SetWindowPos(_window, 0, work.X, work.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        if (!Native.GetWindowRect(_window, out var frame)) return;
        var fitted = CenterIn(frame.Right - frame.Left, frame.Bottom - frame.Top, work);
        Native.SetWindowPos(_window, 0, fitted.X, fitted.Y, fitted.Width, fitted.Height, SwpNoZOrder | SwpNoActivate);
    }

    // A restored rectangle in workspace coordinates moved to the center of a work area, its size scaled for the
    // monitor's DPI. Workspace coordinates equal screen coordinates on a monitor without a taskbar at its top or left;
    // the difference is a few pixels at most, which the fit absorbs.
    private Rect Centered(Rect normal, ScreenRect work, uint dpi)
    {
        var current = Native.GetDpiForWindow(_window);
        var width = (int)Math.Round((normal.Right - normal.Left) * (double)dpi / (current == 0 ? 96 : current));
        var height = (int)Math.Round((normal.Bottom - normal.Top) * (double)dpi / (current == 0 ? 96 : current));
        var fitted = CenterIn(width, height, work);
        return new Rect { Left = fitted.X, Top = fitted.Y, Right = fitted.X + fitted.Width, Bottom = fitted.Y + fitted.Height };
    }

    private static ScreenRect CenterIn(int width, int height, ScreenRect work)
    {
        width = Math.Max(1, Math.Min(width, work.Width));
        height = Math.Max(1, Math.Min(height, work.Height));
        return new ScreenRect(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height);
    }

    public ValueTask SetSizeAsync(uint width, uint height, CancellationToken cancellationToken = default)
        => InvokeAsync(() => Native.SetWindowPos(
            _window, 0, 0, 0, ScaleForWindow(width, _window), ScaleForWindow(height, _window), SwpNoZOrder | SwpNoActivate | SwpNoMove), cancellationToken);

    public ValueTask SetPositionAsync(int x, int y, CancellationToken cancellationToken = default)
        => InvokeAsync(() => Native.SetWindowPos(
            _window, 0, x, y, 0, 0, SwpNoZOrder | SwpNoActivate | SwpNoSize), cancellationToken);

    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            // A hidden window that restores a maximized placement opens maximized when it is first shown; a full-screen
            // one keeps covering its monitor.
            Native.ShowWindow(_window, !visible ? SwHide : _maximizeWhenShown && !_fullScreen ? SwMaximize : SwShow);
            if (visible)
            {
                _maximizeWhenShown = false;
            }
            if (_controller is not null)
            {
                _controller.IsVisible = visible;
            }
        }, cancellationToken);

    public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            Native.ReleaseCapture();
            Native.SendMessage(_window, WmNcLeftButtonDown, HitCaption, 0);
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await CloseCoreAsync(CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task CloseCoreAsync(CancellationToken cancellationToken)
    {
        if (IsOpen)
        {
            // An open menu closes first; the window goes once the menu's loop returned.
            await InvokeAsync(() => { if (!DeferDestroyForMenu()) Native.DestroyWindow(_window); }, cancellationToken).ConfigureAwait(false);
            await _closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        if (!IsOpen || _window == 0)
        {
            throw new InvalidOperationException("The embedded WebView window is not open.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (CheckNativeAccess()) { action(); return ValueTask.CompletedTask; }
        var work = new NativeDispatchWork(action, cancellationToken);
        _nativeWork.Enqueue(work);
        // A window destroyed after the open check never drains this queue again.
        if (!Native.PostMessage(_window, WmAppDispatch, 0, 0) || !IsOpen)
        {
            FailNativeWork();
        }
        return new ValueTask(work.WaitAsync());
    }

    private void FailNativeWork()
    {
        while (_nativeWork.TryDequeue(out var work))
        {
            work.Fail(new InvalidOperationException("The embedded WebView window closed before the operation ran."));
        }
    }

    private static int ScaleForWindow(uint value, nint window)
        => checked((int)Math.Round(value * (Native.GetDpiForWindow(window) / 96d)));

    private static int ScaleForDpi(uint value, uint dpi) => checked((int)Math.Round(value * (dpi / 96d)));

    private static uint UnscaleForDpi(int value, uint dpi) => (uint)Math.Max(1, Math.Round(value * 96d / (dpi == 0 ? 96 : dpi)));

    // Fits a restored placement to the monitors that exist now. The position is in physical pixels and the size in
    // device-independent pixels, which scale with the monitor the window lands on.
    private static (int X, int Y, uint Width, uint Height) FitPlacement(int x, int y, uint width, uint height)
    {
        var monitors = WindowsMonitors.TryRead();
        if (monitors.Count == 0)
        {
            return (x, y, width, height);
        }
        var startDpi = WindowsMonitors.Dpi(Native.MonitorFromPoint(new Point { X = x, Y = y }, MonitorDefaultToNearest));
        var start = new ScreenRect(x, y, ScaleForDpi(width, startDpi), ScaleForDpi(height, startDpi));
        if (WindowPlacementClamp.Choose(start, [.. monitors.Select(static monitor => monitor.WorkArea)]) is not { } target)
        {
            return (x, y, width, height);
        }
        var dpi = monitors.First(monitor => monitor.WorkArea == target).Dpi;
        var fitted = WindowPlacementClamp.Clamp(new ScreenRect(x, y, ScaleForDpi(width, dpi), ScaleForDpi(height, dpi)), [target]);
        return (fitted.X, fitted.Y, UnscaleForDpi(fitted.Width, dpi), UnscaleForDpi(fitted.Height, dpi));
    }

    // Window thread only: after every move, resize, maximize, restore and DPI change.
    private void UpdateState()
    {
        if (_window == 0 || Native.IsIconic(_window) || !Native.GetWindowRect(_window, out var frame))
        {
            // A minimized window keeps the state it had.
            return;
        }
        var dpi = Native.GetDpiForWindow(_window);
        var kiosk = _options?.Kiosk == true;
        var fullScreen = kiosk || _fullScreen;
        var zoomed = Native.IsZoomed(_window);
        var maximized = !fullScreen && zoomed;
        var current = new DesktopWindowPlacement
        {
            X = frame.Left,
            Y = frame.Top,
            Width = (int)UnscaleForDpi(frame.Right - frame.Left, dpi),
            Height = (int)UnscaleForDpi(frame.Bottom - frame.Top, dpi),
        };
        if (!maximized && !fullScreen)
        {
            _normal = current;
        }
        var state = new DesktopWindowState
        {
            X = current.X,
            Y = current.Y,
            Width = current.Width,
            Height = current.Height,
            IsMaximized = maximized,
            IsFullScreen = fullScreen,
            MonitorId = WindowsMonitors.IdOf(Native.MonitorFromWindow(_window, MonitorDefaultToNearest)),
            // A full-screen window restores to the maximized state it left.
            Placement = (_normal ?? current) with
            {
                Maximized = _fullScreen ? _windowedMaximized : maximized,
                FullScreen = _fullScreen && !kiosk,
            },
        };
        if (Equals(state, Volatile.Read(ref _state)))
        {
            return;
        }
        Volatile.Write(ref _state, state);
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Never unwind through the window procedure.
        }
    }

    private void Run(Uri url)
    {
        var comInitialized = false;
        nint previousDpiContext = 0;
        try
        {
            // Scope DPI awareness to our owned UI thread, preserving the caller's
            // process policy and any other UI framework hosted in the application.
            previousDpiContext = Native.SetThreadDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2
            if (previousDpiContext == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The WebView2 UI thread could not enable per-monitor DPI awareness.");
            }
            // NativeAOT does not supply the managed WebView2 wrapper's COM setup.
            Marshal.ThrowExceptionForHR(Native.CoInitializeEx(0, 2)); // COINIT_APARTMENTTHREADED
            comInitialized = true;
            EnsureWindowClass();
            var options = _options ?? throw new InvalidOperationException("The embedded host has no launch options.");
            var logicalWidth = options.Width;
            var logicalHeight = options.Height;
            var x = options.X ?? 100;
            var y = options.Y ?? 100;
            if (options.Placement is not null)
            {
                // Keep a restored window on a monitor that exists now, also without a saved position.
                (x, y, logicalWidth, logicalHeight) = FitPlacement(x, y, logicalWidth, logicalHeight);
                _normal = new DesktopWindowPlacement { X = x, Y = y, Width = (int)logicalWidth, Height = (int)logicalHeight };
            }
            var width = checked((int)logicalWidth);
            var height = checked((int)logicalHeight);
            if (options.Centered)
            {
                x = Math.Max(0, (Native.GetSystemMetrics(SmCxScreen) - width) / 2);
                y = Math.Max(0, (Native.GetSystemMetrics(SmCyScreen) - height) / 2);
            }

            // A monitor to open on replaces the position; the window then centers on its work area.
            var monitor = options.Monitor is { } monitorId ? WindowsMonitors.Find(monitorId) : null;
            if (monitor is { } chosen)
            {
                x = chosen.WorkArea.X;
                y = chosen.WorkArea.Y;
            }
            var style = options.Frameless || options.Kiosk
                ? WsPopup | (options.Resizable ? WsThickFrame : 0)
                : WsCaption | WsSysMenu | WsMinimizeBox | (options.Resizable ? WsThickFrame | WsMaximizeBox : 0);
            // Mirrors the frame, caption buttons and system menu; the WebView2 child keeps its own layout (#389).
            var extendedStyle = options.LayoutDirection == Runic.Platform.DesktopLayoutDirection.RightToLeft
                ? WsExLayoutRtl | WsExNoInheritLayout
                : 0;
            _window = Native.CreateWindowEx(
                extendedStyle,
                WindowClassName,
                "Loading...",
                style,
                x,
                y,
                width,
                height,
                0,
                0,
                Native.GetModuleHandle(null),
                GCHandle.ToIntPtr(_selfHandle));
            if (_window == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The WebView2 host window could not be created.");
            }
            _provider = WindowsAnnouncementProvider.Create(_window);

            width = ScaleForWindow(logicalWidth, _window);
            height = ScaleForWindow(logicalHeight, _window);
            if (monitor is { } target)
            {
                var fitted = CenterIn(width, height, target.WorkArea);
                (x, y, width, height) = (fitted.X, fitted.Y, fitted.Width, fitted.Height);
            }
            else if (options.Centered)
            {
                x = Math.Max(0, (Native.GetSystemMetrics(SmCxScreen) - width) / 2);
                y = Math.Max(0, (Native.GetSystemMetrics(SmCyScreen) - height) / 2);
            }
            Native.SetWindowPos(_window, 0, x, y, width, height, SwpNoZOrder | SwpNoActivate);

            SynchronizationContext.SetSynchronizationContext(new WindowSynchronizationContext(this));
            _ = InitializeAsync(url);
            while (Native.GetMessage(out var message, 0, 0, 0) > 0)
            {
                Native.TranslateMessage(in message);
                Native.DispatchMessage(in message);
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            HandleClosed();
        }
        finally
        {
            try
            {
                _controller?.Dispose();
                _controller = null;
                if (_icon != 0)
                {
                    Native.DestroyIcon(_icon);
                    _icon = 0;
                }
                if (_selfHandle.IsAllocated)
                {
                    _selfHandle.Free();
                }
            }
            finally
            {
                if (comInitialized) Native.CoUninitialize();
                if (previousDpiContext != 0) Native.SetThreadDpiAwarenessContext(previousDpiContext);
            }
        }
    }

    private async Task InitializeAsync(Uri url)
    {
        try
        {
            var options = _options!;
            _controller = await WindowsWebView2Controller.CreateAsync(_window, options);
            if (options.StartupProgress is { } startupProgress)
            {
                startupProgress("WebView2 controller created");
                _controller.RegisterStartupProgress(startupProgress);
            }
            _controller.Bounds = ClientBounds();
            _controller.IsVisible = !options.Hidden;
            if (options.Transparent)
            {
                _controller.SetTransparent();
            }
            _controller.RegisterEvents(
                title => Native.SetWindowText(_window, title),
                () => Native.PostMessage(_window, WmClose, 0, 0),
                options.AllowedPermissions,
                url);
            _controller.ApplyWebContent(
                new WebContentRules(options.WebContent, url, options.AdditionalOrigins),
                target => WindowsWebView2Interop.OpenExternally(target),
                // From a WebView2 event: navigate once the event has returned.
                target => _ = Task.Run(async () =>
                {
                    try { await InvokeAsync(() => _controller?.Navigate(target), CancellationToken.None).ConfigureAwait(false); }
                    catch (InvalidOperationException) { } // The window closed meanwhile.
                }));
            if (options.DocumentStartScript is { } documentStartScript)
            {
                await _controller.AddDocumentStartScriptAsync(documentStartScript);
            }
            await _controller.EnableFileDropsAsync(url, drop => FilesDropped?.Invoke(this, drop));
            // The declared shortcuts, in every menu bar presentation: the page sees each key first.
            _controller.RegisterAcceleratorKeys(IsMenuShortcut);
            await _controller.EnableMenuKeysAsync(url, OnMenuKey);
            _controller.Navigate(url.AbsoluteUri);
            if (!string.IsNullOrWhiteSpace(options.IconFile))
            {
                _icon = Native.LoadImage(0, options.IconFile, ImageIcon, 0, 0, LrLoadFromFile);
                if (_icon != 0)
                {
                    Native.SendMessage(_window, WmSetIcon, IconSmall, _icon);
                    Native.SendMessage(_window, WmSetIcon, IconBig, _icon);
                }
            }
            var maximize = options.Placement is { Maximized: true } && !options.Kiosk;
            if (options.Kiosk)
            {
                Native.ShowWindow(_window, SwMaximize);
            }
            else if (options.Hidden)
            {
                Native.ShowWindow(_window, SwHide);
                _maximizeWhenShown = maximize;
            }
            else
            {
                Native.ShowWindow(_window, maximize ? SwMaximize : SwShow);
            }
            // A placement saved full-screen opens full-screen on the monitor of its restored bounds.
            if (options.Placement is { FullScreen: true } && !options.Kiosk)
            {
                EnterFullScreen();
            }
            Native.UpdateWindow(_window);
            Volatile.Write(ref _isOpen, 1);
            UpdateState();
            if (!_ready.TrySetResult())
            {
                // The caller cancelled and no longer owns this window.
                Native.DestroyWindow(_window);
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            // WM_CLOSE would ask the application's close confirmation about a window it never received.
            Native.DestroyWindow(_window);
        }
    }

    private Rectangle ClientBounds()
    {
        Native.GetClientRect(_window, out var bounds);
        return new Rectangle(0, 0, Math.Max(0, bounds.Right), Math.Max(0, bounds.Bottom));
    }

    private void DrainDispatchQueue()
    {
        while (_dispatchQueue.TryDequeue(out var work))
        {
            work.Callback(work.State);
        }
        while (_nativeWork.TryDequeue(out var work))
        {
            work.Run();
        }
    }

    private void HandleClosed()
    {
        if (Interlocked.Exchange(ref _isOpen, 0) == 0 && _closed.Task.IsCompleted)
        {
            return;
        }
        _window = 0;
        _nativeShutdown.Cancel();
        FailNativeWork();
        // The message loop ends with the window; pending initialization never resumes.
        _ready.TrySetException(new InvalidOperationException("The embedded WebView window closed before it was ready."));
        _closed.TrySetResult();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void CloseController()
    {
        _controller?.Dispose();
        _controller = null;
    }

    private static unsafe void EnsureWindowClass()
    {
        lock (ClassGate)
        {
            if (_windowClass != 0)
            {
                return;
            }
            s_taskbarButtonCreated = Native.RegisterWindowMessage("TaskbarButtonCreated");
            var windowClass = new WindowClass
            {
                Size = checked((uint)Marshal.SizeOf<WindowClass>()),
                WindowProcedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProcedure,
                Instance = Native.GetModuleHandle(null),
                Cursor = Native.LoadCursor(0, 32512),
                ClassName = Marshal.StringToHGlobalUni(WindowClassName),
            };
            try
            {
                _windowClass = Native.RegisterClassEx(in windowClass);
                if (_windowClass == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The WebView2 window class could not be registered.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(windowClass.ClassName);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == WmNcCreate)
        {
            var create = (CreateStruct*)lParam;
            Native.SetWindowLongPtr(window, GwlpUserData, create->CreateParams);
        }
        var context = Native.GetWindowLongPtr(window, GwlpUserData);
        var host = context == 0 ? null : GCHandle.FromIntPtr(context).Target as WindowsWebView2Host;
        switch (message)
        {
            case WmAppDispatch:
                host?.DrainDispatchQueue();
                return 0;
            case WmSize:
                if (host?._controller is not null)
                {
                    host._controller.Bounds = host.ClientBounds();
                }
                return 0;
            case WmWindowPosChanged:
                host?.UpdateState();
                // DefWindowProc sends WM_SIZE and WM_MOVE from this message.
                return Native.DefWindowProc(window, message, wParam, lParam);
            case WmDpiChanged:
                var suggested = (Rect*)lParam;
                Native.SetWindowPos(window, 0, suggested->Left, suggested->Top,
                    suggested->Right - suggested->Left, suggested->Bottom - suggested->Top,
                    SwpNoZOrder | SwpNoActivate);
                return 0;
            case WmDisplayChange:
                WindowsMonitors.Invalidate();
                host?.UpdateState();
                return Native.DefWindowProc(window, message, wParam, lParam);
            case WmGetMinMaxInfo:
                if (host?._options is { MinimumWidth: { } minimumWidth, MinimumHeight: { } minimumHeight })
                {
                    var info = (MinMaxInfo*)lParam;
                    info->MinimumTrackSize.X = ScaleForWindow(minimumWidth, window);
                    info->MinimumTrackSize.Y = ScaleForWindow(minimumHeight, window);
                }
                return 0;
            case WmClose:
                if (host?._options?.CloseRequested is { } requestClose)
                {
                    requestClose();
                    return 0;
                }
                Native.DestroyWindow(window);
                return 0;
            case WmCommand when host is not null && lParam == 0 && (wParam >> 16) == 0:
                host.RunBarItem((uint)(wParam & 0xFFFF));
                return 0;
            case WmInitMenuPopup when host is not null && (lParam >> 16) == 0:
                host.InitializePopup((nint)wParam);
                return 0;
            case WmSysCommand when host is not null && host.SwallowsKeyMenu(wParam, lParam):
                return 0;
            case WmEnterMenuLoop:
                host?.EnterMenuLoop();
                return 0;
            case WmExitMenuLoop:
                host?.ExitMenuLoop();
                return 0;
            case WmEnterIdle when wParam == MsgfMenu && host?._contextMenu is not null:
                ContextMenuIdle?.Invoke(lParam);
                return 0;
            case WmGetObject when (int)lParam == WindowsAnnouncementProvider.RootObjectId && host is { _provider: not 0 }:
                return WindowsAnnouncementProvider.Return(window, wParam, lParam, host._provider);
            case WmDestroy:
                if (host is { _provider: not 0 and var provider })
                {
                    host._provider = 0;
                    WindowsAnnouncementProvider.Destroy(window, provider);
                }
                host?.CloseController();
                host?.CompleteContextMenus();
                host?.DestroyHiddenBar();
                host?.HandleClosed();
                Native.PostQuitMessage(0);
                return 0;
            default:
                if (message == s_taskbarButtonCreated && message != 0) TaskbarButtonCreated?.Invoke(window);
                return Native.DefWindowProc(window, message, wParam, lParam);
        }
    }

    // Explorer sends the registered TaskbarButtonCreated message when the window's taskbar button appears, and again
    // after Explorer restarts. Runic.Application.Views.Desktop sets this callback to apply the window's badge and
    // progress again; it runs on the window thread with the window.
    internal static Action<nint>? TaskbarButtonCreated { get; set; }
    private static uint s_taskbarButtonCreated;

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class WindowSynchronizationContext(WindowsWebView2Host host) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            host._dispatchQueue.Enqueue((callback, state));
            if (host._window != 0)
            {
                Native.PostMessage(host._window, WmAppDispatch, 0, 0);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        internal uint Size;
        internal uint Style;
        internal nint WindowProcedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal nint MenuName;
        internal nint ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateStruct
    {
        internal nint CreateParams;
        internal nint Instance;
        internal nint Menu;
        internal nint Parent;
        internal int Height;
        internal int Width;
        internal int Y;
        internal int X;
        internal int Style;
        internal nint Name;
        internal nint Class;
        internal uint ExtendedStyle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        internal Point Reserved;
        internal Point MaximumSize;
        internal Point MaximumPosition;
        internal Point MinimumTrackSize;
        internal Point MaximumTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal Rect Monitor;
        internal Rect Work;
        internal uint Flags;
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        internal uint Length;
        internal uint Flags;
        internal uint ShowCommand;
        internal Point MinimumPosition;
        internal Point MaximumPosition;
        internal Rect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal nint Window;
        internal uint Value;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Point;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        internal uint Size;
        internal uint Flags;
        internal nint Active;
        internal nint Focus;
        internal nint Capture;
        internal nint MenuOwner;
        internal nint MoveSize;
        internal nint Caret;
        internal Rect CaretBounds;
    }

    private static partial class Native
    {
        [LibraryImport("user32.dll")]
        internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsChild(nint parent, nint window);

        [LibraryImport("uiautomationcore.dll")]
        internal static partial int UiaRaiseNotificationEvent(nint provider, int kind, int processing, nint displayString, nint activityId);

        [LibraryImport("oleaut32.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint SysAllocString(string value);

        [LibraryImport("oleaut32.dll")]
        internal static partial void SysFreeString(nint value);

        [LibraryImport("ole32.dll")]
        internal static partial int CoInitializeEx(nint reserved, uint concurrencyModel);

        [LibraryImport("ole32.dll")]
        internal static partial void CoUninitialize();

        [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
        internal static partial ushort RegisterClassEx(in WindowClass windowClass);

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial nint SetThreadDpiAwarenessContext(nint context);

        [LibraryImport("user32.dll")]
        internal static partial uint GetDpiForWindow(nint window);

        [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial uint RegisterWindowMessage(string name);

        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        internal static partial nint CreateWindowEx(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
        internal static partial nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);

        [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyWindow(nint window);

        [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
        internal static partial int GetMessage(out Message message, nint window, uint minimum, uint maximum);

        [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TranslateMessage(in Message message);

        [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
        internal static partial nint DispatchMessage(in Message message);

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

        [LibraryImport("user32.dll")]
        internal static partial void PostQuitMessage(int exitCode);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        internal static partial nint SetWindowLongPtr(nint window, int index, nint value);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        internal static partial nint GetWindowLongPtr(nint window, int index);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetClientRect(nint window, out Rect rectangle);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ShowWindow(nint window, int command);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsZoomed(nint window);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsIconic(nint window);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetWindowRect(nint window, out Rect rectangle);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool EnumDisplayMonitors(nint deviceContext, nint clip, nint callback, nint data);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

        [LibraryImport("user32.dll")]
        internal static partial nint MonitorFromPoint(Point point, uint flags);

        [LibraryImport("user32.dll")]
        internal static partial nint MonitorFromWindow(nint window, uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetWindowPlacement(nint window, ref WindowPlacement placement);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetWindowPlacement(nint window, ref WindowPlacement placement);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindowVisible(nint window);

        // Windows 8.1 and later; the effective DPI (MDT_EFFECTIVE_DPI = 0) that per-monitor aware windows use.
        [LibraryImport("shcore.dll")]
        internal static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UpdateWindow(nint window);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetWindowText(nint window, string text);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetForegroundWindow(nint window);

        [LibraryImport("user32.dll")]
        internal static partial nint SetFocus(nint window);

        [LibraryImport("user32.dll")]
        internal static partial int GetSystemMetrics(int index);

        [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
        internal static partial nint LoadCursor(nint instance, nint cursorName);

        [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        internal static partial nint LoadImage(nint instance, string name, uint type, int width, int height, uint load);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyIcon(nint icon);

        [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
        internal static partial nint SendMessage(nint window, uint message, nuint wParam, nint lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ReleaseCapture();

        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint GetModuleHandle(string? moduleName);
    }
}
