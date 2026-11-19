using CsWebUi;
using Runic.Desktop.Internal;

namespace Runic.Application.Views.CsWebUi;

// sdk#1149: a Window's RunicWindowPlacement on CS-WebUI, in the units of WebUiWindow.TryGetPlacement: on Windows the
// outer window rectangle in screen coordinates, on Linux GTK's window size and the frame's position (none on Wayland).
internal static class CsWebUiPlacement
{
    // WebUI's own SetSize and SetPosition, which a window takes before it is shown, refuse values outside these.
    internal const int MinimumShowSize = 100, MaximumShowSize = 3840, MaximumShowX = 3000, MaximumShowY = 1800;

    internal static RunicWindowPlacement ToRunic(WebUiWindowPlacement placement) => new()
    {
        X = placement.HasPosition ? placement.X : null,
        Y = placement.HasPosition ? placement.Y : null,
        Width = placement.Width,
        Height = placement.Height,
        Maximized = placement.IsMaximized,
    };

    // The native placement for target, from the window's current one: a missing position or size keeps the current, and
    // the bounds stay on a work area that exists now, the one they overlap most or the nearest, as on Runic Desktop.
    // Without a position (Wayland) only the size fits, to the largest work area, since the compositor places the window.
    internal static WebUiWindowPlacement Fit(RunicWindowPlacement target, WebUiWindowPlacement current, IReadOnlyList<ScreenRect> workAreas)
    {
        var width = target.Width > 0 ? Math.Min(target.Width, WindowPlacementClamp.MaximumDimension) : current.Width;
        var height = target.Height > 0 ? Math.Min(target.Height, WindowPlacementClamp.MaximumDimension) : current.Height;
        if (!current.HasPosition)
        {
            var (fitWidth, fitHeight) = WindowPlacementClamp.ClampSize(width, height, WindowPlacementClamp.Largest(workAreas));
            return new WebUiWindowPlacement(0, 0, fitWidth, fitHeight, target.Maximized, HasPosition: false);
        }
        var rect = WindowPlacementClamp.Clamp(new ScreenRect(target.X ?? current.X, target.Y ?? current.Y, width, height), workAreas);
        return new WebUiWindowPlacement(rect.X, rect.Y, rect.Width, rect.Height, target.Maximized);
    }

    // The size and position a window takes before it is shown, through WebUI's SetSize and SetPosition, for an initial
    // placement (RunicCsWebUiHostOptions.Placement): fitted to the work areas known then, and within WebUI's limits. A
    // position WebUI refuses, such as one on a monitor left of the primary one, is left to TrySetPlacement after Show.
    internal static ((uint Width, uint Height)? Size, (uint X, uint Y)? Position) BeforeShow(RunicWindowPlacement placement,
        int? defaultWidth, int? defaultHeight, IReadOnlyList<ScreenRect> workAreas)
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
}
