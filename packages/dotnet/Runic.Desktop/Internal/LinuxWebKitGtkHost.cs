using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

internal sealed partial class LinuxWebKitGtkHost : IWebUiEmbeddedHost
{
    private static readonly Lazy<GtkApi> NativeApi = new(static () => new());
    private static GtkApi Api => NativeApi.Value;
    private static readonly Lazy<GtkDispatcher> NativeDispatcher = new(static () => new(Api));
    private static GtkDispatcher Dispatcher => NativeDispatcher.Value;

    private readonly TaskCompletionSource _closed = NewCompletionSource();
    private nint _window;
    private nint _webView;
    private WebUiEmbeddedHostOptions? _options;
    private Uri? _presentedUrl;
    private readonly WebKitGtkWebProcess _webProcess = new();
    // With the MediaCapture grant only: the page's subframes, which the grant depends on.
    private WebKitGtkMediaFrames? _mediaFrames;
    private int _dispatcherLease;
    private readonly CancellationTokenSource _nativeShutdown = new();
    private int _isOpen;
    private int _disposed;
    private DesktopWindowState? _state;
    private DesktopWindowPlacement? _normal;
    private int _stateUpdateQueued;
    private FileDropPairing? _drops;
    // GTK thread only: the file URIs WebKit fetched while the drag moved over the view, per drag context.
    private nint _dragContext;
    private List<string>? _dragPaths;
    private bool _dropRequested;
    // GTK thread only: the window's DesktopWebContentPolicy (SEC-04) and the web context's download-started connection.
    private WebKitGtkWebContent? _webContent;
    private nint _downloadContext;
    private ulong _downloadHandler;

    // Tests observe the URLs handed to the system's handler here instead of launching it.
    internal static Action<string>? ExternalOpenerOverride { get; set; }

    public bool SupportsCloseConfirmation => true;

    // Known before GTK loads; DesktopPlatform.GetAvailability advertises these. State and FileDrop need GTK symbols.
    internal const DesktopWindowCapabilities KnownCapabilities = DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.Focus |
        DesktopWindowCapabilities.Minimize | DesktopWindowCapabilities.Maximize |
        DesktopWindowCapabilities.Resize | DesktopWindowCapabilities.Move | DesktopWindowCapabilities.Announcements |
        DesktopWindowCapabilities.MenuBar | DesktopWindowCapabilities.ContextMenu | DesktopWindowCapabilities.FileDrag;

    // Full screen needs gtk_window_unfullscreen; placing on a monitor also a position, which Wayland does not give.
    public DesktopWindowCapabilities Capabilities => KnownCapabilities |
        (Api.CanReportState ? DesktopWindowCapabilities.State : DesktopWindowCapabilities.None) |
        (Api.CanReceiveFileDrops ? DesktopWindowCapabilities.FileDrop : DesktopWindowCapabilities.None) |
        (Api.CanUseFullScreen ? DesktopWindowCapabilities.FullScreen : DesktopWindowCapabilities.None) |
        (Api.CanPlaceOnMonitor && !Api.IsWaylandDisplay() ? DesktopWindowCapabilities.MonitorPlacement : DesktopWindowCapabilities.None);

    public event EventHandler<DesktopFileDropEventArgs>? FilesDropped;

    public DesktopWindowState? State => Volatile.Read(ref _state);

