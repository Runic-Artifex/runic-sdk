namespace Runic.Desktop.Internal;

// A rectangle in one platform's screen coordinates. Only its own axis directions matter, so AppKit's upward Y works
// as well as the downward Y of Windows and X11.
internal readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    internal long Right => (long)X + Width;

    internal long Bottom => (long)Y + Height;

    internal long IntersectionArea(ScreenRect other)
    {
        var width = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var height = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return width > 0 && height > 0 ? width * height : 0;
    }

    // The squared gap between the two rectangles, zero when they touch or overlap.
    internal double DistanceSquared(ScreenRect other)
    {
        double dx = Math.Max(0, Math.Max(other.X - Right, X - other.Right));
        double dy = Math.Max(0, Math.Max(other.Y - Bottom, Y - other.Bottom));
        return dx * dx + dy * dy;
    }
}

// Keeps a restored window on a monitor that exists now, for example when the placement was saved with an external
// monitor that is no longer connected. Every host collects its monitors' work areas and applies the same rule.
internal static class WindowPlacementClamp
{
    // The largest size a placement may ask for before a host clamps it; no monitor is larger.
    internal const uint MaximumDimension = 65_535;

    // The work area the window overlaps most, or the nearest one when it overlaps none. Then the window shrinks to fit
    // it and moves fully inside it. A window already inside one work area stays where it is.
    internal static ScreenRect Clamp(ScreenRect window, IReadOnlyList<ScreenRect> workAreas)
    {
        var area = Choose(window, workAreas);
        if (area is not { } target)
        {
            return window;
        }
        var width = Math.Max(1, Math.Min(window.Width, target.Width));
        var height = Math.Max(1, Math.Min(window.Height, target.Height));
        var x = (int)Math.Clamp((long)window.X, target.X, Math.Max(target.X, target.Right - width));
        var y = (int)Math.Clamp((long)window.Y, target.Y, Math.Max(target.Y, target.Bottom - height));
        return new ScreenRect(x, y, width, height);
    }

    // Without a saved position the platform places the window; only its size is fitted, to the given work area.
    internal static (int Width, int Height) ClampSize(int width, int height, ScreenRect? workArea) =>
        workArea is { Width: > 0, Height: > 0 } area
            ? (Math.Max(1, Math.Min(width, area.Width)), Math.Max(1, Math.Min(height, area.Height)))
            : (width, height);

    internal static ScreenRect? Choose(ScreenRect window, IReadOnlyList<ScreenRect> workAreas)
    {
        ScreenRect? best = null;
        long bestOverlap = 0;
        foreach (var area in workAreas)
        {
            if (area.Width <= 0 || area.Height <= 0) continue;
            var overlap = window.IntersectionArea(area);
            if (overlap > bestOverlap)
            {
                best = area;
                bestOverlap = overlap;
            }
        }
        if (best is not null) return best;
        var bestDistance = double.MaxValue;
        foreach (var area in workAreas)
        {
            if (area.Width <= 0 || area.Height <= 0) continue;
            var distance = window.DistanceSquared(area);
            if (distance < bestDistance)
            {
                best = area;
                bestDistance = distance;
            }
        }
        return best;
    }

    // The largest work area, for hosts that cannot tell which monitor a window will appear on.
    internal static ScreenRect? Largest(IReadOnlyList<ScreenRect> workAreas)
    {
        ScreenRect? best = null;
        foreach (var area in workAreas)
        {
            if (area.Width <= 0 || area.Height <= 0) continue;
            if (best is not { } current || (long)area.Width * area.Height > (long)current.Width * current.Height)
            {
                best = area;
            }
        }
        return best;
    }
}
