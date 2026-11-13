using Runic.Desktop.Internal;

namespace Runic.Desktop.Gtk4;

// DesktopWindow.MaximizeAsync, RestoreAsync and SetPlacementAsync (sdk#897, W1a; window-api-0.19/DESIGN.md §3.1). GTK 4
// keeps the unmaximized size in the default size, which a placement sets also while the window is maximized; on X11
// Runic sets the position through Xlib on GDK's own display connection, so an unmaximize, the move and a maximize reach
// the window manager in that order. GTK 4 has no call to un-minimize a window: presenting it brings it back.
internal sealed partial class Gtk4WindowHost : IDesktopWindowShowStateHost
{
    // GTK thread only: the size a hidden window given a normal placement takes once it is shown. GTK maps a window that
    // was mapped before at its last size, not its default size, and a window manager may map it maximized again, so the
    // window unmaximizes and takes the size once it is shown and reports that it is not maximized (UpdateState).
    private (int Width, int Height)? _unmaximizedSizeWhenShown;

    public ValueTask MaximizeAsync(CancellationToken cancellationToken = default) =>
        new(Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            var window = GetWindow();
            if (_options?.Kiosk == true) return;
            if (window.IsFullscreen()) window.Unfullscreen();
            if (IsMinimized(window) && window.IsVisible()) Present(window);
            // A hidden window is placed before it is mapped, so it restores to where it was (Present).
            if (!window.IsVisible() && _pendingPosition is null && _normalPosition is { } position && Gtk4X11Placement.IsActive)
            {
                (_pendingPosition, _pendingPositionIsRestoredBounds) = (position, true);
            }
            window.Maximize();
            QueueStateUpdate();
        }, cancellationToken));

    // D064.13: always to the unmaximized size, also from minimized (on X11; Wayland reports no minimized state).
    public ValueTask RestoreAsync(CancellationToken cancellationToken = default) =>
        new(Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            var window = GetWindow();
            if (_options?.Kiosk == true) return;
            if (window.IsFullscreen()) window.Unfullscreen();
            if (!window.IsVisible())
            {
                window.GetDefaultSize(out var width, out var height);
                _unmaximizedSizeWhenShown = (width, height);
            }
            window.Unmaximize();
            if (IsMinimized(window) && window.IsVisible()) Present(window);
            QueueStateUpdate();
        }, cancellationToken));

    public ValueTask SetPlacementAsync(DesktopWindowPlacement placement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return new(Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            var window = GetWindow();
            // A kiosk window stays full-screen; DesktopWindow refuses any other placement for it.
            if (_options?.Kiosk == true) return;
            var x11 = Gtk4X11Placement.IsActive;
            // D064.11: a position Wayland cannot set fails the whole placement, before anything changes.
            if (placement.X is not null && !x11) throw WindowBoundsSupport.PositionUnavailable();
            window.GetDefaultSize(out var defaultWidth, out var defaultHeight);
            var width = placement.Width > 0 ? placement.Width : defaultWidth > 0 ? defaultWidth : window.GetWidth();
            var height = placement.Height > 0 ? placement.Height : defaultHeight > 0 ? defaultHeight : window.GetHeight();
            (int X, int Y)? position = !x11 ? null : placement is { X: { } placedX, Y: { } placedY } ? (placedX, placedY) : _normalPosition;
            // Kept on a monitor that exists now, as at open.
            if (position is { } requested)
            {
                var fitted = WindowPlacementClamp.Clamp(new ScreenRect(requested.X, requested.Y, width, height), Gtk4X11Placement.GetWorkAreas());
                (position, width, height) = ((fitted.X, fitted.Y), fitted.Width, fitted.Height);
            }
            else
            {
                (width, height) = FitToLargestMonitor(width, height);
            }
            var fullScreen = window.IsFullscreen();
            var maximized = window.IsMaximized();
            _unmaximizedSizeWhenShown = !window.IsVisible() && !placement.Maximized ? (width, height) : null;
            if (fullScreen && !placement.FullScreen) window.Unfullscreen();
            window.SetDefaultSize(width, height);
            if (position is { } target)
            {
                if (!window.IsVisible())
                {
                    // Placed before it is mapped (Present); a maximized or full-screen one restores to it.
                    _pendingPosition = target;
                    _pendingPositionIsRestoredBounds = placement.Maximized || placement.FullScreen;
                }
                else
                {
                    // The window manager restores a maximized window before it takes the new frame.
                    if (maximized) window.Unmaximize();
                    if (!Gtk4X11Placement.MoveResize(SurfaceOf(window), target.X, target.Y, width, height))
                    {
                        throw new NotSupportedException("The GTK 4 window has no X11 surface.");
                    }
                }
                _normalPosition = target;
            }
            if (placement.Maximized) window.Maximize();
            else if (maximized) window.Unmaximize();
            if (placement.FullScreen && !fullScreen) EnterFullScreen(window);
            QueueStateUpdate();
        }, cancellationToken));
    }

    // GTK thread only, after Present: see _unmaximizedSizeWhenShown. GTK unmaximizes at its next frame.
    private void UnmaximizeWhenShown(Gtk.Window window)
    {
        if (_unmaximizedSizeWhenShown is null) return;
        window.Unmaximize();
        QueueStateUpdate();
    }

    // GTK thread only, from UpdateState.
    private void ApplyUnmaximizedSize(Gtk.Window window, bool maximized)
    {
        if (_unmaximizedSizeWhenShown is not { } size || maximized || !window.IsVisible()) return;
        _unmaximizedSizeWhenShown = null;
        window.SetDefaultSize(size.Width, size.Height);
        if (_normalPosition is { } position && Gtk4X11Placement.IsActive)
        {
            Gtk4X11Placement.MoveResize(SurfaceOf(window), position.X, position.Y, size.Width, size.Height);
        }
        QueueStateUpdate();
    }

    // GTK thread only.
    private static bool IsMinimized(Gtk.Window window) =>
        SurfaceOf(window) is var surface and not 0 && (ToplevelGetState(surface) & GdkToplevelStateMinimized) != 0;
}
