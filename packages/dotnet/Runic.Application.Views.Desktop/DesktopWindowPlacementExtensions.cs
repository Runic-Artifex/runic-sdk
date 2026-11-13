using Runic.Desktop;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// Converts between Runic Desktop's <see cref="DesktopWindowPlacement"/> and the <see cref="RunicWindowPlacement"/> of
/// <see cref="IRunicWindowPresentation.Placement"/>, so a placement saved from either layer places a Window through the
/// other.
/// </summary>
/// <remarks>
/// Both use the units of <see cref="DesktopWindowState.Placement"/>; nothing is scaled. A placement saved by Runic 0.11
/// or earlier on macOS is measured from the bottom-left corner (<see cref="DesktopWindowPlacement.Origin"/> is
/// <see langword="null"/> or <see cref="DesktopCoordinateOrigin.BottomLeft"/>), which <see cref="RunicWindowPlacement"/>
/// cannot express: pass such a placement to <see cref="DesktopWindowOptions.Placement"/> or
/// <see cref="DesktopWindow.SetPlacementAsync"/>, which convert it once, and save the placement the window reports after.
/// </remarks>
public static class DesktopWindowPlacementExtensions
{
    /// <summary>Gets the <see cref="RunicWindowPlacement"/> with the same values.</summary>
    /// <param name="placement">A placement, such as <see cref="DesktopWindowState.Placement"/>.</param>
    /// <returns>The placement for <see cref="IRunicWindowPresentation.SetPlacementAsync"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is <see langword="null"/>.</exception>
    public static RunicWindowPlacement ToRunicPlacement(this DesktopWindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return new RunicWindowPlacement
        {
            X = placement.X,
            Y = placement.Y,
            Width = placement.Width,
            Height = placement.Height,
            Maximized = placement.Maximized,
            FullScreen = placement.FullScreen,
        };
    }

    /// <summary>
    /// Gets the <see cref="DesktopWindowPlacement"/> with the same values, measured from the top-left
    /// (<see cref="DesktopCoordinateOrigin.TopLeft"/>), as every placement Runic 0.12 and later reports.
    /// </summary>
    /// <param name="placement">A placement, such as <see cref="IRunicWindowPresentation.Placement"/>.</param>
    /// <returns>The placement for <see cref="DesktopWindowOptions.Placement"/> or <see cref="DesktopWindow.SetPlacementAsync"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is <see langword="null"/>.</exception>
    public static DesktopWindowPlacement ToDesktopPlacement(this RunicWindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return new DesktopWindowPlacement
        {
            X = placement.X,
            Y = placement.Y,
            Width = placement.Width,
            Height = placement.Height,
            Maximized = placement.Maximized,
            FullScreen = placement.FullScreen,
            Origin = DesktopCoordinateOrigin.TopLeft,
        };
    }
}
