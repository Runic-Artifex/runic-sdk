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
    // GTK thread only: the bounds the window restores to (WindowRestoreBounds), which UpdateState reports and gives the
    // window again when the window manager returns it from maximized elsewhere.
    private readonly WindowRestoreBounds _restore = new();
    private readonly System.Diagnostics.Stopwatch _restoreClock = System.Diagnostics.Stopwatch.StartNew();

    // GTK thread only: the restore bounds now.
    // The tracker holds the window's frame, an open side panel included; _normal and the placement exclude the panel.
    private RestoreFrame? CurrentRestore() =>
        _restore.Current ?? (_normal is { } normal ? WithPanel(new RestoreFrame(normal.X, normal.Y, normal.Width, normal.Height)) : null);

    // GTK thread only: the window's frame for a page frame, with an open side panel on its edge (side-panel/DESIGN.md
    // §3.1: a placement is the page's, and the window grows by the panel).
    private RestoreFrame WithPanel(RestoreFrame page)
    {
        if (_panel is not { IsShown: true } panel || !_layout.IsShown) return page;
        var bounds = SidePanelPlanner.IncludePanel(
            new DesktopWindowBounds { X = page.X, Y = page.Y, Width = page.Width, Height = page.Height, Scale = 1 }, panel.IsLeft, _layout.Width);
        return new RestoreFrame(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    // GTK thread only, from ShrinkRestoredBounds: a side panel closed while the window was maximized or full-screen; the
    // window restores to the page's bounds, which the tracker holds and gives it once it is restored.
    private void HoldRestoredPage(DesktopWindowPlacement page) =>
        _restore.Set(new RestoreFrame(page.X, page.Y, page.Width, page.Height), _restoreClock.Elapsed, maximized: true);

    // GTK thread only: SetBoundsAsync, MoveAsync and ResizeAsync of a normal window, whose frame is the restore bounds.
    private void ReleaseRestoreBounds() => _restore.Reset(_restore.Current);
    // Which wait the one-second fallback belongs to, so an earlier wait's fallback cannot end a later one.
    private int _maximizeAtSizeWait;

    public ValueTask ShowMaximizedAsync(CancellationToken cancellationToken = default)
        => InvokeWindowAsync(() =>
        {
            if (_options?.Kiosk == true) return;
            var flags = Api.GetWindowStateFlags(_window);
            if ((flags & GdkWindowStateFullscreen) != 0 && Api.CanUseFullScreen) Api.GtkWindowUnfullscreen(_window);
            // The bounds the window maximizes from are the ones it restores to (WindowRestoreBounds).
            if ((flags & GdkWindowStateMaximized) == 0 && CurrentRestore() is { } bounds) _restore.Set(bounds, _restoreClock.Elapsed);
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
            // The window manager restores the frame it saved; the window is given the restore bounds (UpdateState).
            var flags = Api.GetWindowStateFlags(_window);
            if (CurrentRestore() is { } bounds)
            {
                _restore.Set(bounds, _restoreClock.Elapsed, (flags & (GdkWindowStateMaximized | GdkWindowStateIconified)) != 0);
            }
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
            // The page's placement: the window adds an open side panel on its edge before the fit.
            var frame = WithPanel(new RestoreFrame(x, y, width, height));
            (x, y, width, height) = (frame.X, frame.Y, frame.Width, frame.Height);
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
            // The placement's bounds replace any earlier restore bounds (WindowRestoreBounds).
            _restore.Set(new RestoreFrame(x, y, width, height), _restoreClock.Elapsed);
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
                if (placement.Maximized)
                {
                    // A maximized window whose restored size changes maximizes once the window manager has the size.
                    var sized = !maximized && (currentWidth, currentHeight) == (width, height);
                    if (sized) Api.GtkWindowMaximize(_window);
                    else MaximizeOnceSized(width, height);
                }
            }
            if (placement.FullScreen && !fullScreen && Api.CanUseFullScreen) EnterFullScreen();
            // The page's placement after the fit, without an open side panel.
            _normal = ExcludePanel(new DesktopWindowPlacement { X = x, Y = y, Width = width, Height = height, Origin = DesktopCoordinateOrigin.TopLeft });
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
        MaximizeOnceSized(sized.Width, sized.Height);
    }

    // GTK thread only. gtk_window_move reaches the window manager at once, but GTK sends a resize only at its next
    // frame, after this turn: a maximize sent now could reach the window manager first, which then keeps the new
    // position with the old size as the bounds it restores to, and drops the resize of the maximized window. The window
    // maximizes once it reports the size (UpdateState), or after a second where it never does.
    private void MaximizeOnceSized(int width, int height)
    {
        _maximizeAtSize = (width, height);
        var wait = ++_maximizeAtSizeWait;
        _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ => Dispatcher.Post(() =>
        {
            try
            {
                if (wait == _maximizeAtSizeWait) MaximizeAtSize(force: true);
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

    // GTK thread only, from UpdateState: the restore bounds after a report of the window, which a window back from
    // maximized at other bounds is given again. frame is the normal frame of a shown window, null otherwise.
    private RestoreFrame? ObserveRestoreBounds(bool maximized, RestoreFrame? frame)
    {
        var observation = _restore.Observe(maximized, frame, _restoreClock.Elapsed);
        if (observation.Apply is { } apply)
        {
            if (apply is { X: { } x, Y: { } y }) Api.GtkWindowMove(_window, x, y);
            Api.GtkWindowResize(_window, apply.Width, apply.Height);
        }
        return observation.Restore;
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
