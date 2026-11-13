using Runic.Platform;

namespace Runic.Desktop;

/// <summary>
/// A window's frame in the platform's native window units, exactly as <see cref="DesktopWindow.SetBoundsAsync"/> takes
/// it: <see cref="DesktopWindowState.Bounds"/> read from a window sets the same frame again, to the pixel.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="DesktopWindowState.X"/>, <see cref="DesktopWindowState.Y"/>, <see cref="DesktopWindowState.Width"/>
/// and <see cref="DesktopWindowState.Height"/>, which keep the units of <see cref="DesktopWindow.MoveAsync"/> and
/// <see cref="DesktopWindow.ResizeAsync"/> (and of saved placements), all four values here use one unit, so they never
/// round:
/// </para>
/// <list type="table">
/// <listheader><term>Platform</term><description>Units and rectangle</description></listheader>
/// <item><term>Windows</term><description>
/// Physical virtual-screen pixels: the window rectangle (<c>GetWindowRect</c>), including the invisible resize borders
/// of Windows 10 and 11. <see cref="Scale"/> is the window's DPI divided by 96, such as 1.5 at 150 %.</description></item>
/// <item><term>macOS</term><description>
/// Points: the frame, title bar included, its top-left corner measured right and down from the top-left corner of the
/// primary screen (as <see cref="DesktopWindowState.X"/>). <see cref="Scale"/> is 1.</description></item>
/// <item><term>Linux, GTK 3 and GTK 4</term><description>
/// Logical pixels: the top-left corner of the frame and the content size, without decorations, as in
/// <see cref="DesktopWindowState"/>, since GTK sizes a window by its content. On Wayland <see cref="X"/> and
/// <see cref="Y"/> are <see langword="null"/>. <see cref="Scale"/> is 1.</description></item>
/// </list>
/// <para>
/// A length in device-independent pixels (points, logical pixels), such as the width of a panel, becomes native units
/// with <see cref="Scale"/>; <see cref="Expand"/> does this once, so growing a window and shrinking it by the same
/// length returns exactly the bounds it started from. At 150 % a window 1001 pixels wide grows by 372 to 1559 pixels
/// and shrinks back to 1001.
/// </para>
/// <para>
/// A custom window host (<see cref="IDesktopWindowBoundsHost"/>) defines its own units and documents them.
/// </para>
/// </remarks>
public sealed record DesktopWindowBounds
{
    /// <summary>
    /// Gets the horizontal position of the top-left corner, or <see langword="null"/> where the platform reports no
    /// position (Wayland) or to keep the window where it is.
    /// </summary>
    /// <remarks>Applies only together with <see cref="Y"/>.</remarks>
    public int? X { get; init; }

    /// <summary>
    /// Gets the vertical position of the top-left corner, or <see langword="null"/> where the platform reports no position
    /// (Wayland) or to keep the window where it is.
    /// </summary>
    /// <remarks>Applies only together with <see cref="X"/>.</remarks>
    public int? Y { get; init; }

    /// <summary>Gets the width in native units.</summary>
    public int Width { get; init; }

    /// <summary>Gets the height in native units.</summary>
    public int Height { get; init; }

    /// <summary>
    /// Gets the native units per device-independent pixel when the bounds were read: the window's DPI divided by 96 on
    /// Windows, and 1 on macOS (points) and Linux (logical pixels). Defaults to 1.
    /// </summary>
    /// <remarks>
    /// <see cref="Expand"/> uses it to convert lengths. <see cref="DesktopWindow.SetBoundsAsync"/> ignores it: native units
    /// do not depend on the scale.
    /// </remarks>
    public double Scale { get; init; } = 1;

