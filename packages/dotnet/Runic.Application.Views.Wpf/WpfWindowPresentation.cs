using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Runic.Platform;

namespace Runic.Application.Views.Wpf;

public static partial class WpfPlatformServices
{
    /// <summary>
    /// Creates the <see cref="IRunicWindowPresentation"/> of a WPF window, for the ViewModels it shows: they bring it to
    /// the front, minimize, hide or show it, make it full-screen, switch it between light and dark, and read whether it
    /// is active, minimized and visible, as on Runic Desktop.
    /// </summary>
    /// <param name="window">The window, on its own thread.</param>
    /// <returns>The window's presentation. It raises its changes on the window's dispatcher.</returns>
    /// <remarks>
    /// <para>
    /// Register it with the window's other services, for example
    /// <c>services.AddSingleton(WpfPlatformServices.ForWindowPresentation(mainWindow))</c>. Every request runs on the
    /// window's dispatcher, so it can come from any thread. <see cref="IRunicWindowPresentation.PresentAsync"/> is
    /// <see cref="PresentAsync"/>; full screen hides the window's frame and maximizes it, and leaving restores both;
    /// <see cref="IRunicWindowPresentation.SetColorSchemeAsync"/> sets the title bar's dark mode and the page's
    /// <c>prefers-color-scheme</c> of every <c>RunicViewHost</c> in the window. WPF draws its own controls, so the
    /// application's WPF theme stays the application's. <see cref="IRunicWindowPresentation.SetDocumentEditedAsync"/>
    /// reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>, as on Windows generally.
    /// </para>
    /// <para>
    /// The activity follows <see cref="Window.Activated"/>, <see cref="Window.Deactivated"/>,
    /// <see cref="Window.StateChanged"/> and <see cref="UIElement.IsVisibleChanged"/>. Once the window closes, requests
    /// report <see cref="PlatformUnavailableReason.OwnerClosed"/> and the activity is <see cref="RunicWindowActivity.Closed"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The call is not on the window's thread.</exception>
    public static IRunicWindowPresentation ForWindowPresentation(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.VerifyAccess();
        return new WpfWindowPresentation(window);
    }
}

// IRunicWindowPresentation of a WPF window; every member that touches the window runs on its dispatcher.
internal sealed partial class WpfWindowPresentation : IRunicWindowPresentation
{
    private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    private static readonly PlatformResult<PlatformUnit> Closed = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
    private static readonly PlatformResult<PlatformUnit> Unsupported = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);

    private const RunicWindowPresentationFeatures Supported = RunicWindowPresentationFeatures.Present |
        RunicWindowPresentationFeatures.Minimize | RunicWindowPresentationFeatures.Visibility |
        RunicWindowPresentationFeatures.FullScreen | RunicWindowPresentationFeatures.ColorScheme |
        RunicWindowPresentationFeatures.Activity;

    private readonly Window _window;
    private RunicWindowActivity _activity;
    private DesktopColorScheme _colorScheme;
    private volatile bool _closed;
    // Dispatcher only: the frame and state that full screen replaced.
    private (WindowStyle Style, ResizeMode Resize, WindowState State)? _windowed;

    internal WpfWindowPresentation(Window window)
    {
        _window = window;
        _activity = Read();
        window.Activated += OnChanged;
        window.Deactivated += OnChanged;
        window.StateChanged += OnChanged;
        window.IsVisibleChanged += OnVisibleChanged;
        window.Closed += OnClosed;
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

    private void OnChanged(object? sender, EventArgs args) => Update(Read());

    private void OnVisibleChanged(object? sender, DependencyPropertyChangedEventArgs args) => Update(Read());

    private void OnClosed(object? sender, EventArgs args)
    {
        _closed = true;
        _window.Activated -= OnChanged;
        _window.Deactivated -= OnChanged;
        _window.StateChanged -= OnChanged;
        _window.IsVisibleChanged -= OnVisibleChanged;
        _window.Closed -= OnClosed;
        Update(RunicWindowActivity.Closed);
    }

    // On the dispatcher, which is the ViewModels' thread in WPF.
    private void Update(RunicWindowActivity activity)
    {
        var previous = _activity;
        if (Equals(previous, activity)) return;
        Volatile.Write(ref _activity, activity);
        if (previous.IsActive != activity.IsActive) Raise(nameof(IsActive));
        if (previous.IsMinimized != activity.IsMinimized) Raise(nameof(IsMinimized));
        if (previous.IsVisible != activity.IsVisible) Raise(nameof(IsVisible));
        Raise(nameof(Activity));
        ActivityChanged?.Invoke(this, new RunicWindowActivityChangedEventArgs(previous, activity));
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

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
            if (fullScreen && _windowed is null)
            {
                _windowed = (_window.WindowStyle, _window.ResizeMode, _window.WindowState);
                // WPF keeps a maximized window's frame unless it leaves the maximized state first.
                _window.WindowState = WindowState.Normal;
                _window.WindowStyle = WindowStyle.None;
                _window.ResizeMode = ResizeMode.NoResize;
                _window.WindowState = WindowState.Maximized;
            }
            else if (!fullScreen && _windowed is { } windowed)
            {
                _windowed = null;
                _window.WindowState = WindowState.Normal;
                _window.WindowStyle = windowed.Style;
                _window.ResizeMode = windowed.Resize;
                _window.WindowState = windowed.State == WindowState.Minimized ? WindowState.Normal : windowed.State;
            }
        }, cancellationToken);

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
            var handle = new WindowInteropHelper(_window).Handle;
            if (handle != 0)
            {
                var dark = colorScheme switch
                {
                    DesktopColorScheme.Dark => 1,
                    DesktopColorScheme.Light => 0,
                    _ => SystemUsesDarkTheme() ? 1 : 0,
                };
                _ = DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref dark, sizeof(int));
                // The non-client area repaints with the new mode only after a frame change.
                _ = SetWindowPos(handle, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            }
            foreach (var view in Descendants<RunicWebView>(_window)) view.SetColorScheme(colorScheme);
            if (_colorScheme == colorScheme) return;
            _colorScheme = colorScheme;
            Raise(nameof(ColorScheme));
        }, cancellationToken);
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

    // HKCU\…\Themes\Personalize\AppsUseLightTheme, as Runic Desktop's WebView2 window reads it.
    private static bool SystemUsesDarkTheme()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    // DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1 and later).
    private const int ImmersiveDarkMode = 20;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
