using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform;

namespace Runic.Desktop.Internal;

internal sealed partial class MacOsWkWebViewHost : IWebUiEmbeddedHost, IWebUiMainThreadHost
{
    private const ulong BackingBuffered = 2;
    private const ulong ViewWidthSizable = 2;
    private const ulong ViewHeightSizable = 16;
    private const long ApplicationActivationPolicyRegular = 0;
    private const ulong EventMaskAny = ulong.MaxValue;

    private static readonly ObjC Api = new();
    private static readonly ConcurrentDictionary<nint, MacOsWkWebViewHost> Hosts = new();
    private static readonly nint DelegateClass = CreateDelegateClass();
    // Hosts by their WKWebView, for the web content delegate, the drop override and the page's drop label.
    private static readonly ConcurrentDictionary<nint, MacOsWkWebViewHost> WebViews = new();
    private static readonly nint WebViewClass = CreateWebViewClass();
    private static readonly nint TitleObserverClass = CreateTitleObserverClass();
    private static readonly nint WebContentDelegateClass = CreateWebContentDelegateClass();

    // Opens a URL that WebContentRules.ExternalTarget accepted with the system's handler. Tests observe it instead.
    internal static Action<string> ExternalOpener { get; set; } = static target => Api.OpenWithWorkspace(target);
    private static readonly Lazy<nint> FileDropHandler = new(CreateFileDropHandler);
    private FileDropPairing? _drops;
    private IDesktopMenuCommandTarget? _menuCommands;
    // The application's declared menus and their fallback command target; one menu bar per process.
    private static IReadOnlyList<DesktopMenu> s_menuBar = [];
    private static IDesktopMenuCommandTarget? s_applicationMenuCommands;
    // The declared entries shown in the main menu, by native item tag minus one. Main thread only.
    private static ImmutableArray<DeclaredEntry> s_menuEntries = [];
    private const string MenuCommandAction = "runicMenuCommand:";
    private const string DeclaredIdentifier = "runic.app.";
    private const string FileIdentifier = "runic.menu.file";

    public IDesktopMenuCommandTarget? MenuCommands
    {
        get => Volatile.Read(ref _menuCommands);
        set => Volatile.Write(ref _menuCommands, value);
    }

    internal static void SetApplicationMenuCommands(IDesktopMenuCommandTarget? target) => Volatile.Write(ref s_applicationMenuCommands, target);

    // The titles of the standard menus (DesktopHostOptions.StandardMenuTitles), applied when the main menu is completed.
    private static DesktopStandardMenuTitles s_titles = MacOsMainMenu.English;
    internal static void SetStandardMenuTitles(DesktopStandardMenuTitles titles) => Volatile.Write(ref s_titles, titles);

    // The logger of menu command failures (event 3014) while no key window's command path has one.
    private static Microsoft.Extensions.Logging.ILogger? s_menuLogger;
    internal static void SetMenuLogger(Microsoft.Extensions.Logging.ILogger logger) => Volatile.Write(ref s_menuLogger, logger);

    // The application menu bar holds the declared menus for every presentation (D036.13): Page and None keep it.
    public DesktopMenuBarPlacement MenuBarPlacement => IsOpen ? DesktopMenuBarPlacement.Global : DesktopMenuBarPlacement.None;

    // Shows the menus as soon as the main thread can: now while the AppKit loop runs, else when the first window opens.
    internal static void RequestMenuBar(IReadOnlyList<DesktopMenu> menus)
    {
        Volatile.Write(ref s_menuBar, menus);
        if (!OperatingSystem.IsMacOS() || !Api.IsAvailable) return;
        if (IsMainThread || DesktopEventLoop.IsRunning)
        {
            _ = InvokeOnMainAsync(static () =>
            {
                var application = Api.SendNint(Api.GetClass("NSApplication"), "sharedApplication");
                if (!Api.InfoDictionaryBool("LSUIElement"))
                {
                    Api.SendBoolLong(application, "setActivationPolicy:", ApplicationActivationPolicyRegular);
                }
                InstallMainMenu(application);
                ApplyMenuBar(application);
            }, CancellationToken.None).AsTask();
        }
    }
    private static readonly nint MenuTargetClass = CreateMenuTargetClass();
    private static nint s_menuTarget;

    private readonly TaskCompletionSource _closed = NewCompletionSource();
    private nint _application;
    private nint _window;
    private nint _webView;
    private nint _titleObserver;
    private nint _delegate;
    // The web view's navigation and UI delegate, which WebKit references weakly; released with the web view.
    private nint _webContentDelegate;
    private WebContentRules? _webContent;
    // The MediaCapture grant and the origin it is limited to (SEC-37).
    private bool _mediaCaptureGranted;
    private Uri? _presentedUrl;
    private Action? _closeRequested;
    private readonly CancellationTokenSource _nativeShutdown = new();
    private int _isOpen;
    private int _disposed;
    private DesktopWindowState? _state;
    private DesktopWindowPlacement? _normal;
    private const ulong StyleFullScreen = 1UL << 14;

    public bool SupportsCloseConfirmation => true;
    // Known before WebKit loads; DesktopPlatform.GetAvailability advertises these. FileDrop needs the
    // Objective-C WebView subclass.
    internal const DesktopWindowCapabilities KnownCapabilities = DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.Focus |
        DesktopWindowCapabilities.Minimize | DesktopWindowCapabilities.Maximize |
        DesktopWindowCapabilities.Resize | DesktopWindowCapabilities.Move | DesktopWindowCapabilities.Announcements |
        DesktopWindowCapabilities.State | DesktopWindowCapabilities.MenuBar | DesktopWindowCapabilities.ContextMenu |
        DesktopWindowCapabilities.FullScreen | DesktopWindowCapabilities.MonitorPlacement | DesktopWindowCapabilities.FileDrag;
    public DesktopWindowCapabilities Capabilities => KnownCapabilities |
        (WebViewClass != 0 ? DesktopWindowCapabilities.FileDrop : DesktopWindowCapabilities.None);

    public DesktopWindowState? State => Volatile.Read(ref _state);

    public event EventHandler<DesktopFileDropEventArgs>? FilesDropped;

