namespace Runic.Desktop;

/// <summary>The size, position, maximized and full-screen state to save for a window and restore the next time it opens.</summary>
/// <remarks>
/// Take it from <see cref="DesktopWindowState.Placement"/> when the window closes, store it with the application's
/// settings, and pass it back as <see cref="DesktopWindowOptions.Placement"/>. The values use the units that
/// <see cref="DesktopWindow.MoveAsync"/> and <see cref="DesktopWindow.ResizeAsync"/> take on the platform that saved
/// them (see <see cref="DesktopWindowState"/>), so a placement belongs to one machine and one operating system: restoring
/// it elsewhere still opens a visible window, but not necessarily at the same place or size. It is a plain record that
/// <c>System.Text.Json</c> serializes as is. <see cref="Origin"/> tells a placement saved on macOS by Runic 0.11 or
/// earlier, whose position was measured from the bottom of the screen, from a current one.
/// </remarks>
public sealed record DesktopWindowPlacement
{
    /// <summary>Gets the horizontal position of the restored (not maximized) window, or <see langword="null"/> to let the platform choose.</summary>
    /// <remarks>Applies only together with <see cref="Y"/>.</remarks>
    public int? X { get; init; }

    /// <summary>Gets the vertical position of the restored (not maximized) window, or <see langword="null"/> to let the platform choose.</summary>
    /// <remarks>Applies only together with <see cref="X"/>.</remarks>
    public int? Y { get; init; }

    /// <summary>Gets the width of the restored (not maximized) window, or zero (or less) for <see cref="DesktopWindowOptions.Width"/>.</summary>
    public int Width { get; init; }

    /// <summary>Gets the height of the restored (not maximized) window, or zero (or less) for <see cref="DesktopWindowOptions.Height"/>.</summary>
    public int Height { get; init; }

    /// <summary>Gets whether the window was maximized; it then opens maximized and restores to the other values.</summary>
    public bool Maximized { get; init; }

    /// <summary>Gets whether the window was full-screen; it then opens full-screen on the monitor of the other values and leaves full screen to them.</summary>
    /// <remarks>
    /// Set while the window was full-screen when the placement was taken, not for a <see cref="DesktopWindowOptions.Kiosk"/>
    /// window, which opens full-screen anyway. A placement saved by an earlier release has no such value and opens as
    /// before. A window host without <see cref="DesktopWindowCapabilities.FullScreen"/> ignores it.
    /// </remarks>
    public bool FullScreen { get; init; }

    /// <summary>
    /// Gets where <see cref="X"/> and <see cref="Y"/> are measured from, or <see langword="null"/> for a placement saved
    /// before Runic 0.12.
    /// </summary>
    /// <remarks>
    /// The built-in window hosts report <see cref="DesktopCoordinateOrigin.TopLeft"/>. Only macOS reads it: there a
    /// placement with <see langword="null"/> or <see cref="DesktopCoordinateOrigin.BottomLeft"/> is one that Runic 0.11
    /// or earlier saved in AppKit's screen coordinates, the bottom-left corner of the frame measured up from the bottom
    /// of the primary screen. The window opens at the same place on the screen, and the placement it then reports is
    /// measured from the top-left, so the conversion happens once. Windows and Linux always measured from the top-left
    /// and ignore it. A custom window host defines its own units and may ignore it.
    /// </remarks>
    public DesktopCoordinateOrigin? Origin { get; init; }
}

/// <summary>Where the position of a <see cref="DesktopWindowPlacement"/> is measured from.</summary>
/// <remarks>This set is open: a minor release may add values, so handle unknown ones as <see cref="TopLeft"/>.</remarks>
public enum DesktopCoordinateOrigin
{
    /// <summary>
    /// The top-left corner of the window's frame, measured right and down from the top-left corner of the primary
    /// monitor, as every platform reports positions since Runic 0.12.
    /// </summary>
    TopLeft,

    /// <summary>
    /// The bottom-left corner of the window's frame, measured right and up from the bottom-left corner of the primary
    /// screen: AppKit's screen coordinates, which macOS placements used before Runic 0.12.
    /// </summary>
    BottomLeft,
}

