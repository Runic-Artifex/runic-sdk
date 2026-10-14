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
    private static readonly nint MenuTargetClass = CreateMenuTargetClass();
    private static nint s_menuTarget;

    private readonly TaskCompletionSource _closed = NewCompletionSource();
    private nint _application;
    private nint _window;
    private nint _webView;
    private nint _delegate;
    private Action? _closeRequested;
    private readonly CancellationTokenSource _nativeShutdown = new();
    private int _isOpen;
    private int _disposed;

    public bool SupportsCloseConfirmation => true;
    public bool SupportsDocumentStartScript => true;
    public bool SupportsNativeDispatch => true;
    public bool CheckNativeAccess() => IsMainThread;
    public async ValueTask DispatchNativeAsync(Action action, CancellationToken cancellationToken)
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

        _webView = Api.AllocInitWebView(frame, options.DocumentStartScript);
        if (_webView == 0)
        {
            Api.SendVoid(_window, "close");
            throw new InvalidOperationException("WebKit could not create a WKWebView.");
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
        if (window != 0)
        {
            Api.SendVoidNint(window, "setDelegate:", 0);
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
        Api.RegisterClassPair(type);
        return type;
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

        // The script runs at document start in every frame (WKUserScriptInjectionTimeAtDocumentStart = 0).
        internal nint AllocInitWebView(CGRect frame, string? documentStartScript)
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

                var instance = SendNint(GetClass("WKWebView"), "alloc");
                return ((delegate* unmanaged[Cdecl]<nint, nint, CGRect, nint, nint>)_messageSend)(
                    instance, Selector("initWithFrame:configuration:"), frame, configuration);
            }
            finally
            {
                SendVoid(configuration, "release");
            }
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
