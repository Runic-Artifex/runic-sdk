using Runic.Desktop.Internal;

namespace Runic.Desktop.Gtk4;

// DesktopWindow.MaximizeAsync, RestoreAsync and SetPlacementAsync (sdk#897, W1a; window-api-0.19/DESIGN.md §3.1). GTK 4
// keeps the unmaximized size in the default size; on X11 Runic sets the position through Xlib. GTK 4 has no call to
// un-minimize a window: presenting it brings it back.
//
// A placement that changes a maximized window's restored bounds, or maximizes a window at new bounds, waits for the
// window at each step (a frame target): GTK sends an unmaximize or maximize at its next frame, while XMoveResizeWindow
// goes at once, so issued together they reach the window manager in an order that depends on timing, and the window
// manager keeps whatever frame the window had when it maximized as the bounds it restores to. The window unmaximizes,
// takes the frame once it reports that it is unmaximized, and maximizes once it reports the frame (UpdateState).
internal sealed partial class Gtk4WindowHost : IDesktopWindowShowStateHost
{
    // GTK thread only: the frame target the window is waiting for, and one a hidden window starts when it is shown.
    private FrameTarget? _frameTarget;
    private FrameTarget? _frameTargetWhenShown;
    // GTK thread only: a finished frame target checked once more (SettleFrameLater); any later request clears it.
    private FrameTarget? _settlingFrame;
    // GTK thread only: the bounds the window restores to (WindowRestoreBounds), which UpdateState reports and gives the
    // window again when it comes back from maximized elsewhere.
    private readonly WindowRestoreBounds _restore = new();
    private readonly System.Diagnostics.Stopwatch _restoreClock = System.Diagnostics.Stopwatch.StartNew();

    // GTK thread only: the restore bounds now, from the tracker or else from the default size and the last position.
    private RestoreFrame CurrentRestore(Gtk.Window window)
    {
        if (_restore.Current is { } current) return current;
        window.GetDefaultSize(out var width, out var height);
        return new RestoreFrame(_normalPosition?.X, _normalPosition?.Y, width > 0 ? width : window.GetWidth(), height > 0 ? height : window.GetHeight());
    }

