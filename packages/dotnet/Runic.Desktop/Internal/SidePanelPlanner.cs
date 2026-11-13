namespace Runic.Desktop.Internal;

// What SidePanelPlanner knows of a window, read on its thread (or the last report, for DesktopWindow.CheckSidePanel). All
// rectangles in the window's native units and screen coordinates, as DesktopWindowState.Bounds (physical pixels on
// Windows). WorkArea is the work area of the window's monitor, null where the platform reports none (Wayland).
internal readonly record struct SidePanelWindow(
    DesktopWindowBounds Bounds,
    DesktopWindowBounds? Visible,
    ScreenRect? WorkArea,
    bool IsMaximized,
    bool IsFullScreen,
    bool IsSnapped,
    bool IsMinimized);

// A planned change: the bounds to set in one native operation and the panel's width in native units, or why none.
internal readonly record struct SidePanelPlan(DesktopSidePanelRefusal? Refusal, DesktopWindowBounds? Target, int NativeWidth)
{
    internal static SidePanelPlan Refused(DesktopSidePanelRefusal refusal) => new(refusal, null, 0);
}

// sdk#864, P2: the geometry of a side panel (side-panel/DESIGN.md §1.2, §3.1, §3.4; D062.3–D062.5). Pure and portable, so
// Linux CI tests it for every platform; each built-in host reads the window on its thread and applies the plan in one
// native bounds change.
//
// - Open grows the window on one edge by the panel's width in native units (DesktopWindowBounds.Expand rounds once), so
//   the page keeps its size and, on the left edge, its screen position. Close shrinks it back: to exactly the bounds
//   before the open when the window is still at the bounds the open gave it, otherwise by moving the docked edge back.
// - Refusals, in this order: minimized or hidden, full screen (kiosk included), maximized, snapped, the left edge without
//   positions (Wayland), and no room: the grown visible frame must stay inside the work area (D062.3: the window is
//   never moved to make room), and the panel may take at most half of it.
internal static class SidePanelPlanner
{
    // Why the window's state rules out a change of its frame now; null when it allows one.
    internal static DesktopSidePanelRefusal? StateRefusal(in SidePanelWindow window) => window switch
    {
        { IsMinimized: true } => DesktopSidePanelRefusal.Minimized,
        { IsFullScreen: true } => DesktopSidePanelRefusal.FullScreen,
        { IsMaximized: true } => DesktopSidePanelRefusal.Maximized,
        { IsSnapped: true } => DesktopSidePanelRefusal.Snapped,
        _ => null,
    };

    // Opening a panel width device-independent pixels wide on the left or right edge.
    internal static SidePanelPlan Open(in SidePanelWindow window, bool left, int width)
    {
        if (StateRefusal(window) is { } refusal) return SidePanelPlan.Refused(refusal);
        if (left && window.Bounds.X is null) return SidePanelPlan.Refused(DesktopSidePanelRefusal.PositionUnavailable);
        var native = window.Bounds.ToNativeLength(width);
        if (window.WorkArea is { } work && (long)native * 2 > work.Width) return SidePanelPlan.Refused(DesktopSidePanelRefusal.NoRoom);
        if (!Fits(window, left ? -native : 0, native)) return SidePanelPlan.Refused(DesktopSidePanelRefusal.NoRoom);
        return new(null, Grow(window.Bounds, left, native), native);
    }

    // Moving an open panel of nativeWidth to the other edge: the window moves by its width, keeping its size, so the page
    // stays where it is on the screen.
    internal static SidePanelPlan Move(in SidePanelWindow window, bool toLeft, int nativeWidth)
    {
        if (StateRefusal(window) is { } refusal) return SidePanelPlan.Refused(refusal);
        if (window.Bounds.X is not { } x) return SidePanelPlan.Refused(DesktopSidePanelRefusal.PositionUnavailable);
        var shift = toLeft ? -nativeWidth : nativeWidth;
        if (!Fits(window, shift, 0)) return SidePanelPlan.Refused(DesktopSidePanelRefusal.NoRoom);
        return new(null, window.Bounds with { X = checked(x + shift) }, nativeWidth);
    }

