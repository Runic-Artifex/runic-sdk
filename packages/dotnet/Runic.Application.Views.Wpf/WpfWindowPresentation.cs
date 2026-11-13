using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Runic.Desktop.Internal;
using Runic.Platform;

namespace Runic.Application.Views.Wpf;

public static partial class WpfPlatformServices
{
    /// <summary>
    /// Gets the <see cref="IRunicWindowPresentation"/> of a WPF window, for the ViewModels it shows: they bring it to
    /// the front, minimize, hide or show it, make it full-screen, switch it between light and dark, and read whether it
    /// is active, minimized and visible, as on Runic Desktop.
    /// </summary>
    /// <param name="window">The window, on its own thread.</param>
    /// <param name="loggerFactory">
    /// Logs a handler that throws (event 1130, <c>WindowPresentationHandlerFailed</c>); <see langword="null"/> writes
    /// errors to <see cref="System.Diagnostics.Trace"/>.
    /// </param>
    /// <returns>The window's presentation; the same one for every call with this window. It raises its changes on the window's dispatcher.</returns>
    /// <remarks>
    /// <para>
    /// The presentation belongs to one window. Hand it to that window's ViewModels: a single-window application can
    /// register it as a singleton, <c>services.AddSingleton(WpfPlatformServices.ForWindowPresentation(mainWindow))</c>;
    /// an application with several windows creates one per window and passes it to the ViewModels of that window, for
    /// example through the window's own DI scope or the ViewModel's constructor, never as one singleton for all.
    /// Every request runs on the window's dispatcher, so it can come from any thread.
    /// </para>
    /// <para>
    /// <see cref="IRunicWindowPresentation.PresentAsync"/> is <see cref="PresentAsync"/>; full screen hides the window's
    /// frame and maximizes it, and leaving restores both. <see cref="IRunicWindowPresentation.SetColorSchemeAsync"/> sets
    /// the title bar's dark mode, once the window has a handle if it has none yet, and the page's
    /// <c>prefers-color-scheme</c> of every <c>RunicViewHost</c> in the window, including one that opens or is still
    /// starting. WPF draws its own controls, so the application's WPF theme stays the application's.
    /// <see cref="IRunicWindowPresentation.SetDocumentEditedAsync"/> reports
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>, as on Windows generally.
    /// </para>
    /// <para>
    /// <see cref="IRunicWindowPresentation.MaximizeAsync"/> and <see cref="IRunicWindowPresentation.RestoreAsync"/> set
    /// <see cref="Window.WindowState"/>, leaving full screen first. <see cref="IRunicWindowPresentation.Placement"/> is
    /// in WPF's device-independent pixels: <see cref="Window.RestoreBounds"/> while the window is maximized, minimized or
    /// full-screen, else <see cref="Window.Left"/>, <see cref="Window.Top"/> and its actual size.
    /// <see cref="IRunicWindowPresentation.SetPlacementAsync"/> sets the bounds the window restores to, also while it is
    /// maximized or minimized, then its state; WPF does not fit the bounds on a monitor. Windows activates a window that
    /// it maximizes or brings back from minimized.
    /// </para>
    /// <para>
    /// The activity follows <see cref="Window.Activated"/>, <see cref="Window.Deactivated"/>,
    /// <see cref="Window.StateChanged"/> and <see cref="UIElement.IsVisibleChanged"/>. A handler that throws is logged and
    /// does not stop the others, and a change that a handler causes is raised after the handlers of the current one.
    /// Once the window closes, requests report <see cref="PlatformUnavailableReason.OwnerClosed"/> and the activity is
    /// <see cref="RunicWindowActivity.Closed"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The call is not on the window's thread.</exception>
    public static IRunicWindowPresentation ForWindowPresentation(Window window, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.VerifyAccess();
        if (WpfWindowPresentation.Of(window) is { } existing) return existing;
        var presentation = new WpfWindowPresentation(window, loggerFactory?.CreateLogger(WpfViewsLog.Category) ?? TraceFallbackLogger.Instance);
        window.SetValue(WpfWindowPresentation.PresentationProperty, presentation);
        return presentation;
    }
}

