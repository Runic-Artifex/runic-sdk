using System.Diagnostics.CodeAnalysis;

namespace Runic.Desktop;

// sdk#900, W4b: the primitive that keeps a native window open while the application's work drains
// (window-api-0.19/DESIGN.md §4.3, §4.6; D064.15, D064.16), experimental under RUNICDESK105 for one minor (D064.20).

/// <summary>Why a window's close drain (<see cref="DesktopWindowOptions.CloseDrainAsync"/>) runs.</summary>
/// <remarks>
/// <para>This set is open: a minor release may add values, so handle an unknown one as <see cref="User"/>.</para>
/// <para>Experimental (<c>RUNICDESK105</c>); see <c>docs/experimental.md</c>.</para>
/// </remarks>
[Experimental("RUNICDESK105", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public enum DesktopCloseReason
{
    /// <summary>The user closed the window: its close button, Alt+F4, Command and W, the window menu or the window manager.</summary>
    User = 0,

    /// <summary>The application asked with <see cref="DesktopWindow.RequestCloseAsync"/>.</summary>
    Request = 1,

    /// <summary>A quit (<see cref="DesktopHostOptions.ConfirmQuitAsync"/>, such as Command and Q) asked the window's close guard.</summary>
    Quit = 2,

    /// <summary>
    /// The host shuts down: Ctrl+C, SIGTERM, SIGHUP, a closed Windows console, or a quit once every guard agreed. The drain
    /// ends at <see cref="DesktopHostOptions.ShutdownDrainTimeout"/> at the latest.
    /// </summary>
    Shutdown = 3,
}

/// <summary>What a window's close drain (<see cref="DesktopWindowOptions.CloseDrainAsync"/>) is told about its close.</summary>
/// <param name="Reason">Why the drain runs. A drain runs once per window: a later reason, such as a shutdown during the user's close, joins it.</param>
/// <remarks>Experimental (<c>RUNICDESK105</c>); see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK105", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record DesktopCloseDrainContext(DesktopCloseReason Reason);
