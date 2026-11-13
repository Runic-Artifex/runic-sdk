namespace Runic.Desktop;

// sdk#897, W1a: maximize, restore and the placement of an open window (window-api-0.19/DESIGN.md §3.1; D064.11–D064.13,
// D064.20: stable).
public sealed partial class DesktopWindow
{
    /// <summary>Maximizes the window (zooms it on macOS); does nothing when it already is.</summary>
    /// <param name="cancellationToken">Cancels waiting for the window and the queued native operation.</param>
    /// <returns>A task that completes when the window has been asked to maximize; <see cref="StateChanged"/> reports it.</returns>
    /// <remarks>
    /// <para>
    /// Requires <see cref="DesktopWindowCapabilities.WindowPlacement"/>. Unlike <see cref="ToggleMaximizedAsync"/>, a second
    /// call leaves the window maximized. A full-screen window leaves full screen first and then maximizes; a minimized
    /// window is restored maximized; a hidden window stays hidden and shows maximized when it is next shown. With a side
    /// panel open the window keeps it.
    /// </para>
    /// <para>
    /// Windows activates a window that it maximizes, as it does when the user maximizes one. macOS animates the zoom, and a
    /// window manager on Linux decides when the window is maximized, so <see cref="DesktopWindowState.IsMaximized"/> may
    /// change after the call has completed.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The window is a <see cref="DesktopWindowOptions.Kiosk"/> window, which stays full-screen.</exception>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="DesktopException">The window lacks the required capability (<c>window-capability-unavailable</c>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask MaximizeAsync(CancellationToken cancellationToken = default)
    {
        RequireNotKiosk();
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.WindowPlacement,
            () => _engine.ShowMaximizedAsync(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Returns the window to its normal bounds: leaves full screen, un-maximizes and un-minimizes it; does nothing when it
    /// is normal.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the window and the queued native operation.</param>
    /// <returns>A task that completes when the window has been asked to restore; <see cref="StateChanged"/> reports it.</returns>
    /// <remarks>
    /// <para>
    /// Requires <see cref="DesktopWindowCapabilities.WindowPlacement"/>. The window ends at the bounds that
    /// <see cref="DesktopWindowState.Placement"/> reports, also when it was minimized while maximized, where Windows and the
    /// window managers would return it maximized. <see cref="FocusAsync"/> keeps that platform behaviour. A hidden
    /// window stays hidden and shows at its normal bounds when it is next shown.
    /// </para>
    /// <para>
    /// Windows restores a visible maximized window without activating it, but activates a window that it brings back from
    /// minimized. On Linux a minimized window is restored on X11; Wayland tells no application that its window is
    /// minimized, and GTK 4 has no call to restore one, so there <see cref="FocusAsync"/> brings it back.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The window is a <see cref="DesktopWindowOptions.Kiosk"/> window, which stays full-screen.</exception>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="DesktopException">The window lacks the required capability (<c>window-capability-unavailable</c>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask RestoreAsync(CancellationToken cancellationToken = default)
    {
        RequireNotKiosk();
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.WindowPlacement,
            () => _engine.ShowNormalAsync(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Gives the open window a placement, as <see cref="DesktopWindowOptions.Placement"/> gives a window that opens: the
    /// bounds it restores to and whether it is maximized or full-screen.
    /// </summary>
    /// <param name="placement">
    /// <para>
    /// The placement, in the units of <see cref="DesktopWindowState.Placement"/>, so a placement read there, saved and passed
    /// back, places the window where it was:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="DesktopWindowPlacement.Width"/> and <see cref="DesktopWindowPlacement.Height"/> use the
    /// units of <see cref="ResizeAsync"/>: device-independent pixels of the outer frame on Windows, the content size in points
    /// on macOS and in logical pixels on Linux. Zero or less keeps that dimension.</description></item>
    /// <item><description><see cref="DesktopWindowPlacement.X"/> and <see cref="DesktopWindowPlacement.Y"/> are those of
    /// <see cref="DesktopWindowState.X"/> and <see cref="DesktopWindowState.Y"/> (physical pixels on Windows). Both
    /// <see langword="null"/> keep the window's position. On macOS a placement whose
    /// <see cref="DesktopWindowPlacement.Origin"/> is <see langword="null"/> or
    /// <see cref="DesktopCoordinateOrigin.BottomLeft"/>, saved by Runic 0.11 or earlier, converts as at open.</description></item>
    /// </list>
    /// </param>
    /// <param name="cancellationToken">Cancels waiting for the window and the queued native operation.</param>
    /// <returns>
    /// A task that completes when the window has been asked to take the placement; <see cref="StateChanged"/> reports it,
    /// and <see cref="DesktopWindowState.Placement"/> then reports the placement back.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Requires <see cref="DesktopWindowCapabilities.WindowPlacement"/>. Unlike <see cref="SetBoundsAsync"/>, which takes the
    /// exact native frame of a normal window, this sets the bounds a maximized, minimized, full-screen or hidden window
    /// restores to: a maximized window stays maximized with new restored bounds unless
    /// <see cref="DesktopWindowPlacement.Maximized"/> is <see langword="false"/>, a minimized one stays minimized and a hidden
    /// one hidden, and each takes the placement's state when it is next shown or restored.
    /// </para>
    /// <para>
    /// The placement is kept on a monitor that exists now, as at open: bounds off every monitor's work area move onto the
    /// work area they overlap most, or the nearest one, and bounds larger than it shrink to fit. On Windows the width and
    /// height convert with the scale of that monitor. <see cref="DesktopWindowState.Placement"/> reports the bounds after
    /// this fit.
    /// </para>
    /// <para>
    /// With a side panel open (<see cref="SidePanel"/>), the placement is that of the window's page, as
    /// <see cref="DesktopWindowState.Placement"/> reports it: the window grows by the panel on its edge, so the page takes
    /// the placement's bounds.
    /// </para>
    /// <para>
    /// Windows activates a window that the placement maximizes or brings back from minimized; a window that stays normal or
    /// maximized keeps its activation. On Wayland a placement with a position fails as a whole and leaves the window as it
    /// is: pass one without <see cref="DesktopWindowPlacement.X"/> and <see cref="DesktopWindowPlacement.Y"/>, as
    /// <see cref="DesktopWindowState.Placement"/> reports there.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Only one of <see cref="DesktopWindowPlacement.X"/> and <see cref="DesktopWindowPlacement.Y"/> is set, or
    /// <see cref="DesktopWindowPlacement.Origin"/> is not a defined value.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The window is a <see cref="DesktopWindowOptions.Kiosk"/> window, which stays full-screen, and
    /// <see cref="DesktopWindowPlacement.FullScreen"/> is <see langword="false"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="DesktopException">
    /// The window lacks the required capability (<c>window-capability-unavailable</c>), or cannot be positioned
    /// (<c>window-position-unavailable</c>).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask SetPlacementAsync(DesktopWindowPlacement placement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (placement.X is null != placement.Y is null)
        {
            throw new ArgumentException("Set both X and Y of the placement, or neither to keep the window's position.", nameof(placement));
        }
        if (placement.Origin is { } origin && !Enum.IsDefined(origin))
        {
            throw new ArgumentException($"The placement's origin {origin} is not a DesktopCoordinateOrigin value.", nameof(placement));
        }
        if (!placement.FullScreen) RequireNotKiosk();
        var sanitized = placement with
        {
            Width = Math.Min(Math.Max(0, placement.Width), Internal.WindowPlacementClamp.MaximumDimension),
            Height = Math.Min(Math.Max(0, placement.Height), Internal.WindowPlacementClamp.MaximumDimension),
        };
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.WindowPlacement,
            () => _engine.SetPlacementAsync(sanitized, cancellationToken), cancellationToken);
    }

    // A kiosk window stays full-screen (SetFullScreenAsync refuses the same).
    private void RequireNotKiosk()
    {
        if (IsOpen && _engine.IsKiosk)
        {
            throw new InvalidOperationException("A kiosk window stays full-screen.");
        }
    }
}
