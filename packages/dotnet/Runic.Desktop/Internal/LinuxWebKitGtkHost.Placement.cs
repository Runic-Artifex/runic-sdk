namespace Runic.Desktop.Internal;

// DesktopWindow.MaximizeAsync, RestoreAsync and SetPlacementAsync (sdk#897, W1a; window-api-0.19/DESIGN.md §3.1). A
// placement of a shown window leaves full screen and maximized state, moves and resizes, and maximizes or enters full
// screen again in one main-loop turn: the window manager restores the window, takes the new geometry as the bounds it
// restores to and maximizes it, in that order.
//
// A hidden window waits until it is shown (SetVisibleAsync): GTK keeps the size of a window that was mapped before when
// it is mapped again, maps a window hidden while maximized maximized again although it was unmaximized meanwhile, and a
// window maximized before it is mapped restores to the geometry it had when it was hidden.
internal sealed partial class LinuxWebKitGtkHost
{
    private static readonly Lazy<nint> GtkWindowDeiconify = new(static () => Gtk3Export("gtk_window_deiconify"));

    // GTK thread only: what a hidden window takes once it is shown, and the size a shown window waits for before it
    // maximizes.
    private (int? X, int? Y, int Width, int Height)? _geometryWhenShown;
    private bool? _maximizedWhenShown;
    private (int Width, int Height)? _maximizeAtSize;

    public ValueTask ShowMaximizedAsync(CancellationToken cancellationToken = default)
        => InvokeWindowAsync(() =>
        {
            if (_options?.Kiosk == true) return;
            var flags = Api.GetWindowStateFlags(_window);
            if ((flags & GdkWindowStateFullscreen) != 0 && Api.CanUseFullScreen) Api.GtkWindowUnfullscreen(_window);
            if (!Api.GtkWidgetGetVisible(_window))
            {
                _maximizedWhenShown = true;
                ReportPlacement(maximized: true, fullScreen: false);
                return;
            }
            if ((flags & GdkWindowStateIconified) != 0) Deiconify();
            Api.GtkWindowMaximize(_window);
        }, cancellationToken);

    // On X11 a minimized window comes back normal (D064.13). Wayland tells GTK nothing of a minimized window, and
    // gtk_window_deiconify does nothing there.
    public ValueTask ShowNormalAsync(CancellationToken cancellationToken = default)
        => InvokeWindowAsync(() =>
        {
            if (_options?.Kiosk == true) return;
            _maximizeAtSize = null;
            var flags = Api.GetWindowStateFlags(_window);
            if ((flags & GdkWindowStateFullscreen) != 0 && Api.CanUseFullScreen) Api.GtkWindowUnfullscreen(_window);
            if (!Api.GtkWidgetGetVisible(_window))
            {
                _maximizedWhenShown = false;
                ReportPlacement(maximized: false, fullScreen: false);
                return;
            }
            Api.GtkWindowUnmaximize(_window);
            if ((flags & GdkWindowStateIconified) != 0) Deiconify();
        }, cancellationToken);

