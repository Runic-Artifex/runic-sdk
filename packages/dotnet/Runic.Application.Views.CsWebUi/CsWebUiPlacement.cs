using CsWebUi;

namespace Runic.Application.Views.CsWebUi;

// A monitor's work area, in the units of WebUiWindow.TryGetPlacement (CsWebUiWorkAreas). A plain struct rather than a
// record, and arrays rather than lists, so NativeAOT compiles no record members or collection instantiations for it.
internal readonly struct WorkArea(int x, int y, int width, int height)
{
    internal int X { get; } = x;
    internal int Y { get; } = y;
    internal int Width { get; } = width;
    internal int Height { get; } = height;
    internal long Right => (long)X + Width;
    internal long Bottom => (long)Y + Height;
}

// sdk#1149: a Window's RunicWindowPlacement on CS-WebUI, in the units of WebUiWindow.TryGetPlacement: on Windows the
// outer window rectangle in screen coordinates, on Linux GTK's window size and the frame's position (none on Wayland).
internal static class CsWebUiPlacement
{
    // WebUI's own SetSize and SetPosition, which a window takes before it is shown, refuse values outside these.
    internal const int MinimumShowSize = 100, MaximumShowSize = 3840, MaximumShowX = 3000, MaximumShowY = 1800;

    // The largest size a placement may ask for, as on Runic Desktop (WindowPlacementClamp.MaximumDimension).
    internal const int MaximumDimension = 65_535;

    internal static RunicWindowPlacement ToRunic(WebUiWindowPlacement placement) => new()
    {
        X = placement.HasPosition ? placement.X : null,
        Y = placement.HasPosition ? placement.Y : null,
        Width = placement.Width,
        Height = placement.Height,
        Maximized = placement.IsMaximized,
    };

    // The native placement for target, from the window's current one: a missing position or size keeps the current, and
    // the bounds stay on a work area that exists now, the one they overlap most or the nearest, by Runic Desktop's rule
    // (WindowPlacementClamp). Without a position (Wayland) only the size fits, to the largest work area, since the
    // compositor places the window.
    internal static WebUiWindowPlacement Fit(RunicWindowPlacement target, WebUiWindowPlacement current, WorkArea[] workAreas)
    {
        var width = target.Width > 0 ? Math.Min(target.Width, MaximumDimension) : current.Width;
        var height = target.Height > 0 ? Math.Min(target.Height, MaximumDimension) : current.Height;
        if (!current.HasPosition)
        {
            if (Largest(workAreas) is int largest)
            {
                width = Math.Max(1, Math.Min(width, workAreas[largest].Width));
                height = Math.Max(1, Math.Min(height, workAreas[largest].Height));
            }
            return new WebUiWindowPlacement(0, 0, width, height, target.Maximized, HasPosition: false);
        }
        int x = target.X ?? current.X, y = target.Y ?? current.Y;
        if (Choose(x, y, width, height, workAreas) is int chosen)
        {
            var area = workAreas[chosen];
            width = Math.Max(1, Math.Min(width, area.Width));
            height = Math.Max(1, Math.Min(height, area.Height));
            x = (int)Math.Clamp((long)x, area.X, Math.Max(area.X, area.Right - width));
            y = (int)Math.Clamp((long)y, area.Y, Math.Max(area.Y, area.Bottom - height));
        }
        return new WebUiWindowPlacement(x, y, width, height, target.Maximized);
    }

    // The size and position a window takes before it is shown, through WebUI's SetSize and SetPosition, for an initial
    // placement (RunicCsWebUiHostOptions.Placement): fitted to the work areas known then, and within WebUI's limits. A
    // position WebUI refuses, such as one on a monitor left of the primary one, is left to TrySetPlacement after Show.
    internal static ((uint Width, uint Height)? Size, (uint X, uint Y)? Position) BeforeShow(RunicWindowPlacement placement,
        int? defaultWidth, int? defaultHeight, WorkArea[] workAreas)
    {
        var width = placement.Width > 0 ? placement.Width : defaultWidth ?? 0;
        var height = placement.Height > 0 ? placement.Height : defaultHeight ?? 0;
        var sized = width > 0 && height > 0;
        // Without a size, a one-pixel window places only the position.
        var current = new WebUiWindowPlacement(0, 0, sized ? width : 1, sized ? height : 1, HasPosition: placement.X is not null);
        var fitted = Fit(placement with { Width = current.Width, Height = current.Height }, current, workAreas);
        (uint, uint)? size = sized
            ? ((uint)Math.Clamp(fitted.Width, MinimumShowSize, MaximumShowSize), (uint)Math.Clamp(fitted.Height, MinimumShowSize, MaximumShowSize))
            : null;
        (uint, uint)? position = fitted.HasPosition && fitted.X is >= 0 and <= MaximumShowX && fitted.Y is >= 0 and <= MaximumShowY
            ? ((uint)fitted.X, (uint)fitted.Y)
            : null;
        return (size, position);
    }

    // The index of the work area the window overlaps most, or the nearest one when it overlaps none; null without one.
    private static int? Choose(int x, int y, int width, int height, WorkArea[] workAreas)
    {
        long right = (long)x + width, bottom = (long)y + height;
        int? best = null;
        long bestOverlap = 0;
        for (var index = 0; index < workAreas.Length; index++)
        {
            var area = workAreas[index];
            if (area.Width <= 0 || area.Height <= 0) continue;
            long overlapWidth = Math.Min(right, area.Right) - Math.Max(x, area.X), overlapHeight = Math.Min(bottom, area.Bottom) - Math.Max(y, area.Y);
            var overlap = overlapWidth > 0 && overlapHeight > 0 ? overlapWidth * overlapHeight : 0;
            if (overlap > bestOverlap)
            {
                best = index;
                bestOverlap = overlap;
            }
        }
        if (best is not null) return best;
        var bestDistance = double.MaxValue;
        for (var index = 0; index < workAreas.Length; index++)
        {
            var area = workAreas[index];
            if (area.Width <= 0 || area.Height <= 0) continue;
            double dx = Math.Max(0, Math.Max(area.X - right, x - area.Right)), dy = Math.Max(0, Math.Max(area.Y - bottom, y - area.Bottom));
            if (dx * dx + dy * dy < bestDistance)
            {
                best = index;
                bestDistance = dx * dx + dy * dy;
            }
        }
        return best;
    }

    // The index of the largest work area, for a window whose monitor the platform chooses.
    private static int? Largest(WorkArea[] workAreas)
    {
        int? best = null;
        for (var index = 0; index < workAreas.Length; index++)
        {
            var area = workAreas[index];
            if (area.Width > 0 && area.Height > 0
                && (best is not int current || (long)area.Width * area.Height > (long)workAreas[current].Width * workAreas[current].Height))
                best = index;
        }
        return best;
    }
}
