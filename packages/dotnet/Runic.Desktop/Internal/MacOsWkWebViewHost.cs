using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

internal sealed partial class MacOsWkWebViewHost : IWebUiEmbeddedHost, IWebUiMainThreadHost
{
    private const ulong StyleBorderless = 0;
    private const ulong StyleTitled = 1;
    private const ulong StyleClosable = 2;
    private const ulong StyleMiniaturizable = 4;
    private const ulong StyleResizable = 8;
    private const ulong BackingBuffered = 2;
    private const ulong ViewWidthSizable = 2;
    private const ulong ViewHeightSizable = 16;
    private const long ApplicationActivationPolicyRegular = 0;
    private const ulong EventMaskAny = ulong.MaxValue;

    private static readonly ObjC Api = new();
    private static readonly ConcurrentDictionary<nint, MacOsWkWebViewHost> Hosts = new();
    private static readonly ConcurrentQueue<NativeDispatchWork> MainQueue = new();
    private static readonly nint DelegateClass = CreateDelegateClass();
    // Hosts by their WKWebView, for the drop override and the page's drop label.
    private static readonly ConcurrentDictionary<nint, MacOsWkWebViewHost> WebViews = new();
    private static readonly nint WebViewClass = CreateWebViewClass();
    private static readonly nint TitleObserverClass = CreateTitleObserverClass();
    private static readonly Lazy<nint> FileDropHandler = new(CreateFileDropHandler);
    private FileDropPairing? _drops;
    private IDesktopMenuCommandTarget? _menuCommands;
    // The application's declared menus and their fallback command target; one menu bar per process.
    private static IReadOnlyList<DesktopMenu> s_menuBar = [];
    private static IDesktopMenuCommandTarget? s_applicationMenuCommands;
    private const string MenuCommandAction = "runicMenuCommand:";
    private const string DeclaredIdentifier = "runic.app.";
    private const string FileIdentifier = "runic.menu.file";

    public IDesktopMenuCommandTarget? MenuCommands
    {
        get => Volatile.Read(ref _menuCommands);
        set => Volatile.Write(ref _menuCommands, value);
    }

    internal static void SetApplicationMenuCommands(IDesktopMenuCommandTarget? target) => Volatile.Write(ref s_applicationMenuCommands, target);

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
    private Action? _closeRequested;
    private readonly CancellationTokenSource _nativeShutdown = new();
    private int _isOpen;
    private int _disposed;
    private DesktopWindowState? _state;
    private DesktopWindowPlacement? _normal;
    private const ulong StyleFullScreen = 1UL << 14;

    public bool SupportsCloseConfirmation => true;
    public DesktopWindowCapabilities Capabilities => DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.Focus |
        DesktopWindowCapabilities.Minimize | DesktopWindowCapabilities.Maximize |
        DesktopWindowCapabilities.Resize | DesktopWindowCapabilities.Move | DesktopWindowCapabilities.Announcements |
        DesktopWindowCapabilities.State | (WebViewClass != 0 ? DesktopWindowCapabilities.FileDrop : DesktopWindowCapabilities.None);

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

    public ValueTask SetSizeAsync(uint width, uint height, CancellationToken cancellationToken = default)
        => InvokeAsync(() => Api.SendVoidSize(_window, "setContentSize:", new CGSize(width, height)), cancellationToken);

    public ValueTask SetPositionAsync(int x, int y, CancellationToken cancellationToken = default)
        => InvokeAsync(() => Api.SendVoidPoint(_window, "setFrameOrigin:", new CGPoint(x, y)), cancellationToken);

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

    internal static void ProcessApplicationEvents()
    {
        var pool = Api.SendNint(Api.SendNint(Api.GetClass("NSAutoreleasePool"), "alloc"), "init");
        try
        {
            ProcessPendingMainThreadWork();
            // NSApp exists once a window or a window-independent service, such as a status item, created it. Its
            // events arrive with or without an open window. Before that, the run loop still runs the main dispatch
            // queue, through which those services start.
            var application = Api.SharedApplicationIfCreated();
            if (application != 0) PumpApplicationEvents(application);
            else Api.RunLoopOnce();
        }
        finally { Api.SendVoid(pool, "drain"); }
    }

