using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

internal sealed class LinuxWebKitGtkHost : IWebUiEmbeddedHost
{
    private static readonly GtkApi Api = new();
    private static readonly GtkDispatcher Dispatcher = new(Api);

    private readonly TaskCompletionSource _closed = NewCompletionSource();
    private nint _window;
    private nint _webView;
    private WebUiEmbeddedHostOptions? _options;
    private int _dispatcherLease;
    private readonly CancellationTokenSource _nativeShutdown = new();
    private int _isOpen;
    private int _disposed;

    public bool SupportsCloseConfirmation => true;
    public bool SupportsNativeDispatch => true;
    public bool CheckNativeAccess() => Dispatcher.CheckAccess;
    public async ValueTask DispatchNativeAsync(Action action, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _nativeShutdown.Token);
        await InvokeWindowAsync(action, linked.Token).ConfigureAwait(false);
    }

    public event EventHandler? Closed;

    internal static bool IsSupported => OperatingSystem.IsLinux() && Api.IsAvailable;

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
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "WebKitGTK 4.1 or 4.0 and GTK 3 are required for embedded WebViews on Linux.");
        }
        if (IsOpen)
        {
            await NavigateAsync(url, cancellationToken).ConfigureAwait(false);
            return;
        }

        _options = options;
        try
        {
            await Dispatcher.AcquireAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _dispatcherLease, 1);
            await Dispatcher.InvokeAsync(() => Create(url), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (Interlocked.Exchange(ref _dispatcherLease, 0) != 0)
            {
                await Dispatcher.ReleaseAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        return InvokeWindowAsync(() => Api.LoadUri(_webView, url.AbsoluteUri), cancellationToken);
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
        => InvokeWindowAsync(() =>
        {
            Api.GtkWindowPresent(_window);
            Api.GtkWidgetGrabFocus(_webView);
        }, cancellationToken);

    public ValueTask MinimizeAsync(CancellationToken cancellationToken = default)
        => InvokeWindowAsync(() => Api.GtkWindowIconify(_window), cancellationToken);

    public ValueTask MaximizeAsync(CancellationToken cancellationToken = default)
        => InvokeWindowAsync(() =>
        {
            if (Api.GtkWindowIsMaximized(_window))
            {
                Api.GtkWindowUnmaximize(_window);
            }
            else
            {
                Api.GtkWindowMaximize(_window);
            }
        }, cancellationToken);

    public ValueTask SetSizeAsync(uint width, uint height, CancellationToken cancellationToken = default)
        => InvokeWindowAsync(() => Api.GtkWindowResize(_window, checked((int)width), checked((int)height)), cancellationToken);

    public ValueTask SetPositionAsync(uint x, uint y, CancellationToken cancellationToken = default)
        => InvokeWindowAsync(() => Api.GtkWindowMove(_window, checked((int)x), checked((int)y)), cancellationToken);

    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        => InvokeWindowAsync(() =>
        {
            if (visible)
            {
                Api.GtkWidgetShowAll(_window);
            }
            else
            {
                Api.GtkWidgetHide(_window);
            }
        }, cancellationToken);

    public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default)
        => InvokeWindowAsync(() => Api.BeginMove(_window), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await CloseCoreAsync(CancellationToken.None).ConfigureAwait(false);
        if (Interlocked.Exchange(ref _dispatcherLease, 0) != 0)
        {
            await Dispatcher.ReleaseAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    private async Task CloseCoreAsync(CancellationToken cancellationToken)
    {
        if (IsOpen)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_window != 0)
                {
                    Api.GtkWidgetDestroy(_window);
                }
            }, cancellationToken).ConfigureAwait(false);
            await _closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask InvokeWindowAsync(Action action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsOpen)
        {
            throw new InvalidOperationException("The embedded WebView window is not open.");
        }

        return new ValueTask(Dispatcher.InvokeAsync(action, cancellationToken));
    }

    private unsafe void Create(Uri url)
    {
        var options = _options ?? throw new InvalidOperationException("The embedded host has no launch options.");
        _window = Api.GtkWindowNew();
        _webView = Api.WebKitWebViewNew();
        if (_window == 0 || _webView == 0)
        {
            throw new InvalidOperationException("GTK could not create the embedded WebView window.");
        }

        var width = checked((int)options.Width);
        var height = checked((int)options.Height);
        Api.GtkWindowSetDefaultSize(_window, width, height);
        if (options.MinimumWidth is { } minimumWidth && options.MinimumHeight is { } minimumHeight)
        {
            Api.GtkWidgetSetSizeRequest(_window, checked((int)minimumWidth), checked((int)minimumHeight));
        }
        Api.GtkContainerAdd(_window, _webView);
        Api.GtkWindowSetDecorated(_window, !options.Frameless && !options.Kiosk);
        Api.GtkWindowSetResizable(_window, options.Resizable);
        if (options.Centered)
        {
            Api.GtkWindowSetPosition(_window, 1);
        }
        else if (options.X is { } x && options.Y is { } y)
        {
            Api.GtkWindowMove(_window, checked((int)x), checked((int)y));
        }
        if (options.Transparent)
        {
            Api.MakeTransparent(_window, _webView);
        }
        if (!string.IsNullOrWhiteSpace(options.IconFile))
        {
            Api.GtkWindowSetIconFromFile(_window, options.IconFile);
        }

        Api.Connect(_window, "delete-event", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnDelete, this);
        Api.Connect(_window, "destroy", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnDestroyed, this);
        Api.Connect(_webView, "notify::title", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnTitleChanged, this);
        Api.Connect(_webView, "permission-request", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnPermissionRequest, this);
        if (options.Frameless && options.Resizable)
        {
            Api.EnableResizeEvents(_webView);
            Api.Connect(_webView, "button-press-event", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnButtonPress, this);
        }
        Api.LoadUri(_webView, url.AbsoluteUri);
        Api.GtkWidgetShowAll(_window);
        if (options.Kiosk)
        {
            Api.GtkWindowFullscreen(_window);
        }
        if (options.Hidden)
        {
            Api.GtkWidgetHide(_window);
        }

        Volatile.Write(ref _isOpen, 1);
    }

    private void HandleDestroyed()
    {
        if (Interlocked.Exchange(ref _isOpen, 0) == 0)
        {
            return;
        }

        _window = 0;
        _webView = 0;
        _nativeShutdown.Cancel();
        _closed.TrySetResult();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnDelete(nint window, nint nativeEvent, nint context)
    {
        if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost host && host._options?.CloseRequested is { } requestClose)
        {
            requestClose();
            return 1;
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDestroyed(nint _, nint context)
    {
        if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost host)
        {
            host.HandleDestroyed();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnTitleChanged(nint webView, nint _, nint context)
    {
        if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost host && host._window != 0)
        {
            var title = Api.WebKitWebViewGetTitle(webView);
            if (!string.IsNullOrEmpty(title))
            {
                Api.GtkWindowSetTitle(host._window, title);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnButtonPress(nint widget, nint webEvent, nint context)
    {
        if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost host && host._window != 0)
        {
            return Api.BeginResize(host._window, widget, webEvent) ? 1 : 0;
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPermissionRequest(nint _, nint request, nint context)
    {
        if (GCHandle.FromIntPtr(context).Target is not LinuxWebKitGtkHost host ||
            (host._options?.AllowedPermissions & DesktopPermissionGrant.MediaCapture) != 0)
        {
            return 0;
        }

        Api.DenyPermission(request);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleaseSignalContext(nint context, nint _)
    {
        GCHandle.FromIntPtr(context).Free();
    }

    private static unsafe nint SignalContextDestroyCallback =>
        (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&ReleaseSignalContext;

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

#pragma warning disable CA1001 // Process-lifetime dispatcher synchronization is retained by the static host factory.
    private sealed class GtkDispatcher
    {
        private readonly GtkApi _api;
        private readonly TaskCompletionSource _initialized = NewCompletionSource();
        private readonly SemaphoreSlim _leaseGate = new(1, 1);
        private readonly object _loopGate = new();
        private readonly AutoResetEvent _startLoop = new(false);
        private TaskCompletionSource _loopRunning = NewCompletionSource();
        private TaskCompletionSource _loopStopped = NewCompletionSource();
        private int _threadId;
        internal bool CheckAccess => Environment.CurrentManagedThreadId == Volatile.Read(ref _threadId);
        private int _leases;
        private int _started;

        internal GtkDispatcher(GtkApi api) => _api = api;

        internal async Task AcquireAsync(CancellationToken cancellationToken)
        {
            EnsureStarted();
            await _initialized.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await _leaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_leases++ == 0)
                {
                    lock (_loopGate)
                    {
                        _loopRunning = NewCompletionSource();
                        _loopStopped = NewCompletionSource();
                    }
                    _startLoop.Set();
                }
            }
            finally
            {
                _leaseGate.Release();
            }
        }

        internal async Task ReleaseAsync()
        {
            await _leaseGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (--_leases != 0)
                {
                    return;
                }

                Task stopped;
                lock (_loopGate)
                {
                    stopped = _loopStopped.Task;
                }
                await InvokeAsync(_api.GtkMainQuit, CancellationToken.None).ConfigureAwait(false);
                await stopped.ConfigureAwait(false);
            }
            finally
            {
                _leaseGate.Release();
            }
        }

        internal async Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CheckAccess) { action(); return; }
            EnsureStarted();
            await _initialized.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task running;
            lock (_loopGate)
            {
                running = _loopRunning.Task;
            }
            await running.WaitAsync(cancellationToken).ConfigureAwait(false);
            var work = new NativeDispatchWork(action, cancellationToken);
            var handle = GCHandle.Alloc(work);
            if (_api.GIdleAdd(WorkItemCallback, GCHandle.ToIntPtr(handle)) == 0)
            {
                handle.Free();
                throw new InvalidOperationException("GTK rejected a main-loop work item.");
            }

            await work.WaitAsync().ConfigureAwait(false);
        }

        private void EnsureStarted()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                return;
            }

            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Runic Desktop WebKitGTK",
            };
            thread.Start();
        }

        private void Run()
        {
            Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
            try
            {
                if (!_api.GtkInitCheck())
                {
                    throw new InvalidOperationException("GTK could not connect to a display server.");
                }
                _initialized.TrySetResult();
                while (true)
                {
                    _startLoop.WaitOne();
                    TaskCompletionSource running;
                    TaskCompletionSource stopped;
                    lock (_loopGate)
                    {
                        running = _loopRunning;
                        stopped = _loopStopped;
                    }
                    running.TrySetResult();
                    _api.GtkMain();
                    stopped.TrySetResult();
                }
            }
            catch (Exception exception)
            {
                _initialized.TrySetException(exception);
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int RunWorkItem(nint context)
        {
            var handle = GCHandle.FromIntPtr(context);
            var work = (NativeDispatchWork)handle.Target!;
            handle.Free();
            work.Run();
            return 0;
        }

        private static unsafe nint WorkItemCallback =>
            (nint)(delegate* unmanaged[Cdecl]<nint, int>)&RunWorkItem;
    }
#pragma warning restore CA1001

    private sealed unsafe class GtkApi
    {
        private readonly nint _gtk;
        private readonly nint _gObject;
        private readonly nint _webkit;

        internal GtkApi()
        {
            if (!OperatingSystem.IsLinux()
                || !TryLoad(["libgtk-3.so.0"], out _gtk)
                || !TryLoad(["libgobject-2.0.so.0"], out _gObject)
                || !TryLoad(["libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.0.so.37"], out _webkit))
            {
                return;
            }

            try
            {
                GtkInitCheckPointer = Required(_gtk, "gtk_init_check");
                GtkMainPointer = Required(_gtk, "gtk_main");
                GtkMainQuitPointer = Required(_gtk, "gtk_main_quit");
                GIdleAddPointer = Required(_gtk, "g_idle_add");
                GtkWindowNewPointer = Required(_gtk, "gtk_window_new");
                GtkWidgetShowAllPointer = Required(_gtk, "gtk_widget_show_all");
                GtkWidgetHidePointer = Required(_gtk, "gtk_widget_hide");
                GtkContainerAddPointer = Required(_gtk, "gtk_container_add");
                GtkWindowSetDefaultSizePointer = Required(_gtk, "gtk_window_set_default_size");
                GtkWidgetSetSizeRequestPointer = Required(_gtk, "gtk_widget_set_size_request");
                GtkWindowSetTitlePointer = Required(_gtk, "gtk_window_set_title");
                GtkWindowMovePointer = Required(_gtk, "gtk_window_move");
                GtkWindowResizePointer = Required(_gtk, "gtk_window_resize");
                GtkWindowSetPositionPointer = Required(_gtk, "gtk_window_set_position");
                GtkWindowSetDecoratedPointer = Required(_gtk, "gtk_window_set_decorated");
                GtkWindowSetResizablePointer = Required(_gtk, "gtk_window_set_resizable");
                GtkWidgetDestroyPointer = Required(_gtk, "gtk_widget_destroy");
                GtkWindowPresentPointer = Required(_gtk, "gtk_window_present");
                GtkWindowIconifyPointer = Required(_gtk, "gtk_window_iconify");
                GtkWindowMaximizePointer = Required(_gtk, "gtk_window_maximize");
                GtkWindowUnmaximizePointer = Required(_gtk, "gtk_window_unmaximize");
                GtkWindowIsMaximizedPointer = Required(_gtk, "gtk_window_is_maximized");
                GtkWindowFullscreenPointer = Required(_gtk, "gtk_window_fullscreen");
                GtkWidgetGrabFocusPointer = Required(_gtk, "gtk_widget_grab_focus");
                GtkWidgetAddEventsPointer = Required(_gtk, "gtk_widget_add_events");
                GtkWidgetGetAllocatedWidthPointer = Required(_gtk, "gtk_widget_get_allocated_width");
                GtkWidgetGetAllocatedHeightPointer = Required(_gtk, "gtk_widget_get_allocated_height");
                GtkWindowBeginMoveDragPointer = Required(_gtk, "gtk_window_begin_move_drag");
                GtkWindowBeginResizeDragPointer = Required(_gtk, "gtk_window_begin_resize_drag");
                GdkDisplayGetDefaultPointer = Required(_gtk, "gdk_display_get_default");
                GdkDisplayGetDefaultSeatPointer = Required(_gtk, "gdk_display_get_default_seat");
                GdkSeatGetPointerPointer = Required(_gtk, "gdk_seat_get_pointer");
                GdkDeviceGetPositionPointer = Required(_gtk, "gdk_device_get_position");
                GSignalConnectDataPointer = Required(_gtk, "g_signal_connect_data");
                GObjectUnrefPointer = Required(_gObject, "g_object_unref");
                GtkWindowSetIconFromFilePointer = Optional(_gtk, "gtk_window_set_icon_from_file");
                GtkWidgetSetVisualPointer = Optional(_gtk, "gtk_widget_set_visual");
                GtkWidgetGetScreenPointer = Optional(_gtk, "gtk_widget_get_screen");
                GdkScreenGetRgbaVisualPointer = Optional(_gtk, "gdk_screen_get_rgba_visual");
                GtkWidgetSetAppPaintablePointer = Optional(_gtk, "gtk_widget_set_app_paintable");
                WebKitWebContextNewPointer = Required(_webkit, "webkit_web_context_new");
                WebKitWebViewNewWithContextPointer = Required(_webkit, "webkit_web_view_new_with_context");
                WebKitWebViewLoadUriPointer = Required(_webkit, "webkit_web_view_load_uri");
                WebKitWebViewGetTitlePointer = Required(_webkit, "webkit_web_view_get_title");
                WebKitPermissionRequestDenyPointer = Required(_webkit, "webkit_permission_request_deny");
                WebKitWebViewSetBackgroundColorPointer = Optional(_webkit, "webkit_web_view_set_background_color");
                IsAvailable = true;
            }
            catch (EntryPointNotFoundException)
            {
                IsAvailable = false;
            }
        }

        internal bool IsAvailable { get; }

        private nint GtkInitCheckPointer { get; }
        private nint GtkMainPointer { get; }
        private nint GtkMainQuitPointer { get; }
        private nint GIdleAddPointer { get; }
        private nint GtkWindowNewPointer { get; }
        private nint GtkWidgetShowAllPointer { get; }
        private nint GtkWidgetHidePointer { get; }
        private nint GtkContainerAddPointer { get; }
        private nint GtkWindowSetDefaultSizePointer { get; }
        private nint GtkWidgetSetSizeRequestPointer { get; }
        private nint GtkWindowSetTitlePointer { get; }
        private nint GtkWindowMovePointer { get; }
        private nint GtkWindowResizePointer { get; }
        private nint GtkWindowSetPositionPointer { get; }
        private nint GtkWindowSetDecoratedPointer { get; }
        private nint GtkWindowSetResizablePointer { get; }
        private nint GtkWidgetDestroyPointer { get; }
        private nint GtkWindowPresentPointer { get; }
        private nint GtkWindowIconifyPointer { get; }
        private nint GtkWindowMaximizePointer { get; }
        private nint GtkWindowUnmaximizePointer { get; }
        private nint GtkWindowIsMaximizedPointer { get; }
        private nint GtkWindowFullscreenPointer { get; }
        private nint GtkWidgetGrabFocusPointer { get; }
        private nint GtkWidgetAddEventsPointer { get; }
        private nint GtkWidgetGetAllocatedWidthPointer { get; }
        private nint GtkWidgetGetAllocatedHeightPointer { get; }
        private nint GtkWindowBeginMoveDragPointer { get; }
        private nint GtkWindowBeginResizeDragPointer { get; }
        private nint GdkDisplayGetDefaultPointer { get; }
        private nint GdkDisplayGetDefaultSeatPointer { get; }
        private nint GdkSeatGetPointerPointer { get; }
        private nint GdkDeviceGetPositionPointer { get; }
        private nint GSignalConnectDataPointer { get; }
        private nint GObjectUnrefPointer { get; }
        private nint GtkWindowSetIconFromFilePointer { get; }
        private nint GtkWidgetSetVisualPointer { get; }
        private nint GtkWidgetGetScreenPointer { get; }
        private nint GdkScreenGetRgbaVisualPointer { get; }
        private nint GtkWidgetSetAppPaintablePointer { get; }
        private nint WebKitWebContextNewPointer { get; }
        private nint WebKitWebViewNewWithContextPointer { get; }
        private nint WebKitWebViewLoadUriPointer { get; }
        private nint WebKitWebViewGetTitlePointer { get; }
        private nint WebKitPermissionRequestDenyPointer { get; }
        private nint WebKitWebViewSetBackgroundColorPointer { get; }

        internal bool GtkInitCheck() => ((delegate* unmanaged[Cdecl]<nint, nint, int>)GtkInitCheckPointer)(0, 0) != 0;
        internal void GtkMain() => ((delegate* unmanaged[Cdecl]<void>)GtkMainPointer)();
        internal void GtkMainQuit() => ((delegate* unmanaged[Cdecl]<void>)GtkMainQuitPointer)();
        internal uint GIdleAdd(nint callback, nint data) => ((delegate* unmanaged[Cdecl]<nint, nint, uint>)GIdleAddPointer)(callback, data);
        internal nint GtkWindowNew() => ((delegate* unmanaged[Cdecl]<int, nint>)GtkWindowNewPointer)(0);
        internal void GtkWidgetShowAll(nint widget) => ((delegate* unmanaged[Cdecl]<nint, void>)GtkWidgetShowAllPointer)(widget);
        internal void GtkWidgetHide(nint widget) => ((delegate* unmanaged[Cdecl]<nint, void>)GtkWidgetHidePointer)(widget);
        internal void GtkContainerAdd(nint container, nint widget) => ((delegate* unmanaged[Cdecl]<nint, nint, void>)GtkContainerAddPointer)(container, widget);
        internal void GtkWindowSetDefaultSize(nint window, int width, int height) => ((delegate* unmanaged[Cdecl]<nint, int, int, void>)GtkWindowSetDefaultSizePointer)(window, width, height);
        internal void GtkWidgetSetSizeRequest(nint widget, int width, int height) => ((delegate* unmanaged[Cdecl]<nint, int, int, void>)GtkWidgetSetSizeRequestPointer)(widget, width, height);
        internal void GtkWindowMove(nint window, int x, int y) => ((delegate* unmanaged[Cdecl]<nint, int, int, void>)GtkWindowMovePointer)(window, x, y);
        internal void GtkWindowResize(nint window, int width, int height) => ((delegate* unmanaged[Cdecl]<nint, int, int, void>)GtkWindowResizePointer)(window, width, height);
        internal void GtkWindowSetPosition(nint window, int position) => ((delegate* unmanaged[Cdecl]<nint, int, void>)GtkWindowSetPositionPointer)(window, position);
        internal void GtkWindowSetDecorated(nint window, bool decorated) => ((delegate* unmanaged[Cdecl]<nint, int, void>)GtkWindowSetDecoratedPointer)(window, decorated ? 1 : 0);
        internal void GtkWindowSetResizable(nint window, bool resizable) => ((delegate* unmanaged[Cdecl]<nint, int, void>)GtkWindowSetResizablePointer)(window, resizable ? 1 : 0);
        internal void GtkWidgetDestroy(nint window) => ((delegate* unmanaged[Cdecl]<nint, void>)GtkWidgetDestroyPointer)(window);
        internal void GtkWindowPresent(nint window) => ((delegate* unmanaged[Cdecl]<nint, void>)GtkWindowPresentPointer)(window);
        internal void GtkWindowIconify(nint window) => ((delegate* unmanaged[Cdecl]<nint, void>)GtkWindowIconifyPointer)(window);
        internal void GtkWindowMaximize(nint window) => ((delegate* unmanaged[Cdecl]<nint, void>)GtkWindowMaximizePointer)(window);
        internal void GtkWindowUnmaximize(nint window) => ((delegate* unmanaged[Cdecl]<nint, void>)GtkWindowUnmaximizePointer)(window);
        internal bool GtkWindowIsMaximized(nint window) => ((delegate* unmanaged[Cdecl]<nint, int>)GtkWindowIsMaximizedPointer)(window) != 0;
        internal void GtkWindowFullscreen(nint window) => ((delegate* unmanaged[Cdecl]<nint, void>)GtkWindowFullscreenPointer)(window);
        internal void GtkWidgetGrabFocus(nint widget) => ((delegate* unmanaged[Cdecl]<nint, int>)GtkWidgetGrabFocusPointer)(widget);

        internal void GtkWindowSetTitle(nint window, string title)
        {
            using var value = Utf8String.Create(title);
            ((delegate* unmanaged[Cdecl]<nint, byte*, void>)GtkWindowSetTitlePointer)(window, value.Pointer);
        }

        internal void GtkWindowSetIconFromFile(nint window, string path)
        {
            if (GtkWindowSetIconFromFilePointer == 0)
            {
                return;
            }
            using var value = Utf8String.Create(path);
            ((delegate* unmanaged[Cdecl]<nint, byte*, nint, int>)GtkWindowSetIconFromFilePointer)(window, value.Pointer, 0);
        }

        internal void Connect(nint instance, string signal, nint callback, LinuxWebKitGtkHost host)
        {
            using var value = Utf8String.Create(signal);
            var handle = GCHandle.Alloc(host);
            var handler = ((delegate* unmanaged[Cdecl]<nint, byte*, nint, nint, nint, int, ulong>)GSignalConnectDataPointer)(
                instance,
                value.Pointer,
                callback,
                GCHandle.ToIntPtr(handle),
                SignalContextDestroyCallback,
                0);
            if (handler == 0)
            {
                handle.Free();
                throw new InvalidOperationException($"GTK rejected the '{signal}' signal handler.");
            }
        }

        internal nint WebKitWebViewNew()
        {
            var context = ((delegate* unmanaged[Cdecl]<nint>)WebKitWebContextNewPointer)();
            if (context == 0)
            {
                return 0;
            }

            try
            {
                return ((delegate* unmanaged[Cdecl]<nint, nint>)WebKitWebViewNewWithContextPointer)(context);
            }
            finally
            {
                ((delegate* unmanaged[Cdecl]<nint, void>)GObjectUnrefPointer)(context);
            }
        }

        internal void LoadUri(nint webView, string uri)
        {
            using var value = Utf8String.Create(uri);
            ((delegate* unmanaged[Cdecl]<nint, byte*, void>)WebKitWebViewLoadUriPointer)(webView, value.Pointer);
        }

        internal string? WebKitWebViewGetTitle(nint webView)
        {
            var value = ((delegate* unmanaged[Cdecl]<nint, nint>)WebKitWebViewGetTitlePointer)(webView);
            return value == 0 ? null : Marshal.PtrToStringUTF8(value);
        }

        internal void DenyPermission(nint request) =>
            ((delegate* unmanaged[Cdecl]<nint, void>)WebKitPermissionRequestDenyPointer)(request);

        internal void BeginMove(nint window)
        {
            var x = 0;
            var y = 0;
            var display = ((delegate* unmanaged[Cdecl]<nint>)GdkDisplayGetDefaultPointer)();
            var seat = display == 0 ? 0 : ((delegate* unmanaged[Cdecl]<nint, nint>)GdkDisplayGetDefaultSeatPointer)(display);
            var pointer = seat == 0 ? 0 : ((delegate* unmanaged[Cdecl]<nint, nint>)GdkSeatGetPointerPointer)(seat);
            if (pointer != 0)
            {
                ((delegate* unmanaged[Cdecl]<nint, nint, int*, int*, void>)GdkDeviceGetPositionPointer)(pointer, 0, &x, &y);
            }
            ((delegate* unmanaged[Cdecl]<nint, int, int, int, uint, void>)GtkWindowBeginMoveDragPointer)(window, 1, x, y, 0);
        }

        internal void EnableResizeEvents(nint widget) =>
            ((delegate* unmanaged[Cdecl]<nint, int, void>)GtkWidgetAddEventsPointer)(widget, 256);

        internal bool BeginResize(nint window, nint widget, nint webEvent)
        {
            var value = (GdkEventButton*)webEvent;
            if (value->Button != 1)
            {
                return false;
            }
            var width = ((delegate* unmanaged[Cdecl]<nint, int>)GtkWidgetGetAllocatedWidthPointer)(widget);
            var height = ((delegate* unmanaged[Cdecl]<nint, int>)GtkWidgetGetAllocatedHeightPointer)(widget);
            const int border = 6;
            var left = value->X <= border;
            var right = value->X >= width - border;
            var top = value->Y <= border;
            var bottom = value->Y >= height - border;
            var edge = top && left ? 0
                : top && right ? 2
                : bottom && left ? 5
                : bottom && right ? 7
                : left ? 3
                : right ? 4
                : top ? 1
                : bottom ? 6
                : -1;
            if (edge < 0)
            {
                return false;
            }
            ((delegate* unmanaged[Cdecl]<nint, int, int, int, int, uint, void>)GtkWindowBeginResizeDragPointer)(
                window, edge, checked((int)value->Button), checked((int)value->RootX), checked((int)value->RootY), value->Time);
            return true;
        }

        internal void MakeTransparent(nint window, nint webView)
        {
            if (GtkWidgetSetVisualPointer != 0 && GtkWidgetGetScreenPointer != 0 && GdkScreenGetRgbaVisualPointer != 0)
            {
                var screen = ((delegate* unmanaged[Cdecl]<nint, nint>)GtkWidgetGetScreenPointer)(window);
                var visual = screen == 0 ? 0 : ((delegate* unmanaged[Cdecl]<nint, nint>)GdkScreenGetRgbaVisualPointer)(screen);
                if (visual != 0)
                {
                    ((delegate* unmanaged[Cdecl]<nint, nint, void>)GtkWidgetSetVisualPointer)(window, visual);
                }
            }
            if (GtkWidgetSetAppPaintablePointer != 0)
            {
                ((delegate* unmanaged[Cdecl]<nint, int, void>)GtkWidgetSetAppPaintablePointer)(window, 1);
            }
            if (WebKitWebViewSetBackgroundColorPointer != 0)
            {
                var color = new GdkRgba();
                ((delegate* unmanaged[Cdecl]<nint, GdkRgba*, void>)WebKitWebViewSetBackgroundColorPointer)(webView, &color);
            }
        }

        private static bool TryLoad(IEnumerable<string> names, out nint handle)
        {
            foreach (var name in names)
            {
                if (NativeLibrary.TryLoad(name, out handle))
                {
                    return true;
                }
            }
            handle = 0;
            return false;
        }

        private static nint Required(nint library, string name) =>
            NativeLibrary.TryGetExport(library, name, out var address)
                ? address
                : throw new EntryPointNotFoundException(name);

        private static nint Optional(nint library, string name)
        {
            NativeLibrary.TryGetExport(library, name, out var address);
            return address;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GdkRgba
        {
            internal double Red;
            internal double Green;
            internal double Blue;
            internal double Alpha;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GdkEventButton
        {
            internal int Type;
            internal nint Window;
            internal byte SendEvent;
            private readonly byte Padding1;
            private readonly byte Padding2;
            private readonly byte Padding3;
            internal uint Time;
            internal double X;
            internal double Y;
            internal nint Axes;
            internal uint State;
            internal uint Button;
            internal nint Device;
            internal double RootX;
            internal double RootY;
        }
    }

    private sealed unsafe class Utf8String : IDisposable
    {
        private readonly nint _memory;

        private Utf8String(string value)
        {
            _memory = Marshal.StringToCoTaskMemUTF8(value);
        }

        internal byte* Pointer => (byte*)_memory;

        internal static Utf8String Create(string value) => new(value);

        public void Dispose() => Marshal.FreeCoTaskMem(_memory);
    }
}