/// <summary>The bounds and state of an embedded window, as <see cref="DesktopWindow.State"/> last reported them.</summary>
/// <remarks>
/// <para>
/// The values use the units that <see cref="DesktopWindow.MoveAsync"/> and <see cref="DesktopWindow.ResizeAsync"/>
/// take on each platform, so a value read here moves or resizes the window back to the same place:
/// </para>
/// <list type="table">
/// <listheader><term>Platform</term><description>Position and size</description></listheader>
/// <item><term>Windows</term><description>
/// <see cref="X"/> and <see cref="Y"/> are the top-left corner of the frame in physical virtual-screen pixels;
/// <see cref="Width"/> and <see cref="Height"/> are the outer frame in device-independent pixels (1/96 inch) at the
/// window's monitor scale.</description></item>
/// <item><term>macOS</term><description>
/// Points: <see cref="X"/> and <see cref="Y"/> are the top-left corner of the frame, title bar included, measured right
/// and down from the top-left corner of the primary screen (the one with the menu bar), so a screen above or left of it
/// has negative coordinates; <see cref="Width"/> and <see cref="Height"/> are the content size, without the title bar.
/// Runic 0.11 and earlier used AppKit's bottom-left origin; see <see cref="DesktopWindowPlacement.Origin"/>.</description></item>
/// <item><term>Linux, GTK 3</term><description>
/// Logical pixels: <see cref="X"/> and <see cref="Y"/> are the top-left corner of the frame as the window manager
/// reports it; <see cref="Width"/> and <see cref="Height"/> are the content size, without decorations. On Wayland the
/// position is <see langword="null"/>: Wayland reports none.</description></item>
/// <item><term>Linux, GTK 4</term><description>
/// <see cref="Width"/> and <see cref="Height"/> are the content size in logical pixels. On X11 <see cref="X"/> and
/// <see cref="Y"/> are the top-left corner of the visible frame in logical pixels, which Runic reads through Xlib; on
/// Wayland the position is <see langword="null"/>: GTK 4 has no position API there.</description></item>
/// </list>
/// <para>
/// A minimized window keeps the state it had before it was minimized.
/// </para>
/// </remarks>
public sealed record DesktopWindowState
{
    /// <summary>Gets the current horizontal position, or <see langword="null"/> where the platform reports none.</summary>
    public int? X { get; init; }

    /// <summary>Gets the current vertical position, or <see langword="null"/> where the platform reports none.</summary>
    public int? Y { get; init; }

    /// <summary>Gets the current width; for a maximized or full-screen window, the maximized or full-screen width.</summary>
    public int Width { get; init; }

    /// <summary>Gets the current height; for a maximized or full-screen window, the maximized or full-screen height.</summary>
    public int Height { get; init; }

    /// <summary>Gets whether the window is maximized (zoomed on macOS).</summary>
    public bool IsMaximized { get; init; }

    /// <summary>Gets whether the window is full-screen.</summary>
    /// <remarks>
    /// A <see cref="DesktopWindowOptions.Kiosk"/> window is full-screen, as is a window after
    /// <see cref="DesktopWindow.SetFullScreenAsync"/>, and on macOS and Linux the user can also make a window full-screen.
    /// <see cref="Placement"/> records it, so a window closed full-screen opens full-screen again.
    /// </remarks>
    public bool IsFullScreen { get; init; }

    /// <summary>
    /// Gets the <see cref="Runic.Platform.DesktopMonitor.Id"/> of the monitor that holds most of the window, or
    /// <see langword="null"/> where the host does not report it.
    /// </summary>
    public string? MonitorId { get; init; }

    /// <summary>
    /// Gets what to save to restore the window: its bounds when last neither maximized nor full-screen, and whether it is
    /// maximized or full-screen.
    /// </summary>
    public DesktopWindowPlacement Placement { get; init; } = new();
}

/// <summary>Provides the new state of a window for <see cref="DesktopWindow.StateChanged"/>.</summary>
public sealed class DesktopWindowStateChangedEventArgs : EventArgs
{
    /// <summary>Creates event data for <paramref name="state"/>.</summary>
    /// <param name="state">The new state.</param>
    public DesktopWindowStateChangedEventArgs(DesktopWindowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
    }

    /// <summary>Gets the new state.</summary>
    public DesktopWindowState State { get; }
}