    public event EventHandler? StateChanged;
    public bool SupportsDocumentStartScript => true;
    public bool SupportsNativeDispatch => true;
    public bool CheckNativeAccess() => Dispatcher.CheckAccess;
    public async ValueTask InvokeNativeAsync(Action action, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _nativeShutdown.Token);
        await InvokeWindowAsync(action, linked.Token).ConfigureAwait(false);
    }

    public event EventHandler? Closed;

    internal static bool IsSupported => OperatingSystem.IsLinux()
        && LinuxDesktopRuntime.CanUse(LinuxEmbeddedBackend.Gtk3WebKit41)
        && LinuxDesktopRuntime.IsLibraryAvailable(Gtk3NativeLibraries.Gtk)
        && LinuxDesktopRuntime.IsLibraryAvailable(Gtk3NativeLibraries.WebKit)
        // A library without every required export leaves the function pointers unset.
        && Api.IsAvailable;

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
                "WebKitGTK 4.1 and GTK 3 are required for embedded WebViews on Linux.");
        }
        if (IsOpen)
        {
            await NavigateAsync(url, cancellationToken).ConfigureAwait(false);
            return;
        }

        LinuxDesktopRuntime.ClaimBackend(LinuxEmbeddedBackend.Gtk3WebKit41);
        _options = options;
        _presentedUrl = url;
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

    // WebKitGTK reports no live-region change to AT-SPI (#318), so the window's accessible object announces instead:
    // ATK's notification signal, which the AT-SPI bridge sends as Object:Announcement with the politeness.
    public ValueTask AnnounceAsync(string message, DesktopAnnouncementPriority priority, CancellationToken cancellationToken = default)
        => InvokeWindowAsync(() => Api.Announce(_window, message, priority == DesktopAnnouncementPriority.Assertive), cancellationToken);

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

    public ValueTask SetPositionAsync(int x, int y, CancellationToken cancellationToken = default)
        => InvokeWindowAsync(() => Api.GtkWindowMove(_window, x, y), cancellationToken);

    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        => InvokeWindowAsync(() =>
        {
            if (visible)
            {
                Api.GtkWidgetShowAll(_window);
                // The global menu keeps showing the bar.
                if (_globalPlacement && _menuBar != 0) Api.GtkWidgetHide(_menuBar);
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
        _webView = Api.WebKitWebViewNew(options.Language);
        if (_window == 0 || _webView == 0)
        {
            throw new InvalidOperationException("GTK could not create the embedded WebView window.");
        }

        var width = checked((int)options.Width);
        var height = checked((int)options.Height);
        int? x = options.X;
        int? y = options.Y;
        var isWayland = Api.IsWaylandDisplay();
        // DesktopWindowOptions.MonitorId: centered on its work area on X11; on Wayland only a full-screen placement uses it.
        var chosen = options.MonitorId is { } monitorId ? FindMonitor(monitorId) : null;
        _fullScreenMonitor = chosen?.Index ?? -1;
        if (options.Placement is not null && Api.CanReportState)
        {
            // Keep a restored window on a monitor that exists now. Wayland lets the compositor place it.
            var workAreas = Api.GetWorkAreas();
            if (x is { } placedX && y is { } placedY && !isWayland)
            {
                var fitted = WindowPlacementClamp.Clamp(new ScreenRect(placedX, placedY, width, height), workAreas);
                (x, y, width, height) = (fitted.X, fitted.Y, fitted.Width, fitted.Height);
            }
            else
            {
                var target = chosen is { } monitor ? ToScreenRect(monitor.Monitor.WorkArea) : WindowPlacementClamp.Largest(workAreas);
                (width, height) = WindowPlacementClamp.ClampSize(width, height, target);
            }
            _normal = new DesktopWindowPlacement
            {
                X = isWayland ? null : x,
                Y = isWayland ? null : y,
                Width = width,
                Height = height,
            };
        }
        Api.GtkWindowSetDefaultSize(_window, width, height);
        if (options.MinimumWidth is { } minimumWidth && options.MinimumHeight is { } minimumHeight)
        {
            Api.GtkWidgetSetSizeRequest(_window, checked((int)minimumWidth), checked((int)minimumHeight));
        }
        // The Native menu bar goes above the view in a box (B10); other windows hold the view alone.
        var menuBox = CreateMenuBox(options);
        Api.GtkContainerAdd(_window, menuBox != 0 ? menuBox : _webView);
        // DesktopWindowOptions.LayoutDirection: the window and its widgets; the page keeps its own dir.
        if (options.LayoutDirection != Runic.Platform.DesktopLayoutDirection.Default)
        {
            var direction = options.LayoutDirection == Runic.Platform.DesktopLayoutDirection.RightToLeft ? 2 : 1;
            Api.GtkWidgetSetDirection(_window, direction);
            if (menuBox != 0) Api.GtkWidgetSetDirection(menuBox, direction);
            Api.GtkWidgetSetDirection(_webView, direction);
        }
        Api.GtkWindowSetDecorated(_window, !options.Frameless && !options.Kiosk);
        Api.GtkWindowSetResizable(_window, options.Resizable);
        if (chosen is { } onMonitor && !isWayland)
        {
            var area = CenterIn(width, height, ToScreenRect(onMonitor.Monitor.WorkArea));
            Api.GtkWindowMove(_window, area.X, area.Y);
        }
        else if (options.Centered)
        {
            Api.GtkWindowSetPosition(_window, 1);
        }
        else if (x is { } positionX && y is { } positionY)
        {
            Api.GtkWindowMove(_window, positionX, positionY);
        }
        if (options.DocumentStartScript is { } documentStartScript)
        {
            Api.AddDocumentStartScript(_webView, documentStartScript);
        }
        var contentManager = Api.AddKeyboardScript(_webView);
        if (options.Transparent)
        {
            Api.MakeTransparent(_window, _webView);
        }
        if (!string.IsNullOrWhiteSpace(options.IconFile))
        {
            Api.GtkWindowSetIconFromFile(_window, options.IconFile);
        }

        Api.Connect(_window, "delete-event", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnDelete, this);
        Api.Connect(_window, "focus", (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, int>)&OnWindowFocus, this);
        Api.Connect(_window, "destroy", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnDestroyed, this);
        Api.Connect(_webView, "notify::title", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnTitleChanged, this);
        Api.Connect(_webView, "permission-request", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnPermissionRequest, this);
        Api.Connect(_webView, "web-process-terminated", (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&OnWebProcessTerminated, this);
        if (Api.CanReportState)
        {
            // Before GTK's own handlers, which stop the emission; the state is read from a later main-loop iteration,
            // once GTK has recorded the new size, position and state.
            Api.Connect(_window, "configure-event", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnWindowGeometryEvent, this);
            Api.Connect(_window, "window-state-event", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnWindowGeometryEvent, this);
        }
        // WebKit sends each key to the page first. It runs handlers connected after its
        // own only for keys the page did not handle. The keyboard script reports
        // Control and the wheel, which WebKit would otherwise scroll with.
        Api.Connect(_webView, "key-press-event", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnUnhandledKeyPress, this, after: true);
        ConnectMenuEvents();
        Api.Connect(contentManager, "script-message-received::" + WebKitGtkKeyboard.ZoomMessageHandler, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnZoomMessage, this);
        if ((options.AllowedPermissions & DesktopPermissionGrant.MediaCapture) != 0)
        {
            _mediaFrames = new WebKitGtkMediaFrames(url);
            Api.AddMediaFramesScript(contentManager);
            Api.Connect(contentManager, "script-message-received::" + WebKitGtkMediaFrames.MessageHandler, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnMediaFramesMessage, this);
        }
        if (Api.CanReceiveFileDrops)
        {
            // Before WebKit's handlers: the drop's paths come from GTK, the page only reports the label of its target.
            _drops = new FileDropPairing(drop => FilesDropped?.Invoke(this, drop), FileDropSupport.LabelWait);
            Api.AddFileDropScript(contentManager);
            Api.Connect(contentManager, "script-message-received::" + FileDropSupport.WebKitMessageHandler, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnFileDropMessage, this);
            Api.Connect(_webView, "drag-data-received", (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, int, nint, uint, uint, nint, void>)&OnDragDataReceived, this);
            Api.Connect(_webView, "drag-drop", (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, int, uint, nint, int>)&OnDragDrop, this);
        }
        if (options.Frameless && options.Resizable)
        {
            Api.EnableResizeEvents(_webView);
            Api.Connect(_webView, "button-press-event", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnButtonPress, this);
        }
        ApplyWebContent(new WebContentRules(options.WebContent, url, options.AdditionalOrigins));
        Api.LoadUri(_webView, url.AbsoluteUri);
        if (options.Placement is { Maximized: true } && !options.Kiosk)
        {
            // Before mapping, so the window manager maps it maximized and keeps the restored size.
            Api.GtkWindowMaximize(_window);
        }
        Api.GtkWidgetShowAll(_window);
        if (options.Kiosk)
        {
            Api.GtkWindowFullscreen(_window);
        }
        else if (options.Placement is { FullScreen: true } && Api.CanUseFullScreen)
        {
            // A placement saved full-screen: on the chosen monitor (also on Wayland), else where the window opened.
            EnterFullScreen();
        }
        if (options.Hidden)
        {
            Api.GtkWidgetHide(_window);
        }
        // The window is realized now, also when hidden, so it has its X11 window.
        StartGlobalMenu(options);

        Volatile.Write(ref _isOpen, 1);
        UpdateState();
    }

    // GTK thread only. Reads the window after GTK has processed its configure and state events.
    private void UpdateState()
    {
        Volatile.Write(ref _stateUpdateQueued, 0);
        if (_window == 0 || !Api.CanReportState)
        {
            return;
        }
        var flags = Api.GetWindowStateFlags(_window);
        if ((flags & GdkWindowStateIconified) != 0)
        {
            // A minimized window keeps the state it had.
            return;
        }
        var maximized = (flags & GdkWindowStateMaximized) != 0 || Api.GtkWindowIsMaximized(_window);
        var fullScreen = (flags & GdkWindowStateFullscreen) != 0;
        var tiled = (flags & GdkWindowStateTiled) != 0;
        var (width, height) = Api.GtkWindowGetSize(_window);
        int? x = null;
        int? y = null;
        if (!Api.IsWaylandDisplay())
        {
            (var positionX, var positionY) = Api.GtkWindowGetPosition(_window);
            (x, y) = (positionX, positionY);
        }
        var current = new DesktopWindowPlacement { X = x, Y = y, Width = Math.Max(1, width), Height = Math.Max(1, height) };
        if (!maximized && !fullScreen && !tiled)
        {
            _normal = current;
        }
        var kiosk = _options?.Kiosk == true;
        var state = new DesktopWindowState
        {
            X = x,
            Y = y,
            Width = current.Width,
            Height = current.Height,
            IsMaximized = maximized,
            IsFullScreen = fullScreen,
            MonitorId = MonitorIdOf(Api.MonitorOfWindow(_window)),
            Placement = (_normal ?? current) with { Maximized = maximized, FullScreen = fullScreen && !kiosk },
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
            // Never unwind through GTK.
        }
    }

    private const int GdkWindowStateIconified = 1 << 1;
    private const int GdkWindowStateMaximized = 1 << 2;
    private const int GdkWindowStateFullscreen = 1 << 4;
    private const int GdkWindowStateTiled = 1 << 8;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnWindowGeometryEvent(nint window, nint nativeEvent, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost { IsOpen: true } host
                && Interlocked.Exchange(ref host._stateUpdateQueued, 1) == 0)
            {
                Dispatcher.Post(host.UpdateState);
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
        return 0;
    }

    private void HandleDestroyed()
    {
        if (Interlocked.Exchange(ref _isOpen, 0) == 0)
        {
            return;
        }

        // WebKitGTK's GTK 3 backend can drop its internal key-binding widget before the
        // container's disposal enumerates children. Destroy children while the
        // WebView still exposes them, so GTK releases their native resources.
        ReleaseMenus();
        Api.DestroyWebViewChildren(_webView);
        if (_downloadContext != 0)
        {
            Api.DisconnectAndRelease(_downloadContext, _downloadHandler);
            _downloadContext = 0;
            _downloadHandler = 0;
        }
        _webContent?.Clear();
        ReleaseDragOut();
        _drops?.Dispose();
        _dragContext = 0;
        _dragPaths = null;
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

    // When Tab leaves the page, WebKit asks the window to move focus on. The view is
    // the window's only focusable widget, so GTK would clear the window's focus and
    // give it back, which the page sees as the window losing and regaining focus.
    // Keep focus on the view; the keyboard script then focuses the other end of the page.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnWindowFocus(nint window, int direction, nint context)
    {
        const int TabForward = 0;
        const int TabBackward = 1;
        return GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost host
            && host._webView != 0
            && direction is TabForward or TabBackward
            && Api.GtkWindowGetFocus(window) == host._webView ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnUnhandledKeyPress(nint webView, nint keyEvent, nint context)
    {
        if (GCHandle.FromIntPtr(context).Target is not LinuxWebKitGtkHost host)
        {
            return 0;
        }
        var command = Api.GetZoomCommand(keyEvent);
        if (command == PageZoomCommand.None)
        {
            // The menu bar's shortcuts (B10), for every presentation.
            try
            {
                var (keyval, state, keycode) = Api.ReadKey(keyEvent);
                Span<uint> alternates = stackalloc uint[8];
                return host.TryRunShortcut(keyval, state, alternates[..Api.KeycodeKeyvals(keycode, alternates)]) ? 1 : 0;
            }
            catch
            {
                // Native callbacks cannot unwind through GTK.
                return 0;
            }
        }
        Api.SetZoomLevel(webView, WebKitGtkKeyboard.Apply(command, Api.GetZoomLevel(webView)));
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnZoomMessage(nint manager, nint message, nint context)
    {
        if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost { _webView: not 0 } host)
        {
            var command = WebKitGtkKeyboard.GetWheelZoomCommand(Api.GetMessageInt32(message));
            if (command != PageZoomCommand.None)
            {
                Api.SetZoomLevel(host._webView, WebKitGtkKeyboard.Apply(command, Api.GetZoomLevel(host._webView)));
            }
        }
    }

    // WebKit asks for the drag's data while it moves over the view, so the URIs are usually known before the drop.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDragDataReceived(nint widget, nint context, int x, int y, nint data, uint info, uint time, nint userData)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is not LinuxWebKitGtkHost { IsOpen: true } host || data == 0) return;
            var uris = Api.GetSelectionUris(data);
            if (uris is null) return;
            host._dragContext = context;
            host._dragPaths = FileDropSupport.ParseUriList(uris);
            if (host._dropRequested)
            {
                host._dropRequested = false;
                host.DeliverNativeDrop();
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnDragDrop(nint widget, nint context, int x, int y, uint time, nint userData)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is LinuxWebKitGtkHost { IsOpen: true } host)
            {
                // The action WebKit chose for the page's dropEffect, kept for a drop whose URIs arrive later.
                host._dropEffect = DropEffectOf(context);
                if (host._dragContext == context && host._dragPaths is not null)
                {
                    host.DeliverNativeDrop();
                }
                else if (Api.DragOffersUriList(context))
                {
                    // WebKit did not fetch the URIs during the motion: ask for them; they arrive in drag-data-received.
                    host._dropRequested = true;
                    Api.RequestUriList(widget, context, time);
                }
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
        // WebKit's own handler runs next and gives the page its drop event.
        return 0;
    }

    private void DeliverNativeDrop()
    {
        var paths = _dragPaths;
        _dragPaths = null;
        _dragContext = 0;
        if (paths is { Count: > 0 }) _drops?.NativeDrop(paths, [], _dropEffect);
    }

    // The script runs in every frame (D037.2); a report from a subframe of another origin than the presented one
    // refuses the drop.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFileDropMessage(nint manager, nint message, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost { IsOpen: true } host)
            {
                var report = FileDropSupport.DecodeReport(Api.GetMessageString(message));
                host._drops?.PageReport(report.Label, refused: !GtkDragSupport.AcceptsReport(host._presentedUrl, report.Origin));
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMediaFramesMessage(nint manager, nint message, nint context)
    {
        try
        {
            // Also before the window reports itself open: a frame the first page loads must not be missed.
            if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost host)
            {
                host._mediaFrames?.Receive(Api.GetMessageString(message));
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWebProcessTerminated(nint webView, int reason, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost { IsOpen: true } host && host._webView == webView)
            {
                host._mediaFrames?.Reset();
                var (reload, diagnostic) = host._webProcess.Terminated(reason, Environment.TickCount64);
                if (reload)
                {
                    Api.Reload(webView);
                }
                host._options?.ReportDiagnostic?.Invoke(diagnostic);
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPermissionRequest(nint webView, nint request, nint context)
    {
        // An unhandled request is denied by WebKit, so a grant must allow explicitly.
        if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost host && host.AllowsMediaCapture(webView, request))
        {
            Api.AllowPermission(request);
        }
        else
        {
            Api.DenyPermission(request);
        }
        return 1;
    }

    // WebKitGTK does not identify the requesting frame: the top-level document must have the presented origin, and so
    // must every subframe (WebKitGtkMediaFrames). Screen capture is not part of the grant.
    private bool AllowsMediaCapture(nint webView, nint request) =>
        _options is { } options && (options.AllowedPermissions & DesktopPermissionGrant.MediaCapture) != 0 &&
        _presentedUrl is { } presented &&
        _mediaFrames is { HasForeignFrames: false } &&
        Api.IsCameraOrMicrophoneRequest(request) &&
        PresentationSecurityPolicy.IsSameOrigin(presented, Api.WebKitWebViewGetUri(webView));

    // DesktopWebContentPolicy (SEC-04), before the first load: developer extras, top-level navigations, new windows and
    // downloads. See WebKitGtkWebContent for how the main frame's navigations are told apart.
    private unsafe void ApplyWebContent(WebContentRules rules)
    {
        _webContent = new WebKitGtkWebContent(rules, Api.CanIdentifyMainFrame);
        Api.SetDeveloperExtras(_webView, rules.DevToolsEnabled);
        Api.Connect(_webView, "decide-policy", (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, nint, int>)&OnDecidePolicy, this);
        Api.Connect(_webView, "create", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)&OnCreate, this);
        Api.Connect(_webView, "load-failed", (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, nint, nint, int>)&OnLoadFailed, this);
        if (!rules.AllowsDownloads)
        {
            // <a download> and other downloads WebKit starts without a response decision. The context is the view's own
            // (WebKitWebViewNew), referenced here until the window is destroyed.
            (_downloadContext, _downloadHandler) = Api.ConnectDownloadStarted(_webView,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnDownloadStarted, this);
        }
    }

    private const int PolicyDecisionNavigationAction = 0;
    private const int PolicyDecisionNewWindowAction = 1;
    private const int PolicyDecisionResponse = 2;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnDecidePolicy(nint webView, nint decision, int type, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost { IsOpen: true, _webContent: { } content } host)
            {
                return host.DecidePolicy(content, decision, type) ? 1 : 0;
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
        return 0;
    }

    // Whether this handler decided; WebKit's default handling applies otherwise.
    private bool DecidePolicy(WebKitGtkWebContent content, nint decision, int type)
    {
        switch (type)
        {
            case PolicyDecisionNavigationAction:
            {
                var (url, userGesture) = Api.GetNavigation(Api.GetNavigationAction(decision));
                return Apply(decision, content.ForNavigationAction(url, userGesture), url);
            }
            case PolicyDecisionNewWindowAction:
            {
                // Ignored, so WebKit emits no create signal for it.
                var (url, userGesture) = Api.GetNavigation(Api.GetNavigationAction(decision));
                Api.IgnorePolicy(decision);
                OpenNewWindow(content.Rules.ForNewWindow(url, userGesture), url);
                return true;
            }
            case PolicyDecisionResponse:
                if (Api.IsMainFrameMainResource(decision)
                    && content.ForMainFrameResponse(Api.GetResponseUri(decision)) is var navigation and not WebContentRules.Decision.Allow)
                {
                    return Apply(decision, navigation, Api.GetResponseUri(decision));
                }
                if (content.CancelsResponse(Api.IsMimeTypeSupported(decision)))
                {
                    Api.IgnorePolicy(decision);
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private bool Apply(nint decision, WebContentRules.Decision navigation, string? url)
    {
        if (navigation == WebContentRules.Decision.Allow) return false;
        Api.IgnorePolicy(decision);
        if (navigation == WebContentRules.Decision.OpenExternally) OpenExternally(url);
        return true;
    }

    // window.open, and a target="_blank" WebKit did not ask decide-policy about. Never creates a view.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnCreate(nint webView, nint navigationAction, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost { IsOpen: true, _webContent: { } content } host)
            {
                var (url, userGesture) = Api.GetNavigation(navigationAction);
                host.OpenNewWindow(content.Rules.ForNewWindow(url, userGesture), url);
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
        return 0;
    }

    private void OpenNewWindow(WebContentRules.Decision decision, string? url)
    {
        if (decision == WebContentRules.Decision.OpenExternally)
        {
            OpenExternally(url);
        }
        else if (decision == WebContentRules.Decision.NavigateInWindow && url is not null)
        {
            // From WebKit's signal: load once it has returned.
            Dispatcher.Post(() =>
            {
                if (IsOpen && _webView != 0) Api.LoadUri(_webView, url);
            });
        }
    }

    // A failed main-frame load: one waiting for its response may still open externally, and WebKit's error page must not
    // replace the application's document for a URL the window would not show.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnLoadFailed(nint webView, int loadEvent, nint failingUri, nint error, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost { IsOpen: true, _webContent: { } content } host)
            {
                var url = Marshal.PtrToStringUTF8(failingUri);
                if (content.ForFailedMainFrameLoad(url, out var suppress) == WebContentRules.Decision.OpenExternally)
                {
                    host.OpenExternally(url);
                }
                return suppress ? 1 : 0;
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDownloadStarted(nint webContext, nint download, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost { _webView: not 0 } host
                && Api.GetDownloadWebView(download) == host._webView)
            {
                Api.CancelDownload(download);
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    // A URL WebContentRules.ExternalTarget accepts, with the system's default handler, after WebKit's signal returned.
    private void OpenExternally(string? url)
    {
        if (WebContentRules.ExternalTarget(url) is not { } target) return;
        Dispatcher.Post(() =>
        {
            if (ExternalOpenerOverride is { } opener)
            {
                opener(target);
            }
            else if (_window != 0)
            {
                Api.ShowUri(_window, target);
            }
        });
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

        // GTK thread only: runs the action from a later main-loop iteration, after the current event is handled.
        internal void Post(Action action)
        {
            var work = new NativeDispatchWork(action, CancellationToken.None);
            var handle = GCHandle.Alloc(work);
            if (_api.GIdleAdd(WorkItemCallback, GCHandle.ToIntPtr(handle)) == 0)
            {
                handle.Free();
            }
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
                || !TryLoad(["libwebkit2gtk-4.1.so.0"], out _webkit))
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
                GtkContainerForAllPointer = Required(_gtk, "gtk_container_forall");
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
                GtkWindowGetFocusPointer = Required(_gtk, "gtk_window_get_focus");
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
                GtkWidgetGetAccessiblePointer = Required(_gtk, "gtk_widget_get_accessible");
                GSignalLookupPointer = Required(_gObject, "g_signal_lookup");
                GSignalEmitvPointer = Required(_gObject, "g_signal_emitv");
                GValueInitPointer = Required(_gObject, "g_value_init");
                GValueUnsetPointer = Required(_gObject, "g_value_unset");
                GValueSetObjectPointer = Required(_gObject, "g_value_set_object");
                GValueSetStringPointer = Required(_gObject, "g_value_set_string");
                GValueSetIntPointer = Required(_gObject, "g_value_set_int");
                GtkWindowSetIconFromFilePointer = Optional(_gtk, "gtk_window_set_icon_from_file");
                GtkWindowGetPositionPointer = Optional(_gtk, "gtk_window_get_position");
                GtkWindowGetSizePointer = Optional(_gtk, "gtk_window_get_size");
                GtkWidgetGetWindowPointer = Optional(_gtk, "gtk_widget_get_window");
                GdkWindowGetStatePointer = Optional(_gtk, "gdk_window_get_state");
                GdkDisplayGetNMonitorsPointer = Optional(_gtk, "gdk_display_get_n_monitors");
                GdkDisplayGetMonitorPointer = Optional(_gtk, "gdk_display_get_monitor");
                GdkMonitorGetWorkareaPointer = Optional(_gtk, "gdk_monitor_get_workarea");
                GdkMonitorGetGeometryPointer = Optional(_gtk, "gdk_monitor_get_geometry");
                GdkMonitorGetScaleFactorPointer = Optional(_gtk, "gdk_monitor_get_scale_factor");
                GdkMonitorIsPrimaryPointer = Optional(_gtk, "gdk_monitor_is_primary");
                GdkMonitorGetModelPointer = Optional(_gtk, "gdk_monitor_get_model");
                GdkMonitorGetManufacturerPointer = Optional(_gtk, "gdk_monitor_get_manufacturer");
                GdkDisplayGetMonitorAtWindowPointer = Optional(_gtk, "gdk_display_get_monitor_at_window");
                GdkDisplayGetDefaultScreenPointer = Optional(_gtk, "gdk_display_get_default_screen");
                GdkScreenGetMonitorPlugNamePointer = Optional(_gtk, "gdk_screen_get_monitor_plug_name");
                GtkWindowUnfullscreenPointer = Optional(_gtk, "gtk_window_unfullscreen");
                GtkWindowFullscreenOnMonitorPointer = Optional(_gtk, "gtk_window_fullscreen_on_monitor");
                GtkWidgetSetDirectionPointer = Optional(_gtk, "gtk_widget_set_direction");
                WebKitWebContextSetPreferredLanguagesPointer = Optional(_webkit, "webkit_web_context_set_preferred_languages");
                GdkWaylandDisplayGetTypePointer = Optional(_gtk, "gdk_wayland_display_get_type");
                GtkSelectionDataGetUrisPointer = Optional(_gtk, "gtk_selection_data_get_uris");
                GStrfreevPointer = Optional(_gtk, "g_strfreev");
                GtkDragGetDataPointer = Optional(_gtk, "gtk_drag_get_data");
                GdkAtomInternPointer = Optional(_gtk, "gdk_atom_intern");
                GdkDragContextListTargetsPointer = Optional(_gtk, "gdk_drag_context_list_targets");
                JscValueToStringPointer = Optional(_webkit, "jsc_value_to_string");
                GtkWidgetSetVisualPointer = Optional(_gtk, "gtk_widget_set_visual");
                GtkWidgetGetScreenPointer = Optional(_gtk, "gtk_widget_get_screen");
                GdkScreenGetRgbaVisualPointer = Optional(_gtk, "gdk_screen_get_rgba_visual");
                GtkWidgetSetAppPaintablePointer = Optional(_gtk, "gtk_widget_set_app_paintable");
                WebKitWebContextNewPointer = Required(_webkit, "webkit_web_context_new");
                WebKitWebViewNewWithContextPointer = Required(_webkit, "webkit_web_view_new_with_context");
                WebKitWebViewLoadUriPointer = Required(_webkit, "webkit_web_view_load_uri");
                WebKitWebViewReloadPointer = Required(_webkit, "webkit_web_view_reload");
                WebKitWebViewGetTitlePointer = Required(_webkit, "webkit_web_view_get_title");
                WebKitPermissionRequestDenyPointer = Required(_webkit, "webkit_permission_request_deny");
                WebKitPermissionRequestAllowPointer = Required(_webkit, "webkit_permission_request_allow");
                WebKitUserMediaPermissionRequestGetTypePointer = Required(_webkit, "webkit_user_media_permission_request_get_type");
                WebKitDeviceInfoPermissionRequestGetTypePointer = Required(_webkit, "webkit_device_info_permission_request_get_type");
                WebKitUserMediaPermissionIsForDisplayDevicePointer = Optional(_webkit, "webkit_user_media_permission_is_for_display_device");
                WebKitWebViewGetUriPointer = Required(_webkit, "webkit_web_view_get_uri");
                GTypeCheckInstanceIsAPointer = Required(_gObject, "g_type_check_instance_is_a");
                WebKitWebViewGetUserContentManagerPointer = Required(_webkit, "webkit_web_view_get_user_content_manager");
                WebKitUserScriptNewPointer = Required(_webkit, "webkit_user_script_new");
                WebKitUserScriptUnrefPointer = Required(_webkit, "webkit_user_script_unref");
                WebKitUserContentManagerAddScriptPointer = Required(_webkit, "webkit_user_content_manager_add_script");
                WebKitWebViewSetBackgroundColorPointer = Optional(_webkit, "webkit_web_view_set_background_color");
                WebKitWebViewSetZoomLevelPointer = Required(_webkit, "webkit_web_view_set_zoom_level");
                WebKitWebViewGetZoomLevelPointer = Required(_webkit, "webkit_web_view_get_zoom_level");
                WebKitUserScriptNewForWorldPointer = Required(_webkit, "webkit_user_script_new_for_world");
                WebKitUserContentManagerRegisterScriptMessageHandlerInWorldPointer = Required(_webkit, "webkit_user_content_manager_register_script_message_handler_in_world");
                WebKitJavascriptResultGetJsValuePointer = Required(_webkit, "webkit_javascript_result_get_js_value");
                JscValueToInt32Pointer = Required(_webkit, "jsc_value_to_int32");
                GdkKeymapGetForDisplayPointer = Required(_gtk, "gdk_keymap_get_for_display");
                GdkKeymapGetEntriesForKeycodePointer = Required(_gtk, "gdk_keymap_get_entries_for_keycode");
                GFreePointer = Required(_gtk, "g_free");
                GErrorFreePointer = Required(_gtk, "g_error_free");
                GObjectRefPointer = Required(_gObject, "g_object_ref");
                GSignalHandlerDisconnectPointer = Required(_gObject, "g_signal_handler_disconnect");
                GtkShowUriOnWindowPointer = Optional(_gtk, "gtk_show_uri_on_window");
                WebKitWebViewGetSettingsPointer = Required(_webkit, "webkit_web_view_get_settings");
                WebKitSettingsSetEnableDeveloperExtrasPointer = Required(_webkit, "webkit_settings_set_enable_developer_extras");
                WebKitWebViewGetContextPointer = Required(_webkit, "webkit_web_view_get_context");
                WebKitNavigationPolicyDecisionGetNavigationActionPointer = Required(_webkit, "webkit_navigation_policy_decision_get_navigation_action");
                WebKitNavigationActionGetRequestPointer = Required(_webkit, "webkit_navigation_action_get_request");
                WebKitNavigationActionIsUserGesturePointer = Required(_webkit, "webkit_navigation_action_is_user_gesture");
                WebKitUriRequestGetUriPointer = Required(_webkit, "webkit_uri_request_get_uri");
                WebKitPolicyDecisionIgnorePointer = Required(_webkit, "webkit_policy_decision_ignore");
                WebKitResponsePolicyDecisionGetRequestPointer = Required(_webkit, "webkit_response_policy_decision_get_request");
                WebKitResponsePolicyDecisionIsMimeTypeSupportedPointer = Required(_webkit, "webkit_response_policy_decision_is_mime_type_supported");
                // WebKitGTK 2.40.
                WebKitResponsePolicyDecisionIsMainFrameMainResourcePointer = Optional(_webkit, "webkit_response_policy_decision_is_main_frame_main_resource");
                WebKitDownloadGetWebViewPointer = Required(_webkit, "webkit_download_get_web_view");
                WebKitDownloadCancelPointer = Required(_webkit, "webkit_download_cancel");
                IsAvailable = true;
            }
            catch (EntryPointNotFoundException)
            {
                IsAvailable = false;
            }
        }

        internal bool IsAvailable { get; }

        // GTK 3.22 and later, which WebKitGTK 4.1 requires, export every function the window state needs.
        internal bool CanReportState => IsAvailable && GtkWindowGetPositionPointer != 0 && GtkWindowGetSizePointer != 0 &&
            GtkWidgetGetWindowPointer != 0 && GdkWindowGetStatePointer != 0 && GdkDisplayGetNMonitorsPointer != 0 &&
            GdkDisplayGetMonitorPointer != 0 && GdkMonitorGetWorkareaPointer != 0;

        internal bool CanReceiveFileDrops => IsAvailable && GtkSelectionDataGetUrisPointer != 0 && GStrfreevPointer != 0 &&
            GtkDragGetDataPointer != 0 && GdkAtomInternPointer != 0 && GdkDragContextListTargetsPointer != 0 && JscValueToStringPointer != 0;

        private nint GtkSelectionDataGetUrisPointer { get; }
        private nint GStrfreevPointer { get; }
        private nint GtkDragGetDataPointer { get; }
        private nint GdkAtomInternPointer { get; }
        private nint GdkDragContextListTargetsPointer { get; }
        private nint JscValueToStringPointer { get; }

        // The selection's URIs as one text/uri-list, or null when it holds none.
        internal string? GetSelectionUris(nint data)
        {
            var uris = ((delegate* unmanaged[Cdecl]<nint, nint*>)GtkSelectionDataGetUrisPointer)(data);
            if (uris == null) return null;
            try
            {
                var builder = new System.Text.StringBuilder();
                for (var entry = uris; *entry != 0; entry++)
                {
                    builder.Append(Marshal.PtrToStringUTF8(*entry)).Append('\n');
                }
                return builder.ToString();
            }
            finally
            {
                ((delegate* unmanaged[Cdecl]<nint*, void>)GStrfreevPointer)(uris);
            }
        }

        private nint UriListAtom()
        {
            using var name = Utf8String.Create("text/uri-list");
            return ((delegate* unmanaged[Cdecl]<byte*, int, nint>)GdkAtomInternPointer)(name.Pointer, 0);
        }

        // Whether the drag offers text/uri-list; the context's targets are a GList of atoms.
        internal bool DragOffersUriList(nint context)
        {
            var atom = UriListAtom();
            for (var item = ((delegate* unmanaged[Cdecl]<nint, nint>)GdkDragContextListTargetsPointer)(context); item != 0;
                item = ((nint*)item)[1])
            {
                if (((nint*)item)[0] == atom) return true;
            }
            return false;
        }

        internal void RequestUriList(nint widget, nint context, uint time) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, uint, void>)GtkDragGetDataPointer)(widget, context, UriListAtom(), time);

        internal string? GetMessageString(nint javascriptResult)
        {
            var value = ((delegate* unmanaged[Cdecl]<nint, nint>)WebKitJavascriptResultGetJsValuePointer)(javascriptResult);
            if (value == 0) return null;
            var text = ((delegate* unmanaged[Cdecl]<nint, nint>)JscValueToStringPointer)(value);
            try { return Marshal.PtrToStringUTF8(text); }
            finally { ((delegate* unmanaged[Cdecl]<nint, void>)GFreePointer)(text); }
        }

        // The drop script runs in the keyboard script's isolated world, where the page cannot reach its handler, in every
        // frame, so drops into same-origin frames carry their label; the handler checks the reported frame origin.
        internal void AddFileDropScript(nint manager) =>
            AddIsolatedScript(manager, FileDropSupport.WebKitScript, FileDropSupport.WebKitMessageHandler, allFrames: true, "file drop");

        // In every frame, in the same isolated world.
        internal void AddMediaFramesScript(nint manager) =>
            AddIsolatedScript(manager, WebKitGtkMediaFrames.Script, WebKitGtkMediaFrames.MessageHandler, allFrames: true, "media frames");

        private void AddIsolatedScript(nint manager, string text, string handlerName, bool allFrames, string purpose)
        {
            using var source = Utf8String.Create(text);
            using var handler = Utf8String.Create(handlerName);
            using var world = Utf8String.Create(WebKitGtkKeyboard.ScriptWorld);
            if (((delegate* unmanaged[Cdecl]<nint, byte*, byte*, int>)WebKitUserContentManagerRegisterScriptMessageHandlerInWorldPointer)(
                manager, handler.Pointer, world.Pointer) == 0)
            {
                throw new InvalidOperationException($"WebKitGTK could not register the {purpose} message handler.");
            }
            // WEBKIT_USER_CONTENT_INJECT_ALL_FRAMES (0) or _TOP_FRAME (1), at WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START (0).
            var script = ((delegate* unmanaged[Cdecl]<byte*, int, int, byte*, nint, nint, nint>)WebKitUserScriptNewForWorldPointer)(
                source.Pointer, allFrames ? 0 : 1, 0, world.Pointer, 0, 0);
            if (script == 0)
            {
                throw new InvalidOperationException($"WebKitGTK could not create the {purpose} script.");
            }
            try
            {
                ((delegate* unmanaged[Cdecl]<nint, nint, void>)WebKitUserContentManagerAddScriptPointer)(manager, script);
            }
            finally
            {
                ((delegate* unmanaged[Cdecl]<nint, void>)WebKitUserScriptUnrefPointer)(script);
            }
        }

        private nint GtkWindowGetPositionPointer { get; }
        private nint GtkWindowGetSizePointer { get; }
        private nint GtkWidgetGetWindowPointer { get; }
        private nint GdkWindowGetStatePointer { get; }
        private nint GdkDisplayGetNMonitorsPointer { get; }
        private nint GdkMonitorGetGeometryPointer { get; }
        private nint GdkMonitorGetScaleFactorPointer { get; }
        private nint GdkMonitorIsPrimaryPointer { get; }
        private nint GdkMonitorGetModelPointer { get; }
        private nint GdkMonitorGetManufacturerPointer { get; }
        private nint GdkDisplayGetMonitorAtWindowPointer { get; }
        private nint GdkDisplayGetDefaultScreenPointer { get; }
        private nint GdkScreenGetMonitorPlugNamePointer { get; }
        private nint GtkWindowUnfullscreenPointer { get; }
        private nint GtkWindowFullscreenOnMonitorPointer { get; }
        private nint GtkWidgetSetDirectionPointer { get; }
        private nint WebKitWebContextSetPreferredLanguagesPointer { get; }

        // GTK 3.22 monitors (gdk_display_get_monitor and the GdkMonitor getters) on the GTK thread.
        internal bool CanListMonitors => IsAvailable && GdkDisplayGetNMonitorsPointer != 0 && GdkDisplayGetMonitorPointer != 0 &&
            GdkMonitorGetWorkareaPointer != 0 && GdkMonitorGetGeometryPointer != 0 && GdkMonitorGetScaleFactorPointer != 0 &&
            GdkMonitorIsPrimaryPointer != 0 && GdkDisplayGetMonitorAtWindowPointer != 0;

        internal bool CanUseFullScreen => IsAvailable && GtkWindowUnfullscreenPointer != 0;

        internal bool CanPlaceOnMonitor => CanListMonitors && GtkWindowFullscreenOnMonitorPointer != 0 && GdkDisplayGetDefaultScreenPointer != 0;

        // The monitors of the default display, in GDK's order, with their GdkMonitor. GTK thread only.
        internal List<(nint Handle, Runic.Platform.DesktopMonitor Monitor)> ReadMonitors()
        {
            List<(nint, Runic.Platform.DesktopMonitor)> monitors = [];
            var display = ((delegate* unmanaged[Cdecl]<nint>)GdkDisplayGetDefaultPointer)();
            if (display == 0 || !CanListMonitors) return monitors;
            var screen = GdkDisplayGetDefaultScreenPointer == 0 ? 0 : ((delegate* unmanaged[Cdecl]<nint, nint>)GdkDisplayGetDefaultScreenPointer)(display);
            var count = ((delegate* unmanaged[Cdecl]<nint, int>)GdkDisplayGetNMonitorsPointer)(display);
            HashSet<string> ids = [];
            for (var index = 0; index < count; index++)
            {
                var monitor = ((delegate* unmanaged[Cdecl]<nint, int, nint>)GdkDisplayGetMonitorPointer)(display, index);
                if (monitor == 0) continue;
                GdkRectangle geometry, area;
                ((delegate* unmanaged[Cdecl]<nint, GdkRectangle*, void>)GdkMonitorGetGeometryPointer)(monitor, &geometry);
                ((delegate* unmanaged[Cdecl]<nint, GdkRectangle*, void>)GdkMonitorGetWorkareaPointer)(monitor, &area);
                var model = GdkMonitorGetModelPointer == 0 ? null
                    : Marshal.PtrToStringUTF8(((delegate* unmanaged[Cdecl]<nint, nint>)GdkMonitorGetModelPointer)(monitor));
                var manufacturer = GdkMonitorGetManufacturerPointer == 0 ? null
                    : Marshal.PtrToStringUTF8(((delegate* unmanaged[Cdecl]<nint, nint>)GdkMonitorGetManufacturerPointer)(monitor));
                // The connector, such as HDMI-1: stable while the monitor stays on it.
                string? connector = null;
                if (screen != 0 && GdkScreenGetMonitorPlugNamePointer != 0)
                {
                    var name = ((delegate* unmanaged[Cdecl]<nint, int, nint>)GdkScreenGetMonitorPlugNamePointer)(screen, index);
                    if (name != 0)
                    {
                        connector = Marshal.PtrToStringUTF8(name);
                        if (GFreePointer != 0) ((delegate* unmanaged[Cdecl]<nint, void>)GFreePointer)(name);
                    }
                }
                var id = !string.IsNullOrEmpty(connector) ? connector : $"monitor-{index}";
                if (!ids.Add(id)) id = $"{id}-{index}";
                var displayName = string.Join(' ', new[] { manufacturer, model }.Where(static part => !string.IsNullOrWhiteSpace(part)));
                monitors.Add((monitor, new Runic.Platform.DesktopMonitor(id, displayName.Length > 0 ? displayName : id)
                {
                    Bounds = new(geometry.X, geometry.Y, geometry.Width, geometry.Height),
                    WorkArea = new(area.X, area.Y, area.Width, area.Height),
                    Scale = Math.Max(1, ((delegate* unmanaged[Cdecl]<nint, int>)GdkMonitorGetScaleFactorPointer)(monitor)),
                    IsPrimary = ((delegate* unmanaged[Cdecl]<nint, int>)GdkMonitorIsPrimaryPointer)(monitor) != 0,
                }));
            }
            // The primary monitor first, the others in GDK's order.
            var primary = monitors.FindIndex(static monitor => monitor.Item2.IsPrimary);
            if (primary > 0)
            {
                var first = monitors[primary];
                monitors.RemoveAt(primary);
                monitors.Insert(0, first);
            }
            return monitors;
        }

        // The GdkMonitor that holds most of the window, or zero. GTK thread only.
        internal nint MonitorOfWindow(nint window)
        {
            if (!CanListMonitors) return 0;
            var gdkWindow = ((delegate* unmanaged[Cdecl]<nint, nint>)GtkWidgetGetWindowPointer)(window);
            var display = ((delegate* unmanaged[Cdecl]<nint>)GdkDisplayGetDefaultPointer)();
            return gdkWindow == 0 || display == 0 ? 0
                : ((delegate* unmanaged[Cdecl]<nint, nint, nint>)GdkDisplayGetMonitorAtWindowPointer)(display, gdkWindow);
        }

        // The index of a GdkMonitor in the default display, which gtk_window_fullscreen_on_monitor takes; -1 if absent.
        internal int MonitorIndex(nint monitor)
        {
            var display = ((delegate* unmanaged[Cdecl]<nint>)GdkDisplayGetDefaultPointer)();
            if (display == 0 || monitor == 0) return -1;
            var count = ((delegate* unmanaged[Cdecl]<nint, int>)GdkDisplayGetNMonitorsPointer)(display);
            for (var index = 0; index < count; index++)
            {
                if (((delegate* unmanaged[Cdecl]<nint, int, nint>)GdkDisplayGetMonitorPointer)(display, index) == monitor) return index;
            }
            return -1;
        }

        internal void GtkWindowUnfullscreen(nint window) => ((delegate* unmanaged[Cdecl]<nint, void>)GtkWindowUnfullscreenPointer)(window);

        internal void GtkWindowFullscreenOnMonitor(nint window, int index)
        {
            var display = ((delegate* unmanaged[Cdecl]<nint>)GdkDisplayGetDefaultPointer)();
            var screen = display == 0 ? 0 : ((delegate* unmanaged[Cdecl]<nint, nint>)GdkDisplayGetDefaultScreenPointer)(display);
            if (screen == 0) GtkWindowFullscreen(window);
            else ((delegate* unmanaged[Cdecl]<nint, nint, int, void>)GtkWindowFullscreenOnMonitorPointer)(window, screen, index);
        }

        // GtkTextDirection: 1 left to right, 2 right to left. A container passes it to children without their own.
        internal void GtkWidgetSetDirection(nint widget, int direction)
        {
            if (GtkWidgetSetDirectionPointer != 0) ((delegate* unmanaged[Cdecl]<nint, int, void>)GtkWidgetSetDirectionPointer)(widget, direction);
        }

        // Connects a static handler without user data; the connection lasts as long as the instance.
        internal ulong ConnectStatic(nint instance, string signal, nint callback)
        {
            using var value = Utf8String.Create(signal);
            return ((delegate* unmanaged[Cdecl]<nint, byte*, nint, nint, nint, int, ulong>)GSignalConnectDataPointer)(
                instance, value.Pointer, callback, 0, 0, 0);
        }

        internal nint DefaultDisplay() => ((delegate* unmanaged[Cdecl]<nint>)GdkDisplayGetDefaultPointer)();

        internal nint DefaultScreen()
        {
            var display = DefaultDisplay();
            return display == 0 || GdkDisplayGetDefaultScreenPointer == 0 ? 0
                : ((delegate* unmanaged[Cdecl]<nint, nint>)GdkDisplayGetDefaultScreenPointer)(display);
        }
        private nint GdkDisplayGetMonitorPointer { get; }
        private nint GdkMonitorGetWorkareaPointer { get; }
        private nint GdkWaylandDisplayGetTypePointer { get; }

        internal (int X, int Y) GtkWindowGetPosition(nint window)
        {
            int x = 0, y = 0;
            ((delegate* unmanaged[Cdecl]<nint, int*, int*, void>)GtkWindowGetPositionPointer)(window, &x, &y);
            return (x, y);
        }

        internal (int Width, int Height) GtkWindowGetSize(nint window)
        {
            int width = 0, height = 0;
            ((delegate* unmanaged[Cdecl]<nint, int*, int*, void>)GtkWindowGetSizePointer)(window, &width, &height);
            return (width, height);
        }

        internal int GetWindowStateFlags(nint window)
        {
            var gdkWindow = ((delegate* unmanaged[Cdecl]<nint, nint>)GtkWidgetGetWindowPointer)(window);
            return gdkWindow == 0 ? 0 : ((delegate* unmanaged[Cdecl]<nint, int>)GdkWindowGetStatePointer)(gdkWindow);
        }

        internal bool IsWaylandDisplay()
        {
            if (GdkWaylandDisplayGetTypePointer == 0)
            {
                return false;
            }
            var display = ((delegate* unmanaged[Cdecl]<nint>)GdkDisplayGetDefaultPointer)();
            return display != 0 && IsInstanceOf(display, GdkWaylandDisplayGetTypePointer);
        }

        // The work area of every monitor, in GDK's application pixels: the monitor without panels and docks.
        internal List<ScreenRect> GetWorkAreas()
        {
            List<ScreenRect> areas = [];
            var display = ((delegate* unmanaged[Cdecl]<nint>)GdkDisplayGetDefaultPointer)();
            if (display == 0)
            {
                return areas;
            }
            var count = ((delegate* unmanaged[Cdecl]<nint, int>)GdkDisplayGetNMonitorsPointer)(display);
            for (var index = 0; index < count; index++)
            {
                var monitor = ((delegate* unmanaged[Cdecl]<nint, int, nint>)GdkDisplayGetMonitorPointer)(display, index);
                if (monitor == 0)
                {
                    continue;
                }
                GdkRectangle area;
                ((delegate* unmanaged[Cdecl]<nint, GdkRectangle*, void>)GdkMonitorGetWorkareaPointer)(monitor, &area);
                areas.Add(new ScreenRect(area.X, area.Y, area.Width, area.Height));
            }
            return areas;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GdkRectangle
        {
            internal int X;
            internal int Y;
            internal int Width;
            internal int Height;
        }

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
        private nint GtkContainerForAllPointer { get; }
        private nint GtkWindowPresentPointer { get; }
        private nint GtkWindowIconifyPointer { get; }
        private nint GtkWindowMaximizePointer { get; }
        private nint GtkWindowUnmaximizePointer { get; }
        private nint GtkWindowIsMaximizedPointer { get; }
        private nint GtkWindowFullscreenPointer { get; }
        private nint GtkWidgetGrabFocusPointer { get; }
        private nint GtkWindowGetFocusPointer { get; }
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
        private nint GtkWidgetGetAccessiblePointer { get; }
        private nint GSignalLookupPointer { get; }
        private nint GSignalEmitvPointer { get; }
        private nint GValueInitPointer { get; }
        private nint GValueUnsetPointer { get; }
        private nint GValueSetObjectPointer { get; }
        private nint GValueSetStringPointer { get; }
        private nint GValueSetIntPointer { get; }
        private nint GObjectUnrefPointer { get; }
        private nint GtkWindowSetIconFromFilePointer { get; }
        private nint GtkWidgetSetVisualPointer { get; }
        private nint GtkWidgetGetScreenPointer { get; }
        private nint GdkScreenGetRgbaVisualPointer { get; }
        private nint GtkWidgetSetAppPaintablePointer { get; }
        private nint WebKitWebContextNewPointer { get; }
        private nint WebKitWebViewNewWithContextPointer { get; }
        private nint WebKitWebViewLoadUriPointer { get; }
        private nint WebKitWebViewReloadPointer { get; }
        private nint WebKitWebViewGetTitlePointer { get; }
        private nint WebKitPermissionRequestDenyPointer { get; }
        private nint WebKitPermissionRequestAllowPointer { get; }
        private nint WebKitUserMediaPermissionRequestGetTypePointer { get; }
        private nint WebKitDeviceInfoPermissionRequestGetTypePointer { get; }
        private nint WebKitUserMediaPermissionIsForDisplayDevicePointer { get; }
        private nint WebKitWebViewGetUriPointer { get; }
        private nint GTypeCheckInstanceIsAPointer { get; }
        private nint WebKitWebViewGetUserContentManagerPointer { get; }
        private nint WebKitUserScriptNewPointer { get; }
        private nint WebKitUserScriptUnrefPointer { get; }
        private nint WebKitUserContentManagerAddScriptPointer { get; }
        private nint WebKitWebViewSetBackgroundColorPointer { get; }
        private nint WebKitWebViewSetZoomLevelPointer { get; }
        private nint WebKitWebViewGetZoomLevelPointer { get; }
        private nint WebKitUserScriptNewForWorldPointer { get; }
        private nint WebKitUserContentManagerRegisterScriptMessageHandlerInWorldPointer { get; }
        private nint WebKitJavascriptResultGetJsValuePointer { get; }
        private nint JscValueToInt32Pointer { get; }
        private nint GdkKeymapGetForDisplayPointer { get; }
        private nint GdkKeymapGetEntriesForKeycodePointer { get; }
        private nint GFreePointer { get; }
        private nint GErrorFreePointer { get; }
        private nint GObjectRefPointer { get; }
        private nint GSignalHandlerDisconnectPointer { get; }
        private nint GtkShowUriOnWindowPointer { get; }
        private nint WebKitWebViewGetSettingsPointer { get; }
        private nint WebKitSettingsSetEnableDeveloperExtrasPointer { get; }
        private nint WebKitWebViewGetContextPointer { get; }
        private nint WebKitNavigationPolicyDecisionGetNavigationActionPointer { get; }
        private nint WebKitNavigationActionGetRequestPointer { get; }
        private nint WebKitNavigationActionIsUserGesturePointer { get; }
        private nint WebKitUriRequestGetUriPointer { get; }
        private nint WebKitPolicyDecisionIgnorePointer { get; }
        private nint WebKitResponsePolicyDecisionGetRequestPointer { get; }
        private nint WebKitResponsePolicyDecisionIsMimeTypeSupportedPointer { get; }
        private nint WebKitResponsePolicyDecisionIsMainFrameMainResourcePointer { get; }
        private nint WebKitDownloadGetWebViewPointer { get; }
        private nint WebKitDownloadCancelPointer { get; }

        // Whether a response decision tells the main frame's document apart; see WebKitGtkWebContent.
        internal bool CanIdentifyMainFrame => WebKitResponsePolicyDecisionIsMainFrameMainResourcePointer != 0;

        internal void SetDeveloperExtras(nint webView, bool enabled)
        {
            var settings = ((delegate* unmanaged[Cdecl]<nint, nint>)WebKitWebViewGetSettingsPointer)(webView);
            if (settings == 0) throw new InvalidOperationException("WebKitGTK did not provide the view's settings.");
            ((delegate* unmanaged[Cdecl]<nint, int, void>)WebKitSettingsSetEnableDeveloperExtrasPointer)(settings, enabled ? 1 : 0);
        }

        internal nint GetNavigationAction(nint decision) =>
            ((delegate* unmanaged[Cdecl]<nint, nint>)WebKitNavigationPolicyDecisionGetNavigationActionPointer)(decision);

        // The navigation's URL and whether a user gesture (a click or key press) started it.
        internal (string? Url, bool UserGesture) GetNavigation(nint navigationAction)
        {
            if (navigationAction == 0) return (null, false);
            var request = ((delegate* unmanaged[Cdecl]<nint, nint>)WebKitNavigationActionGetRequestPointer)(navigationAction);
            var userGesture = ((delegate* unmanaged[Cdecl]<nint, int>)WebKitNavigationActionIsUserGesturePointer)(navigationAction) != 0;
            return (RequestUri(request), userGesture);
        }

        internal void IgnorePolicy(nint decision) => ((delegate* unmanaged[Cdecl]<nint, void>)WebKitPolicyDecisionIgnorePointer)(decision);

        internal bool IsMainFrameMainResource(nint decision) => CanIdentifyMainFrame &&
            ((delegate* unmanaged[Cdecl]<nint, int>)WebKitResponsePolicyDecisionIsMainFrameMainResourcePointer)(decision) != 0;

        internal bool IsMimeTypeSupported(nint decision) =>
            ((delegate* unmanaged[Cdecl]<nint, int>)WebKitResponsePolicyDecisionIsMimeTypeSupportedPointer)(decision) != 0;

        internal string? GetResponseUri(nint decision) =>
            RequestUri(((delegate* unmanaged[Cdecl]<nint, nint>)WebKitResponsePolicyDecisionGetRequestPointer)(decision));

        private string? RequestUri(nint request) => request == 0
            ? null
            : Marshal.PtrToStringUTF8(((delegate* unmanaged[Cdecl]<nint, nint>)WebKitUriRequestGetUriPointer)(request));

        // Connects download-started on the view's web context and keeps a reference to the context for the disconnection.
        internal (nint Context, ulong Handler) ConnectDownloadStarted(nint webView, nint callback, LinuxWebKitGtkHost host)
        {
            var context = ((delegate* unmanaged[Cdecl]<nint, nint>)WebKitWebViewGetContextPointer)(webView);
            if (context == 0) throw new InvalidOperationException("WebKitGTK did not provide the view's web context.");
            ((delegate* unmanaged[Cdecl]<nint, nint>)GObjectRefPointer)(context);
            try
            {
                return (context, Connect(context, "download-started", callback, host));
            }
            catch
            {
                ((delegate* unmanaged[Cdecl]<nint, void>)GObjectUnrefPointer)(context);
                throw;
            }
        }

        internal void DisconnectAndRelease(nint instance, ulong handler)
        {
            ((delegate* unmanaged[Cdecl]<nint, ulong, void>)GSignalHandlerDisconnectPointer)(instance, handler);
            ((delegate* unmanaged[Cdecl]<nint, void>)GObjectUnrefPointer)(instance);
        }

        internal nint GetDownloadWebView(nint download) =>
            ((delegate* unmanaged[Cdecl]<nint, nint>)WebKitDownloadGetWebViewPointer)(download);

        internal void CancelDownload(nint download) => ((delegate* unmanaged[Cdecl]<nint, void>)WebKitDownloadCancelPointer)(download);

        // gtk_show_uri_on_window (GTK 3.22) with GDK_CURRENT_TIME; inside Flatpak GTK asks the OpenURI portal. A failure
        // is not reported: the page cannot act on it.
        internal void ShowUri(nint window, string uri)
        {
            if (GtkShowUriOnWindowPointer == 0) return;
            using var value = Utf8String.Create(uri);
            nint error = 0;
            if (((delegate* unmanaged[Cdecl]<nint, byte*, uint, nint*, int>)GtkShowUriOnWindowPointer)(window, value.Pointer, 0, &error) == 0
                && error != 0)
            {
                ((delegate* unmanaged[Cdecl]<nint, void>)GErrorFreePointer)(error);
            }
        }

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
        internal void DestroyWebViewChildren(nint webView)
        {
            if (webView != 0)
            {
                ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)GtkContainerForAllPointer)(
                    webView, (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&DestroyChild, GtkWidgetDestroyPointer);
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void DestroyChild(nint widget, nint destroy)
            => ((delegate* unmanaged[Cdecl]<nint, void>)destroy)(widget);

        internal void GtkWidgetDestroy(nint window) => ((delegate* unmanaged[Cdecl]<nint, void>)GtkWidgetDestroyPointer)(window);
        internal void GtkWindowPresent(nint window) => ((delegate* unmanaged[Cdecl]<nint, void>)GtkWindowPresentPointer)(window);
        internal void GtkWindowIconify(nint window) => ((delegate* unmanaged[Cdecl]<nint, void>)GtkWindowIconifyPointer)(window);
        internal void GtkWindowMaximize(nint window) => ((delegate* unmanaged[Cdecl]<nint, void>)GtkWindowMaximizePointer)(window);
        internal void GtkWindowUnmaximize(nint window) => ((delegate* unmanaged[Cdecl]<nint, void>)GtkWindowUnmaximizePointer)(window);
        internal bool GtkWindowIsMaximized(nint window) => ((delegate* unmanaged[Cdecl]<nint, int>)GtkWindowIsMaximizedPointer)(window) != 0;
        internal void GtkWindowFullscreen(nint window) => ((delegate* unmanaged[Cdecl]<nint, void>)GtkWindowFullscreenPointer)(window);
        internal void GtkWidgetGrabFocus(nint widget) => ((delegate* unmanaged[Cdecl]<nint, int>)GtkWidgetGrabFocusPointer)(widget);
        internal nint GtkWindowGetFocus(nint window) => ((delegate* unmanaged[Cdecl]<nint, nint>)GtkWindowGetFocusPointer)(window);

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

        // Emits AtkObject::notification (ATK 2.50, with AtkLive politeness) on the widget's accessible object, or
        // AtkObject::announcement (ATK 2.46, always polite) on an older ATK. g_signal_emitv avoids a variadic call.
        internal void Announce(nint widget, string message, bool assertive)
        {
            var accessible = ((delegate* unmanaged[Cdecl]<nint, nint>)GtkWidgetGetAccessiblePointer)(widget);
            if (accessible == 0)
            {
                throw new InvalidOperationException("The window has no accessible object.");
            }
            // G_TYPE_FROM_INSTANCE: the instance's class starts with its GType.
            var type = **(nuint**)accessible;
            var signal = Lookup("notification", type, out var withPoliteness);
            var values = stackalloc GValue[3];
            new Span<GValue>(values, 3).Clear();
            var count = withPoliteness ? 3 : 2;
            ((delegate* unmanaged[Cdecl]<GValue*, nuint, GValue*>)GValueInitPointer)(&values[0], type);
            ((delegate* unmanaged[Cdecl]<GValue*, nint, void>)GValueSetObjectPointer)(&values[0], accessible);
            ((delegate* unmanaged[Cdecl]<GValue*, nuint, GValue*>)GValueInitPointer)(&values[1], GTypeString);
            using (var text = Utf8String.Create(message))
            {
                ((delegate* unmanaged[Cdecl]<GValue*, byte*, void>)GValueSetStringPointer)(&values[1], text.Pointer);
            }
            if (withPoliteness)
            {
                ((delegate* unmanaged[Cdecl]<GValue*, nuint, GValue*>)GValueInitPointer)(&values[2], GTypeInt);
                ((delegate* unmanaged[Cdecl]<GValue*, int, void>)GValueSetIntPointer)(&values[2], assertive ? AtkLiveAssertive : AtkLivePolite);
            }
            try
            {
                ((delegate* unmanaged[Cdecl]<GValue*, uint, uint, GValue*, void>)GSignalEmitvPointer)(values, signal, 0, null);
            }
            finally
            {
                for (var index = 0; index < count; index++)
                {
                    ((delegate* unmanaged[Cdecl]<GValue*, void>)GValueUnsetPointer)(&values[index]);
                }
            }
        }

        private uint Lookup(string name, nuint type, out bool withPoliteness)
        {
            using (var notification = Utf8String.Create(name))
            {
                var signal = ((delegate* unmanaged[Cdecl]<byte*, nuint, uint>)GSignalLookupPointer)(notification.Pointer, type);
                if (signal != 0)
                {
                    withPoliteness = true;
                    return signal;
                }
            }
            withPoliteness = false;
            using var announcement = Utf8String.Create("announcement");
            var fallback = ((delegate* unmanaged[Cdecl]<byte*, nuint, uint>)GSignalLookupPointer)(announcement.Pointer, type);
            return fallback != 0
                ? fallback
                : throw new NotSupportedException("ATK 2.46 or later is required for announcements.");
        }

        // G_TYPE_MAKE_FUNDAMENTAL(6) and (16), and AtkLive's values.
        private const nuint GTypeInt = 6 << 2;
        private const nuint GTypeString = 16 << 2;
        private const int AtkLivePolite = 1;
        private const int AtkLiveAssertive = 2;

        // GType g_type; union { gint64 v_int64; gpointer v_pointer; ... } data[2].
        [StructLayout(LayoutKind.Sequential)]
        private struct GValue
        {
            internal nuint Type;
            internal long Data0;
            internal long Data1;
        }

        internal ulong Connect(nint instance, string signal, nint callback, LinuxWebKitGtkHost host, bool after = false)
        {
            using var value = Utf8String.Create(signal);
            var handle = GCHandle.Alloc(host);
            var handler = ((delegate* unmanaged[Cdecl]<nint, byte*, nint, nint, nint, int, ulong>)GSignalConnectDataPointer)(
                instance,
                value.Pointer,
                callback,
                GCHandle.ToIntPtr(handle),
                SignalContextDestroyCallback,
                after ? 1 : 0);
            if (handler == 0)
            {
                handle.Free();
                throw new InvalidOperationException($"GTK rejected the '{signal}' signal handler.");
            }
            return handler;
        }

        internal nint WebKitWebViewNew(string? language = null)
        {
            var context = ((delegate* unmanaged[Cdecl]<nint>)WebKitWebContextNewPointer)();
            if (context == 0)
            {
                return 0;
            }
            // DesktopWindowOptions.Language: the page's navigator.language and Accept-Language.
            if (language is not null && WebKitWebContextSetPreferredLanguagesPointer != 0)
            {
                using var tag = Utf8String.Create(language);
                var languages = stackalloc nint[2];
                languages[0] = (nint)tag.Pointer;
                languages[1] = 0;
                ((delegate* unmanaged[Cdecl]<nint, nint*, void>)WebKitWebContextSetPreferredLanguagesPointer)(context, languages);
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

        internal void Reload(nint webView) => ((delegate* unmanaged[Cdecl]<nint, void>)WebKitWebViewReloadPointer)(webView);

        internal string? WebKitWebViewGetTitle(nint webView)
        {
            var value = ((delegate* unmanaged[Cdecl]<nint, nint>)WebKitWebViewGetTitlePointer)(webView);
            return value == 0 ? null : Marshal.PtrToStringUTF8(value);
        }

        // All frames, at document start, without URL patterns: the script compares
        // complete origins, including the listener port, itself.
        internal void AddDocumentStartScript(nint webView, string source)
        {
            var manager = ((delegate* unmanaged[Cdecl]<nint, nint>)WebKitWebViewGetUserContentManagerPointer)(webView);
            if (manager == 0)
            {
                throw new InvalidOperationException("WebKitGTK did not provide a user content manager.");
            }

            using var value = Utf8String.Create(source);
            var script = ((delegate* unmanaged[Cdecl]<byte*, int, int, nint, nint, nint>)WebKitUserScriptNewPointer)(
                value.Pointer, 0, 0, 0, 0);
            if (script == 0)
            {
                throw new InvalidOperationException("WebKitGTK could not create the document-start script.");
            }

            try
            {
                ((delegate* unmanaged[Cdecl]<nint, nint, void>)WebKitUserContentManagerAddScriptPointer)(manager, script);
            }
            finally
            {
                ((delegate* unmanaged[Cdecl]<nint, void>)WebKitUserScriptUnrefPointer)(script);
            }
        }

        // Top frame, document start, in its own script world, with the message handler
        // its wheel zoom posts to. Returns the user content manager.
        internal nint AddKeyboardScript(nint webView)
        {
            var manager = ((delegate* unmanaged[Cdecl]<nint, nint>)WebKitWebViewGetUserContentManagerPointer)(webView);
            if (manager == 0)
            {
                throw new InvalidOperationException("WebKitGTK did not provide a user content manager.");
            }

            using var source = Utf8String.Create(WebKitGtkKeyboard.Script);
            using var world = Utf8String.Create(WebKitGtkKeyboard.ScriptWorld);
            using var handler = Utf8String.Create(WebKitGtkKeyboard.ZoomMessageHandler);
            if (((delegate* unmanaged[Cdecl]<nint, byte*, byte*, int>)WebKitUserContentManagerRegisterScriptMessageHandlerInWorldPointer)(
                manager, handler.Pointer, world.Pointer) == 0)
            {
                throw new InvalidOperationException("WebKitGTK could not register the zoom message handler.");
            }
            var script = ((delegate* unmanaged[Cdecl]<byte*, int, int, byte*, nint, nint, nint>)WebKitUserScriptNewForWorldPointer)(
                source.Pointer, 1, 0, world.Pointer, 0, 0);
            if (script == 0)
            {
                throw new InvalidOperationException("WebKitGTK could not create the keyboard script.");
            }

            try
            {
                ((delegate* unmanaged[Cdecl]<nint, nint, void>)WebKitUserContentManagerAddScriptPointer)(manager, script);
            }
            finally
            {
                ((delegate* unmanaged[Cdecl]<nint, void>)WebKitUserScriptUnrefPointer)(script);
            }
            return manager;
        }

        internal int GetMessageInt32(nint javascriptResult)
        {
            var value = ((delegate* unmanaged[Cdecl]<nint, nint>)WebKitJavascriptResultGetJsValuePointer)(javascriptResult);
            return value == 0 ? 0 : ((delegate* unmanaged[Cdecl]<nint, int>)JscValueToInt32Pointer)(value);
        }

        internal double GetZoomLevel(nint webView) =>
            ((delegate* unmanaged[Cdecl]<nint, double>)WebKitWebViewGetZoomLevelPointer)(webView);

        internal void SetZoomLevel(nint webView, double level) =>
            ((delegate* unmanaged[Cdecl]<nint, double, void>)WebKitWebViewSetZoomLevelPointer)(webView, level);

        // The produced symbol first; when it is no zoom key, the unshifted and shifted
        // symbols of the same physical key in the event's layout group.
        internal (uint Keyval, uint State, ushort Keycode) ReadKey(nint keyEvent)
        {
            var key = (GdkEventKey*)keyEvent;
            return (key->Keyval, key->State, key->HardwareKeycode);
        }

        // The keyvals of a physical key on the first two levels of every layout group, so a shortcut declared with a
        // Latin letter also matches while a non-Latin layout is active.
        internal int KeycodeKeyvals(ushort keycode, Span<uint> keyvals)
        {
            var keymap = ((delegate* unmanaged[Cdecl]<nint, nint>)GdkKeymapGetForDisplayPointer)(
                ((delegate* unmanaged[Cdecl]<nint>)GdkDisplayGetDefaultPointer)());
            GdkKeymapKey* entries = null;
            uint* values = null;
            var count = 0;
            if (keymap == 0 || ((delegate* unmanaged[Cdecl]<nint, uint, GdkKeymapKey**, uint**, int*, int>)GdkKeymapGetEntriesForKeycodePointer)(
                keymap, keycode, &entries, &values, &count) == 0)
            {
                return 0;
            }
            try
            {
                var found = 0;
                for (var index = 0; index < count && found < keyvals.Length; index++)
                {
                    if (entries[index].Level is 0 or 1) keyvals[found++] = values[index];
                }
                return found;
            }
            finally
            {
                ((delegate* unmanaged[Cdecl]<void*, void>)GFreePointer)(entries);
                ((delegate* unmanaged[Cdecl]<void*, void>)GFreePointer)(values);
            }
        }

        internal PageZoomCommand GetZoomCommand(nint keyEvent)
        {
            var key = (GdkEventKey*)keyEvent;
            var command = WebKitGtkKeyboard.GetZoomCommand(key->Keyval, key->State);
            if (command != PageZoomCommand.None || !WebKitGtkKeyboard.IsZoomModifier(key->State))
            {
                return command;
            }

            var keymap = ((delegate* unmanaged[Cdecl]<nint, nint>)GdkKeymapGetForDisplayPointer)(
                ((delegate* unmanaged[Cdecl]<nint>)GdkDisplayGetDefaultPointer)());
            GdkKeymapKey* entries = null;
            uint* keyvals = null;
            var count = 0;
            if (keymap == 0 || ((delegate* unmanaged[Cdecl]<nint, uint, GdkKeymapKey**, uint**, int*, int>)GdkKeymapGetEntriesForKeycodePointer)(
                keymap, key->HardwareKeycode, &entries, &keyvals, &count) == 0)
            {
                return WebKitGtkKeyboard.GetZoomCommand(key->Keyval, key->State, default, key->HardwareKeycode);
            }
            try
            {
                Span<uint> levels = stackalloc uint[2];
                var found = 0;
                for (var index = 0; index < count && found < levels.Length; index++)
                {
                    if (entries[index].Group == key->Group && entries[index].Level is 0 or 1)
                    {
                        levels[found++] = keyvals[index];
                    }
                }
                return WebKitGtkKeyboard.GetZoomCommand(key->Keyval, key->State, levels[..found], key->HardwareKeycode);
            }
            finally
            {
                ((delegate* unmanaged[Cdecl]<void*, void>)GFreePointer)(entries);
                ((delegate* unmanaged[Cdecl]<void*, void>)GFreePointer)(keyvals);
            }
        }

        internal void DenyPermission(nint request) =>
            ((delegate* unmanaged[Cdecl]<nint, void>)WebKitPermissionRequestDenyPointer)(request);

        internal void AllowPermission(nint request) =>
            ((delegate* unmanaged[Cdecl]<nint, void>)WebKitPermissionRequestAllowPointer)(request);

        // Device enumeration accompanies camera and microphone access; display capture does not.
        internal bool IsCameraOrMicrophoneRequest(nint request) =>
            IsInstanceOf(request, WebKitDeviceInfoPermissionRequestGetTypePointer) ||
            (IsInstanceOf(request, WebKitUserMediaPermissionRequestGetTypePointer) &&
                (WebKitUserMediaPermissionIsForDisplayDevicePointer == 0 ||
                    ((delegate* unmanaged[Cdecl]<nint, int>)WebKitUserMediaPermissionIsForDisplayDevicePointer)(request) == 0));

        private bool IsInstanceOf(nint instance, nint getType) =>
            ((delegate* unmanaged[Cdecl]<nint, nuint, int>)GTypeCheckInstanceIsAPointer)(
                instance, ((delegate* unmanaged[Cdecl]<nuint>)getType)()) != 0;

        internal string? WebKitWebViewGetUri(nint webView)
        {
            var value = ((delegate* unmanaged[Cdecl]<nint, nint>)WebKitWebViewGetUriPointer)(webView);
            return value == 0 ? null : Marshal.PtrToStringUTF8(value);
        }

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
        private struct GdkEventKey
        {
            internal int Type;
            internal nint Window;
            internal byte SendEvent;
            internal uint Time;
            internal uint State;
            internal uint Keyval;
            internal int Length;
            internal nint String;
            internal ushort HardwareKeycode;
            internal byte Group;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GdkKeymapKey
        {
            internal uint Keycode;
            internal int Group;
            internal int Level;
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
