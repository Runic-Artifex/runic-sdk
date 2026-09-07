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

    public ValueTask SetPositionAsync(uint x, uint y, CancellationToken cancellationToken = default)
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

        var date = Api.SendNint(Api.GetClass("NSDate"), "distantPast");
        var mode = Api.CreateString("kCFRunLoopDefaultMode");
        try
        {
            while (true)
            {
                var webEvent = Api.NextEvent(_application, EventMaskAny, date, mode, true);
                if (webEvent == 0)
                {
                    break;
                }
                Api.SendVoidNint(_application, "sendEvent:", webEvent);
            }
            Api.SendVoid(_application, "updateWindows");
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
            foreach (var host in Hosts.Values) host.ProcessEvents();
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
        Api.SendBoolLong(_application, "setActivationPolicy:", ApplicationActivationPolicyRegular);
        Api.SendVoid(_application, "finishLaunching");

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
        if (options.Transparent && Api.RespondsToSelector(_webView, "setUnderPageBackgroundColor:"))
        {
            Api.SendVoidBool(_window, "setOpaque:", false);
            Api.SendVoidNint(_window, "setBackgroundColor:", Api.SendNint(Api.GetClass("NSColor"), "clearColor"));
        }
        if (options.MinimumWidth is { } minimumWidth && options.MinimumHeight is { } minimumHeight)
        {
            Api.SendVoidSize(_window, "setContentMinSize:", new CGSize(minimumWidth, minimumHeight));
        }

        _webView = Api.AllocInitView(Api.GetClass("WKWebView"), frame);
        if (_webView == 0)
        {
            Api.SendVoid(_window, "close");
            throw new InvalidOperationException("WebKit could not create a WKWebView.");
        }
        if (options.Transparent)
        {
            Api.SendVoidNint(_webView, "setUnderPageBackgroundColor:", Api.SendNint(Api.GetClass("NSColor"), "clearColor"));
        }
        Api.SendVoidUlong(_webView, "setAutoresizingMask:", ViewWidthSizable | ViewHeightSizable);
        var contentView = Api.SendNint(_window, "contentView");
        Api.SendVoidNint(contentView, "addSubview:", _webView);
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
        }

        internal bool IsAvailable { get; }

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

        internal nint AllocInitView(nint type, CGRect frame)
        {
            var instance = SendNint(type, "alloc");
            return ((delegate* unmanaged[Cdecl]<nint, nint, CGRect, nint>)_messageSend)(instance, Selector("initWithFrame:"), frame);
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

    }
}