    /// <summary>
    /// Returns these bounds grown on one edge by a length in device-independent pixels (points on macOS, logical pixels on
    /// Linux), or shrunk by a negative length.
    /// </summary>
    /// <param name="edge">
    /// The edge that moves. <see cref="DesktopWindowEdge.Left"/> moves <see cref="X"/> and changes the width, so the right
    /// edge stays where it is; <see cref="DesktopWindowEdge.Right"/> keeps <see cref="X"/>.
    /// <see cref="DesktopWindowEdge.Start"/> and <see cref="DesktopWindowEdge.End"/> follow
    /// <paramref name="layoutDirection"/>.
    /// </param>
    /// <param name="length">The length to add to the width, converted to native units with <see cref="Scale"/> and rounded once, half away from zero.</param>
    /// <param name="layoutDirection">
    /// The direction that resolves <see cref="DesktopWindowEdge.Start"/> and <see cref="DesktopWindowEdge.End"/>, such as
    /// the window's <see cref="DesktopWindowOptions.LayoutDirection"/>: right to left makes the start the right edge;
    /// <see cref="DesktopLayoutDirection.Default"/> and left to right make it the left edge.
    /// </param>
    /// <returns>The new bounds, with the same <see cref="Scale"/>.</returns>
    /// <remarks>
    /// A length rounds to the same number of native units whether it is added or removed, so
    /// <c>bounds.Expand(edge, length).Expand(edge, -length)</c> equals <c>bounds</c>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="edge"/> or <paramref name="layoutDirection"/> is not a defined value, <paramref name="length"/> is
    /// not finite, or the width would not stay positive.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The left edge would move but the bounds have no position (Wayland, where a window cannot be moved), or
    /// <see cref="Scale"/> is not a positive finite number.
    /// </exception>
    public DesktopWindowBounds Expand(DesktopWindowEdge edge, double length,
        DesktopLayoutDirection layoutDirection = DesktopLayoutDirection.Default)
    {
        if (!Enum.IsDefined(edge)) throw new ArgumentOutOfRangeException(nameof(edge));
        if (!Enum.IsDefined(layoutDirection)) throw new ArgumentOutOfRangeException(nameof(layoutDirection));
        if (!double.IsFinite(length)) throw new ArgumentOutOfRangeException(nameof(length), length, "The length must be finite.");
        var delta = ToNativeLength(length);
        var left = ResolvesToLeft(edge, layoutDirection);
        var width = (long)Width + delta;
        if (width < 1 || width > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, $"The width {Width} would become {width}, which is not a positive width.");
        }
        if (!left) return this with { Width = (int)width };
        if (X is not { } x)
        {
            throw new InvalidOperationException(
                "The bounds have no position, so the left edge cannot move: the platform does not let applications position windows (Wayland).");
        }
        return this with { X = checked(x - delta), Width = (int)width };
    }

    // A length in device-independent pixels in native units, rounded half away from zero so that a length and its
    // negation round to opposite values.
    internal int ToNativeLength(double length)
    {
        if (!double.IsFinite(Scale) || Scale <= 0)
        {
            throw new InvalidOperationException($"The bounds' scale {Scale} is not a positive finite number.");
        }
        var native = Math.Round(length * Scale, MidpointRounding.AwayFromZero);
        if (native is > int.MaxValue or < -int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The length is too large for the window.");
        }
        return (int)native;
    }

    internal static bool ResolvesToLeft(DesktopWindowEdge edge, DesktopLayoutDirection layoutDirection) => edge switch
    {
        DesktopWindowEdge.Left => true,
        DesktopWindowEdge.Right => false,
        DesktopWindowEdge.Start => layoutDirection != DesktopLayoutDirection.RightToLeft,
        _ => layoutDirection == DesktopLayoutDirection.RightToLeft,
    };
}

/// <summary>A vertical edge of a window, for <see cref="DesktopWindowBounds.Expand"/>.</summary>
/// <remarks>This set is open: a minor release may add values.</remarks>
public enum DesktopWindowEdge
{
    /// <summary>The left edge, whatever the layout direction.</summary>
    Left,

    /// <summary>The right edge, whatever the layout direction.</summary>
    Right,

    /// <summary>The edge where a line of text starts: left for left-to-right layouts, right for right-to-left ones.</summary>
    Start,

    /// <summary>The edge where a line of text ends: right for left-to-right layouts, left for right-to-left ones.</summary>
    End,
}