    internal static void ProcessPendingMainThreadWork()
    {
        if (!IsMainThread)
        {
            return;
        }

        while (MainQueue.TryDequeue(out var work))
        {
            work.Run();
        }
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
        cancellationToken.ThrowIfCancellationRequested();
        if (IsMainThread)
        {
            action();
            return ValueTask.CompletedTask;
        }

        var work = new NativeDispatchWork(action, cancellationToken);
        MainQueue.Enqueue(work);
        return new ValueTask(work.WaitAsync());
    }

    private void Create(Uri url, WebUiEmbeddedHostOptions options)
    {
        _application = Api.SendNint(Api.GetClass("NSApplication"), "sharedApplication");
        // A bundle with LSUIElement runs without a Dock icon and menu bar, also while it shows a window.
        if (!Api.InfoDictionaryBool("LSUIElement"))
        {
            Api.SendBoolLong(_application, "setActivationPolicy:", ApplicationActivationPolicyRegular);
        }
        Api.SendVoid(_application, "finishLaunching");
        InstallMainMenu(_application);
        ApplyMenuBar(_application);

        var style = options.Frameless || options.Kiosk
            ? StyleBorderless | (options.Resizable ? StyleResizable : 0)
            : StyleTitled | StyleClosable | StyleMiniaturizable | (options.Resizable ? StyleResizable : 0);
        var frame = new CGRect(0, 0, options.Width, options.Height);
        _window = Api.AllocInitWindow(frame, style, BackingBuffered, false);
        if (_window == 0)
        {
            throw new InvalidOperationException("AppKit could not create the embedded WebView window.");
        }
        _delegate = Api.SendNint(Api.SendNint(DelegateClass, "alloc"), "init");
        Api.SendVoidNint(_window, "setDelegate:", _delegate);
        Api.SendVoidBool(_window, "setReleasedWhenClosed:", false);
        Api.SendVoidBool(_window, "setMovableByWindowBackground:", options.Frameless);
        if (options.MinimumWidth is { } minimumWidth && options.MinimumHeight is { } minimumHeight)
        {
            Api.SendVoidSize(_window, "setContentMinSize:", new CGSize(minimumWidth, minimumHeight));
        }

        _webView = Api.AllocInitWebView(frame, options.DocumentStartScript, WebViewClass, WebViewClass != 0 ? FileDropHandler.Value : 0);
        if (_webView == 0)
        {
            Api.SendVoid(_window, "close");
            throw new InvalidOperationException("WebKit could not create a WKWebView.");
        }
        if (WebViewClass != 0)
        {
            _drops = new FileDropPairing(drop => FilesDropped?.Invoke(this, drop), FileDropSupport.LabelWait);
            WebViews[_webView] = this;
        }
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
        Api.SendVoidNint(contentView, "addSubview:", _webView);
        // Keys reach the page without a click first, and the page sees Command keys before the View menu.
        Api.SendNintNint(_window, "makeFirstResponder:", _webView);
        if (options.Centered)
        {
            Api.SendVoid(_window, "center");
        }
        else if (options.X is { } x && options.Y is { } y)
        {
            Api.SendVoidPoint(_window, "setFrameOrigin:", new CGPoint(x, y));
        }
        if (options.Placement is not null)
        {
            FitPlacement(hasPosition: options.X is not null && options.Y is not null);
        }
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
        Navigate(url);
        if (!options.Hidden)
        {
            Api.SendVoidNint(_window, "makeKeyAndOrderFront:", 0);
            Api.SendVoidBool(_application, "activateIgnoringOtherApps:", true);
        }
        if (options.Kiosk)
        {
            Api.SendVoidNint(_window, "toggleFullScreen:", 0);
        }
        else if (options.Placement is { Maximized: true } && !Api.SendBool(_window, "isZoomed"))
        {
            Api.SendVoidNint(_window, "zoom:", 0);
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

    // Main thread only. The frame origin and the content size, the units of setFrameOrigin: and setContentSize:.
    private DesktopWindowPlacement ReadBounds()
    {
        var frame = Api.SendRect(_window, "frame");
        var content = Api.SendRectRect(_window, "contentRectForFrameRect:", frame);
        return new DesktopWindowPlacement
        {
            X = (int)Math.Round(frame.X),
            Y = (int)Math.Round(frame.Y),
            Width = (uint)Math.Max(1, Math.Round(content.Width)),
            Height = (uint)Math.Max(1, Math.Round(content.Height)),
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
            Placement = (_normal ?? current) with { Maximized = maximized },
        };
        if (Equals(state, Volatile.Read(ref _state)))
        {
            return;
        }
        Volatile.Write(ref _state, state);
        StateChanged?.Invoke(this, EventArgs.Empty);
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
                Api.AddItems(applicationMenu, MacOsMainMenu.Application, name, s_menuTarget);
            }
            if (Api.FindSubmenu(mainMenu, MacOsMainMenu.EditIdentifier, MacOsMainMenu.EditActions) == 0)
            {
                Api.AddItems(Api.InsertSubmenu(mainMenu, "Edit", 1, MacOsMainMenu.EditIdentifier), MacOsMainMenu.Edit, name, s_menuTarget);
            }
            // The Cocoa template's File menu: Close sends performClose:, so a window's close guard applies.
            if (Api.FindSubmenu(mainMenu, FileIdentifier, MacOsMainMenu.FileActions) == 0)
            {
                Api.AddItems(Api.InsertSubmenu(mainMenu, "File", 1, FileIdentifier), MacOsMainMenu.File, name, s_menuTarget);
            }
        }
        // The zoom items go into any main menu that has no Command and = or + of its own.
        if (!Api.HasCommandKey(mainMenu, "=") && !Api.HasCommandKey(mainMenu, "+"))
        {
            var view = Api.FindSubmenu(mainMenu, MacOsMainMenu.ViewIdentifier, MacOsMainMenu.ViewActions);
            if (view == 0)
            {
                var edit = Api.IndexOfSubmenu(mainMenu, Api.FindSubmenu(mainMenu, MacOsMainMenu.EditIdentifier, MacOsMainMenu.EditActions));
                view = Api.InsertSubmenu(mainMenu, "View", edit >= 0 ? edit + 1 : Api.SendNint(mainMenu, "numberOfItems"), MacOsMainMenu.ViewIdentifier);
            }
            else if (Api.SendNint(view, "numberOfItems") > 0)
            {
                Api.SendVoidNint(view, "addItem:", Api.SendNint(Api.GetClass("NSMenuItem"), "separatorItem"));
            }
            foreach (var item in MacOsPageZoom.MenuItems)
            {
                Api.AddMenuItem(view, item.Title, MacOsPageZoom.Selector(item.Command), item.KeyEquivalent, MacOsPageZoom.CommandModifier,
                    s_menuTarget, item.Hidden);
            }
        }
        if (complete && Api.SendNint(application, "windowsMenu") == 0
            && Api.FindSubmenu(mainMenu, MacOsMainMenu.WindowIdentifier, MacOsMainMenu.WindowActions) == 0)
        {
            var window = Api.InsertSubmenu(mainMenu, "Window", Api.SendNint(mainMenu, "numberOfItems"), MacOsMainMenu.WindowIdentifier);
            Api.AddItems(window, MacOsMainMenu.Window, "", s_menuTarget);
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
            Api.SetTitle(Api.SendNintNint(mainMenu, "itemAtIndex:", Api.IndexOfSubmenu(mainMenu, standardFile)), standardFile, "File");
        }
        var menus = Volatile.Read(ref s_menuBar);
        foreach (var menu in menus)
        {
            switch (menu.Role)
            {
                case DesktopMenuRole.Application:
                    var applicationMenu = Api.SendNint(mainMenu, "numberOfItems") > 0
                        ? Api.SendNint(Api.SendNintNint(mainMenu, "itemAtIndex:", 0), "submenu") : 0;
                    if (applicationMenu != 0) AddEntries(applicationMenu, menu.Items, insertAt: Math.Min(2, (int)Api.SendNint(applicationMenu, "numberOfItems")), separate: true);
                    break;
                case DesktopMenuRole.File:
                    var file = Api.FindSubmenu(mainMenu, FileIdentifier, MacOsMainMenu.FileActions);
                    if (file == 0)
                    {
                        file = Api.InsertSubmenu(mainMenu, menu.Title, DesktopMenuBar.InsertionIndex(DesktopMenuRole.File,
                            (int)Api.SendNint(mainMenu, "numberOfItems"), -1), DeclaredIdentifier + "file");
                        AddEntries(file, menu.Items, insertAt: null, separate: false);
                    }
                    else
                    {
                        Api.SetTitle(Api.SendNintNint(mainMenu, "itemAtIndex:", Api.IndexOfSubmenu(mainMenu, file)), file, menu.Title);
                        AddEntries(file, menu.Items, insertAt: 0, separate: true);
                    }
                    break;
                default:
                    var windowMenu = Api.SendNint(application, "windowsMenu");
                    var count = (int)Api.SendNint(mainMenu, "numberOfItems");
                    var index = DesktopMenuBar.InsertionIndex(menu.Role, count, windowMenu == 0 ? -1 : (int)Api.IndexOfSubmenu(mainMenu, windowMenu));
                    AddEntries(Api.InsertSubmenu(mainMenu, menu.Title, index, DeclaredIdentifier + "menu"), menu.Items, insertAt: null, separate: false);
                    break;
            }
        }
    }

