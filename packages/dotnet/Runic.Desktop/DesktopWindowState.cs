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

    /// <summary>
    /// Gets the window's frame in native units that round-trip exactly, the rectangle <see cref="DesktopWindow.SetBoundsAsync"/>
    /// takes, or <see langword="null"/> where the window does not report it (without
    /// <see cref="DesktopWindowCapabilities.Bounds"/>).
    /// </summary>
    /// <remarks>
    /// <see cref="DesktopWindowBounds"/> lists the units. <see cref="X"/>, <see cref="Y"/>, <see cref="Width"/>,
    /// <see cref="Height"/> and <see cref="Placement"/> keep theirs, so saved placements keep their meaning; on Windows,
    /// where those mix physical and device-independent pixels, use these bounds to move or grow a window by an exact amount.
    /// For a maximized or full-screen window they are the maximized or full-screen frame.
    /// </remarks>
    public DesktopWindowBounds? Bounds { get; init; }

    /// <summary>
    /// Gets the frame the user sees, in the units of <see cref="Bounds"/>, or <see langword="null"/> where the platform
    /// does not report it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Place a window flush against another window, or check that it fits a monitor's work area, with these bounds:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Windows: the frame without the invisible resize borders of Windows 10 and 11
    /// (<c>DWMWA_EXTENDED_FRAME_BOUNDS</c>), about 7 device-independent pixels left, right and below
    /// <see cref="Bounds"/>.</description></item>
    /// <item><description>macOS: the frame, which has no invisible border, so the same as <see cref="Bounds"/>.</description></item>
    /// <item><description>Linux on X11: the window manager's frame around the window, decorations included
    /// (<c>_NET_FRAME_EXTENTS</c>), without the shadow of GTK's client-side decorations; its size is larger than the
    /// content size of <see cref="Bounds"/> by the decorations.</description></item>
    /// <item><description>Linux on Wayland: <see langword="null"/>; Wayland reports neither the position nor the
    /// decorations.</description></item>
    /// </list>
    /// </remarks>
    public DesktopWindowBounds? VisibleBounds { get; init; }

    /// <summary>
    /// Gets whether the desktop arranged the window against a screen edge or into a layout (snapped, tiled), where
    /// changing its bounds would break the arrangement. Not set while the window is maximized or full-screen.
    /// </summary>
    /// <remarks>
    /// <para>Best effort where the platform has no API for it:</para>
    /// <list type="bullet">
    /// <item><description>Windows: <c>IsWindowArranged</c> where user32 exports it; otherwise the window's bounds differ
    /// from its restored bounds (<c>GetWindowPlacement</c>) while it is not maximized, which is how Snap
    /// arranges a window.</description></item>
    /// <item><description>macOS: no public API reports window tiling, so the frame is compared with the halves and
    /// quarters of its screen's visible frame, allowing for the margins of tiled windows. A window the user sized to a
    /// half of the screen by hand counts as snapped; a tiling arrangement of another shape does not.</description></item>
    /// <item><description>Linux: GTK's tiled state (<c>GDK_WINDOW_STATE_TILED</c>, <c>GDK_TOPLEVEL_STATE_TILED</c>),
    /// which GNOME and KDE set on Wayland and Mutter on X11; on X11 also the visible frame compared with the halves and
    /// quarters of its monitor's work area, for window managers that tile without telling GTK.</description></item>
    /// </list>
    /// </remarks>
    public bool IsSnapped { get; init; }
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

/// <summary>Whether a window is active, minimized and visible, as <see cref="DesktopWindow.Activity"/> last reported it.</summary>
/// <remarks>
/// <para>
/// A window is active while it has the keyboard focus of the desktop: it is the foreground window on Windows, the key
/// window on macOS, and the active window (<c>gtk_window_is_active</c>) on Linux. Another window of the application,
/// a dialog in front of it or another application takes the activity away.
/// </para>
/// <para>
/// A minimized window is in the taskbar or Dock; a hidden window (<see cref="DesktopWindow.SetVisibleAsync"/>,
/// <see cref="DesktopWindowOptions.Hidden"/>) is not visible. A minimized window is neither active nor, on most
/// platforms, visible. Use the values to pause work the user cannot see, such as polling or animations.
/// </para>
/// <para>
/// On Wayland the compositor does not tell an application that its window was minimized. The GTK 4 window reports a
/// window the compositor stopped showing (GTK 4.12 and later, <c>GDK_TOPLEVEL_STATE_SUSPENDED</c>), such as a minimized
/// one, as not visible, but not as minimized; the GTK 3 window learns nothing, so a minimized window there stays visible
/// and only loses the focus. Treat <see cref="IsMinimized"/> as a hint on Linux.
/// </para>
/// </remarks>
public sealed record DesktopWindowActivity
{
    /// <summary>Gets whether the window has the desktop's keyboard focus.</summary>
    public bool IsActive { get; init; }

    /// <summary>Gets whether the window is minimized (iconified, miniaturized).</summary>
    public bool IsMinimized { get; init; }

    /// <summary>Gets whether the window is shown: not hidden and not minimized, though other windows may cover it.</summary>
    public bool IsVisible { get; init; }
}

/// <summary>Provides the new activity of a window for <see cref="DesktopWindow.ActivityChanged"/>.</summary>
public sealed class DesktopWindowActivityChangedEventArgs : EventArgs
{
    /// <summary>Creates event data for <paramref name="activity"/>.</summary>
    /// <param name="activity">The new activity.</param>
    public DesktopWindowActivityChangedEventArgs(DesktopWindowActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        Activity = activity;
    }

    /// <summary>Gets the new activity.</summary>
    public DesktopWindowActivity Activity { get; }
}
