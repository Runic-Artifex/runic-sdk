namespace Runic.Desktop.Internal;

// DesktopWindow.MaximizeAsync, RestoreAsync and SetPlacementAsync (sdk#897, W1a; window-api-0.19/DESIGN.md §3.1). AppKit
// keeps no restored frame apart from the frame: a zoomed window is one whose frame is its screen's standard frame, and
// zoom: remembers the frame it replaced as the user state it returns to. A placement therefore sets the frame with one
// setFrame:display: and then zooms for a maximized placement, so a later restore returns to the frame it set. zoom: needs
// a window on a screen: a hidden or miniaturized window zooms or unzooms when it is next shown, and a full-screen one
// once it has left full screen (windowDidExitFullScreen:).
internal sealed partial class MacOsWkWebViewHost
{
    // Main thread only: the zoom state a hidden or miniaturized window takes when it is next shown.
    private bool _zoomWhenShown;
    private bool _unzoomWhenShown;
    // Main thread only: what a full-screen window takes once it has left full screen, and whether it leaves once the
    // running transition into full screen has ended.
    private bool? _zoomAfterFullScreen;
    private DesktopWindowPlacement? _placementAfterFullScreen;
    private bool _leaveFullScreenAfterTransition;
    // Main thread only: the frame a window that MaximizeAsync zoomed, or a placement left zoomed, restores to. zoom:
    // returns to a frame AppKit saved, which a frame set while the window is zoomed does not replace and a later zoom:
    // does not always either (osx-arm64 CI), so the window takes this frame once it is unzoomed (QueueRestoreFrame), by
    // RestoreAsync or by the user.
    private CGRect? _restoreFrame;
    // Whether the window was seen zoomed since, so a resize during the zoom: animation does not count as unzoomed, and
    // how often the frame was given, as an unzoom animation may still end at AppKit's frame afterwards.
    private bool _restoreFrameArmed;
    private int _restoreFrameTries;

    // Main thread only.
    private void SetRestoreFrame(CGRect? frame, bool zoomedNow)
    {
        (_restoreFrame, _restoreFrameArmed, _restoreFrameTries) = (frame, frame is not null && zoomedNow, 0);
    }