// IRunicWindowPresentation of a WPF window; every member that touches the window runs on its dispatcher.
internal sealed partial class WpfWindowPresentation : IRunicWindowPresentation
{
    private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    private static readonly PlatformResult<PlatformUnit> Closed = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
    private static readonly PlatformResult<PlatformUnit> Unsupported = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);

    internal const RunicWindowPresentationFeatures Supported = RunicWindowPresentationFeatures.Present |
        RunicWindowPresentationFeatures.Minimize | RunicWindowPresentationFeatures.Visibility |
        RunicWindowPresentationFeatures.FullScreen | RunicWindowPresentationFeatures.ColorScheme |
        RunicWindowPresentationFeatures.PerWindowColorScheme | RunicWindowPresentationFeatures.Activity |
        RunicWindowPresentationFeatures.Maximize | RunicWindowPresentationFeatures.Placement;

    // The window's presentation, which ForWindowPresentation returns again and its RunicWebViews read.
    internal static readonly DependencyProperty PresentationProperty = DependencyProperty.RegisterAttached("RunicWindowPresentation",
        typeof(WpfWindowPresentation), typeof(WpfWindowPresentation), new PropertyMetadata(null));

    internal static WpfWindowPresentation? Of(Window? window) => window?.GetValue(PresentationProperty) as WpfWindowPresentation;

    // The scheme set for the window, which a RunicWebView that opens later takes; null until one was set.
    internal static DesktopColorScheme? RequestedColorScheme(Window? window) => Of(window)?._requested;

    private readonly Window _window;
    private readonly ILogger _logger;
    private readonly Queue<Action> _raises = new();
    private bool _raising;
    private RunicWindowActivity _activity;
    private DesktopColorScheme _colorScheme;
    private DesktopColorScheme? _requested;
    private bool _titleBarPending;
    // The window's HwndSource once the title bar watches WM_SETTINGCHANGE through it.
    private HwndSource? _settingChanges;
    private volatile bool _closed;
    // Dispatcher only: the frame and state that full screen replaced.
    private (WindowStyle Style, ResizeMode Resize, WindowState State)? _windowed;
    // sdk#897, W1b: the placement last read on the dispatcher, and whether the window was maximized when it was minimized.
    private RunicWindowPlacement? _placement;
    private bool _maximizedBeforeMinimized;

    internal WpfWindowPresentation(Window window, ILogger logger)
    {
        _window = window;
        _logger = logger;
        _activity = Read();
        _maximizedBeforeMinimized = window.WindowState == WindowState.Maximized;
        _placement = ReadPlacement();
        window.Activated += OnChanged;
        window.Deactivated += OnChanged;
        window.StateChanged += OnChanged;
        window.IsVisibleChanged += OnVisibleChanged;
        window.LocationChanged += OnBoundsChanged;
        window.SizeChanged += OnSizeChanged;
        window.Closed += OnClosed;
    }

    public RunicWindowPlacement? Placement => Volatile.Read(ref _placement);

    // On the dispatcher. A normal window's own bounds; otherwise the ones it restores to. Before the window was shown,
    // Left and Top can be NaN: the placement then has no position.
    private RunicWindowPlacement ReadPlacement()
    {
        var state = _window.WindowState;
        if (state != WindowState.Minimized) _maximizedBeforeMinimized = state == WindowState.Maximized;
        var bounds = state == WindowState.Normal && _windowed is null || _window.RestoreBounds.IsEmpty
            ? new Rect(_window.Left, _window.Top,
                _window.ActualWidth > 0 ? _window.ActualWidth : _window.Width, _window.ActualHeight > 0 ? _window.ActualHeight : _window.Height)
            : _window.RestoreBounds;
        var positioned = double.IsFinite(bounds.X) && double.IsFinite(bounds.Y);
        return new RunicWindowPlacement
        {
            X = positioned ? (int)Math.Round(bounds.X) : null,
            Y = positioned ? (int)Math.Round(bounds.Y) : null,
            Width = double.IsFinite(bounds.Width) ? (int)Math.Round(bounds.Width) : 0,
            Height = double.IsFinite(bounds.Height) ? (int)Math.Round(bounds.Height) : 0,
            Maximized = _windowed is { } windowed ? windowed.State == WindowState.Maximized : _maximizedBeforeMinimized,
            FullScreen = _windowed is not null,
        };
    }

    private void OnBoundsChanged(object? sender, EventArgs args) => UpdatePlacement();

    private void OnSizeChanged(object? sender, SizeChangedEventArgs args) => UpdatePlacement();

    private void UpdatePlacement()
    {
        if (_closed) return;
        var placement = ReadPlacement();
        if (Equals(placement, _placement)) return;
        Volatile.Write(ref _placement, placement);
        RaiseInOrder(() => Raise(nameof(Placement)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<RunicWindowActivityChangedEventArgs>? ActivityChanged;

    public RunicWindowPresentationFeatures Features => _closed ? RunicWindowPresentationFeatures.None : Supported;

    public RunicWindowActivity Activity => Volatile.Read(ref _activity);

    public bool IsActive => Activity.IsActive;

    public bool IsMinimized => Activity.IsMinimized;

    public bool IsVisible => Activity.IsVisible;

    public DesktopColorScheme ColorScheme => _colorScheme;

    private RunicWindowActivity Read()
    {
        var minimized = _window.WindowState == WindowState.Minimized;
        return new RunicWindowActivity(!minimized && _window.IsActive, minimized, !minimized && _window.IsVisible);
    }

    private void OnChanged(object? sender, EventArgs args)
    {
        Update(Read());
        UpdatePlacement();
    }

    private void OnVisibleChanged(object? sender, DependencyPropertyChangedEventArgs args) => Update(Read());

    private void OnClosed(object? sender, EventArgs args)
    {
        _closed = true;
        _window.Activated -= OnChanged;
        _window.Deactivated -= OnChanged;
        _window.StateChanged -= OnChanged;
        _window.IsVisibleChanged -= OnVisibleChanged;
        _window.LocationChanged -= OnBoundsChanged;
        _window.SizeChanged -= OnSizeChanged;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnClosed;
        _settingChanges?.RemoveHook(OnWindowMessage);
        _settingChanges = null;
        Update(RunicWindowActivity.Closed);
    }

    // On the dispatcher, which is the ViewModels' thread in WPF. A change raised by a handler waits for the handlers of
    // the current one.
    internal void Update(RunicWindowActivity activity)
    {
        var previous = _activity;
        if (Equals(previous, activity)) return;
        Volatile.Write(ref _activity, activity);
        RaiseInOrder(() =>
        {
            if (previous.IsActive != activity.IsActive) Raise(nameof(IsActive));
            if (previous.IsMinimized != activity.IsMinimized) Raise(nameof(IsMinimized));
            if (previous.IsVisible != activity.IsVisible) Raise(nameof(IsVisible));
            Raise(nameof(Activity));
            if (ActivityChanged is not { } handlers) return;
            var args = new RunicWindowActivityChangedEventArgs(previous, activity);
            foreach (EventHandler<RunicWindowActivityChangedEventArgs> handler in handlers.GetInvocationList())
            {
                try { handler(this, args); }
                catch (Exception error) when (error is not OutOfMemoryException) { HandlerFailed(error, nameof(ActivityChanged)); }
            }
        });
    }

    private void RaiseInOrder(Action raise)
    {
        _raises.Enqueue(raise);
        if (_raising) return;
        _raising = true;
        try
        {
            while (_raises.TryDequeue(out var next)) next();
        }
        finally { _raising = false; }
    }

    private void Raise(string property)
    {
        if (PropertyChanged is not { } handlers) return;
        var args = new PropertyChangedEventArgs(property);
        foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception error) when (error is not OutOfMemoryException) { HandlerFailed(error, nameof(PropertyChanged)); }
        }
    }

    private void HandlerFailed(Exception error, string handler) =>
        WpfViewsLog.WindowPresentationHandlerFailed(_logger, error, handler, WpfViewsLog.ErrorType(error));

    public ValueTask<PlatformResult<PlatformUnit>> PresentAsync(DesktopActivationContext? activation = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _closed ? ValueTask.FromResult(Closed) : WpfPlatformServices.PresentAsync(_window, cancellationToken);
    }

    public ValueTask<PlatformResult<PlatformUnit>> MinimizeAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _window.WindowState = WindowState.Minimized, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            if (visible) _window.Show();
            else _window.Hide();
        }, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            if (fullScreen) EnterFullScreen();
            else LeaveFullScreen();
        }, cancellationToken);

    // Dispatcher only.
    private void EnterFullScreen()
    {
        if (_windowed is not null) return;
        _windowed = (_window.WindowStyle, _window.ResizeMode, _window.WindowState);
        // WPF keeps a maximized window's frame unless it leaves the maximized state first.
        _window.WindowState = WindowState.Normal;
        _window.WindowStyle = WindowStyle.None;
        _window.ResizeMode = ResizeMode.NoResize;
        _window.WindowState = WindowState.Maximized;
    }

    // Dispatcher only: back to the frame and state full screen replaced, or to state.
    private void LeaveFullScreen(WindowState? state = null)
    {
        if (_windowed is not { } windowed) return;
        _windowed = null;
        _window.WindowState = WindowState.Normal;
        _window.WindowStyle = windowed.Style;
        _window.ResizeMode = windowed.Resize;
        _window.WindowState = state ?? (windowed.State == WindowState.Minimized ? WindowState.Normal : windowed.State);
    }

    // sdk#897, W1b (window-api-0.19/DESIGN.md §3.4): WindowState, leaving full screen first.
    public ValueTask<PlatformResult<PlatformUnit>> MaximizeAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            LeaveFullScreen(WindowState.Maximized);
            _window.WindowState = WindowState.Maximized;
            UpdatePlacement();
        }, cancellationToken);

    // A window minimized while maximized ends normal (D064.13).
    public ValueTask<PlatformResult<PlatformUnit>> RestoreAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            LeaveFullScreen(WindowState.Normal);
            _window.WindowState = WindowState.Normal;
            // Win32 brings a window minimized while maximized back maximized (WPF_RESTORETOMAXIMIZED).
            if (_window.WindowState != WindowState.Normal) _window.WindowState = WindowState.Normal;
            UpdatePlacement();
        }, cancellationToken);

    // Left, Top, Width and Height of a window that is not normal are the bounds it restores to; WPF stores them in the
    // native window's restore placement. The state follows, so a maximized window keeps its new restored bounds.
    public ValueTask<PlatformResult<PlatformUnit>> SetPlacementAsync(RunicWindowPlacement placement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (placement.X is null != placement.Y is null)
            throw new ArgumentException("Set both X and Y of the placement, or neither to keep the Window's position.", nameof(placement));
        return RunAsync(() =>
        {
            var state = placement.Maximized ? WindowState.Maximized : WindowState.Normal;
            if (!placement.FullScreen) LeaveFullScreen(_window.WindowState == WindowState.Minimized ? WindowState.Minimized : state);
            if (placement is { X: { } x, Y: { } y })
            {
                _window.Left = x;
                _window.Top = y;
            }
            if (placement.Width > 0) _window.Width = placement.Width;
            if (placement.Height > 0) _window.Height = placement.Height;
            if (placement.FullScreen)
            {
                EnterFullScreen();
                // The state the window returns to when it leaves full screen.
                _windowed = _windowed!.Value with { State = state };
            }
            else if (_window.WindowState == WindowState.Minimized) _maximizedBeforeMinimized = placement.Maximized;
            else _window.WindowState = state;
            UpdatePlacement();
        }, cancellationToken);
    }

    public ValueTask<PlatformResult<PlatformUnit>> SetDocumentEditedAsync(bool edited, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_closed ? Closed : Unsupported);
    }

    public ValueTask<PlatformResult<PlatformUnit>> SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(colorScheme)) throw new ArgumentOutOfRangeException(nameof(colorScheme));
        return RunAsync(() =>
        {
            _requested = colorScheme;
            ApplyTitleBar();
            foreach (var view in Descendants<RunicWebView>(_window)) view.SetColorScheme(colorScheme);
            if (_colorScheme == colorScheme) return;
            _colorScheme = colorScheme;
            RaiseInOrder(() => Raise(nameof(ColorScheme)));
        }, cancellationToken);
    }

    // The title bar's dark mode, now or, for a window without a handle yet, once it has one. NoPreference gives it the
    // system's app mode, and again after each change of it (#888); a window whose scheme was never set keeps WPF's.
    private void ApplyTitleBar()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == 0)
        {
            if (_titleBarPending) return;
            _titleBarPending = true;
            _window.SourceInitialized += OnSourceInitialized;
            return;
        }
        if (_settingChanges is null && HwndSource.FromHwnd(handle) is { } source)
        {
            _settingChanges = source;
            source.AddHook(OnWindowMessage);
        }
        var dark = WindowsWebView2Host.TitleBarDarkMode(_requested ?? DesktopColorScheme.NoPreference,
            WindowsWebView2Host.SystemUsesDarkTheme(), WebUiSystemTheme.IsHighContrast);
        WindowsWebView2Host.SetImmersiveDarkMode(handle, dark);
        // The non-client area repaints with the new mode only after a frame change.
        _ = SetWindowPos(handle, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    // WM_SETTINGCHANGE of the app mode or a contrast theme: a window with NoPreference takes the system's new app mode.
    // Its RunicWebViews' pages follow the system through their WebView2 profiles already.
    private nint OnWindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmSettingChange && _requested == DesktopColorScheme.NoPreference && !_closed &&
            WindowsWebView2Host.ChangesSystemTitleBar((nuint)wParam, lParam == 0 ? null : Marshal.PtrToStringUni(lParam)))
        {
            ApplyTitleBar();
        }
        return 0;
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        _window.SourceInitialized -= OnSourceInitialized;
        _titleBarPending = false;
        ApplyTitleBar();
    }

    private async ValueTask<PlatformResult<PlatformUnit>> RunAsync(Action request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed || _window.Dispatcher.HasShutdownStarted) return Closed;
        try
        {
            if (_window.Dispatcher.CheckAccess()) request();
            else await _window.Dispatcher.InvokeAsync(request, DispatcherPriority.Normal, cancellationToken);
            return _closed ? Closed : Done;
        }
        // WPF refuses to show or change a window after it closed.
        catch (InvalidOperationException) when (_closed) { return Closed; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _window.Dispatcher.HasShutdownStarted) { return Closed; }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private const int WmSettingChange = 0x001A;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