    public event EventHandler? StateChanged;
    public bool SupportsDocumentStartScript => true;
    public bool SupportsNativeDispatch => true;
    public bool CheckNativeAccess() => IsMainThread;
    public async ValueTask InvokeNativeAsync(Action action, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _nativeShutdown.Token);
        await InvokeAsync(action, linked.Token).ConfigureAwait(false);
    }

    public event EventHandler? Closed;

    internal static bool IsSupported => OperatingSystem.IsMacOS() && Api.IsAvailable;

    internal static bool IsMainThread => OperatingSystem.IsMacOS() && Native.PthreadMainNp() != 0;

    public bool IsOpen => Volatile.Read(ref _isOpen) != 0;

    public nint NativeHandle => IsOpen ? Volatile.Read(ref _window) : 0;

    public ValueTask ShowAsync(
        Uri url,
        WebUiEmbeddedHostOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("WKWebView is required for embedded WebViews on macOS.");
        }
        if (!IsMainThread && DesktopEventLoop.IsRunning)
            return InvokeOnMainAsync(() => ShowAsync(url, options, cancellationToken).AsTask().GetAwaiter().GetResult(), cancellationToken);
        if (!IsMainThread)
        {
            throw new InvalidOperationException(
                "The first macOS embedded WebView must be shown from the process main thread. Use ShowWebView before awaiting other work.");
        }
        if (IsOpen)
        {
            Navigate(url);
            return ValueTask.CompletedTask;
        }

        _closeRequested = options.CloseRequested;
        Create(url, options);
        return ValueTask.CompletedTask;
    }

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        return InvokeAsync(() => Navigate(url), cancellationToken);
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
        {
            return ValueTask.CompletedTask;
        }
        // A context menu of the window tracks modally on the main thread; it closes first, returning null.
        if (Volatile.Read(ref s_contextMenu) is { } menu && ReferenceEquals(menu.Owner, this)) menu.Dismiss();
        return new ValueTask(CloseCoreAsync(cancellationToken));
    }

    public ValueTask FocusAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            Api.SendVoidNint(_window, "makeKeyAndOrderFront:", 0);
            Api.SendVoidBool(_application, "activateIgnoringOtherApps:", true);
        }, cancellationToken);

    public ValueTask MinimizeAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(() => Api.SendVoidNint(_window, "miniaturize:", 0), cancellationToken);

    // NSAccessibilityAnnouncementRequestedNotification for the window, which VoiceOver speaks; assertive messages
    // have NSAccessibilityPriorityHigh, polite ones NSAccessibilityPriorityMedium.
    public ValueTask AnnounceAsync(string message, DesktopAnnouncementPriority priority, CancellationToken cancellationToken = default)
        => InvokeAsync(() => Api.Announce(_window, message, priority == DesktopAnnouncementPriority.Assertive), cancellationToken);

    public ValueTask MaximizeAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(() => Api.SendVoidNint(_window, "zoom:", 0), cancellationToken);

    // setContentSize: keeps the frame's AppKit origin, its bottom-left corner; the top-left corner stays instead, as on
    // Windows and GTK, so a resize leaves X and Y as they were.
    public ValueTask SetSizeAsync(uint width, uint height, CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            var before = Api.SendRect(_window, "frame");
            Api.SendVoidSize(_window, "setContentSize:", new CGSize(width, height));
            if (IsFullScreen) return;
            var after = Api.SendRect(_window, "frame");
            Api.SendVoidPoint(_window, "setFrameOrigin:", new CGPoint(after.X, before.Y + before.Height - after.Height));
        }, cancellationToken);

    // x and y are the frame's top-left corner, measured down from the primary screen's top (MacOsCoordinates).
    public ValueTask SetPositionAsync(int x, int y, CancellationToken cancellationToken = default)
        => InvokeAsync(() => SetFramePosition(x, y, bottomLeft: false), cancellationToken);

    // Main thread only. Moves the frame's corner to x, y: its top-left corner in Runic's coordinates, or for a placement
    // saved by Runic 0.11 or earlier its bottom-left corner in AppKit's, which setFrameOrigin: takes as it is.
    private void SetFramePosition(int x, int y, bool bottomLeft)
    {
        var frame = Api.SendRect(_window, "frame");
        Api.SendVoidPoint(_window, "setFrameOrigin:",
            new CGPoint(x, MacOsCoordinates.FrameBottom(y, frame.Height, PrimaryScreenHeight(), bottomLeft)));
    }

    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            if (visible)
            {
                Api.SendVoidNint(_window, "makeKeyAndOrderFront:", 0);
            }
            else
            {
                Api.SendVoidNint(_window, "orderOut:", 0);
            }
        }, cancellationToken);

    public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            var currentEvent = Api.SendNint(_application, "currentEvent");
            if (currentEvent != 0)
            {
                Api.SendVoidNint(_window, "performWindowDragWithEvent:", currentEvent);
            }
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await CloseCoreAsync(CancellationToken.None).ConfigureAwait(false);
        await InvokeOnMainAsync(ReleaseNativeObjects, CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public void ProcessEvents()
    {
        if (!IsMainThread || _application == 0)
        {
            return;
        }

        ProcessPendingMainThreadWork();
        PumpApplicationEvents(_application);
    }

    // Sends every queued AppKit event, which also runs the main dispatch queue.
    private static void PumpApplicationEvents(nint application)
    {
        var date = Api.SendNint(Api.GetClass("NSDate"), "distantPast");
        var mode = Api.CreateString("kCFRunLoopDefaultMode");
        try
        {
            while (true)
            {
                var webEvent = Api.NextEvent(application, EventMaskAny, date, mode, true);
                if (webEvent == 0)
                {
                    break;
                }
                Api.SendVoidNint(application, "sendEvent:", webEvent);
            }
            Api.SendVoid(application, "updateWindows");
        }
        finally
        {
            Api.SendVoid(mode, "release");
        }
    }

    // Runs the main-thread work queued on the main dispatch queue, when called on the main thread without the Desktop
    // event loop, which runs it itself.
    internal static void ProcessPendingMainThreadWork()
    {
        if (!IsMainThread)
        {
            return;
        }

        Api.RunLoopOnce();
    }

    private async Task CloseCoreAsync(CancellationToken cancellationToken)
    {
        if (IsOpen)
        {
            await InvokeAsync(() => Api.SendVoid(_window, "close"), cancellationToken).ConfigureAwait(false);
            await _closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private unsafe ValueTask InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsOpen)
        {
            throw new InvalidOperationException("The embedded WebView window is not open.");
        }
        if (IsMainThread)
        {
            action();
            return ValueTask.CompletedTask;
        }

        return InvokeOnMainAsync(action, cancellationToken);
    }

    private static ValueTask InvokeOnMainAsync(Action action, CancellationToken cancellationToken)
    {
        // The main dispatch queue, which the run loop drains in its common modes: also while a menu tracks or a window
        // live-resizes.
        return MacOsMainThread.InvokeAsync(action, cancellationToken);
    }

    private void Create(Uri url, WebUiEmbeddedHostOptions options)
    {
        if (WebContentDelegateClass == 0)
        {
            // Without its delegate the window could not apply its web content policy.
            throw new InvalidOperationException("The Objective-C runtime could not create the WKWebView navigation delegate.");
        }
        _application = Api.SendNint(Api.GetClass("NSApplication"), "sharedApplication");
        // A bundle with LSUIElement runs without a Dock icon and menu bar, also while it shows a window.
        if (!Api.InfoDictionaryBool("LSUIElement"))
        {
            Api.SendBoolLong(_application, "setActivationPolicy:", ApplicationActivationPolicyRegular);
        }
        // DesktopEventLoop.Run's AppKit loop sends finishLaunching once; without it, the first window does.
        MacOsApplication.EnsureLaunched(_application);
        InstallMainMenu(_application);
        ApplyMenuBar(_application);

        var style = MacOsWindowChrome.StyleMask(options);
        var frame = new CGRect(0, 0, options.Width, options.Height);
        // Runic's NSWindow subclass, so a frameless or kiosk window can become key and take keys.
        _window = Api.AllocInitWindow(WindowClass, frame, style, BackingBuffered, false);
        if (_window == 0)
        {
            throw new InvalidOperationException("AppKit could not create the embedded WebView window.");
        }
        _delegate = Api.SendNint(Api.SendNint(DelegateClass, "alloc"), "init");
        Api.SendVoidNint(_window, "setDelegate:", _delegate);
        // DesktopWindowOptions.ColorScheme: the window, its web view and the page's prefers-color-scheme follow the
        // window's appearance; nil, the default, follows the system.
        if (Api.NamedAppearance(options.ColorScheme) is var appearance and not 0)
        {
            Api.SendVoidNint(_window, "setAppearance:", appearance);
        }
        Api.SendVoidBool(_window, "setReleasedWhenClosed:", false);
        Api.SendVoidBool(_window, "setMovableByWindowBackground:", options.Frameless);
        ApplyTitleBar(options);
        if (options.MinimumWidth is { } minimumWidth && options.MinimumHeight is { } minimumHeight)
        {
            Api.SendVoidSize(_window, "setContentMinSize:", new CGSize(minimumWidth, minimumHeight));
        }

        _webView = Api.AllocInitWebView(frame, options.DocumentStartScript, WebViewClass, WebViewClass != 0 ? FileDropHandler.Value : 0,
            WebViewClass != 0 && _usesDragRegions ? WindowChromeHandler.Value : 0);
        if (_webView == 0)
        {
            Api.SendVoid(_window, "close");
            throw new InvalidOperationException("WebKit could not create a WKWebView.");
        }
        _reportDiagnostic = options.ReportDiagnostic;
        ApplySpellCheckingLanguages(options.SpellCheckingLanguages);
        if (WebViewClass != 0)
        {
            _drops = new FileDropPairing(drop => FilesDropped?.Invoke(this, drop), FileDropSupport.LabelWait);
        }
        WebViews[_webView] = this;
        _mediaCaptureGranted = (options.AllowedPermissions & DesktopPermissionGrant.MediaCapture) != 0;
        _presentedUrl = url;
        ApplyWebContent(new WebContentRules(options.WebContent, url, options.AdditionalOrigins));
        // underPageBackgroundColor requires macOS 12; ask the created view, not the unset field.
        if (options.Transparent && Api.RespondsToSelector(_webView, "setUnderPageBackgroundColor:"))
        {
            var clear = Api.SendNint(Api.GetClass("NSColor"), "clearColor");
            Api.SendVoidBool(_window, "setOpaque:", false);
            Api.SendVoidNint(_window, "setBackgroundColor:", clear);
            Api.SendVoidNint(_webView, "setUnderPageBackgroundColor:", clear);
        }
        Api.SendVoidUlong(_webView, "setAutoresizingMask:", ViewWidthSizable | ViewHeightSizable);
        // The window takes the page's <title>, as in Safari, on GTK and WebView2: VoiceOver and the Window menu name the
        // window by it.
        _titleObserver = TitleObserverClass != 0 ? Api.SendNint(Api.SendNint(TitleObserverClass, "alloc"), "init") : 0;
        if (_titleObserver != 0)
        {
            Api.AddObserver(_webView, _titleObserver, "title");
        }
        // A trackpad pinch magnifies the page, as in Safari; WebKit lets the page cancel the gesture first.
        Api.SendVoidBool(_webView, "setAllowsMagnification:", true);
        var contentView = Api.SendNint(_window, "contentView");
        // Under a hidden or transparent title bar the content view fills the window, and the page with it.
        Api.SendVoidRect(_webView, "setFrame:", Api.SendRect(contentView, "bounds"));
        Api.SendVoidNint(contentView, "addSubview:", _webView);
        // Keys reach the page without a click first, and the page sees Command keys before the View menu.
        Api.SendNintNint(_window, "makeFirstResponder:", _webView);
        if (options.Centered)
        {
            Api.SendVoid(_window, "center");
        }
        else if (options.X is { } x && options.Y is { } y)
        {
            // X and Y come from the placement when there is one; a placement from Runic 0.11 or earlier converts here,
            // and the window then reports a top-left placement.
            SetFramePosition(x, y, MacOsCoordinates.IsBottomLeft(options.Placement));
        }
        if (options.Placement is not null)
        {
            FitPlacement(hasPosition: options.X is not null && options.Y is not null);
        }
        // A monitor to open on replaces the position; a placement with a position leaves it unset.
        if (options.MonitorId is { } monitorId && FindScreen(monitorId) is { } screen)
        {
            MoveToScreen(screen);
        }
        _kiosk = options.Kiosk;
        if (!string.IsNullOrWhiteSpace(options.IconFile))
        {
            var path = Api.CreateString(options.IconFile);
            var image = Api.SendNintNint(Api.SendNint(Api.GetClass("NSImage"), "alloc"), "initWithContentsOfFile:", path);
            if (image != 0)
            {
                Api.SendVoidNint(_application, "setApplicationIconImage:", image);
                Api.SendVoid(image, "release");
            }
            Api.SendVoid(path, "release");
        }

        Hosts[_window] = this;
        Volatile.Write(ref _isOpen, 1);
        ApplyTrafficLights();
        Navigate(url);
        if (!options.Hidden)
        {
            Api.SendVoidNint(_window, "makeKeyAndOrderFront:", 0);
            Api.SendVoidBool(_application, "activateIgnoringOtherApps:", true);
        }
        if (options.Kiosk)
        {
            // A borderless window enters full screen only as a full-screen primary window.
            RequestFullScreen();
        }
        else
        {
            if (options.Placement is { Maximized: true } && !Api.SendBool(_window, "isZoomed"))
            {
                Api.SendVoidNint(_window, "zoom:", 0);
            }
            // A placement saved full-screen opens full-screen on the screen of its restored bounds.
            if (options.Placement is { FullScreen: true })
            {
                RequestFullScreen();
            }
        }
        UpdateState();
    }

    // Keeps a restored window on a screen that exists now: the frame, title bar included, moves into the visible
    // frame (without the menu bar and Dock) it overlaps most, or the nearest, and shrinks to fit it.
    private void FitPlacement(bool hasPosition)
    {
        var areas = Api.GetScreenVisibleFrames();
        var frame = Api.SendRect(_window, "frame");
        var rect = new ScreenRect((int)Math.Round(frame.X), (int)Math.Round(frame.Y),
            (int)Math.Round(frame.Width), (int)Math.Round(frame.Height));
        if (hasPosition)
        {
            rect = WindowPlacementClamp.Clamp(rect, areas);
            Api.SendVoidRectBool(_window, "setFrame:display:", new CGRect(rect.X, rect.Y, rect.Width, rect.Height), false);
        }
        else
        {
            var mainScreen = Api.SendNint(Api.GetClass("NSScreen"), "mainScreen");
            ScreenRect? main = null;
            if (mainScreen != 0)
            {
                var visible = Api.SendRect(mainScreen, "visibleFrame");
                main = new ScreenRect((int)Math.Round(visible.X), (int)Math.Round(visible.Y),
                    (int)Math.Round(visible.Width), (int)Math.Round(visible.Height));
            }
            var (width, height) = WindowPlacementClamp.ClampSize(rect.Width, rect.Height, main ?? WindowPlacementClamp.Largest(areas));
            Api.SendVoidRectBool(_window, "setFrame:display:", new CGRect(frame.X, frame.Y, width, height), false);
            // Without a saved position the window opens centered, where AppKit places a new document window.
            Api.SendVoid(_window, "center");
        }
        _normal = ReadBounds();
    }

    // Main thread only. The frame's top-left corner, the units of SetPositionAsync, and the content size, the units of
    // setContentSize:.
    private DesktopWindowPlacement ReadBounds()
    {
        var frame = Api.SendRect(_window, "frame");
        var content = Api.SendRectRect(_window, "contentRectForFrameRect:", frame);
        var (x, y) = MacOsCoordinates.FrameTopLeft(frame.X, frame.Y, frame.Height, PrimaryScreenHeight());
        return new DesktopWindowPlacement
        {
            X = x,
            Y = y,
            Width = (int)Math.Max(1, Math.Round(content.Width)),
            Height = (int)Math.Max(1, Math.Round(content.Height)),
            Origin = DesktopCoordinateOrigin.TopLeft,
        };
    }

    // Main thread only: after every move, resize, zoom and full-screen change.
    private void UpdateState()
    {
        if (_window == 0 || Api.SendBool(_window, "isMiniaturized"))
        {
            // A minimized window keeps the state it had.
            return;
        }
        var fullScreen = (Api.SendUlong(_window, "styleMask") & StyleFullScreen) != 0;
        if (_fullScreenTransition)
        {
            // Reported once the animation ends, so the bounds before full screen stay the restorable ones.
            return;
        }
        if (!fullScreen && _pendingMonitor is not null)
        {
            ApplyPendingMonitor();
        }
        var maximized = !fullScreen && Api.SendBool(_window, "isZoomed");
        var current = ReadBounds();
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
            MonitorId = CurrentMonitorId(),
            Placement = (_normal ?? current) with { Maximized = maximized, FullScreen = fullScreen && !_kiosk },
        };
        if (Equals(state, Volatile.Read(ref _state)))
        {
            return;
        }
        Volatile.Write(ref _state, state);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FullScreenTransitionStarted(nint self, nint selector, nint notification)
    {
        try
        {
            if (Hosts.TryGetValue(Api.SendNint(notification, "object"), out var host)) host._fullScreenTransition = true;
        }
        catch
        {
            // Never unwind through AppKit.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FullScreenTransitionEnded(nint self, nint selector, nint notification)
    {
        try
        {
            EndFullScreenTransition(Api.SendNint(notification, "object"));
        }
        catch
        {
            // Never unwind through AppKit.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FullScreenTransitionFailed(nint self, nint selector, nint window)
    {
        try
        {
            EndFullScreenTransition(window);
            if (Hosts.TryGetValue(window, out var host)) host.RetryFullScreen();
        }
        catch
        {
            // Never unwind through AppKit.
        }
    }

    private static void EndFullScreenTransition(nint window)
    {
        if (Hosts.TryGetValue(window, out var host) && host.IsOpen)
        {
            host._fullScreenTransition = false;
            host.UpdateState();
            host.ApplyTrafficLights();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WindowGeometryChanged(nint self, nint selector, nint notification)
    {
        try
        {
            var window = Api.SendNint(notification, "object");
            if (Hosts.TryGetValue(window, out var host) && host.IsOpen)
            {
                host.UpdateState();
                // AppKit lays the title bar out again after a resize.
                host.ApplyTrafficLights();
            }
        }
        catch
        {
            // Never unwind through AppKit.
        }
    }

    private void Navigate(Uri url)
    {
        var value = Api.CreateString(url.AbsoluteUri);
        try
        {
            var nativeUrl = Api.SendNintNint(Api.GetClass("NSURL"), "URLWithString:", value);
            var request = Api.SendNintNint(Api.GetClass("NSURLRequest"), "requestWithURL:", nativeUrl);
            Api.SendNintNint(_webView, "loadRequest:", request);
        }
        finally
        {
            Api.SendVoid(value, "release");
        }
    }

    // DesktopWebContentPolicy (SEC-04), before the first load: one object is the web view's navigation delegate, which
    // decides top-level navigations, new-window links and responses, and its UI delegate, which answers window.open.
    // Both are weak references in WebKit; the host keeps the object until ReleaseNativeObjects.
    private void ApplyWebContent(WebContentRules rules)
    {
        _webContent = rules;
        // isInspectable (macOS 13.3) enables Safari's Web Inspector; earlier systems decide it themselves.
        if (Api.RespondsToSelector(_webView, "setInspectable:"))
        {
            Api.SendVoidBool(_webView, "setInspectable:", rules.DevToolsEnabled);
        }
        _webContentDelegate = Api.SendNint(Api.SendNint(WebContentDelegateClass, "alloc"), "init");
        Api.SendVoidNint(_webView, "setNavigationDelegate:", _webContentDelegate);
        Api.SendVoidNint(_webView, "setUIDelegate:", _webContentDelegate);
    }

    // Main thread, after the decision handler ran: hands a URL to the system's handler, or loads one in this window.
    private void Complete(MacOsWebContent.Outcome outcome)
    {
        if (!IsOpen)
        {
            return;
        }
        if (outcome.OpenExternally is { } target)
        {
            ExternalOpener(target);
        }
        if (outcome.NavigateInWindow is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Navigate(uri);
        }
    }

    // Main thread only. Applies a zoom command to the page of the key window.
    private void Zoom(PageZoomCommand command)
    {
        if (_webView == 0 || !Api.RespondsToSelector(_webView, "setPageZoom:"))
        {
            return;
        }
        Api.SendVoidDouble(_webView, "setPageZoom:", MacOsPageZoom.Apply(command, Api.SendDouble(_webView, "pageZoom")));
        if (command == PageZoomCommand.Reset)
        {
            Api.SendVoidDouble(_webView, "setMagnification:", 1.0);
        }
    }

    // Completes the application's main menu once, when its first window opens (MacOsMainMenu). Main thread only.
    private static void InstallMainMenu(nint application)
    {
        if (s_menuTarget != 0 || MenuTargetClass == 0)
        {
            return;
        }
        s_menuTarget = Api.SendNint(Api.SendNint(MenuTargetClass, "alloc"), "init");
        var mainMenu = Api.SendNint(application, "mainMenu");
        bool complete = MacOsMainMenu.Completes(mainMenu != 0, mainMenu != 0 && Api.HasAnyKeyEquivalent(mainMenu));
        if (mainMenu == 0)
        {
            mainMenu = Api.NewMenu("");
            Api.SendVoidNint(application, "setMainMenu:", mainMenu);
            Api.SendVoid(mainMenu, "release");
        }
        var titles = Volatile.Read(ref s_titles);
        if (complete)
        {
            var name = Api.Text(Api.SendNint(Api.SendNint(Api.GetClass("NSProcessInfo"), "processInfo"), "processName"));
            // The first item's menu is the application menu, which AppKit titles with the application's name.
            var applicationMenu = Api.SendNint(mainMenu, "numberOfItems") > 0
                ? Api.SendNint(Api.SendNintNint(mainMenu, "itemAtIndex:", 0), "submenu") : 0;
            if (applicationMenu == 0)
            {
                applicationMenu = Api.InsertSubmenu(mainMenu, "", 0, null);
            }
            if (Api.SendNint(applicationMenu, "numberOfItems") == 0)
            {
                Api.AddItems(applicationMenu, MacOsMainMenu.ApplicationItems(titles), name, s_menuTarget);
            }
            if (Api.FindSubmenu(mainMenu, MacOsMainMenu.EditIdentifier, MacOsMainMenu.EditActions) == 0)
            {
                Api.AddItems(Api.InsertSubmenu(mainMenu, titles.Edit, 1, MacOsMainMenu.EditIdentifier), MacOsMainMenu.EditItems(titles), name, s_menuTarget);
            }
            // The Cocoa template's File menu: Close sends performClose:, so a window's close guard applies.
            if (Api.FindSubmenu(mainMenu, FileIdentifier, MacOsMainMenu.FileActions) == 0)
            {
                Api.AddItems(Api.InsertSubmenu(mainMenu, titles.File, 1, FileIdentifier), MacOsMainMenu.FileItems(titles), name, s_menuTarget);
            }
            InstallOpenRecent(mainMenu, titles);
        }
        // The zoom items go into any main menu that has no Command and = or + of its own.
        if (!Api.HasCommandKey(mainMenu, "=") && !Api.HasCommandKey(mainMenu, "+"))
        {
            var view = Api.FindSubmenu(mainMenu, MacOsMainMenu.ViewIdentifier, MacOsMainMenu.ViewActions);
            if (view == 0)
            {
                var edit = Api.IndexOfSubmenu(mainMenu, Api.FindSubmenu(mainMenu, MacOsMainMenu.EditIdentifier, MacOsMainMenu.EditActions));
                view = Api.InsertSubmenu(mainMenu, titles.View, edit >= 0 ? edit + 1 : Api.SendNint(mainMenu, "numberOfItems"), MacOsMainMenu.ViewIdentifier);
            }
            else if (Api.SendNint(view, "numberOfItems") > 0)
            {
                Api.SendVoidNint(view, "addItem:", Api.SendNint(Api.GetClass("NSMenuItem"), "separatorItem"));
            }
            foreach (var item in MacOsPageZoom.Items(titles))
            {
                Api.AddMenuItem(view, item.Title, MacOsPageZoom.Selector(item.Command), item.KeyEquivalent, MacOsPageZoom.CommandModifier,
                    s_menuTarget, item.Hidden);
            }
        }
        if (complete && Api.SendNint(application, "windowsMenu") == 0
            && Api.FindSubmenu(mainMenu, MacOsMainMenu.WindowIdentifier, MacOsMainMenu.WindowActions) == 0)
        {
            var window = Api.InsertSubmenu(mainMenu, titles.Window, Api.SendNint(mainMenu, "numberOfItems"), MacOsMainMenu.WindowIdentifier);
            Api.AddItems(window, MacOsMainMenu.WindowItems(titles), "", s_menuTarget);
            Api.SendVoidNint(application, "setWindowsMenu:", window);
        }
    }

    // Main thread only. Replaces the declared menus: removes those of the previous bar, then inserts the File role's
    // items before Close, the Application role's after About, Help last, and the others before the Window menu.
    private static void ApplyMenuBar(nint application)
    {
        var mainMenu = Api.SendNint(application, "mainMenu");
        if (mainMenu == 0 || s_menuTarget == 0) return;
        Api.RemoveIdentifiedItems(mainMenu, DeclaredIdentifier);
        // The standard File menu takes the declared File menu's title, and its own back without one.
        if (Api.FindSubmenu(mainMenu, FileIdentifier, MacOsMainMenu.FileActions) is var standardFile and not 0)
        {
            Api.SetTitle(Api.SendNintNint(mainMenu, "itemAtIndex:", Api.IndexOfSubmenu(mainMenu, standardFile)), standardFile,
                Volatile.Read(ref s_titles).File);
        }
        var menus = Volatile.Read(ref s_menuBar);
        var declared = ImmutableArray.CreateBuilder<DeclaredEntry>();
        foreach (var menu in menus)
        {
            switch (menu.Role)
            {
                case DesktopMenuRole.Application:
                    var applicationMenu = Api.SendNint(mainMenu, "numberOfItems") > 0
                        ? Api.SendNint(Api.SendNintNint(mainMenu, "itemAtIndex:", 0), "submenu") : 0;
                    if (applicationMenu != 0)
                    {
                        AddEntries(applicationMenu, menu.Items, insertAt: Math.Min(2, (int)Api.SendNint(applicationMenu, "numberOfItems")),
                            separate: true, menu.IsEnabled, declared);
                    }
                    break;
                case DesktopMenuRole.File:
                    var file = Api.FindSubmenu(mainMenu, FileIdentifier, MacOsMainMenu.FileActions);
                    if (file == 0)
                    {
                        file = Api.InsertSubmenu(mainMenu, menu.Title, DesktopMenuBar.InsertionIndex(DesktopMenuRole.File,
                            (int)Api.SendNint(mainMenu, "numberOfItems"), -1), DeclaredIdentifier + "file");
                        Declare(Api.SendNintNint(mainMenu, "itemAtIndex:", Api.IndexOfSubmenu(mainMenu, file)), menu, menu.IsEnabled, declared);
                        AddEntries(file, menu.Items, insertAt: null, separate: false, menu.IsEnabled, declared);
                    }
                    else
                    {
                        Api.SetTitle(Api.SendNintNint(mainMenu, "itemAtIndex:", Api.IndexOfSubmenu(mainMenu, file)), file, menu.Title);
                        // Open Recent follows the declared items, such as Open…, without a separator, as in the Cocoa template.
                        AddEntries(file, menu.Items, insertAt: 0, separate: FindOpenRecent(file) == 0, menu.IsEnabled, declared);
                    }
                    break;
                default:
                    var windowMenu = Api.SendNint(application, "windowsMenu");
                    var count = (int)Api.SendNint(mainMenu, "numberOfItems");
                    var index = DesktopMenuBar.InsertionIndex(menu.Role, count, windowMenu == 0 ? -1 : (int)Api.IndexOfSubmenu(mainMenu, windowMenu));
                    var submenu = Api.InsertSubmenu(mainMenu, menu.Title, index, DeclaredIdentifier + "menu");
                    Declare(Api.SendNintNint(mainMenu, "itemAtIndex:", Api.IndexOfSubmenu(mainMenu, submenu)), menu, menu.IsEnabled, declared);
                    AddEntries(submenu, menu.Items, insertAt: null, separate: false, menu.IsEnabled, declared);
                    break;
            }
        }
        s_menuEntries = declared.ToImmutable();
    }

    // A declared entry and whether it and every menu around it are enabled.
    private readonly record struct DeclaredEntry(DesktopMenuEntry Entry, bool Enabled);

    // Maps the native item to its record through the tag. A disabled submenu's item gets the command action, so that
    // AppKit's validation (ValidateMenuItem) disables it; an enabled one keeps AppKit's own submenu action.
    private static void Declare(nint native, DesktopMenuEntry entry, bool enabled, ImmutableArray<DeclaredEntry>.Builder declared)
    {
        if (native == 0) return;
        declared.Add(new DeclaredEntry(entry, enabled));
        Api.SendVoidNint(native, "setTag:", declared.Count);
        if (entry is DesktopMenu && !enabled) Api.SetAction(native, MenuCommandAction, s_menuTarget);
    }

    // The declared entry of a native item with the command action, or null for another item.
    private static DeclaredEntry? Declared(nint native)
    {
        if (native == 0 || !Api.HasAction(native, MenuCommandAction)) return null;
        var entries = s_menuEntries;
        var tag = Api.SendNint(native, "tag");
        return tag >= 1 && tag <= entries.Length ? entries[(int)tag - 1] : null;
    }

    // Adds a declared menu's entries, at an index of an existing menu with a separator after them, or at its end.
    private static void AddEntries(nint menu, ImmutableArray<DesktopMenuEntry> entries, int? insertAt, bool separate, bool enabled,
        ImmutableArray<DeclaredEntry>.Builder declared)
    {
        var index = insertAt;
        void Add(nint item)
        {
            Api.SetIdentifier(item, DeclaredIdentifier + "item");
            if (index is { } at)
            {
                Api.InsertItem(menu, item, at);
                index = at + 1;
            }
            else
            {
                Api.SendVoidNint(menu, "addItem:", item);
            }
        }
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case DesktopMenuSeparator:
                    Add(Api.SendNint(Api.GetClass("NSMenuItem"), "separatorItem"));
                    break;
                case DesktopMenuItem item:
                    var (key, modifiers) = item.Shortcut is { Key: not null } shortcut && !DesktopMenuBar.IsReserved(shortcut)
                        ? DesktopMenuBar.ToMacKeyEquivalent(shortcut)
                        : ("", 0UL);
                    var native = Api.NewMenuItem(item.Title, MenuCommandAction, key, modifiers, s_menuTarget);
                    Declare(native, item, enabled && item.IsEnabled, declared);
                    // NSControlStateValueOn and Off.
                    if (item.IsChecked is { } isChecked) Api.SendVoidNint(native, "setState:", isChecked ? 1 : 0);
                    Add(native);
                    Api.SendVoid(native, "release");
                    break;
                case DesktopMenu submenu:
                    var holder = Api.NewMenuItem(submenu.Title, null, "", 0, 0);
                    var child = Api.NewMenu(submenu.Title);
                    Api.SendVoidNint(holder, "setSubmenu:", child);
                    Declare(holder, submenu, enabled && submenu.IsEnabled, declared);
                    AddEntries(child, submenu.Items, insertAt: null, separate: false, enabled && submenu.IsEnabled, declared);
                    Add(holder);
                    Api.SendVoid(child, "release");
                    Api.SendVoid(holder, "release");
                    break;
            }
        }
        if (separate && entries.Length > 0)
        {
            Add(Api.SendNint(Api.GetClass("NSMenuItem"), "separatorItem"));
        }
    }

    // The command path of the key window, or of the main window while a panel is key: its target, then the
    // application's (MenuCommandChain). Without such a window, the application's target alone.
    private static MenuCommandChain? KeyWindowCommands()
    {
        var application = Api.SharedApplicationIfCreated();
        if (application == 0) return null;
        foreach (var selector in (string[])["keyWindow", "mainWindow"])
        {
            if (Hosts.TryGetValue(Api.SendNint(application, selector), out var host) && host.IsOpen && host.MenuCommands is MenuCommandChain chain)
                return chain;
        }
        return null;
    }

    internal static bool CanExecuteMenuCommand(DesktopMenuItem item) => KeyWindowCommands() is { } chain
        ? chain.CanExecute(item)
        : MenuCommandChain.CanExecute(item, null, Volatile.Read(ref s_applicationMenuCommands), Volatile.Read(ref s_menuLogger));

    internal static bool ExecuteMenuCommand(DesktopMenuItem item) => KeyWindowCommands() is { } chain
        ? chain.TryExecute(item)
        : MenuCommandChain.TryExecute(item, null, Volatile.Read(ref s_applicationMenuCommands), Volatile.Read(ref s_menuLogger));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MenuCommand(nint self, nint selector, nint sender)
    {
        try
        {
            if (Declared(sender) is { Enabled: true, Entry: DesktopMenuItem item }) ExecuteMenuCommand(item);
        }
        catch
        {
            // Never unwind through AppKit.
        }
    }

    // AppKit asks before it shows a menu or answers a key equivalent. Only the declared items depend on a command.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ValidateMenuItem(nint self, nint selector, nint item)
    {
        try
        {
            if (!Api.HasAction(item, MenuCommandAction)) return 1;
            return Declared(item) is { Enabled: true } declared && (declared.Entry is not DesktopMenuItem menuItem || CanExecuteMenuCommand(menuItem))
                ? (byte)1 : (byte)0;
        }
        catch
        {
            return 0;
        }
    }

    private const string ContextMenuAction = "runicContextMenuItem:";
    private const string ContextMenuTickAction = "runicContextMenuTick:";
    // The context menu that is tracking, or about to; AppKit tracks one menu at a time.
    private static ContextMenuSession? s_contextMenu;

    // The last steps of the context menus, with Environment.TickCount64 and whether on the main thread; for the native
    // smoke to print.
    internal static readonly ConcurrentQueue<string> ContextMenuTrace = new();

    private static void TraceContextMenu(string step)
    {
        ContextMenuTrace.Enqueue($"{Environment.TickCount64}{(IsMainThread ? " main" : "")} {step}");
        while (ContextMenuTrace.Count > 64) ContextMenuTrace.TryDequeue(out _);
    }

    // The NSMenu of the context menu that is tracking now, or zero; for the native smoke.
    internal static nint TrackingContextMenu => Volatile.Read(ref s_contextMenu) is { Menu: not 0 and var menu } ? menu : 0;

    // One context menu: its items by tag minus one, and how it ended.
    private sealed class ContextMenuSession(MacOsWkWebViewHost owner, CancellationToken cancellationToken)
    {
        private static int s_lastId;
        private int _dismissed;
        // For the trace of the native smoke.
        internal int Id { get; } = Interlocked.Increment(ref s_lastId);
        internal int CancelTraced;
        internal MacOsWkWebViewHost Owner { get; } = owner;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal TaskCompletionSource<DesktopMenuItem?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<DesktopMenuItem> Items { get; } = [];
        internal nint Menu { get; set; }

        // Whether the menu should close: a later menu, the window closing or the caller's token.
        internal bool IsDismissed => Volatile.Read(ref _dismissed) != 0 || CancellationToken.IsCancellationRequested;

        // Asks the tracking menu to close from any thread; the tracking run loop's timer closes it.
        internal void Dismiss()
        {
            if (Interlocked.Exchange(ref _dismissed, 1) == 0) TraceContextMenu($"dismiss #{Id}");
        }

        internal void Complete(DesktopMenuItem? chosen)
        {
            if (!Result.Task.IsCompleted) TraceContextMenu($"complete #{Id} {(chosen?.Title ?? (CancellationToken.IsCancellationRequested ? "cancelled" : "null"))}");
            if (chosen is null && CancellationToken.IsCancellationRequested) Result.TrySetCanceled(CancellationToken);
            else Result.TrySetResult(chosen);
        }
    }

    public ValueTask<DesktopMenuItem?> ShowContextMenuAsync(ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (MenuTargetClass == 0) throw new NotSupportedException("The AppKit menu target class could not be created.");
        // A new context menu dismisses the open one, which returns null.
        Volatile.Read(ref s_contextMenu)?.Dismiss();
        var session = new ContextMenuSession(this, cancellationToken);
        TraceContextMenu($"request #{session.Id}");
        var shown = InvokeAsync(() => ScheduleContextMenu(new PendingContextMenu(this, session, items, options)), cancellationToken);
        return new ValueTask<DesktopMenuItem?>(WaitForContextMenuAsync(session, shown));
    }

    private const string DockMenuAction = "runicDockMenuItem:";
    // The Dock menu's items by tag minus one, and the target of its items. Main thread only.
    private static List<DesktopMenuItem> s_dockItems = [];
    private static nint s_dockTarget;

    // MacOSDesktopOptions.DockMenu as a new autoreleased NSMenu, which applicationDockMenu: returns each time the Dock
    // menu opens: items are enabled from their command's availability now. Commands go to DesktopHost.MenuCommands.
    // Main thread only; 0 without the menu target class.
    internal static nint CreateDockMenu(IReadOnlyList<DesktopMenuEntry> entries)
    {
        if (MenuTargetClass == 0) return 0;
        if (s_dockTarget == 0) s_dockTarget = Api.SendNint(Api.SendNint(MenuTargetClass, "alloc"), "init");
        var items = new List<DesktopMenuItem>();
        var menu = Api.NewMenu("");
        AddDockEntries(menu, entries, enabled: true, items);
        s_dockItems = items;
        return Api.SendNint(menu, "autorelease");
    }

    private static void AddDockEntries(nint menu, IEnumerable<DesktopMenuEntry> entries, bool enabled, List<DesktopMenuItem> items)
    {
        Api.SendVoidBool(menu, "setAutoenablesItems:", false);
        var application = Volatile.Read(ref s_applicationMenuCommands);
        var logger = Volatile.Read(ref s_menuLogger);
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case DesktopMenuSeparator:
                    Api.SendVoidNint(menu, "addItem:", Api.SendNint(Api.GetClass("NSMenuItem"), "separatorItem"));
                    break;
                case DesktopMenuItem item:
                    var native = Api.NewMenuItem(item.Title, DockMenuAction, "", 0, s_dockTarget);
                    items.Add(item);
                    Api.SendVoidNint(native, "setTag:", items.Count);
                    Api.SendVoidBool(native, "setEnabled:", enabled && item.IsEnabled && MenuCommandChain.CanExecute(item, null, application, logger));
                    if (item.IsChecked is { } isChecked) Api.SendVoidNint(native, "setState:", isChecked ? 1 : 0);
                    Api.SendVoidNint(menu, "addItem:", native);
                    Api.SendVoid(native, "release");
                    break;
                case DesktopMenu submenu:
                    var holder = Api.NewMenuItem(submenu.Title, null, "", 0, 0);
                    var child = Api.NewMenu(submenu.Title);
                    Api.SendVoidNint(holder, "setSubmenu:", child);
                    Api.SendVoidBool(holder, "setEnabled:", enabled && submenu.IsEnabled);
                    AddDockEntries(child, submenu.Items, enabled && submenu.IsEnabled, items);
                    Api.SendVoidNint(menu, "addItem:", holder);
                    Api.SendVoid(child, "release");
                    Api.SendVoid(holder, "release");
                    break;
                default:
                    // An entry kind of a later release.
                    break;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DockMenuItem(nint self, nint selector, nint sender)
    {
        try
        {
            var tag = Api.SendNint(sender, "tag");
            var items = s_dockItems;
            if (tag >= 1 && tag <= items.Count)
                MenuCommandChain.TryExecute(items[(int)tag - 1], null, Volatile.Read(ref s_applicationMenuCommands), Volatile.Read(ref s_menuLogger));
        }
        catch
        {
            // Never unwind through AppKit.
        }
    }

    private sealed record PendingContextMenu(MacOsWkWebViewHost Owner, ContextMenuSession Session, ImmutableArray<DesktopMenuEntry> Items,
        DesktopContextMenuOptions Options);

    // The context menu that tracks next. Main thread only.
    private static PendingContextMenu? s_pendingContextMenu;
    private const string TrackContextMenuAction = "runicTrackContextMenu:";

    // Main thread, inside a block of the main dispatch queue, which the run loop does not drain again while the menu
    // tracks: the menu tracks from a perform request instead, so main-thread work queued meanwhile still runs. The request
    // runs in the default mode only: a menu asked for while another menu tracks, such as the context menu it replaces,
    // starts once that tracking has ended rather than inside it, where AppKit would not track it.
    private static void ScheduleContextMenu(PendingContextMenu pending)
    {
        if (s_menuTarget == 0)
        {
            pending.Owner.ShowContextMenu(pending.Session, pending.Items, pending.Options);
            return;
        }
        TraceContextMenu($"schedule #{pending.Session.Id}");
        // A later request replaces one that has not tracked yet, which returns null, and closes the one that tracks:
        // this runs in the common modes, during that tracking, which the menu's timer then ends. The request's own
        // check on the calling thread can miss a menu that starts tracking after it.
        Interlocked.Exchange(ref s_pendingContextMenu, pending)?.Session.Complete(null);
        if (Volatile.Read(ref s_contextMenu) is { } tracking && !ReferenceEquals(tracking, pending.Session)) tracking.Dismiss();
        Api.PerformInDefaultMode(s_menuTarget, TrackContextMenuAction);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void TrackContextMenu(nint self, nint selector, nint argument)
    {
        // A menu that still tracks, as from a nested modal loop that runs the default mode, ends first.
        if (Volatile.Read(ref s_contextMenu) is { Menu: not 0 } stillTracking)
        {
            TraceContextMenu($"defer while #{stillTracking.Id} tracks");
            if (Volatile.Read(ref s_pendingContextMenu) is not null) Api.PerformInDefaultMode(s_menuTarget, TrackContextMenuAction);
            return;
        }
        if (Interlocked.Exchange(ref s_pendingContextMenu, null) is not { } pending) return;
        try
        {
            pending.Owner.ShowContextMenu(pending.Session, pending.Items, pending.Options);
        }
        catch (Exception error)
        {
            // Never unwind through AppKit; the caller sees the failure.
            pending.Session.Result.TrySetException(error);
        }
    }

    private static async Task<DesktopMenuItem?> WaitForContextMenuAsync(ContextMenuSession session, ValueTask shown)
    {
        await shown.ConfigureAwait(false);
        return await session.Result.Task.ConfigureAwait(false);
    }

    // Main thread only. Builds the NSMenu and tracks it modally over the web view; a timer in the tracking run loop
    // mode closes it when the session is dismissed.
    private void ShowContextMenu(ContextMenuSession session, ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions options)
    {
        if (session.IsDismissed || !IsOpen || s_menuTarget == 0)
        {
            session.Complete(null);
            return;
        }
        var menu = Api.NewMenu("");
        Api.SendVoidBool(menu, "setAutoenablesItems:", false);
        AddContextEntries(menu, items, enabled: true, session);
        session.Menu = menu;
        var previous = Interlocked.Exchange(ref s_contextMenu, session);
        previous?.Dismiss();
        var timer = Api.NewRepeatingTimer(0.05, s_menuTarget, ContextMenuTickAction);
        var selected = false;
        TraceContextMenu($"track #{session.Id}");
        try
        {
            CGPoint location;
            nint view;
            if (options.X is { } x && options.Y is { } y)
            {
                // CSS pixels of the viewport to the web view's points, measured from its top-left corner.
                var zoom = Api.SendDouble(_webView, "pageZoom");
                if (!double.IsFinite(zoom) || zoom <= 0) zoom = 1;
                var bounds = Api.SendRect(_webView, "bounds");
                var top = y * zoom;
                location = new CGPoint(x * zoom, Api.SendBool(_webView, "isFlipped") ? top : bounds.Height - top);
                view = _webView;
            }
            else
            {
                // Screen coordinates, which a nil view takes.
                location = Api.SendPoint(Api.GetClass("NSEvent"), "mouseLocation");
                view = 0;
            }
            selected = Api.PopUpMenu(menu, location, view);
        }
        finally
        {
            Api.SendVoid(timer, "invalidate");
            Api.SendVoid(timer, "release");
            Interlocked.CompareExchange(ref s_contextMenu, null, session);
            session.Menu = 0;
            TraceContextMenu($"tracked #{session.Id}{(selected ? " selected" : "")}");
            Api.SendVoid(menu, "release");
        }
        if (session.Result.Task.IsCompleted) return;
        if (selected)
        {
            // AppKit sends the chosen item's action during tracking; should it come later, it is queued before this.
            MacOsMainThread.Post(() => session.Complete(null));
            return;
        }
        session.Complete(null);
    }

    private static void AddContextEntries(nint menu, ImmutableArray<DesktopMenuEntry> entries, bool enabled, ContextMenuSession session)
    {
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case DesktopMenuSeparator:
                    Api.SendVoidNint(menu, "addItem:", Api.SendNint(Api.GetClass("NSMenuItem"), "separatorItem"));
                    break;
                case DesktopMenuItem item:
                    // The shortcut is a hint: the menu exists only while it tracks.
                    var (key, modifiers) = item.Shortcut is { Key.Length: > 0 } shortcut && !DesktopMenuBar.IsReserved(shortcut)
                        ? DesktopMenuBar.ToMacKeyEquivalent(shortcut)
                        : ("", 0UL);
                    var native = Api.NewMenuItem(item.Title, ContextMenuAction, key, modifiers, s_menuTarget);
                    session.Items.Add(item);
                    Api.SendVoidNint(native, "setTag:", session.Items.Count);
                    Api.SendVoidBool(native, "setEnabled:", enabled && item.IsEnabled);
                    // NSControlStateValueOn and Off; a radio item shows the check mark, as AppKit has no radio look.
                    if (item.IsChecked is { } isChecked) Api.SendVoidNint(native, "setState:", isChecked ? 1 : 0);
                    Api.SendVoidNint(menu, "addItem:", native);
                    Api.SendVoid(native, "release");
                    break;
                case DesktopMenu submenu:
                    var holder = Api.NewMenuItem(submenu.Title, null, "", 0, 0);
                    var child = Api.NewMenu(submenu.Title);
                    Api.SendVoidBool(child, "setAutoenablesItems:", false);
                    Api.SendVoidNint(holder, "setSubmenu:", child);
                    Api.SendVoidBool(holder, "setEnabled:", enabled && submenu.IsEnabled);
                    AddContextEntries(child, submenu.Items, enabled && submenu.IsEnabled, session);
                    Api.SendVoidNint(menu, "addItem:", holder);
                    Api.SendVoid(child, "release");
                    Api.SendVoid(holder, "release");
                    break;
                default:
                    // An entry kind of a later release.
                    break;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ContextMenuItem(nint self, nint selector, nint sender)
    {
        try
        {
            if (Volatile.Read(ref s_contextMenu) is not { } session) return;
            var tag = Api.SendNint(sender, "tag");
            if (tag >= 1 && tag <= session.Items.Count) session.Result.TrySetResult(session.Items[(int)tag - 1]);
        }
        catch
        {
            // Never unwind through AppKit.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ContextMenuTick(nint self, nint selector, nint timer)
    {
        try
        {
            if (Volatile.Read(ref s_contextMenu) is { Menu: not 0 } session && (session.IsDismissed || !session.Owner.IsOpen))
            {
                if (Interlocked.Exchange(ref session.CancelTraced, 1) == 0) TraceContextMenu($"cancelTracking #{session.Id}");
                Api.SendVoid(session.Menu, "cancelTracking");
            }
        }
        catch
        {
            // Never unwind through AppKit.
        }
    }

    // Without the Desktop event loop, Quit asks every window to close, as its close button does: a close guard decides
    // for its window.
    private static void QuitApplication()
    {
        foreach (var host in Hosts.Values.ToArray())
        {
            if (host._closeRequested is { } requestClose)
            {
                requestClose();
            }
            else if (host.IsOpen)
            {
                Api.SendVoid(host._window, "close");
            }
        }
    }

    private void HandleClosed()
    {
        if (Interlocked.Exchange(ref _isOpen, 0) == 0)
        {
            return;
        }
        Hosts.TryRemove(_window, out _);
        WebViews.TryRemove(_webView, out _);
        EndDrags();
        lock (_promiseGate) _drops?.Dispose();
        _nativeShutdown.Cancel();
        _closed.TrySetResult();
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var host = (MacOsWkWebViewHost)state!;
            host.Closed?.Invoke(host, EventArgs.Empty);
        }, this);
    }

    private void ReleaseNativeObjects()
    {
        var window = Interlocked.Exchange(ref _window, 0);
        var webView = Interlocked.Exchange(ref _webView, 0);
        var windowDelegate = Interlocked.Exchange(ref _delegate, 0);
        var titleObserver = Interlocked.Exchange(ref _titleObserver, 0);
        var webContentDelegate = Interlocked.Exchange(ref _webContentDelegate, 0);
        if (window != 0)
        {
            Api.SendVoidNint(window, "setDelegate:", 0);
        }
        if (webContentDelegate != 0)
        {
            // WebKit holds the delegates weakly: clear them before the object goes.
            if (webView != 0)
            {
                Api.SendVoidNint(webView, "setNavigationDelegate:", 0);
                Api.SendVoidNint(webView, "setUIDelegate:", 0);
            }
            Api.SendVoid(webContentDelegate, "release");
        }
        if (titleObserver != 0)
        {
            if (webView != 0)
            {
                Api.RemoveObserver(webView, titleObserver, "title");
            }
            Api.SendVoid(titleObserver, "release");
        }
        if (webView != 0)
        {
            Api.SendVoid(webView, "release");
        }
        if (window != 0)
        {
            Api.SendVoid(window, "release");
        }
        if (windowDelegate != 0)
        {
            Api.SendVoid(windowDelegate, "release");
        }
    }

    private static unsafe nint CreateDelegateClass()
    {
        if (!OperatingSystem.IsMacOS() || !Api.IsAvailable)
        {
            return 0;
        }
        var existing = Api.GetClass("RunicDesktopWindowDelegate");
        if (existing != 0)
        {
            return existing;
        }
        var type = Api.AllocateClassPair(Api.GetClass("NSObject"), "RunicDesktopWindowDelegate");
        if (type == 0)
        {
            return 0;
        }
        Api.AddMethod(type, "windowWillClose:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&WindowWillClose, "v@:@");
        Api.AddMethod(type, "windowShouldClose:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&WindowShouldClose, RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B@:@" : "c@:@");
        foreach (var selector in (string[])["windowDidMove:", "windowDidResize:", "windowDidDeminiaturize:", "windowDidChangeScreen:"])
        {
            Api.AddMethod(type, selector, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&WindowGeometryChanged, "v@:@");
        }
        // The frame changes during the full-screen animation; the restorable bounds must not follow it.
        foreach (var selector in (string[])["windowWillEnterFullScreen:", "windowWillExitFullScreen:"])
        {
            Api.AddMethod(type, selector, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&FullScreenTransitionStarted, "v@:@");
        }
        foreach (var selector in (string[])["windowDidEnterFullScreen:", "windowDidExitFullScreen:"])
        {
            Api.AddMethod(type, selector, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&FullScreenTransitionEnded, "v@:@");
        }
        // These two pass the window itself, not a notification.
        foreach (var selector in (string[])["windowDidFailToEnterFullScreen:", "windowDidFailToExitFullScreen:"])
        {
            Api.AddMethod(type, selector, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&FullScreenTransitionFailed, "v@:@");
        }
        Api.RegisterClassPair(type);
        return type;
    }

    // A WKWebView that reads the paths and promised files of a drop from the dragging pasteboard, then lets WebKit
    // perform the drop, so the page still receives its drop event. It remembers the operation WebKit answers while a
    // drag moves over the page (MacOsDragSource.cs), which is the drop's effect.
    private static unsafe nint CreateWebViewClass()
    {
        if (!OperatingSystem.IsMacOS() || !Api.IsAvailable)
        {
            return 0;
        }
        var existing = Api.GetClass("RunicDesktopWebView");
        if (existing != 0)
        {
            return existing;
        }
        var type = Api.AllocateClassPair(Api.GetClass("WKWebView"), "RunicDesktopWebView");
        if (type == 0)
        {
            return 0;
        }
        Api.AddMethod(type, "performDragOperation:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&PerformDragOperation,
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B@:@" : "c@:@");
        foreach (var selector in (string[])["draggingEntered:", "draggingUpdated:"])
        {
            Api.AddMethod(type, selector, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, ulong>)&DraggingUpdated, "Q@:@");
        }
        // The page's drag regions move the window (MacOsWkWebViewHost.WindowChrome.cs).
        Api.AddMethod(type, "mouseDown:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&WebViewMouseDown, "v@:@");
        Api.AddMethod(type, "acceptsFirstMouse:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&WebViewAcceptsFirstMouse,
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B@:@" : "c@:@");
        Api.RegisterClassPair(type);
        return type;
    }

    // Observes a WKWebView's title (key-value observing) and gives it to the view's window.
    private static unsafe nint CreateTitleObserverClass()
    {
        if (!OperatingSystem.IsMacOS() || !Api.IsAvailable)
        {
            return 0;
        }
        var existing = Api.GetClass("RunicDesktopTitleObserver");
        if (existing != 0)
        {
            return existing;
        }
        var type = Api.AllocateClassPair(Api.GetClass("NSObject"), "RunicDesktopTitleObserver");
        if (type == 0)
        {
            return 0;
        }
        Api.AddMethod(type, "observeValueForKeyPath:ofObject:change:context:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&TitleChanged, "v@:@@@^v");
        Api.RegisterClassPair(type);
        return type;
    }

    // WKNavigationDelegate and WKUIDelegate of a window's web view (SEC-04). WebKit asks respondsToSelector: when the
    // delegate is set, so only these methods change its behaviour; it keeps its defaults for every other one, such as
    // reloading after the web content process terminates. A block parameter is encoded "@?".
    private static unsafe nint CreateWebContentDelegateClass()
    {
        if (!OperatingSystem.IsMacOS() || !Api.IsAvailable)
        {
            return 0;
        }
        var existing = Api.GetClass("RunicDesktopWebContentDelegate");
        if (existing != 0)
        {
            return existing;
        }
        var type = Api.AllocateClassPair(Api.GetClass("NSObject"), "RunicDesktopWebContentDelegate");
        if (type == 0)
        {
            return 0;
        }
        Api.AddMethod(type, "webView:decidePolicyForNavigationAction:decisionHandler:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DecidePolicyForNavigationAction, "v@:@@@?");
        Api.AddMethod(type, "webView:decidePolicyForNavigationResponse:decisionHandler:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DecidePolicyForNavigationResponse, "v@:@@@?");
        // macOS 11.3: an <a download> or an attachment answered with the Download policy becomes a WKDownload.
        Api.AddMethod(type, "webView:navigationAction:didBecomeDownload:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&NavigationBecameDownload, "v@:@@@");
        Api.AddMethod(type, "webView:navigationResponse:didBecomeDownload:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&NavigationBecameDownload, "v@:@@@");
        Api.AddMethod(type, "webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>)&CreateWebView, "@@:@@@@");
        // WKUIDelegate, macOS 12: camera and microphone requests follow AllowedPermissions (SEC-37).
        Api.AddMethod(type, "webView:requestMediaCapturePermissionForOrigin:initiatedByFrame:type:decisionHandler:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, void>)&RequestMediaCapturePermission, "v@:@@@q@?");
        Api.RegisterClassPair(type);
        return type;
    }

    // The handler is called exactly once, with Cancel when anything fails; the window acts on the outcome afterwards.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DecidePolicyForNavigationAction(nint self, nint selector, nint webView, nint navigationAction, nint decisionHandler)
    {
        var outcome = MacOsWebContent.Cancel;
        MacOsWkWebViewHost? host = null;
        nint pool = 0;
        try
        {
            pool = Api.SendNint(Api.SendNint(Api.GetClass("NSAutoreleasePool"), "alloc"), "init");
            if (WebViews.TryGetValue(webView, out host) && host._webContent is { } rules)
            {
                var frame = Api.SendNint(navigationAction, "targetFrame");
                bool? mainFrame = frame == 0 ? null : Api.SendBool(frame, "isMainFrame");
                // shouldPerformDownload: macOS 11.3.
                var download = Api.RespondsToSelector(navigationAction, "shouldPerformDownload") && Api.SendBool(navigationAction, "shouldPerformDownload");
                outcome = MacOsWebContent.ForNavigationAction(rules, Api.RequestUrl(navigationAction), mainFrame,
                    Api.SendNint(navigationAction, "navigationType"), download);
            }
        }
        catch
        {
            outcome = MacOsWebContent.Cancel;
        }
        try
        {
            MacOsWebContent.InvokePolicyHandler(decisionHandler, outcome.Policy);
        }
        catch
        {
            // Never unwind through WebKit.
        }
        try
        {
            host?.Complete(outcome);
        }
        catch
        {
            // Never unwind through WebKit.
        }
        try
        {
            if (pool != 0) Api.SendVoid(pool, "drain");
        }
        catch
        {
            // Never unwind through WebKit.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DecidePolicyForNavigationResponse(nint self, nint selector, nint webView, nint navigationResponse, nint decisionHandler)
    {
        var policy = MacOsWebContent.PolicyCancel;
        nint pool = 0;
        try
        {
            pool = Api.SendNint(Api.SendNint(Api.GetClass("NSAutoreleasePool"), "alloc"), "init");
            if (WebViews.TryGetValue(webView, out var host) && host._webContent is { } rules)
            {
                policy = MacOsWebContent.ForNavigationResponse(rules, Api.SendBool(navigationResponse, "canShowMIMEType"),
                    Api.ContentDisposition(navigationResponse));
            }
        }
        catch
        {
            policy = MacOsWebContent.PolicyCancel;
        }
        try
        {
            MacOsWebContent.InvokePolicyHandler(decisionHandler, policy);
        }
        catch
        {
            // Never unwind through WebKit.
        }
        try
        {
            if (pool != 0) Api.SendVoid(pool, "drain");
        }
        catch
        {
            // Never unwind through WebKit.
        }
    }

    // window.open: no second WebView. WebKit's pop-up blocker (javaScriptCanOpenWindowsAutomatically NO, set in
    // AllocInitWebView) lets only a script that handles a user gesture reach this. Links with a target came through
    // DecidePolicyForNavigationAction, which cancelled them.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint CreateWebView(nint self, nint selector, nint webView, nint configuration, nint navigationAction, nint windowFeatures)
    {
        nint pool = 0;
        try
        {
            pool = Api.SendNint(Api.SendNint(Api.GetClass("NSAutoreleasePool"), "alloc"), "init");
            if (WebViews.TryGetValue(webView, out var host) && host._webContent is { } rules)
            {
                var outcome = MacOsWebContent.ForNewWindow(rules, Api.RequestUrl(navigationAction), userInitiated: true);
                try
                {
                    host.Complete(outcome);
                }
                catch
                {
                    // The page gets no window either way.
                }
            }
        }
        catch
        {
            // Never unwind through WebKit.
        }
        try
        {
            if (pool != 0) Api.SendVoid(pool, "drain");
        }
        catch
        {
            // Never unwind through WebKit.
        }
        return 0;
    }

    // The handler is called exactly once, with Deny when anything fails.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RequestMediaCapturePermission(nint self, nint selector, nint webView, nint origin, nint frame, nint type, nint decisionHandler)
    {
        var decision = MacOsWebContent.PermissionDeny;
        nint pool = 0;
        try
        {
            pool = Api.SendNint(Api.SendNint(Api.GetClass("NSAutoreleasePool"), "alloc"), "init");
            if (WebViews.TryGetValue(webView, out var host) && host._presentedUrl is { } presented)
            {
                var frameOrigin = frame == 0 ? 0 : Api.SendNint(frame, "securityOrigin");
                var topLevel = Api.SendNint(webView, "URL");
                var topLevelText = topLevel == 0 ? 0 : Api.SendNint(topLevel, "absoluteString");
                decision = MacOsWebContent.ForMediaCapture(
                    host._mediaCaptureGranted,
                    presented,
                    SecurityOriginUrl(origin),
                    SecurityOriginUrl(frameOrigin),
                    topLevelText == 0 ? null : Api.Text(topLevelText),
                    type);
            }
        }
        catch
        {
            decision = MacOsWebContent.PermissionDeny;
        }
        try
        {
            MacOsWebContent.InvokePolicyHandler(decisionHandler, decision);
        }
        catch
        {
            // Never unwind through WebKit.
        }
        try
        {
            if (pool != 0) Api.SendVoid(pool, "drain");
        }
        catch
        {
            // Never unwind through WebKit.
        }
    }

    // A WKSecurityOrigin as an origin URL, or null.
    private static string? SecurityOriginUrl(nint origin)
    {
        if (origin == 0) return null;
        var protocol = Api.SendNint(origin, "protocol");
        var host = Api.SendNint(origin, "host");
        return MacOsWebContent.OriginUrl(
            protocol == 0 ? null : Api.Text(protocol),
            host == 0 ? null : Api.Text(host),
            Api.SendNint(origin, "port"));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void TitleChanged(nint self, nint selector, nint keyPath, nint webView, nint change, nint context)
    {
        try
        {
            var title = Api.SendNint(webView, "title");
            var window = Api.SendNint(webView, "window");
            if (title != 0 && window != 0 && Api.SendUlong(title, "length") > 0)
            {
                Api.SendVoidNint(window, "setTitle:", title);
                // A new title lays the title bar out again.
                if (Hosts.TryGetValue(window, out var host) && host.IsOpen) host.ApplyTrafficLights();
            }
        }
        catch
        {
            // Native callbacks cannot unwind through AppKit.
        }
    }

    // Receives the page's drop label from the isolated content world.
    private static unsafe nint CreateFileDropHandler()
    {
        var type = Api.GetClass("RunicDesktopFileDropHandler");
        if (type == 0)
        {
            type = Api.AllocateClassPair(Api.GetClass("NSObject"), "RunicDesktopFileDropHandler");
            if (type == 0)
            {
                return 0;
            }
            Api.AddMethod(type, "userContentController:didReceiveScriptMessage:",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&DidReceiveFileDropMessage, "v@:@@");
            Api.RegisterClassPair(type);
        }
        return Api.SendNint(Api.SendNint(type, "alloc"), "init");
    }

    // Runic.Desktop.Tests reads a pasteboard through the same path as a drop, on the macOS runner.
    internal static bool HasFileDropWebView => WebViewClass != 0;

    internal static List<string> ReadDroppedFilePaths(nint draggingInfo) => Api.ReadDroppedFilePaths(draggingInfo);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte PerformDragOperation(nint self, nint selector, nint sender)
    {
        try
        {
            if (WebViews.TryGetValue(self, out var host) && host.IsOpen)
            {
                host.ReadDrop(sender);
            }
        }
        catch
        {
            // Never unwind through AppKit.
        }
        try
        {
            return Api.SendSuperBoolNint(self, Api.GetClass("WKWebView"), "performDragOperation:", sender) ? (byte)1 : (byte)0;
        }
        catch
        {
            return 0;
        }
    }

    // The script runs in every frame (D037.2). WebKit's frameInfo, not the page, says which frame reported: a subframe
    // of another origin than the presented one refuses the drop.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidReceiveFileDropMessage(nint self, nint selector, nint controller, nint message)
    {
        try
        {
            if (WebViews.TryGetValue(Api.SendNint(message, "webView"), out var host) && host.IsOpen)
            {
                var body = Api.SendNint(message, "body");
                var report = FileDropSupport.DecodeReport(Api.IsString(body) ? Api.Text(body) : null);
                var frame = Api.SendNint(message, "frameInfo");
                var mainFrame = frame != 0 && Api.SendBool(frame, "isMainFrame");
                var origin = mainFrame || frame == 0 ? null : SecurityOriginUrl(Api.SendNint(frame, "securityOrigin"));
                host.PageReport(report.Label, refused: !MacOsDragOperations.AcceptsFrame(mainFrame, host._presentedUrl, origin));
            }
        }
        catch
        {
            // Never unwind through AppKit.
        }
    }

    private static unsafe nint CreateMenuTargetClass()
    {
        if (!OperatingSystem.IsMacOS() || !Api.IsAvailable)
        {
            return 0;
        }
        var existing = Api.GetClass("RunicDesktopMenuTarget");
        if (existing != 0)
        {
            return existing;
        }
        var type = Api.AllocateClassPair(Api.GetClass("NSObject"), "RunicDesktopMenuTarget");
        if (type == 0)
        {
            return 0;
        }
        Api.AddMethod(type, MacOsPageZoom.Selector(PageZoomCommand.In), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&ZoomIn, "v@:@");
        Api.AddMethod(type, MacOsPageZoom.Selector(PageZoomCommand.Out), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&ZoomOut, "v@:@");
        Api.AddMethod(type, MacOsPageZoom.Selector(PageZoomCommand.Reset), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&ActualSize, "v@:@");
        Api.AddMethod(type, MacOsMainMenu.QuitAction, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Quit, "v@:@");
        Api.AddMethod(type, MenuCommandAction, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&MenuCommand, "v@:@");
        Api.AddMethod(type, ContextMenuAction, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&ContextMenuItem, "v@:@");
        Api.AddMethod(type, ContextMenuTickAction, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&ContextMenuTick, "v@:@");
        Api.AddMethod(type, DockMenuAction, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&DockMenuItem, "v@:@");
        Api.AddMethod(type, TrackContextMenuAction, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&TrackContextMenu, "v@:@");
        Api.AddMethod(type, "validateMenuItem:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&ValidateMenuItem,
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B@:@" : "c@:@");
        Api.RegisterClassPair(type);
        return type;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ZoomIn(nint self, nint selector, nint sender) => ZoomKeyWindow(PageZoomCommand.In);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ZoomOut(nint self, nint selector, nint sender) => ZoomKeyWindow(PageZoomCommand.Out);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ActualSize(nint self, nint selector, nint sender) => ZoomKeyWindow(PageZoomCommand.Reset);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Quit(nint self, nint selector, nint sender)
    {
        try
        {
            // Under the Desktop event loop Quit is AppKit's terminate:, which Runic's application delegate answers with
            // the one quit path (DesktopHostOptions.ConfirmQuitAsync); without it, Quit closes the windows.
            if (MacOsApplication.IsOwner) Api.SendVoidNint(Api.SendNint(Api.GetClass("NSApplication"), "sharedApplication"), "terminate:", sender);
            else QuitApplication();
        }
        catch
        {
            // Never unwind through AppKit.
        }
    }

    private static void ZoomKeyWindow(PageZoomCommand command)
    {
        try
        {
            var application = Api.SendNint(Api.GetClass("NSApplication"), "sharedApplication");
            if (Hosts.TryGetValue(Api.SendNint(application, "keyWindow"), out var host)
                || Hosts.TryGetValue(Api.SendNint(application, "mainWindow"), out host))
            {
                host.Zoom(command);
            }
        }
        catch
        {
            // Never unwind through AppKit.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte WindowShouldClose(nint self, nint selector, nint window)
    {
        if (Hosts.TryGetValue(window, out var host) && host._closeRequested is { } requestClose)
        {
            requestClose();
            return 0;
        }
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WindowWillClose(nint self, nint selector, nint notification)
    {
        var window = Api.SendNint(notification, "object");
        if (Hosts.TryGetValue(window, out var host))
        {
            host.HandleClosed();
        }
    }

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGPoint(double X, double Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGSize(double Width, double Height);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGRect(double X, double Y, double Width, double Height);

#pragma warning disable CA1822
    private sealed unsafe partial class ObjC
    {
        private readonly nint _appKit;
        private readonly nint _messageSend;
        // Intel Macs return structures larger than 16 bytes, such as NSRect, through objc_msgSend_stret.
        private readonly nint _messageSendStructure;
        private readonly nint _messageSendSuper;
        private readonly nint _webKit;
        private readonly nint _runLoopDefaultMode;

        internal ObjC()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return;
            }
            if (NativeLibrary.TryLoad("/System/Library/Frameworks/AppKit.framework/AppKit", out _appKit)
                && NativeLibrary.TryLoad("/System/Library/Frameworks/WebKit.framework/WebKit", out _webKit)
                && NativeLibrary.TryLoad("/usr/lib/libobjc.A.dylib", out var library)
                && NativeLibrary.TryGetExport(library, "objc_msgSend", out _messageSend))
            {
                NativeLibrary.TryGetExport(library, "objc_msgSendSuper", out _messageSendSuper);
                _messageSendStructure = RuntimeInformation.ProcessArchitecture == Architecture.X64
                    && NativeLibrary.TryGetExport(library, "objc_msgSend_stret", out var structure)
                        ? structure
                        : _messageSend;
                IsAvailable = GetClass("WKWebView") != 0 && GetClass("NSWindow") != 0;
            }
            if (IsAvailable
                && NativeLibrary.TryLoad("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", out var coreFoundation)
                && NativeLibrary.TryGetExport(coreFoundation, "kCFRunLoopDefaultMode", out var mode))
            {
                _runLoopDefaultMode = *(nint*)mode;
            }
        }

        internal bool IsAvailable { get; }

        // NSApp, without creating it.
        internal nint SharedApplicationIfCreated() =>
            IsAvailable && NativeLibrary.TryGetExport(_appKit, "NSApp", out var variable) ? *(nint*)variable : 0;

        // Runs the main run loop once without waiting, which runs the main dispatch queue.
        internal void RunLoopOnce()
        {
            if (_runLoopDefaultMode != 0)
            {
                _ = Native.CFRunLoopRunInMode(_runLoopDefaultMode, 0, 0);
            }
        }

        internal bool InfoDictionaryBool(string key)
        {
            var name = CreateString(key);
            try
            {
                var value = SendNintNint(SendNint(GetClass("NSBundle"), "mainBundle"), "objectForInfoDictionaryKey:", name);
                return value != 0 && RespondsToSelector(value, "boolValue")
                    && ((delegate* unmanaged[Cdecl]<nint, nint, byte>)_messageSend)(value, Selector("boolValue")) != 0;
            }
            finally
            {
                SendVoid(name, "release");
            }
        }

        // navigationAction.request.URL.absoluteString, or null.
        internal string? RequestUrl(nint navigationAction)
        {
            var request = navigationAction == 0 ? 0 : SendNint(navigationAction, "request");
            var url = request == 0 ? 0 : SendNint(request, "URL");
            var text = url == 0 ? 0 : SendNint(url, "absoluteString");
            return text == 0 ? null : Text(text);
        }

        // The Content-Disposition header of navigationResponse.response when it is an HTTP response, or null.
        internal string? ContentDisposition(nint navigationResponse)
        {
            if (navigationResponse == 0 || !RespondsToSelector(navigationResponse, "response")) return null;
            var response = SendNint(navigationResponse, "response");
            if (response == 0 || !RespondsToSelector(response, "valueForHTTPHeaderField:")) return null;
            var name = CreateString("Content-Disposition");
            try
            {
                var value = SendNintNint(response, "valueForHTTPHeaderField:", name);
                return value == 0 ? null : Text(value);
            }
            finally
            {
                SendVoid(name, "release");
            }
        }

        // [[NSWorkspace sharedWorkspace] openURL:], as Runic.Platform.MacOS's MacUriLauncher: the default browser or
        // mail client.
        internal void OpenWithWorkspace(string target)
        {
            var pool = SendNint(SendNint(GetClass("NSAutoreleasePool"), "alloc"), "init");
            var text = CreateString(target);
            try
            {
                var url = SendNintNint(GetClass("NSURL"), "URLWithString:", text);
                if (url != 0)
                {
                    _ = SendBool(SendNint(GetClass("NSWorkspace"), "sharedWorkspace"), "openURL:", url);
                }
            }
            finally
            {
                SendVoid(text, "release");
                SendVoid(pool, "drain");
            }
        }

        internal string Text(nint value) =>
            value == 0 ? "" : Marshal.PtrToStringUTF8(SendNint(value, "UTF8String")) ?? "";

        internal double SendDouble(nint receiver, string selector) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, double>)_messageSend)(receiver, Selector(selector));

        internal void SendVoidDouble(nint receiver, string selector, double value) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, double, void>)_messageSend)(receiver, Selector(selector), value);

        internal nint NewMenu(string title)
        {
            var text = CreateString(title);
            try
            {
                return SendNintNint(SendNint(GetClass("NSMenu"), "alloc"), "initWithTitle:", text);
            }
            finally
            {
                SendVoid(text, "release");
            }
        }

        // Inserts an item with a new submenu at the index and returns the submenu, which the item retains.
        internal nint InsertSubmenu(nint menu, string title, nint index, string? identifier)
        {
            var submenu = NewMenu(title);
            var item = AllocMenuItem(title, 0, "");
            SendVoidNint(item, "setSubmenu:", submenu);
            if (identifier is not null)
            {
                var text = CreateString(identifier);
                SendVoidNint(item, "setIdentifier:", text);
                SendVoid(text, "release");
            }
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)_messageSend)(menu, Selector("insertItem:atIndex:"), item,
                Math.Min(index, SendNint(menu, "numberOfItems")));
            SendVoid(item, "release");
            SendVoid(submenu, "release");
            return submenu;
        }

        internal void AddItems(nint menu, IEnumerable<MacOsMainMenu.Item> items, string name, nint target)
        {
            foreach (var item in items)
            {
                if (item.Action is null)
                {
                    SendVoidNint(menu, "addItem:", SendNint(GetClass("NSMenuItem"), "separatorItem"));
                    continue;
                }
                AddMenuItem(menu, DesktopStandardMenuTitles.Format(item.Title, name), item.Action,
                    item.KeyEquivalent, item.Modifiers, item.OwnTarget ? target : 0, false);
            }
        }

        // The submenu of a top-level item with the identifier, or whose submenu has one of the actions; 0 otherwise.
        internal nint FindSubmenu(nint mainMenu, string identifier, string[] actions)
        {
            for (nint index = 0, count = SendNint(mainMenu, "numberOfItems"); index < count; index++)
            {
                var item = SendNintNint(mainMenu, "itemAtIndex:", index);
                var submenu = SendNint(item, "submenu");
                if (submenu == 0)
                {
                    continue;
                }
                if (Text(SendNint(item, "identifier")) == identifier)
                {
                    return submenu;
                }
                for (nint child = 0, children = SendNint(submenu, "numberOfItems"); child < children; child++)
                {
                    var action = SendNint(SendNintNint(submenu, "itemAtIndex:", child), "action");
                    if (action != 0 && actions.Contains(Marshal.PtrToStringUTF8(Native.SelGetName(action)), StringComparer.Ordinal))
                    {
                        return submenu;
                    }
                }
            }
            return 0;
        }

        // Removes the items whose identifier starts with the prefix, in every submenu.
        internal void RemoveIdentifiedItems(nint menu, string prefix)
        {
            for (var index = SendNint(menu, "numberOfItems") - 1; index >= 0; index--)
            {
                var item = SendNintNint(menu, "itemAtIndex:", index);
                if (Text(SendNint(item, "identifier")).StartsWith(prefix, StringComparison.Ordinal))
                {
                    SendVoidNint(menu, "removeItemAtIndex:", index);
                }
                else if (SendNint(item, "submenu") is var submenu and not 0)
                {
                    RemoveIdentifiedItems(submenu, prefix);
                }
            }
        }

        internal nint NewMenuItem(string title, string? action, string keyEquivalent, ulong modifiers, nint target)
        {
            var item = AllocMenuItem(title, action is null ? 0 : Selector(action), keyEquivalent);
            SendVoidUlong(item, "setKeyEquivalentModifierMask:", modifiers);
            if (target != 0) SendVoidNint(item, "setTarget:", target);
            return item;
        }

        internal void InsertItem(nint menu, nint item, int index) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)_messageSend)(menu, Selector("insertItem:atIndex:"), item,
                Math.Min(index, SendNint(menu, "numberOfItems")));

        internal void SetIdentifier(nint item, string identifier)
        {
            var text = CreateString(identifier);
            SendVoidNint(item, "setIdentifier:", text);
            SendVoid(text, "release");
        }

        internal void SetTitle(nint item, nint submenu, string title)
        {
            var text = CreateString(title);
            if (item != 0) SendVoidNint(item, "setTitle:", text);
            if (submenu != 0) SendVoidNint(submenu, "setTitle:", text);
            SendVoid(text, "release");
        }

        internal void SetAction(nint item, string action, nint target)
        {
            SendVoidNint(item, "setAction:", Selector(action));
            SendVoidNint(item, "setTarget:", target);
        }

        internal bool HasAction(nint item, string action)
        {
            var selector = SendNint(item, "action");
            return selector != 0 && Marshal.PtrToStringUTF8(Native.SelGetName(selector)) == action;
        }

        internal bool SendBool(nint receiver, string selector, nint argument) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)_messageSend)(receiver, Selector(selector), argument) != 0;

        internal nint IndexOfSubmenu(nint mainMenu, nint submenu) =>
            submenu == 0 ? -1 : SendNintNint(mainMenu, "indexOfItemWithSubmenu:", submenu);

        // Whether the menu or a submenu has an item for Command and the key, without other modifiers.
        internal bool HasCommandKey(nint menu, string key) => AnyItem(menu, item =>
            MacOsMainMenu.IsCommandKey(Text(SendNint(item, "keyEquivalent")), unchecked((ulong)SendNint(item, "keyEquivalentModifierMask")), key));

        internal bool HasAnyKeyEquivalent(nint menu) => AnyItem(menu, item => Text(SendNint(item, "keyEquivalent")).Length > 0);

        private bool AnyItem(nint menu, Func<nint, bool> match)
        {
            for (nint index = 0, count = SendNint(menu, "numberOfItems"); index < count; index++)
            {
                var item = SendNintNint(menu, "itemAtIndex:", index);
                if (match(item) || (SendNint(item, "submenu") is var submenu and not 0 && AnyItem(submenu, match)))
                {
                    return true;
                }
            }
            return false;
        }

        // A target of 0 sends the action along the responder chain. A hidden item answers its key equivalent
        // (allowsKeyEquivalentWhenHidden, macOS 10.13).
        internal void AddMenuItem(nint menu, string title, string action, string keyEquivalent, ulong modifiers, nint target, bool hidden)
        {
            var item = AllocMenuItem(title, Selector(action), keyEquivalent);
            SendVoidUlong(item, "setKeyEquivalentModifierMask:", modifiers);
            if (target != 0)
            {
                SendVoidNint(item, "setTarget:", target);
            }
            if (hidden)
            {
                SendVoidBool(item, "setHidden:", true);
                SendVoidBool(item, "setAllowsKeyEquivalentWhenHidden:", true);
            }
            SendVoidNint(menu, "addItem:", item);
            SendVoid(item, "release");
        }

        private nint AllocMenuItem(string title, nint action, string keyEquivalent)
        {
            var text = CreateString(title);
            var key = CreateString(keyEquivalent);
            try
            {
                return ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint>)_messageSend)(
                    SendNint(GetClass("NSMenuItem"), "alloc"), Selector("initWithTitle:action:keyEquivalent:"), text, action, key);
            }
            finally
            {
                SendVoid(key, "release");
                SendVoid(text, "release");
            }
        }

        internal nint GetClass(string name)
        {
            using var value = Utf8String.Create(name);
            return Native.ObjCGetClass(value.Pointer);
        }

        internal nint AllocateClassPair(nint superclass, string name)
        {
            using var value = Utf8String.Create(name);
            return Native.ObjCAllocateClassPair(superclass, value.Pointer, 0);
        }

        internal void AddMethod(nint type, string selector, nint implementation, string types)
        {
            using var signature = Utf8String.Create(types);
            Native.ClassAddMethod(type, Selector(selector), implementation, signature.Pointer);
        }

        internal void RegisterClassPair(nint type) => Native.ObjCRegisterClassPair(type);

        internal nint SendNint(nint receiver, string selector) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, nint>)_messageSend)(receiver, Selector(selector));

        internal nint SendNintNint(nint receiver, string selector, nint value) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)_messageSend)(receiver, Selector(selector), value);

        internal void AddObserver(nint receiver, nint observer, string keyPath)
        {
            var path = CreateString(keyPath);
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, ulong, nint, void>)_messageSend)(
                receiver, Selector("addObserver:forKeyPath:options:context:"), observer, path, 0, 0);
            SendVoid(path, "release");
        }

        internal void RemoveObserver(nint receiver, nint observer, string keyPath)
        {
            var path = CreateString(keyPath);
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)_messageSend)(
                receiver, Selector("removeObserver:forKeyPath:"), observer, path);
            SendVoid(path, "release");
        }

        internal void SendVoid(nint receiver, string selector) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, void>)_messageSend)(receiver, Selector(selector));

        internal void SendVoidNint(nint receiver, string selector, nint value) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)_messageSend)(receiver, Selector(selector), value);

        internal void SendVoidBool(nint receiver, string selector, bool value) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, byte, void>)_messageSend)(receiver, Selector(selector), value ? (byte)1 : (byte)0);

        internal void SendBoolLong(nint receiver, string selector, long value) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, long, byte>)_messageSend)(receiver, Selector(selector), value);

        internal bool RespondsToSelector(nint receiver, string selector) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)_messageSend)(
                receiver, Selector("respondsToSelector:"), Selector(selector)) != 0;

        internal void SendVoidUlong(nint receiver, string selector, ulong value) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, ulong, void>)_messageSend)(receiver, Selector(selector), value);

        internal void SendVoidSize(nint receiver, string selector, CGSize value) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, CGSize, void>)_messageSend)(receiver, Selector(selector), value);

        internal void SendVoidPoint(nint receiver, string selector, CGPoint value) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, CGPoint, void>)_messageSend)(receiver, Selector(selector), value);

        internal bool SendBool(nint receiver, string selector) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, byte>)_messageSend)(receiver, Selector(selector)) != 0;

        // A 16-byte NSPoint comes back in registers on both architectures, so objc_msgSend returns it.
        internal CGPoint SendPoint(nint receiver, string selector) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, CGPoint>)_messageSend)(receiver, Selector(selector));

        // [menu popUpMenuPositioningItem:nil atLocation:location inView:view]: tracks modally, true when an item was chosen.
        internal bool PopUpMenu(nint menu, CGPoint location, nint view) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, CGPoint, nint, byte>)_messageSend)(
                menu, Selector("popUpMenuPositioningItem:atLocation:inView:"), 0, location, view) != 0;

        // A retained repeating NSTimer that sends action to target, scheduled in the common run loop modes, which
        // include the event tracking mode of an open menu.
        internal nint NewRepeatingTimer(double interval, nint target, string action)
        {
            var timer = ((delegate* unmanaged[Cdecl]<nint, nint, double, nint, nint, nint, byte, nint>)_messageSend)(
                GetClass("NSTimer"), Selector("timerWithTimeInterval:target:selector:userInfo:repeats:"), interval, target,
                Selector(action), 0, 1);
            SendVoid(timer, "retain");
            var mode = CreateString("kCFRunLoopCommonModes");
            try
            {
                ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)_messageSend)(
                    SendNint(GetClass("NSRunLoop"), "currentRunLoop"), Selector("addTimer:forMode:"), timer, mode);
            }
            finally
            {
                SendVoid(mode, "release");
            }
            return timer;
        }

        // [target performSelectorOnMainThread:selector withObject:nil waitUntilDone:NO modes:@[kCFRunLoopDefaultMode]].
        internal void PerformInDefaultMode(nint target, string selector)
        {
            var mode = CreateString("kCFRunLoopDefaultMode");
            try
            {
                var modes = SendNintNint(GetClass("NSArray"), "arrayWithObject:", mode);
                ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, byte, nint, void>)_messageSend)(
                    target, Selector("performSelectorOnMainThread:withObject:waitUntilDone:modes:"), Selector(selector), 0, 0, modes);
            }
            finally
            {
                SendVoid(mode, "release");
            }
        }

        internal ulong SendUlong(nint receiver, string selector) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, ulong>)_messageSend)(receiver, Selector(selector));

        internal uint SendUint(nint receiver, string selector) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, uint>)_messageSend)(receiver, Selector(selector));

        // [[NSNotificationCenter defaultCenter] addObserver:selector:name:object:nil].
        internal void AddNotificationObserver(nint observer, string selector, string name)
        {
            var text = CreateString(name);
            try
            {
                ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)_messageSend)(
                    SendNint(GetClass("NSNotificationCenter"), "defaultCenter"), Selector("addObserver:selector:name:object:"),
                    observer, Selector(selector), text, 0);
            }
            finally
            {
                SendVoid(text, "release");
            }
        }

        // The CGDirectDisplayID of an NSScreen: deviceDescription[@"NSScreenNumber"].
        internal uint ScreenNumber(nint screen)
        {
            var key = CreateString("NSScreenNumber");
            try
            {
                var description = SendNint(screen, "deviceDescription");
                var number = description == 0 ? 0 : SendNintNint(description, "objectForKey:", key);
                return number == 0 ? 0 : SendUint(number, "unsignedIntValue");
            }
            finally
            {
                SendVoid(key, "release");
            }
        }

        internal CGRect SendRect(nint receiver, string selector) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, CGRect>)_messageSendStructure)(receiver, Selector(selector));

        internal CGRect SendRectRect(nint receiver, string selector, CGRect value) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, CGRect, CGRect>)_messageSendStructure)(receiver, Selector(selector), value);

        internal void SendVoidRectBool(nint receiver, string selector, CGRect value, bool flag) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, CGRect, byte, void>)_messageSend)(receiver, Selector(selector), value, flag ? (byte)1 : (byte)0);

        // The visible frame of every screen: without the menu bar and the Dock, in AppKit screen coordinates.
        internal List<ScreenRect> GetScreenVisibleFrames()
        {
            List<ScreenRect> frames = [];
            var screens = SendNint(GetClass("NSScreen"), "screens");
            if (screens == 0)
            {
                return frames;
            }
            var count = (long)SendUlong(screens, "count");
            for (long index = 0; index < count; index++)
            {
                var screen = SendNintNint(screens, "objectAtIndex:", (nint)index);
                if (screen == 0)
                {
                    continue;
                }
                var visible = SendRect(screen, "visibleFrame");
                frames.Add(new ScreenRect((int)Math.Round(visible.X), (int)Math.Round(visible.Y),
                    (int)Math.Round(visible.Width), (int)Math.Round(visible.Height)));
            }
            return frames;
        }

        // The script runs at document start in every frame (WKUserScriptInjectionTimeAtDocumentStart = 0).
        internal nint AllocInitWebView(CGRect frame, string? documentStartScript, nint webViewClass, nint fileDropHandler, nint windowChromeHandler)
        {
            var configuration = SendNint(SendNint(GetClass("WKWebViewConfiguration"), "alloc"), "init");
            if (configuration == 0)
            {
                return 0;
            }

            try
            {
                if (documentStartScript is not null)
                {
                    var source = CreateString(documentStartScript);
                    try
                    {
                        var script = ((delegate* unmanaged[Cdecl]<nint, nint, nint, long, byte, nint>)_messageSend)(
                            SendNint(GetClass("WKUserScript"), "alloc"),
                            Selector("initWithSource:injectionTime:forMainFrameOnly:"),
                            source,
                            0,
                            0);
                        if (script == 0)
                        {
                            return 0;
                        }
                        SendVoidNint(SendNint(configuration, "userContentController"), "addUserScript:", script);
                        SendVoid(script, "release");
                    }
                    finally
                    {
                        SendVoid(source, "release");
                    }
                }

                if (fileDropHandler != 0)
                {
                    AddIsolatedScript(SendNint(configuration, "userContentController"), fileDropHandler,
                        FileDropSupport.WebKitMessageHandler, FileDropSupport.WebKitScript, "runic-desktop-file-drop", mainFrameOnly: false);
                }
                if (windowChromeHandler != 0)
                {
                    AddIsolatedScript(SendNint(configuration, "userContentController"), windowChromeHandler,
                        MacOsWindowChrome.MessageHandler, MacOsWindowChrome.Script, MacOsWindowChrome.ContentWorld, mainFrameOnly: true);
                }

                // WebKit's pop-up blocker: window.open reaches the UI delegate only from a script that handles a user
                // gesture, which CreateWebView relies on. Without a gesture window.open returns null, as before.
                SendVoidBool(SendNint(configuration, "preferences"), "setJavaScriptCanOpenWindowsAutomatically:", false);

                var instance = SendNint(webViewClass != 0 ? webViewClass : GetClass("WKWebView"), "alloc");
                return ((delegate* unmanaged[Cdecl]<nint, nint, CGRect, nint, nint>)_messageSend)(
                    instance, Selector("initWithFrame:configuration:"), frame, configuration);
            }
            finally
            {
                SendVoid(configuration, "release");
            }
        }

        // In its own content world (macOS 11 and later), where the page cannot reach the handler; otherwise in the page's.
        // The file drop script runs in every frame, so drops into same-origin iframes carry their label; the handler
        // checks the frame's origin. The window chrome script runs in the main frame only.
        private void AddIsolatedScript(nint controller, nint handler, string handlerName, string scriptSource, string contentWorld, bool mainFrameOnly)
        {
            var source = CreateString(scriptSource);
            var name = CreateString(handlerName);
            var mainFrame = mainFrameOnly ? (byte)1 : (byte)0;
            try
            {
                var worldClass = GetClass("WKContentWorld");
                nint script;
                if (worldClass != 0 && RespondsToSelector(controller, "addScriptMessageHandler:contentWorld:name:"))
                {
                    var worldName = CreateString(contentWorld);
                    try
                    {
                        var world = SendNintNint(worldClass, "worldWithName:", worldName);
                        ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)_messageSend)(
                            controller, Selector("addScriptMessageHandler:contentWorld:name:"), handler, world, name);
                        script = ((delegate* unmanaged[Cdecl]<nint, nint, nint, long, byte, nint, nint>)_messageSend)(
                            SendNint(GetClass("WKUserScript"), "alloc"),
                            Selector("initWithSource:injectionTime:forMainFrameOnly:inContentWorld:"), source, 0, mainFrame, world);
                    }
                    finally
                    {
                        SendVoid(worldName, "release");
                    }
                }
                else
                {
                    ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)_messageSend)(
                        controller, Selector("addScriptMessageHandler:name:"), handler, name);
                    script = ((delegate* unmanaged[Cdecl]<nint, nint, nint, long, byte, nint>)_messageSend)(
                        SendNint(GetClass("WKUserScript"), "alloc"),
                        Selector("initWithSource:injectionTime:forMainFrameOnly:"), source, 0, mainFrame);
                }
                if (script != 0)
                {
                    SendVoidNint(controller, "addUserScript:", script);
                    SendVoid(script, "release");
                }
            }
            finally
            {
                SendVoid(source, "release");
                SendVoid(name, "release");
            }
        }

        // The file URLs on a drag's pasteboard, as paths.
        internal List<string> ReadDroppedFilePaths(nint draggingInfo)
        {
            List<string> paths = [];
            var pasteboard = SendNint(draggingInfo, "draggingPasteboard");
            if (pasteboard == 0)
            {
                return paths;
            }
            var classes = SendNintNint(GetClass("NSArray"), "arrayWithObject:", GetClass("NSURL"));
            nint options = 0;
            if (NativeLibrary.TryGetExport(_appKit, "NSPasteboardURLReadingFileURLsOnlyKey", out var key))
            {
                var yes = ((delegate* unmanaged[Cdecl]<nint, nint, byte, nint>)_messageSend)(
                    GetClass("NSNumber"), Selector("numberWithBool:"), 1);
                options = ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)_messageSend)(
                    GetClass("NSDictionary"), Selector("dictionaryWithObject:forKey:"), yes, *(nint*)key);
            }
            var urls = ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)_messageSend)(
                pasteboard, Selector("readObjectsForClasses:options:"), classes, options);
            if (urls == 0)
            {
                return paths;
            }
            var count = (long)SendUlong(urls, "count");
            for (long index = 0; index < count; index++)
            {
                var url = SendNintNint(urls, "objectAtIndex:", (nint)index);
                if (url == 0 || !SendBool(url, "isFileURL"))
                {
                    continue;
                }
                // A file reference URL (file:///.file/id=...) names its file by identifier; standardize it to the path.
                var resolved = RespondsToSelector(url, "filePathURL") ? SendNint(url, "filePathURL") : url;
                var path = Text(SendNint(resolved != 0 ? resolved : url, "path"));
                if (path.Length > 0 && Path.IsPathFullyQualified(path) && !paths.Contains(path, StringComparer.Ordinal))
                {
                    paths.Add(path);
                }
            }
            return paths;
        }

        internal bool SendSuperBoolNint(nint receiver, nint superclass, string selector, nint value)
        {
            var super = new ObjCSuper(receiver, superclass);
            return ((delegate* unmanaged[Cdecl]<ObjCSuper*, nint, nint, byte>)_messageSendSuper)(&super, Selector(selector), value) != 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct ObjCSuper(nint receiver, nint superclass)
        {
            private readonly nint _receiver = receiver;
            private readonly nint _superclass = superclass;
        }

        internal nint AllocInitWindow(nint windowClass, CGRect frame, ulong style, ulong backing, bool defer)
        {
            var instance = SendNint(windowClass != 0 ? windowClass : GetClass("NSWindow"), "alloc");
            return ((delegate* unmanaged[Cdecl]<nint, nint, CGRect, ulong, ulong, byte, nint>)_messageSend)(
                instance, Selector("initWithContentRect:styleMask:backing:defer:"), frame, style, backing, defer ? (byte)1 : (byte)0);
        }

        internal nint NextEvent(nint application, ulong mask, nint date, nint mode, bool dequeue) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, ulong, nint, nint, byte, nint>)_messageSend)(
                application, Selector("nextEventMatchingMask:untilDate:inMode:dequeue:"), mask, date, mode, dequeue ? (byte)1 : (byte)0);

        internal void Announce(nint element, string message, bool assertive)
        {
            const long PriorityMedium = 50;
            const long PriorityHigh = 90;
            if (!NativeLibrary.TryGetExport(_appKit, "NSAccessibilityPostNotificationWithUserInfo", out var post)
                || !NativeLibrary.TryGetExport(_appKit, "NSAccessibilityAnnouncementRequestedNotification", out var notification)
                || !NativeLibrary.TryGetExport(_appKit, "NSAccessibilityAnnouncementKey", out var announcementKey)
                || !NativeLibrary.TryGetExport(_appKit, "NSAccessibilityPriorityKey", out var priorityKey))
            {
                throw new NotSupportedException("AppKit does not offer accessibility announcements.");
            }
            var text = CreateString(message);
            var level = ((delegate* unmanaged[Cdecl]<nint, nint, long, nint>)_messageSend)(
                SendNint(GetClass("NSNumber"), "alloc"), Selector("initWithLong:"), assertive ? PriorityHigh : PriorityMedium);
            var userInfo = SendNint(SendNint(GetClass("NSMutableDictionary"), "alloc"), "init");
            try
            {
                SetObject(userInfo, text, *(nint*)announcementKey);
                SetObject(userInfo, level, *(nint*)priorityKey);
                ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)post)(element, *(nint*)notification, userInfo);
            }
            finally
            {
                SendVoid(userInfo, "release");
                SendVoid(level, "release");
                SendVoid(text, "release");
            }
        }

        // [NSAppearance appearanceNamed:NSAppearanceNameAqua or NSAppearanceNameDarkAqua], or 0 for NoPreference.
        internal nint NamedAppearance(DesktopColorScheme colorScheme)
        {
            var name = colorScheme switch
            {
                DesktopColorScheme.Light => "NSAppearanceNameAqua",
                DesktopColorScheme.Dark => "NSAppearanceNameDarkAqua",
                _ => null,
            };
            if (name is null || !NativeLibrary.TryGetExport(_appKit, name, out var constant)) return 0;
            return SendNintNint(GetClass("NSAppearance"), "appearanceNamed:", *(nint*)constant);
        }

        // [[NSFileManager defaultManager] URLsForDirectory:NSDownloadsDirectory inDomains:NSUserDomainMask].firstObject.path
        internal string? UserDownloadsDirectory()
        {
            const ulong DownloadsDirectory = 15, UserDomainMask = 1;
            var manager = SendNint(GetClass("NSFileManager"), "defaultManager");
            var urls = ((delegate* unmanaged[Cdecl]<nint, nint, ulong, ulong, nint>)_messageSend)(
                manager, Selector("URLsForDirectory:inDomains:"), DownloadsDirectory, UserDomainMask);
            var url = urls == 0 ? 0 : SendNint(urls, "firstObject");
            var path = url == 0 ? 0 : SendNint(url, "path");
            return path == 0 ? null : Text(path);
        }

        // An autoreleased file NSURL for the path.
        internal nint FileUrl(string path)
        {
            var text = CreateString(path);
            try
            {
                return ((delegate* unmanaged[Cdecl]<nint, nint, nint, byte, nint>)_messageSend)(
                    GetClass("NSURL"), Selector("fileURLWithPath:isDirectory:"), text, 0);
            }
            finally
            {
                SendVoid(text, "release");
            }
        }

        // Marks a downloaded file as from the web (com.apple.quarantine), with the URL it came from, as browsers do: Gatekeeper
        // then checks it when it is opened. NSURLQuarantinePropertiesKey with Launch Services' quarantine keys.
        internal void SetDownloadQuarantine(string path, string? source)
        {
            if (!NativeLibrary.TryLoad("/System/Library/Frameworks/CoreServices.framework/CoreServices", out var coreServices)
                || !NativeLibrary.TryLoad("/System/Library/Frameworks/Foundation.framework/Foundation", out var foundation)
                || !NativeLibrary.TryGetExport(foundation, "NSURLQuarantinePropertiesKey", out var propertiesKey)
                || !NativeLibrary.TryGetExport(coreServices, "kLSQuarantineTypeKey", out var typeKey)
                || !NativeLibrary.TryGetExport(coreServices, "kLSQuarantineTypeWebDownload", out var webDownload)
                || !NativeLibrary.TryGetExport(coreServices, "kLSQuarantineDataURLKey", out var dataUrlKey))
            {
                return;
            }
            var url = FileUrl(path);
            if (url == 0)
            {
                return;
            }
            var properties = SendNint(SendNint(GetClass("NSMutableDictionary"), "alloc"), "init");
            var sourceText = source is null ? 0 : CreateString(source);
            try
            {
                SetObject(properties, *(nint*)webDownload, *(nint*)typeKey);
                var sourceUrl = sourceText == 0 ? 0 : SendNintNint(GetClass("NSURL"), "URLWithString:", sourceText);
                if (sourceUrl != 0)
                {
                    SetObject(properties, sourceUrl, *(nint*)dataUrlKey);
                }
                _ = ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, byte>)_messageSend)(
                    url, Selector("setResourceValue:forKey:error:"), properties, *(nint*)propertiesKey, 0);
            }
            finally
            {
                if (sourceText != 0) SendVoid(sourceText, "release");
                SendVoid(properties, "release");
            }
        }

        private void SetObject(nint dictionary, nint value, nint key) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)_messageSend)(dictionary, Selector("setObject:forKey:"), value, key);

        internal nint CreateString(string value)
        {
            using var utf8 = Utf8String.Create(value);
            var instance = SendNint(GetClass("NSString"), "alloc");
            return ((delegate* unmanaged[Cdecl]<nint, nint, byte*, nint>)_messageSend)(
                instance, Selector("initWithUTF8String:"), utf8.Pointer);
        }

        private static nint Selector(string name)
        {
            using var value = Utf8String.Create(name);
            return Native.SelRegisterName(value.Pointer);
        }
    }
#pragma warning restore CA1822

    private sealed unsafe class Utf8String : IDisposable
    {
        private readonly nint _memory;

        private Utf8String(string value) => _memory = Marshal.StringToCoTaskMemUTF8(value);

        internal byte* Pointer => (byte*)_memory;

        internal static Utf8String Create(string value) => new(value);

        public void Dispose() => Marshal.FreeCoTaskMem(_memory);
    }

    private static partial class Native
    {
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
        internal static unsafe partial nint ObjCGetClass(byte* name);

        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_getName")]
        internal static partial nint SelGetName(nint selector);

        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
        internal static unsafe partial nint SelRegisterName(byte* name);

        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_allocateClassPair")]
        internal static unsafe partial nint ObjCAllocateClassPair(nint superclass, byte* name, nuint extraBytes);

        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "class_addMethod")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool ClassAddMethod(nint type, nint selector, nint implementation, byte* types);

        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_registerClassPair")]
        internal static partial void ObjCRegisterClassPair(nint type);

        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_main_np")]
        internal static partial int PthreadMainNp();

        [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRunLoopRunInMode")]
        internal static partial int CFRunLoopRunInMode(nint mode, double seconds, byte returnAfterSourceHandled);

    }
}