    // Changing an open panel's width from nativeWidth to width device-independent pixels: the docked edge moves by the
    // difference.
    internal static SidePanelPlan Resize(in SidePanelWindow window, bool left, int nativeWidth, int width)
    {
        if (StateRefusal(window) is { } refusal) return SidePanelPlan.Refused(refusal);
        if (left && window.Bounds.X is null) return SidePanelPlan.Refused(DesktopSidePanelRefusal.PositionUnavailable);
        var native = window.Bounds.ToNativeLength(width);
        var delta = native - nativeWidth;
        if (window.WorkArea is { } work && (long)native * 2 > work.Width) return SidePanelPlan.Refused(DesktopSidePanelRefusal.NoRoom);
        // Only growth needs room; a narrower panel always fits.
        if (delta > 0 && !Fits(window, left ? -delta : 0, delta)) return SidePanelPlan.Refused(DesktopSidePanelRefusal.NoRoom);
        var bounds = window.Bounds;
        var resized = bounds with { Width = checked(bounds.Width + delta) };
        if (resized.Width < 1) return SidePanelPlan.Refused(DesktopSidePanelRefusal.NoRoom);
        return new(null, left ? resized with { X = checked(bounds.X!.Value - delta) } : resized, native);
    }

    // The bounds after closing: exactly before when the window is still at opened, the bounds the open (or the last move
    // or width change) gave it; otherwise current without the panel (ExcludePanel).
    internal static DesktopWindowBounds Close(DesktopWindowBounds current, DesktopWindowBounds opened, DesktopWindowBounds before,
        bool left, int nativeWidth) =>
        SameFrame(current, opened) ? before with { Scale = current.Scale } : ExcludePanel(current, left, nativeWidth);

    // The bounds of a window that shows a panel of nativeWidth without it: the docked edge moved back by the panel's width.
    // DesktopWindowState.Placement, which applications save, is these bounds, so a restored window opens at its page's
    // size (side-panel/DESIGN.md §3.1).
    internal static DesktopWindowBounds ExcludePanel(DesktopWindowBounds bounds, bool left, int nativeWidth)
    {
        var width = Math.Max(1, bounds.Width - nativeWidth);
        var removed = bounds.Width - width;
        return left && bounds.X is { } x ? bounds with { X = checked(x + removed), Width = width } : bounds with { Width = width };
    }

    // The inverse of ExcludePanel: the bounds of a window whose page has bounds, with a panel of nativeWidth on its edge.
    // DesktopWindow.SetPlacementAsync takes the page's placement, as State.Placement reports it, and the window grows by
    // the panel, so the placement round-trips (window-api-0.19/DESIGN.md §3.1).
    internal static DesktopWindowBounds IncludePanel(DesktopWindowBounds page, bool left, int nativeWidth)
    {
        var grown = page with { Width = checked(page.Width + nativeWidth) };
        return left && page.X is { } x ? grown with { X = checked(x - nativeWidth) } : grown;
    }

    // A restored rectangle of a maximized or full-screen window whose panel closes: the rectangle it restores to shrinks by
    // the panel's width on the docked edge.
    internal static ScreenRect ShrinkRestored(ScreenRect restored, bool left, int nativeWidth)
    {
        var width = Math.Max(1, restored.Width - nativeWidth);
        var removed = restored.Width - width;
        return left ? restored with { X = restored.X + removed, Width = width } : restored with { Width = width };
    }

    // How the client area of width clientWidth divides: the page's and the panel's left edge and width. The panel keeps
    // its width while the window has room for it; the page takes the rest.
    internal static (int PageX, int PageWidth, int PanelX, int PanelWidth) Layout(int clientWidth, bool left, int nativeWidth)
    {
        clientWidth = Math.Max(0, clientWidth);
        var panel = Math.Clamp(nativeWidth, 0, clientWidth);
        var page = clientWidth - panel;
        return left ? (panel, page, 0, panel) : (0, page, page, panel);
    }

    private static DesktopWindowBounds Grow(DesktopWindowBounds bounds, bool left, int native)
    {
        var grown = bounds with { Width = checked(bounds.Width + native) };
        return left ? grown with { X = checked(bounds.X!.Value - native) } : grown;
    }

    // Whether the visible frame moved by shift and grown by growth on its right stays inside the work area horizontally.
    // Without a work area nothing can be checked, and it fits. Without visible bounds the window rectangle stands in.
    private static bool Fits(in SidePanelWindow window, int shift, int growth)
    {
        if (window.WorkArea is not { } work) return true;
        var frame = window.Visible ?? window.Bounds;
        if (frame.X is not { } x) return true;
        long newLeft = (long)x + shift;
        long newRight = (long)x + frame.Width + shift + growth;
        return newLeft >= work.X && newRight <= (long)work.X + work.Width;
    }

    internal static bool SameFrame(DesktopWindowBounds a, DesktopWindowBounds b) =>
        a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;
}
