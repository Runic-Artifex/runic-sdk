namespace Runic.Desktop.Internal;

// Shared parts of DesktopWindow.SetBoundsAsync, SetMinimumSizeAsync and DesktopWindowState.IsSnapped for the built-in
// hosts, linked into Runic.Desktop.Gtk4. Portable, so Linux CI tests the geometry of every platform.
internal static class WindowBoundsSupport
{
    internal const string PositionUnavailableCode = "window-position-unavailable";

    // A position on a display that lets no application position its windows (Wayland).
    internal static DesktopException PositionUnavailable() => new(DesktopErrorCategory.Unavailable, PositionUnavailableCode,
        "This platform lets no application position its windows (Wayland): set bounds without X and Y to resize the window.");

    // On the host's thread: the frame of a maximized, full-screen or minimized window is the desktop's.
    internal static InvalidOperationException NotRestored() => new(RestoredOnlyMessage);

    internal const string RestoredOnlyMessage =
        "The window is maximized, full-screen or minimized; restore it before setting its bounds.";

    // Whether a frame fills a half or a quarter of a work area, as the desktop tiles windows: every edge lies on the
    // tile's edge, within tolerance outwards and up to margin inwards (macOS 15 leaves margins around tiled windows when
    // "Tiled windows have margins" is on). A frame that fills the whole work area is maximized, not tiled. All values in
    // one unit and coordinate system.
    internal static bool LooksTiled(ScreenRect frame, ScreenRect workArea, int tolerance, int margin)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || workArea.Width <= 1 || workArea.Height <= 1) return false;
        var halfWidth = workArea.Width / 2;
        var halfHeight = workArea.Height / 2;
        var (left, top) = (workArea.X, workArea.Y);
        var (right, bottom) = (workArea.X + workArea.Width, workArea.Y + workArea.Height);
        var (middleX, middleY) = (left + halfWidth, top + halfHeight);
        ReadOnlySpan<(int Left, int Top, int Right, int Bottom)> tiles =
        [
            (left, top, middleX, bottom), (middleX, top, right, bottom),
            (left, top, right, middleY), (left, middleY, right, bottom),
            (left, top, middleX, middleY), (middleX, top, right, middleY),
            (left, middleY, middleX, bottom), (middleX, middleY, right, bottom),
        ];
        foreach (var tile in tiles)
        {
            if (Inset(frame.X - tile.Left) && Inset(tile.Right - (frame.X + frame.Width))
                && Inset(frame.Y - tile.Top) && Inset(tile.Bottom - (frame.Y + frame.Height)))
            {
                return true;
            }
        }
        return false;

        bool Inset(int inset) => inset >= -tolerance && inset <= margin + tolerance;
    }

    // The work area, of several, that holds most of a frame; null when it overlaps none.
    internal static ScreenRect? WorkAreaOf(ScreenRect frame, IReadOnlyList<ScreenRect> workAreas)
    {
        ScreenRect? best = null;
        long bestArea = 0;
        foreach (var area in workAreas)
        {
            long width = Math.Min(frame.X + frame.Width, area.X + area.Width) - Math.Max(frame.X, area.X);
            long height = Math.Min(frame.Y + frame.Height, area.Y + area.Height) - Math.Max(frame.Y, area.Y);
            if (width <= 0 || height <= 0 || width * height <= bestArea) continue;
            (best, bestArea) = (area, width * height);
        }
        return best;
    }

    // Windows: a window rectangle that differs from the restored rectangle of GetWindowPlacement, converted from
    // workspace to screen coordinates, while the window is neither maximized, minimized nor full-screen: Snap arranges a
    // window without changing its restored bounds. Used where user32 lacks IsWindowArranged.
    internal static bool DiffersFromRestored(ScreenRect window, ScreenRect restoredInWorkspace, int workspaceOffsetX, int workspaceOffsetY) =>
        window != restoredInWorkspace with { X = restoredInWorkspace.X + workspaceOffsetX, Y = restoredInWorkspace.Y + workspaceOffsetY };
}
