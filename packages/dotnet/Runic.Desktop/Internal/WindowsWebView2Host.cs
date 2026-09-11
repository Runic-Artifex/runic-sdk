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
    private const uint WmSize = 0x0005;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmNcCreate = 0x0081;
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

    private static readonly string WindowClassName = $"Runic.Desktop.WebView2.{Environment.ProcessId}";
    private static readonly object ClassGate = new();
    private static ushort _windowClass;

    private readonly TaskCompletionSource _ready = NewCompletionSource();
    private readonly TaskCompletionSource _closed = NewCompletionSource();
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _dispatchQueue = new();
    private WindowsWebView2Controller? _controller;
    private WebUiEmbeddedHostOptions? _options;
    private Thread? _thread;
    private GCHandle _selfHandle;
    private nint _window;
    private nint _icon;
    private readonly CancellationTokenSource _nativeShutdown = new();
    private int _isOpen;
    private int _disposed;
    private int _maximized;

    public bool SupportsCloseConfirmation => true;
    public bool SupportsNativeDispatch => true;
    public bool CheckNativeAccess() => ReferenceEquals(Thread.CurrentThread, _thread);
    public async ValueTask DispatchNativeAsync(Action action, CancellationToken cancellationToken)
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

        _options = options;
        _selfHandle = GCHandle.Alloc(this);
        _thread = new Thread(() => Run(url))
        {
            IsBackground = true,
            Name = "Runic Desktop WebView2",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        cancellationToken.ThrowIfCancellationRequested();
        await _ready.Task.ConfigureAwait(false);
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

    public ValueTask MaximizeAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            var maximized = Interlocked.Exchange(ref _maximized, _maximized == 0 ? 1 : 0) != 0;
            Native.ShowWindow(_window, maximized ? SwRestore : SwMaximize);
        }, cancellationToken);

    public ValueTask SetSizeAsync(uint width, uint height, CancellationToken cancellationToken = default)
        => InvokeAsync(() => Native.SetWindowPos(
            _window, 0, 0, 0, ScaleForWindow(width, _window), ScaleForWindow(height, _window), SwpNoZOrder | SwpNoActivate | SwpNoMove), cancellationToken);

    public ValueTask SetPositionAsync(uint x, uint y, CancellationToken cancellationToken = default)
        => InvokeAsync(() => Native.SetWindowPos(
            _window, 0, checked((int)x), checked((int)y), 0, 0, SwpNoZOrder | SwpNoActivate | SwpNoSize), cancellationToken);

    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            Native.ShowWindow(_window, visible ? SwShow : SwHide);
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
            await InvokeAsync(() => Native.DestroyWindow(_window), cancellationToken).ConfigureAwait(false);
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
        _dispatchQueue.Enqueue((static state => ((NativeDispatchWork)state!).Run(), work));
        Native.PostMessage(_window, WmAppDispatch, 0, 0);
        return new ValueTask(work.WaitAsync());
    }

    private static int ScaleForWindow(uint value, nint window)
        => checked((int)Math.Round(value * (Native.GetDpiForWindow(window) / 96d)));

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
            var width = checked((int)options.Width);
            var height = checked((int)options.Height);
            var x = options.X is { } configuredX ? checked((int)configuredX) : 100;
            var y = options.Y is { } configuredY ? checked((int)configuredY) : 100;
            if (options.Centered)
            {
                x = Math.Max(0, (Native.GetSystemMetrics(SmCxScreen) - width) / 2);
                y = Math.Max(0, (Native.GetSystemMetrics(SmCyScreen) - height) / 2);
            }

            var style = options.Frameless || options.Kiosk
                ? WsPopup | (options.Resizable ? WsThickFrame : 0)
                : WsCaption | WsSysMenu | WsMinimizeBox | (options.Resizable ? WsThickFrame | WsMaximizeBox : 0);
            _window = Native.CreateWindowEx(
                0,
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

            width = ScaleForWindow(options.Width, _window);
            height = ScaleForWindow(options.Height, _window);
            if (options.Centered)
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
            _controller.Bounds = ClientBounds();
            _controller.IsVisible = !options.Hidden;
            if (options.Transparent)
            {
                _controller.SetTransparent();
            }
            _controller.RegisterEvents(
                title => Native.SetWindowText(_window, title),
                () => Native.PostMessage(_window, WmClose, 0, 0),
                options.AllowedPermissions);
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
            if (options.Kiosk)
            {
                Native.ShowWindow(_window, SwMaximize);
            }
            else
            {
                Native.ShowWindow(_window, options.Hidden ? SwHide : SwShow);
            }
            Native.UpdateWindow(_window);
            Volatile.Write(ref _isOpen, 1);
            _ready.TrySetResult();
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            Native.PostMessage(_window, WmClose, 0, 0);
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
    }

    private void HandleClosed()
    {
        if (Interlocked.Exchange(ref _isOpen, 0) == 0 && _closed.Task.IsCompleted)
        {
            return;
        }
        _window = 0;
        _nativeShutdown.Cancel();
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
            case WmDpiChanged:
                var suggested = (Rect*)lParam;
                Native.SetWindowPos(window, 0, suggested->Left, suggested->Top,
                    suggested->Right - suggested->Left, suggested->Bottom - suggested->Top,
                    SwpNoZOrder | SwpNoActivate);
                return 0;
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
            case WmDestroy:
                host?.CloseController();
                host?.HandleClosed();
                Native.PostQuitMessage(0);
                return 0;
            default:
                return Native.DefWindowProc(window, message, wParam, lParam);
        }
    }

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

    private static partial class Native
    {
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
