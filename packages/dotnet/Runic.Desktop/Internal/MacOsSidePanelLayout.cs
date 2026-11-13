namespace Runic.Desktop.Internal;

// sdk#864, P3: the AppKit side of the side panel's geometry on macOS, beside SidePanelPlanner (which plans in
// DesktopWindowState.Bounds, whole points with the top-left corner in Runic's coordinates). Portable, so Linux CI tests it.
internal static class MacOsSidePanelLayout
{
    // The AppKit frame (bottom-left origin) for a planned target, from the window's AppKit frame and its bounds as
    // reported now. The plan's change applies as a difference to the frame, so a frame at fractional points keeps its
    // fraction, and a close back to the bounds before the open restores that frame exactly. The top edge stays where the
    // plan puts it, as Y in Runic's coordinates grows downwards and AppKit's upwards.
    internal static (double X, double Y, double Width, double Height) Frame(
        (double X, double Y, double Width, double Height) frame, DesktopWindowBounds current, DesktopWindowBounds target)
    {
        double dx = (target.X ?? current.X ?? 0) - (current.X ?? 0);
        double dy = (target.Y ?? current.Y ?? 0) - (current.Y ?? 0);
        var width = frame.Width + (target.Width - current.Width);
        var height = frame.Height + (target.Height - current.Height);
        var top = frame.Y + frame.Height - dy;
        return (frame.X + dx, top - height, width, height);
    }

    // How the content view's bounds divide between the page and a shown panel nativeWidth points wide: the panel keeps
    // its width while the window has room for it, the page takes the rest (SidePanelPlanner.Layout, in points).
    internal static (double PageX, double PageWidth, double PanelX, double PanelWidth) Layout(double x, double width, bool left, double panelWidth)
    {
        width = Math.Max(0, width);
        var panel = Math.Clamp(panelWidth, 0, width);
        var page = width - panel;
        return left ? (x + panel, page, x, panel) : (x, page, x + page, panel);
    }
}
