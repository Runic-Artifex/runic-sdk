namespace Runic.Desktop.Internal;

// DesktopWindow.SetBoundsAsync and SetMinimumSizeAsync, and the exact, visible and snapped parts of DesktopWindowState
// (#864, B1). Bounds are the frame in points, title bar included, its top-left corner in Runic's coordinates
// (MacOsCoordinates); setFrame:display: takes the frame back in one call.
internal sealed partial class MacOsWkWebViewHost
{
    // The margins macOS 15 can leave around tiled windows ("Tiled windows have margins"), in points: a frame that far
    // inside a half or a quarter of its screen's visible frame counts as tiled.
    private const int TiledMargin = 16;

    public ValueTask SetBoundsAsync(DesktopWindowBounds bounds, CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            if (IsFullScreen || _fullScreenTransition || Api.SendBool(_window, "isZoomed") || Api.SendBool(_window, "isMiniaturized"))
            {
                throw WindowBoundsSupport.NotRestored();
            }
            var frame = Api.SendRect(_window, "frame");
            // Without a position the top-left corner stays: AppKit's origin is the bottom-left one.
            var x = bounds.X ?? frame.X;
            var bottom = bounds.Y is { } y
                ? MacOsCoordinates.FlipY(y, bounds.Height, PrimaryScreenHeight())
                : frame.Y + frame.Height - bounds.Height;
            Api.SendVoidRectBool(_window, "setFrame:display:", new CGRect(x, bottom, bounds.Width, bounds.Height), true);
            UpdateState();
        }, cancellationToken);

    public ValueTask SetMinimumSizeAsync(int width, int height, CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            // The page's minimum; an open side panel adds its width (ApplyContentMinimum).
            _pageMinimum = (width, height);
            ApplyContentMinimum();
            if (IsFullScreen || Api.SendBool(_window, "isZoomed")) return;
            // A window that is smaller grows to the minimum and keeps its top-left corner, as SetSizeAsync does.
            var panelWidth = _panel is { IsShown: true } panel ? panel.NativeWidth : 0;
            var before = Api.SendRect(_window, "frame");
            var content = Api.SendRectRect(_window, "contentRectForFrameRect:", before);
            if (content.Width >= width + panelWidth && content.Height >= height) return;
            Api.SendVoidSize(_window, "setContentSize:", new CGSize(Math.Max(content.Width, width + panelWidth), Math.Max(content.Height, height)));
            var after = Api.SendRect(_window, "frame");
            Api.SendVoidPoint(_window, "setFrameOrigin:", new CGPoint(after.X, before.Y + before.Height - after.Height));
        }, cancellationToken);

    // Main thread only: the frame as bounds, which the frame the user sees equals (AppKit draws the shadow outside it),
    // and whether it fills a half or a quarter of its screen's visible frame: macOS reports no tiling state.
    private (DesktopWindowBounds Bounds, bool Snapped) ReadExactBounds(bool maximized, bool fullScreen)
    {
        var frame = Api.SendRect(_window, "frame");
        var (x, y) = MacOsCoordinates.FrameTopLeft(frame.X, frame.Y, frame.Height, PrimaryScreenHeight());
        var bounds = new DesktopWindowBounds
        {
            X = x,
            Y = y,
            Width = (int)Math.Round(frame.Width),
            Height = (int)Math.Round(frame.Height),
            Scale = 1,
        };
        var snapped = false;
        var screen = Api.SendNint(_window, "screen");
        if (!maximized && !fullScreen && screen != 0)
        {
            var visible = Api.SendRect(screen, "visibleFrame");
            // Both rectangles in AppKit's coordinates; the comparison does not depend on the flip.
            snapped = WindowBoundsSupport.LooksTiled(
                new ScreenRect((int)Math.Round(frame.X), (int)Math.Round(frame.Y), (int)Math.Round(frame.Width), (int)Math.Round(frame.Height)),
                new ScreenRect((int)Math.Round(visible.X), (int)Math.Round(visible.Y), (int)Math.Round(visible.Width), (int)Math.Round(visible.Height)),
                tolerance: 1, margin: TiledMargin);
        }
        return (bounds, snapped);
    }
}