    // Adds a declared menu's entries, at an index of an existing menu with a separator after them, or at its end.
    private static void AddEntries(nint menu, IReadOnlyList<DesktopMenuEntry> entries, int? insertAt, bool separate)
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
                case DesktopMenuBarSeparator:
                    Add(Api.SendNint(Api.GetClass("NSMenuItem"), "separatorItem"));
                    break;
                case DesktopMenuBarItem item:
                    var (key, modifiers) = item.Shortcut is { Key: not null } shortcut && !DesktopMenuBar.IsReserved(shortcut)
                        ? DesktopMenuBar.ToMacKeyEquivalent(shortcut)
                        : ("", 0UL);
                    var native = Api.NewMenuItem(item.Title, MenuCommandAction, key, modifiers, s_menuTarget);
                    Api.SetRepresentedString(native, item.CommandId);
                    Add(native);
                    Api.SendVoid(native, "release");
                    break;
                case DesktopMenu submenu:
                    var holder = Api.NewMenuItem(submenu.Title, null, "", 0, 0);
                    var child = Api.NewMenu(submenu.Title);
                    Api.SendVoidNint(holder, "setSubmenu:", child);
                    AddEntries(child, submenu.Items, insertAt: null, separate: false);
                    Add(holder);
                    Api.SendVoid(child, "release");
                    Api.SendVoid(holder, "release");
                    break;
            }
        }
        if (separate && entries.Count > 0)
        {
            Add(Api.SendNint(Api.GetClass("NSMenuItem"), "separatorItem"));
        }
    }

    // The target of the key window, or of the main window while a panel is key, then the application's.
    private static IEnumerable<IDesktopMenuCommandTarget> MenuTargets()
    {
        var application = Api.SharedApplicationIfCreated();
        if (application != 0)
        {
            foreach (var selector in (string[])["keyWindow", "mainWindow"])
            {
                if (Hosts.TryGetValue(Api.SendNint(application, selector), out var host) && host.IsOpen && host.MenuCommands is { } target)
                {
                    yield return target;
                    break;
                }
            }
        }
        if (Volatile.Read(ref s_applicationMenuCommands) is { } fallback) yield return fallback;
    }

    internal static bool CanExecuteMenuCommand(string commandId) => MenuTargets().Any(target => SafeCanExecute(target, commandId));

    internal static bool ExecuteMenuCommand(string commandId)
    {
        foreach (var target in MenuTargets())
        {
            if (!SafeCanExecute(target, commandId)) continue;
            target.Execute(commandId);
            return true;
        }
        return false;
    }

    private static bool SafeCanExecute(IDesktopMenuCommandTarget target, string commandId)
    {
        try { return target.CanExecute(commandId); }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MenuCommand(nint self, nint selector, nint sender)
    {
        try
        {
            if (Api.RepresentedString(sender) is { Length: > 0 } commandId) ExecuteMenuCommand(commandId);
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
            return Api.RepresentedString(item) is { Length: > 0 } commandId && CanExecuteMenuCommand(commandId) ? (byte)1 : (byte)0;
        }
        catch
        {
            return 0;
        }
    }

    // Quit asks every window to close, as its close button does: a close guard decides for its window.
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
        _drops?.Dispose();
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
        if (window != 0)
        {
            Api.SendVoidNint(window, "setDelegate:", 0);
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
        foreach (var selector in (string[])["windowDidMove:", "windowDidResize:", "windowDidEnterFullScreen:", "windowDidExitFullScreen:", "windowDidDeminiaturize:"])
        {
            Api.AddMethod(type, selector, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&WindowGeometryChanged, "v@:@");
        }
        Api.RegisterClassPair(type);
        return type;
    }

    // A WKWebView that reads the paths of dropped files from the dragging pasteboard, then lets WebKit perform the
    // drop, so the page still receives its drop event.
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
            if (WebViews.TryGetValue(self, out var host) && host.IsOpen && Api.ReadDroppedFilePaths(sender) is { Count: > 0 } paths)
            {
                host._drops?.NativeDrop(paths);
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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidReceiveFileDropMessage(nint self, nint selector, nint controller, nint message)
    {
        try
        {
            if (WebViews.TryGetValue(Api.SendNint(message, "webView"), out var host) && host.IsOpen)
            {
                host._drops?.PageReport(FileDropSupport.DecodeLabel(Api.Text(Api.SendNint(message, "body"))));
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
            QuitApplication();
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
    private sealed unsafe class ObjC
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
                AddMenuItem(menu, string.Format(System.Globalization.CultureInfo.InvariantCulture, item.Title, name), item.Action,
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

        internal void SetRepresentedString(nint item, string value)
        {
            var text = CreateString(value);
            SendVoidNint(item, "setRepresentedObject:", text);
            SendVoid(text, "release");
        }

        internal string? RepresentedString(nint item)
        {
            var value = item == 0 ? 0 : SendNint(item, "representedObject");
            return value != 0 && SendBool(value, "isKindOfClass:", GetClass("NSString")) ? Text(value) : null;
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

        internal ulong SendUlong(nint receiver, string selector) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, ulong>)_messageSend)(receiver, Selector(selector));

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
        internal nint AllocInitWebView(CGRect frame, string? documentStartScript, nint webViewClass, nint fileDropHandler)
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
                    AddFileDropScript(SendNint(configuration, "userContentController"), fileDropHandler);
                }

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
        private void AddFileDropScript(nint controller, nint handler)
        {
            var source = CreateString(FileDropSupport.WebKitScript);
            var name = CreateString(FileDropSupport.WebKitMessageHandler);
            try
            {
                var worldClass = GetClass("WKContentWorld");
                nint script;
                if (worldClass != 0 && RespondsToSelector(controller, "addScriptMessageHandler:contentWorld:name:"))
                {
                    var worldName = CreateString("runic-desktop-file-drop");
                    try
                    {
                        var world = SendNintNint(worldClass, "worldWithName:", worldName);
                        ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)_messageSend)(
                            controller, Selector("addScriptMessageHandler:contentWorld:name:"), handler, world, name);
                        script = ((delegate* unmanaged[Cdecl]<nint, nint, nint, long, byte, nint, nint>)_messageSend)(
                            SendNint(GetClass("WKUserScript"), "alloc"),
                            Selector("initWithSource:injectionTime:forMainFrameOnly:inContentWorld:"), source, 0, 1, world);
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
                        Selector("initWithSource:injectionTime:forMainFrameOnly:"), source, 0, 1);
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

        internal nint AllocInitWindow(CGRect frame, ulong style, ulong backing, bool defer)
        {
            var instance = SendNint(GetClass("NSWindow"), "alloc");
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
