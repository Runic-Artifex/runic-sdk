using Runic.Desktop.Internal;

namespace Runic.Desktop.Gtk4;

// DesktopWindow.SetBoundsAsync and SetMinimumSizeAsync, and the exact, visible and snapped parts of DesktopWindowState
// (#864, B1). Logical pixels: the X11 position of the visible frame and the content size, as the state reports them.
internal sealed partial class Gtk4WindowHost : IDesktopWindowBoundsHost
{
    private const int GdkToplevelStateTiled = 1 << 7;
    // GDK_TOPLEVEL_STATE_TOP_TILED, RIGHT_TILED, BOTTOM_TILED and LEFT_TILED.
    private const int GdkToplevelStateTiledEdges = (1 << 8) | (1 << 10) | (1 << 12) | (1 << 14);

    // How long a frame request waits for the window manager before it is sent once more (RequestFrame).
    private static readonly TimeSpan FrameRequestWait = TimeSpan.FromMilliseconds(500);

    // GTK thread only: the X11 frame the window last asked the window manager for, until the window has it (RequestFrame).
    private FrameRequest? _frameRequest;

    private sealed record FrameRequest(int X, int Y, int Width, int Height)
    {
        internal bool Retried { get; set; }
    }

    // X11: one XMoveResizeWindow, so the window manager receives one configure request with both (RequestFrame).
    // Wayland: the default size alone, as ResizeAsync.
    public ValueTask SetBoundsAsync(DesktopWindowBounds bounds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        return new(Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            var window = GetWindow();
            var surface = SurfaceOf(window);
            (_settlingFrame, _frameRequest) = (null, null);
            ReleaseRestoreBounds();
            var state = surface == 0 ? 0 : ToplevelGetState(surface);
            if (window.IsMaximized() || window.IsFullscreen() || (state & GdkToplevelStateMinimized) != 0)
            {
                throw WindowBoundsSupport.NotRestored();
            }
            if (bounds is { X: { } x, Y: { } y })
            {
                if (!Gtk4X11Placement.IsActive) throw WindowBoundsSupport.PositionUnavailable();
                if (!window.IsVisible())
                {
                    // An unmapped window is placed when it is shown.
                    window.SetDefaultSize(bounds.Width, bounds.Height);
                    _pendingPosition = (x, y);
                    return;
                }
                if (!RequestFrame(window, x, y, bounds.Width, bounds.Height))
                {
                    throw new NotSupportedException("The GTK 4 window has no X11 surface.");
                }
            }
            else
            {
                window.SetDefaultSize(bounds.Width, bounds.Height);
            }
            QueueStateUpdate();
        }, cancellationToken));
    }

    // GTK thread only. Gives a visible X11 window new bounds: one XMoveResizeWindow, and the default size only once the
    // window manager has given the window that size (SettleFrameRequest). GTK 4 takes the window manager's size as its
    // default size. A default size set first makes GDK resize the window itself at its next layout, while the window
    // still has its old size, with an XMoveResizeWindow at the position the window has at that moment
    // (gdk_x11_surface_toplevel_resize). When GDK sends that before the window manager has applied Runic's request, the
    // window manager applies GDK's request after Runic's: the window goes back to its old position, offset by the
    // window manager's decorations, which GDK's root position includes (#926). A window that does not have the frame
    // after FrameRequestWait gets the request once more. False without an X11 surface.
    private bool RequestFrame(Gtk.Window window, int x, int y, int width, int height)
    {
        if (!window.IsVisible())
        {
            // No window manager configures an unmapped window: the X server applies the request as it is.
            window.SetDefaultSize(width, height);
            return Gtk4X11Placement.MoveResize(SurfaceOf(window), x, y, width, height);
        }
        if (!Gtk4X11Placement.MoveResize(SurfaceOf(window), x, y, width, height)) return false;
        var request = new FrameRequest(x, y, width, height);
        _frameRequest = request;
        CheckFrameRequestLater(request);
        QueueStateUpdate();
        return true;
    }

    // GTK thread only, from UpdateState: the window manager gave the requested size, so GTK's default size takes it
    // without a resize of GDK's own; the request ends once the window is at its position too.
    private void SettleFrameRequest(Gtk.Window window, bool maximizedOrFullScreen, (int X, int Y)? position, int width, int height)
    {
        if (_frameRequest is not { } request || maximizedOrFullScreen || width != request.Width || height != request.Height) return;
        window.GetDefaultSize(out var defaultWidth, out var defaultHeight);
        if (defaultWidth != width || defaultHeight != height) window.SetDefaultSize(width, height);
        if (position == (request.X, request.Y)) _frameRequest = null;
    }

    private void CheckFrameRequestLater(FrameRequest request) =>
        _ = Task.Delay(FrameRequestWait).ContinueWith(_ => Gtk4Dispatcher.Instance.Post(() =>
        {
            try
            {
                if (!ReferenceEquals(_frameRequest, request) || _window is not { } window || !IsOpen) return;
                UpdateState();
                if (!ReferenceEquals(_frameRequest, request)) return;
                if (window.IsMaximized() || window.IsFullscreen() || !window.IsVisible() || IsMinimized(window) || request.Retried)
                {
                    // The window manager kept another frame: GTK keeps the size it has.
                    _frameRequest = null;
                    return;
                }
                request.Retried = true;
                Gtk4X11Placement.MoveResize(SurfaceOf(window), request.X, request.Y, request.Width, request.Height);
                CheckFrameRequestLater(request);
                QueueStateUpdate();
            }
            catch
            {
                // Never unwind through GTK.
            }
        }), TaskScheduler.Default);

    // The size request of the overlay that holds the web view, as DesktopWindowOptions.MinimumWidth and MinimumHeight set
    // it: GTK grows a smaller window to it and keeps the user from making it smaller. An open side panel adds its width.
    public ValueTask SetMinimumSizeAsync(int width, int height, CancellationToken cancellationToken = default) =>
        new(Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            GetWindow();
            (_minimumWidth, _minimumHeight) = (width, height);
            ApplyMinimumSize();
        }, cancellationToken));

    // GTK thread only, for UpdateState: the bounds, the visible frame (X11 only), and whether the desktop tiled the window:
    // GDK's tiled state, or on X11 a visible frame that fills a half or a quarter of its monitor's work area.
    private static (DesktopWindowBounds Bounds, DesktopWindowBounds? Visible, bool Snapped) ReadExactBounds(
        Gtk.Window window, (int X, int Y)? position, int width, int height, bool maximized, bool fullScreen)
    {
        var bounds = new DesktopWindowBounds { X = position?.X, Y = position?.Y, Width = width, Height = height, Scale = 1 };
        var surface = SurfaceOf(window);
        DesktopWindowBounds? visible = null;
        if (position is not null && Gtk4X11Placement.GetVisibleFrame(surface, width, height) is { } frame)
        {
            visible = new DesktopWindowBounds { X = frame.X, Y = frame.Y, Width = frame.Width, Height = frame.Height, Scale = 1 };
        }
        var state = surface == 0 ? 0 : ToplevelGetState(surface);
        var tiled = (state & (GdkToplevelStateTiled | GdkToplevelStateTiledEdges)) != 0;
        if (!tiled && visible is { X: { } x, Y: { } y })
        {
            var rectangle = new ScreenRect(x, y, visible.Width, visible.Height);
            tiled = WindowBoundsSupport.WorkAreaOf(rectangle, Gtk4X11Placement.GetWorkAreas()) is { } area
                && WindowBoundsSupport.LooksTiled(rectangle, area, tolerance: 0, margin: 0);
        }
        return (bounds, visible, tiled && !maximized && !fullScreen);
    }
}