    public ValueTask SetPlacementAsync(DesktopWindowPlacement placement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return InvokeWindowAsync(() =>
        {
            // A kiosk window stays full-screen; DesktopWindow refuses any other placement for it.
            if (_options?.Kiosk == true) return;
            var isWayland = Api.IsWaylandDisplay();
            // D064.11: a position Wayland cannot set fails the whole placement, before anything changes.
            if (placement.X is not null && isWayland) throw WindowBoundsSupport.PositionUnavailable();
            var flags = Api.GetWindowStateFlags(_window);
            var fullScreen = (flags & GdkWindowStateFullscreen) != 0;
            var maximized = (flags & GdkWindowStateMaximized) != 0 || Api.GtkWindowIsMaximized(_window);
            var basis = _normal ?? Volatile.Read(ref _state)?.Placement;
            var (currentWidth, currentHeight) = Api.GtkWindowGetSize(_window);
            var width = placement.Width > 0 ? placement.Width : basis?.Width ?? currentWidth;
            var height = placement.Height > 0 ? placement.Height : basis?.Height ?? currentHeight;
            int? x = isWayland ? null : placement.X ?? basis?.X;
            int? y = isWayland ? null : placement.Y ?? basis?.Y;
            // Kept on a monitor that exists now, as at open.
            var workAreas = Api.GetWorkAreas();
            if (x is { } placedX && y is { } placedY)
            {
                var fitted = WindowPlacementClamp.Clamp(new ScreenRect(placedX, placedY, width, height), workAreas);
                (x, y, width, height) = (fitted.X, fitted.Y, fitted.Width, fitted.Height);
            }
            else
            {
                (width, height) = WindowPlacementClamp.ClampSize(width, height, WindowPlacementClamp.Largest(workAreas));
            }
            _maximizeAtSize = null;
            if (fullScreen && !placement.FullScreen && Api.CanUseFullScreen) Api.GtkWindowUnfullscreen(_window);
            if (!Api.GtkWidgetGetVisible(_window))
            {
                (_geometryWhenShown, _maximizedWhenShown) = ((x, y, width, height), placement.Maximized);
            }
            else
            {
                if (maximized) Api.GtkWindowUnmaximize(_window);
                if (x is { } newX && y is { } newY) Api.GtkWindowMove(_window, newX, newY);
                Api.GtkWindowResize(_window, width, height);
                if (placement.Maximized) Api.GtkWindowMaximize(_window);
            }
            if (placement.FullScreen && !fullScreen && Api.CanUseFullScreen) EnterFullScreen();
            _normal = new DesktopWindowPlacement { X = x, Y = y, Width = width, Height = height, Origin = DesktopCoordinateOrigin.TopLeft };
            ReportPlacement(placement.Maximized, placement.FullScreen && Api.CanUseFullScreen);
        }, cancellationToken);
    }

    // GTK thread only: whether the window reports a maximized placement while it waits to be shown or to maximize.
    private bool? PendingMaximized => _maximizeAtSize is not null ? true : _maximizedWhenShown;

    // GTK thread only, after the window was mapped (SetVisibleAsync): unmaximized, then moved and resized, then maximized,
    // in that order.
    private void ApplyPlacementWhenShown()
    {
        var (geometry, maximize) = (_geometryWhenShown, _maximizedWhenShown);
        (_geometryWhenShown, _maximizedWhenShown) = (null, null);
        if (maximize is null && geometry is null) return;
        if (maximize is false || (geometry is not null && Api.GtkWindowIsMaximized(_window))) Api.GtkWindowUnmaximize(_window);
        if (geometry is { } pending)
        {
            if (pending is { X: { } x, Y: { } y }) Api.GtkWindowMove(_window, x, y);
            Api.GtkWindowResize(_window, pending.Width, pending.Height);
        }
        if (maximize is not true) return;
        if (geometry is not { } sized)
        {
            Api.GtkWindowMaximize(_window);
            return;
        }
        // GTK sends the new size at its next frame, after this turn: the window maximizes once it reports the size
        // (UpdateState), so the window manager restores it to that size, or after a second where it never does.
        _maximizeAtSize = (sized.Width, sized.Height);
        _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ => Dispatcher.Post(() =>
        {
            try
            {
                MaximizeAtSize(force: true);
            }
            catch
            {
                // Never unwind through GTK.
            }
        }), TaskScheduler.Default);
    }

    // GTK thread only: from UpdateState with the reported size, or when the wait for it ended.
    private void MaximizeAtSize(bool force, int width = 0, int height = 0)
    {
        if (_maximizeAtSize is not { } size || !IsOpen || _window == 0) return;
        if (!force && (width, height) != size) return;
        _maximizeAtSize = null;
        Api.GtkWindowMaximize(_window);
    }

    // GTK thread only.
    private void Deiconify()
    {
        if (GtkWindowDeiconify.Value is var export and not 0)
        {
            unsafe { ((delegate* unmanaged[Cdecl]<nint, void>)export)(_window); }
        }
    }

    // GTK thread only: the placement as asked, before the window manager's configure and state events report it (and
    // UpdateState reads the window again). A minimized or hidden window keeps its state otherwise.
    private void ReportPlacement(bool maximized, bool fullScreen)
    {
        if (Volatile.Read(ref _state) is not { } state) return;
        var updated = state with { Placement = (_normal ?? state.Placement) with { Maximized = maximized, FullScreen = fullScreen } };
        if (Equals(updated, state)) return;
        Volatile.Write(ref _state, updated);
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Never unwind through GTK.
        }
    }
}