    public ValueTask ShowMaximizedAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            if (_kiosk) return;
            if (IsFullScreen || _fullScreenTransition)
            {
                _zoomAfterFullScreen = true;
                LeaveFullScreen();
                ReportPlacement();
                return;
            }
            var miniaturized = Api.SendBool(_window, "isMiniaturized");
            // The frame the window zooms from is the one it restores to: AppKit may return to a frame it saved earlier.
            if (!Api.SendBool(_window, "isZoomed")) SetRestoreFrame(Api.SendRect(_window, "frame"), zoomedNow: false);
            if (miniaturized || !Api.SendBool(_window, "isVisible"))
            {
                (_zoomWhenShown, _unzoomWhenShown) = (true, false);
                // A miniaturized window comes back and zooms once it is back (windowDidDeminiaturize:).
                if (miniaturized) Api.SendVoidNint(_window, "deminiaturize:", 0);
                ReportPlacement();
                return;
            }
            if (!Api.SendBool(_window, "isZoomed")) Api.SendVoidNint(_window, "zoom:", 0);
        }, cancellationToken);

    // Always to the user frame (D064.13): a miniaturized zoomed window comes back and unzooms.
    public ValueTask ShowNormalAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            if (_kiosk) return;
            if (IsFullScreen || _fullScreenTransition)
            {
                _zoomAfterFullScreen = false;
                LeaveFullScreen();
                ReportPlacement();
                return;
            }
            var miniaturized = Api.SendBool(_window, "isMiniaturized");
            if (miniaturized || !Api.SendBool(_window, "isVisible"))
            {
                (_zoomWhenShown, _unzoomWhenShown) = (false, Api.SendBool(_window, "isZoomed") || miniaturized);
                if (miniaturized) Api.SendVoidNint(_window, "deminiaturize:", 0);
                ReportPlacement();
                return;
            }
            if (Api.SendBool(_window, "isZoomed")) Api.SendVoidNint(_window, "zoom:", 0);
        }, cancellationToken);

    public ValueTask SetPlacementAsync(DesktopWindowPlacement placement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return InvokeAsync(() =>
        {
            // A kiosk window stays full-screen; DesktopWindow refuses any other placement for it.
            if (_kiosk) return;
            if (IsFullScreen || _fullScreenTransition)
            {
                // AppKit returns a full-screen window to the frame it had before; the placement replaces it afterwards.
                _placementAfterFullScreen = placement;
                _zoomAfterFullScreen = placement.Maximized;
                _normal = PlacementOf(PlacementFrame(placement));
                if (!placement.FullScreen) LeaveFullScreen();
                ReportPlacement();
                return;
            }
            var miniaturized = Api.SendBool(_window, "isMiniaturized");
            var shown = !miniaturized && Api.SendBool(_window, "isVisible");
            var frame = PlacementFrame(placement);
            Api.SendVoidRectBool(_window, "setFrame:display:", frame, shown);
            _normal = PlacementOf(frame);
            var stillZoomed = Api.SendBool(_window, "isZoomed");
            SetRestoreFrame(placement.Maximized || stillZoomed ? frame : null, stillZoomed);
            if (shown)
            {
                (_zoomWhenShown, _unzoomWhenShown) = (false, false);
                var zoomed = Api.SendBool(_window, "isZoomed");
                if (placement.Maximized && !zoomed) Api.SendVoidNint(_window, "zoom:", 0);
                else if (!placement.Maximized && zoomed)
                {
                    // AppKit still counts the window as zoomed: unzoom it, then it takes the frame (QueueRestoreFrame).
                    Api.SendVoidNint(_window, "zoom:", 0);
                    QueueRestoreFrame();
                }
            }
            else
            {
                (_zoomWhenShown, _unzoomWhenShown) = (placement.Maximized, false);
            }
            if (placement.FullScreen) RequestFullScreen();
            ReportPlacement();
        }, cancellationToken);
    }

    // Main thread only, after the window may have been unzoomed (WindowGeometryChanged, QueuePendingZoom): a window that
    // was zoomed and is shown and no longer zoomed takes the frame a placement gave it. Posted, so setFrame:display: does
    // not run inside the delegate callback.
    private void QueueRestoreFrame()
    {
        if (_restoreFrame is null) return;
        MacOsMainThread.Post(TryRestoreFrame);
    }

    private void TryRestoreFrame()
    {
        try
        {
            if (!IsOpen || _window == 0 || _restoreFrame is not { } frame || IsFullScreen || _fullScreenTransition) return;
            if (Api.SendBool(_window, "isMiniaturized") || !Api.SendBool(_window, "isVisible")) return;
            if (Api.SendBool(_window, "isZoomed"))
            {
                _restoreFrameArmed = true;
                return;
            }
            if (!_restoreFrameArmed) return;
            var current = Api.SendRect(_window, "frame");
            if (current.X == frame.X && current.Y == frame.Y && current.Width == frame.Width && current.Height == frame.Height)
            {
                SetRestoreFrame(null, zoomedNow: false);
                return;
            }
            if (_restoreFrameTries++ >= 4)
            {
                SetRestoreFrame(null, zoomedNow: false);
                return;
            }
            Api.SendVoidRectBool(_window, "setFrame:display:", frame, true);
            _normal = PlacementOf(frame);
            UpdateState();
            // Checked again once an unzoom animation that was still running has ended.
            MacOsMainThread.PostAfter(TimeSpan.FromMilliseconds(300), TryRestoreFrame);
        }
        catch
        {
            // Never unwind through AppKit.
        }
    }

    // Main thread only: whether the window is zoomed, or zooms when it is next shown or leaves full screen.
    private bool PlacementMaximized => IsFullScreen || _fullScreenTransition
        ? _zoomAfterFullScreen ?? false
        : _zoomWhenShown || (Api.SendBool(_window, "isZoomed") && !_unzoomWhenShown);

    // Main thread only. Leaves full screen now, or once the running transition has ended.
    private void LeaveFullScreen()
    {
        if (_fullScreenTransition)
        {
            _leaveFullScreenAfterTransition = true;
            return;
        }
        if (!IsFullScreen) return;
        _fullScreenRetries = 0;
        Api.SendVoidNint(_window, "toggleFullScreen:", 0);
    }

    // Main thread only, after a full-screen transition ended (EndFullScreenTransition): a window that should leave full
    // screen leaves it, and a window that left takes what MaximizeAsync, RestoreAsync or SetPlacementAsync asked for.
    private void ApplyAfterFullScreen()
    {
        if (IsFullScreen)
        {
            if (_leaveFullScreenAfterTransition)
            {
                _leaveFullScreenAfterTransition = false;
                LeaveFullScreen();
            }
            return;
        }
        _leaveFullScreenAfterTransition = false;
        var (placement, zoom) = (_placementAfterFullScreen, _zoomAfterFullScreen);
        (_placementAfterFullScreen, _zoomAfterFullScreen) = (null, null);
        if (placement is not null)
        {
            var frame = PlacementFrame(placement);
            Api.SendVoidRectBool(_window, "setFrame:display:", frame, true);
            _normal = PlacementOf(frame);
            SetRestoreFrame(placement.Maximized ? frame : null, zoomedNow: false);
        }
        if (zoom is { } zoomed && zoomed != Api.SendBool(_window, "isZoomed")) Api.SendVoidNint(_window, "zoom:", 0);
        UpdateState();
    }

    // Main thread only: a shown window takes the zoom state it was given while hidden or miniaturized. Posted, so zoom:
    // does not run inside the delegate callback that reported the window back.
    private void QueuePendingZoom()
    {
        if (!_zoomWhenShown && !_unzoomWhenShown) return;
        MacOsMainThread.Post(() =>
        {
            try
            {
                if (!IsOpen || _window == 0 || IsFullScreen || _fullScreenTransition) return;
                if (Api.SendBool(_window, "isMiniaturized") || !Api.SendBool(_window, "isVisible")) return;
                var zoom = _zoomWhenShown;
                (_zoomWhenShown, _unzoomWhenShown) = (false, false);
                if (zoom != Api.SendBool(_window, "isZoomed")) Api.SendVoidNint(_window, "zoom:", 0);
                UpdateState();
                QueueRestoreFrame();
            }
            catch
            {
                // Never unwind through AppKit.
            }
        });
    }

    // Main thread only: the frame for a placement in AppKit's coordinates, kept on a screen that exists now as at open
    // (FitPlacement). Width and height are the content size; a null position and a zero size keep the restored ones.
    private CGRect PlacementFrame(DesktopWindowPlacement placement)
    {
        var basis = _normal ?? ReadBounds();
        var contentWidth = placement.Width > 0 ? placement.Width : basis.Width;
        var contentHeight = placement.Height > 0 ? placement.Height : basis.Height;
        var size = Api.SendRectRect(_window, "frameRectForContentRect:", new CGRect(0, 0, contentWidth, contentHeight));
        var primaryHeight = PrimaryScreenHeight();
        double x;
        double bottom;
        if (placement is { X: { } placedX, Y: { } placedY })
        {
            x = placedX;
            bottom = MacOsCoordinates.FrameBottom(placedY, size.Height, primaryHeight, MacOsCoordinates.IsBottomLeft(placement));
        }
        else
        {
            var frame = Api.SendRect(_window, "frame");
            x = basis.X ?? frame.X;
            bottom = basis.Y is { } top ? MacOsCoordinates.FlipY(top, size.Height, primaryHeight) : frame.Y + frame.Height - size.Height;
        }
        var rect = WindowPlacementClamp.Clamp(new ScreenRect((int)Math.Round(x), (int)Math.Round(bottom),
            (int)Math.Round(size.Width), (int)Math.Round(size.Height)), Api.GetScreenVisibleFrames());
        return new CGRect(rect.X, rect.Y, rect.Width, rect.Height);
    }

    // Main thread only: reports the placement after a change. UpdateState skips a miniaturized window and one in a
    // full-screen transition, which keep their state but report the placement they restore to.
    private void ReportPlacement()
    {
        if (!_fullScreenTransition && !Api.SendBool(_window, "isMiniaturized"))
        {
            UpdateState();
            return;
        }
        if (Volatile.Read(ref _state) is not { } state || _normal is not { } normal) return;
        var updated = state with { Placement = normal with { Maximized = PlacementMaximized, FullScreen = state.Placement.FullScreen } };
        if (Equals(updated, state)) return;
        Volatile.Write(ref _state, updated);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