    // GTK thread only: the window's frame for a page frame, with an open side panel on its edge.
    private RestoreFrame WithPanel(RestoreFrame page)
    {
        if (_panel is not { IsShown: true } panel || !_layout.IsShown) return page;
        var bounds = SidePanelPlanner.IncludePanel(
            new DesktopWindowBounds { X = page.X, Y = page.Y, Width = page.Width, Height = page.Height, Scale = 1 }, panel.IsLeft, _layout.Width);
        return new RestoreFrame(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    // GTK thread only: SetBoundsAsync, MoveAsync and ResizeAsync of a normal window, whose frame is the restore bounds.
    private void ReleaseRestoreBounds() => _restore.Reset(_restore.Current);

    private sealed class FrameTarget((int X, int Y)? position, int width, int height, bool maximize)
    {
        internal (int X, int Y)? Position { get; } = position;
        internal int Width { get; } = width;
        internal int Height { get; } = height;
        internal bool Maximize { get; } = maximize;
        // Whether the frame was given to the unmaximized window, so the window waits for it to report the frame.
        internal bool Applied { get; set; }
        // How often the frame was given again after the window manager moved the window elsewhere.
        internal int Retries { get; set; }
        // When the frame was last given (Environment.TickCount64), so a burst of reports does not use up the retries.
        internal long LastApplied { get; set; }
    }

    public ValueTask MaximizeAsync(CancellationToken cancellationToken = default) =>
        new(Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            var window = GetWindow();
            if (_options?.Kiosk == true) return;
            EndFrameTargetEarly(window);
            // The bounds the window maximizes from are the ones it restores to (WindowRestoreBounds).
            if (!window.IsMaximized() && !window.IsFullscreen()) _restore.Set(CurrentRestore(window), _restoreClock.Elapsed);
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
            EndFrameTargetEarly(window);
            if (window.IsFullscreen()) window.Unfullscreen();
            var bounds = CurrentRestore(window);
            _restore.Set(bounds, _restoreClock.Elapsed, window.IsMaximized() || IsMinimized(window));
            var restored = new FrameTarget(bounds.X is { } x && bounds.Y is { } y && Gtk4X11Placement.IsActive ? (x, y) : null,
                bounds.Width, bounds.Height, maximize: false);
            if (!window.IsVisible())
            {
                // GTK maps a window that was mapped before at its last size: the restored frame applies once it is shown.
                _frameTargetWhenShown = restored;
                window.Unmaximize();
            }
            else if (window.IsMaximized())
            {
                // The window manager restores the frame it saved when the window maximized, which may not be the bounds a
                // placement or the open gave it: the window takes them once it is unmaximized.
                BeginFrameTarget(window, restored, unmaximize: true);
            }
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
            // The page's placement, as State.Placement reports it, which excludes an open side panel; the window adds the
            // panel on its edge before the fit (side-panel/DESIGN.md §3.1).
            var page = Volatile.Read(ref _state)?.Placement;
            window.GetDefaultSize(out var defaultWidth, out var defaultHeight);
            var width = placement.Width > 0 ? placement.Width : page is { Width: > 0 } kept ? kept.Width : defaultWidth > 0 ? defaultWidth : window.GetWidth();
            var height = placement.Height > 0 ? placement.Height : page is { Height: > 0 } keptHeight ? keptHeight.Height : defaultHeight > 0 ? defaultHeight : window.GetHeight();
            (int X, int Y)? position = !x11 ? null
                : placement is { X: { } placedX, Y: { } placedY } ? (placedX, placedY)
                : page is { X: { } pageX, Y: { } pageY } ? (pageX, pageY) : _normalPosition;
            var frame = WithPanel(new RestoreFrame(position?.X, position?.Y, width, height));
            (position, width, height) = (frame is { X: { } frameX, Y: { } frameY } ? (frameX, frameY) : null, frame.Width, frame.Height);
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
            (_frameTarget, _frameTargetWhenShown, _settlingFrame, _frameRequest) = (null, null, null, null);
            // The placement's bounds replace any earlier restore bounds (WindowRestoreBounds).
            _restore.Set(new RestoreFrame(position?.X, position?.Y, width, height), _restoreClock.Elapsed);
            var fullScreen = window.IsFullscreen();
            var maximized = window.IsMaximized();
            if (fullScreen && !placement.FullScreen) window.Unfullscreen();
            window.SetDefaultSize(width, height);
            if (!window.IsVisible())
            {
                if (position is { } pending)
                {
                    // Placed before it is mapped (Present); a maximized or full-screen one restores to it.
                    _pendingPosition = pending;
                    _pendingPositionIsRestoredBounds = placement.Maximized || placement.FullScreen;
                    _normalPosition = pending;
                }
                if (placement.Maximized) window.Maximize();
                else
                {
                    window.Unmaximize();
                    _frameTargetWhenShown = new FrameTarget(position, width, height, maximize: false);
                }
            }
            else if (!maximized && !placement.Maximized && position is null)
            {
                // Wayland: the default size alone.
            }
            else
            {
                // Also a normal window that stays normal: a window manager can still move a window it placed late, and the
                // frame target gives the frame again then.
                BeginFrameTarget(window, new FrameTarget(position, width, height, placement.Maximized), unmaximize: maximized);
            }
            if (placement.FullScreen && !fullScreen) EnterFullScreen(window);
            QueueStateUpdate();
        }, cancellationToken));
    }

    // GTK thread only, after Present: a hidden window given a normal placement, or restored, takes its frame now.
    // GTK maps it at its last size and a window manager may map it maximized again, so it unmaximizes first.
    private void StartFrameTargetWhenShown(Gtk.Window window)
    {
        if (_frameTargetWhenShown is not { } target) return;
        _frameTargetWhenShown = null;
        BeginFrameTarget(window, target, unmaximize: true);
    }

    // GTK thread only. A check every 1.5 seconds drives a step the window did not report, as when a busy window manager
    // or GTK let an unmaximize or the frame go missing, and gives up after six.
    private void BeginFrameTarget(Gtk.Window window, FrameTarget target, bool unmaximize)
    {
        _frameTarget = target;
        // The target's bounds are the restore bounds from now on, also when a later request ends the wait early.
        if (target.Position is { } position) _normalPosition = position;
        if (unmaximize) window.Unmaximize();
        else ApplyFrame(window, target);
        CheckFrameTargetLater(target, checks: 6);
        QueueStateUpdate();
    }

    private void CheckFrameTargetLater(FrameTarget target, int checks) =>
        _ = Task.Delay(TimeSpan.FromSeconds(1.5)).ContinueWith(_ => Gtk4Dispatcher.Instance.Post(() =>
        {
            try
            {
                if (!ReferenceEquals(_frameTarget, target) || _window is not { } window || !IsOpen) return;
                if (checks <= 1)
                {
                    if (!target.Applied) ApplyFrame(window, target);
                    FinishFrameTarget(window, target);
                    return;
                }
                if (window.IsMaximized()) window.Unmaximize();
                else ApplyFrame(window, target);
                CheckFrameTargetLater(target, checks - 1);
            }
            catch
            {
                // Never unwind through GTK.
            }
        }), TaskScheduler.Default);

    // GTK thread only: a later request ends a pending frame target. Its bounds stay the restore bounds: GTK may have
    // taken the window manager's interim frame as the default size meanwhile.
    private void EndFrameTargetEarly(Gtk.Window window)
    {
        if (_frameTarget is { } target)
        {
            window.SetDefaultSize(target.Width, target.Height);
            if (target.Position is { } position) _normalPosition = position;
        }
        (_frameTarget, _frameTargetWhenShown, _settlingFrame, _frameRequest) = (null, null, null, null);
    }

    // GTK thread only, from UpdateState: the next step once the window reports the last one.
    private void ContinueFrameTarget(Gtk.Window window, bool maximized, (int X, int Y)? position, int width, int height)
    {
        if (_frameTarget is not { } target || maximized || !window.IsVisible()) return;
        if (!target.Applied)
        {
            ApplyFrame(window, target);
            return;
        }
        if (width != target.Width || height != target.Height) return;
        if (target.Position is { } wanted && position != wanted)
        {
            // A window manager that places a newly mapped window itself can move it after the frame was given.
            if (position is not null && Environment.TickCount64 - target.LastApplied >= 500 && target.Retries++ < 5) ApplyFrame(window, target);
            return;
        }
        FinishFrameTarget(window, target);
    }

    private void ApplyFrame(Gtk.Window window, FrameTarget target)
    {
        target.Applied = true;
        target.LastApplied = Environment.TickCount64;
        if (target.Position is not { } position || !RequestFrame(window, position.X, position.Y, target.Width, target.Height))
            window.SetDefaultSize(target.Width, target.Height);
        QueueStateUpdate();
    }

    private void FinishFrameTarget(Gtk.Window window, FrameTarget target)
    {
        _frameTarget = null;
        if (target.Position is { } position) _normalPosition = position;
        if (target.Maximize) window.Maximize();
        else
        {
            _settlingFrame = target;
            SettleFrameLater(target);
        }
        QueueStateUpdate();
    }

    // A window manager that restored the window late can still move it once after it reported the frame: a second
    // later, a window that is still normal and no other request has placed takes the frame again.
    private void SettleFrameLater(FrameTarget target) =>
        _ = Task.Delay(TimeSpan.FromSeconds(1.5)).ContinueWith(_ => Gtk4Dispatcher.Instance.Post(() =>
        {
            try
            {
                if (!ReferenceEquals(_settlingFrame, target) || _window is not { } window || !IsOpen) return;
                _settlingFrame = null;
                if (window.IsMaximized() || window.IsFullscreen() || !window.IsVisible()) return;
                var position = Gtk4X11Placement.GetPosition(SurfaceOf(window));
                if (window.GetWidth() == target.Width && window.GetHeight() == target.Height
                    && (target.Position is null || position == target.Position)) return;
                ApplyFrame(window, target);
            }
            catch
            {
                // Never unwind through GTK.
            }
        }), TaskScheduler.Default);

    // GTK thread only, from UpdateState: the restore bounds after a report of the window, which a window back from
    // maximized at other bounds is given again.
    private RestoreFrame? ObserveRestoreBounds(Gtk.Window window, bool maximized, bool fullScreen, (int X, int Y)? position, int width, int height)
    {
        var shown = window.IsVisible() && !IsMinimized(window);
        RestoreFrame? frame = !shown || maximized || fullScreen || _frameTarget is not null || width <= 0 || height <= 0
            ? null
            : position is { } at ? new RestoreFrame(at.X, at.Y, width, height)
            : Gtk4X11Placement.IsActive ? null : new RestoreFrame(null, null, width, height);
        var observation = _restore.Observe(maximized || fullScreen, frame, _restoreClock.Elapsed);
        if (observation.Apply is { } apply)
        {
            if (apply is not { X: { } x, Y: { } y } || !RequestFrame(window, x, y, apply.Width, apply.Height))
                window.SetDefaultSize(apply.Width, apply.Height);
            QueueStateUpdate();
        }
        if (observation.Restore is { X: { } restoreX, Y: { } restoreY }) _normalPosition = (restoreX, restoreY);
        return observation.Restore;
    }

    // GTK thread only, for UpdateState: the placement a window waiting for its frame target reports, the target's.
    private (int? X, int? Y, int Width, int Height, bool Maximized)? PendingPlacement =>
        (_frameTarget ?? _frameTargetWhenShown) is { } target
            ? (target.Position?.X ?? _normalPosition?.X, target.Position?.Y ?? _normalPosition?.Y, target.Width, target.Height, target.Maximize)
            : null;

    // GTK thread only.
    private static bool IsMinimized(Gtk.Window window) =>
        SurfaceOf(window) is var surface and not 0 && (ToplevelGetState(surface) & GdkToplevelStateMinimized) != 0;
}
