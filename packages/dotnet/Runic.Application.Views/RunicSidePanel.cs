using System.Diagnostics.CodeAnalysis;

namespace Runic.Application.Views;

// sdk#864, P5: the side panel for ViewModels (side-panel/DESIGN.md §3.2; D062.1–D062.10), experimental under
// RUNICDESK104 (D062.7). The types mirror Runic.Desktop's DesktopSidePanel*, which this package does not reference.

/// <summary>
/// What <see cref="IRunicWindowPresentation.OpenSidePanelAsync"/> opens: a page of the Window shown beside its page, in a
/// strip on one edge by which the Window grows.
/// </summary>
/// <remarks>Experimental (<c>RUNICDESK104</c>); see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicSidePanelOptions
{
    /// <summary>The smallest <see cref="Width"/>, in device-independent pixels.</summary>
    public const int MinimumWidth = 120;

    /// <summary>
    /// Gets the edge of the Window that grows: <see cref="RunicSidePanelEdge.End"/>, the default, is the right edge of a
    /// left-to-right Window and the left edge of a right-to-left one.
    /// </summary>
    public RunicSidePanelEdge Edge { get; init; } = RunicSidePanelEdge.End;

    /// <summary>
    /// Gets the panel's width in the Window's units: device-independent pixels on Windows, points on macOS, logical
    /// pixels on Linux. At least <see cref="MinimumWidth"/>; a width over half the monitor's work area is refused with
    /// <see cref="RunicSidePanelRefusal.NoRoom"/>.
    /// </summary>
    public required int Width { get; init; }

    /// <summary>
    /// Gets the page the panel shows: a route of the application's own frontend, relative to the address the Window's
    /// page opened with, such as <c>side-panel</c>, <c>/side-panel</c>, <c>side-panel?tab=log</c> or <c>#/side-panel</c>
    /// for a hash router.
    /// </summary>
    /// <remarks>
    /// The panel's page is a second connection of the same Window, so it binds to the Window's ViewModel tree, for example
    /// to a <c>SidePanel</c> property of the root ViewModel. It does not follow or drive the Window's navigation path. An
    /// absolute URL, a scheme or a path that leaves the page's directory (<c>..</c>) throws
    /// <see cref="ArgumentException"/>.
    /// </remarks>
    public required string Path { get; init; }

    /// <summary>
    /// Gets how long the panel's page may take to load before the Window grows; ten seconds by default. A page that does
    /// not load in time refuses the panel with <see cref="RunicSidePanelRefusal.LoadFailed"/>.
    /// </summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(10);

    // The checks every presentation makes before it answers, so a ViewModel sees the same exceptions in a test as on the
    // desktop. Runic.Desktop also checks that the resolved address stays inside the surface.
    internal static void Validate(RunicSidePanelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateEdge(options.Edge);
        ValidateWidth(options.Width);
        if (options.LoadTimeout <= TimeSpan.Zero || options.LoadTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), options.LoadTimeout, "The load timeout must be positive.");
        if (!IsRelativePath(options.Path))
            throw new ArgumentException($"The side panel's path '{options.Path}' must be a relative path of the Window's page.", nameof(options));
    }

    internal static void ValidateEdge(RunicSidePanelEdge edge)
    {
        if (!Enum.IsDefined(edge)) throw new ArgumentOutOfRangeException(nameof(edge), edge, "The edge is not defined.");
    }

    internal static void ValidateWidth(int width)
    {
        if (width < MinimumWidth)
            throw new ArgumentOutOfRangeException(nameof(width), width, $"A side panel is at least {MinimumWidth} device-independent pixels wide.");
    }

    private static bool IsRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var relative = path.TrimStart('/');
        var pathPart = relative.Split('?', '#')[0];
        return !relative.Contains('\\') && !path.StartsWith("//", StringComparison.Ordinal) && !pathPart.Contains(':')
            && !Uri.UnescapeDataString(pathPart).Split('/').Any(static segment => segment is "." or "..");
    }
}

/// <summary>An edge of a Window, for <see cref="RunicSidePanelOptions.Edge"/> and <see cref="IRunicSidePanel.MoveToEdgeAsync"/>.</summary>
/// <remarks>Experimental (<c>RUNICDESK104</c>).</remarks>
[Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public enum RunicSidePanelEdge
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

/// <summary>The outcome of <see cref="IRunicWindowPresentation.OpenSidePanelAsync"/>: <see cref="Opened"/> or <see cref="Refused"/>.</summary>
/// <remarks>Closed: these two cases are all there are. Experimental (<c>RUNICDESK104</c>).</remarks>
[Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public abstract record RunicSidePanelResult
{
    private RunicSidePanelResult()
    {
    }

    /// <summary>The panel opened and the Window grew.</summary>
    /// <param name="Panel">The open panel, also <see cref="IRunicWindowPresentation.SidePanel"/>.</param>
    public sealed record Opened(IRunicSidePanel Panel) : RunicSidePanelResult;

    /// <summary>The panel did not open, and the Window did not change.</summary>
    /// <param name="Reason">Why; show the content another way, such as an overlay of the Window's page.</param>
    public sealed record Refused(RunicSidePanelRefusal Reason) : RunicSidePanelResult;
}

/// <summary>Why a side panel did not open or change.</summary>
/// <remarks>
/// Open: a minor release may add values; handle an unknown value as <see cref="Unsupported"/>. Experimental
/// (<c>RUNICDESK104</c>).
/// </remarks>
[Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public enum RunicSidePanelRefusal
{
    /// <summary>
    /// The Window's host has no side panel: an installed browser or a serve-only application, a custom window host or
    /// WPF, CS-WebUI, a hosted application's Window or region, and, until their engines support it, macOS and Linux.
    /// </summary>
    Unsupported,

    /// <summary>The Window is maximized; widening it would leave the screen.</summary>
    Maximized,

    /// <summary>The Window is full-screen or a kiosk.</summary>
    FullScreen,

    /// <summary>The desktop arranged the Window against a screen edge or in a layout.</summary>
    Snapped,

    /// <summary>The Window is minimized or hidden.</summary>
    Minimized,

    /// <summary>
    /// The Window, grown by the panel, would leave its monitor's work area, or the panel is wider than half of it. The
    /// Window is not moved to make room.
    /// </summary>
    NoRoom,

    /// <summary>The left edge needs the Window to move, and the platform lets no application position its windows (Wayland).</summary>
    PositionUnavailable,

    /// <summary>The Window already shows a side panel (<see cref="IRunicWindowPresentation.SidePanel"/>).</summary>
    AlreadyOpen,

    /// <summary>The panel's page did not load within <see cref="RunicSidePanelOptions.LoadTimeout"/>, or failed to load.</summary>
    LoadFailed,

    /// <summary>The Window, or the panel asked to change, has closed.</summary>
    Closed,
}

/// <summary>Why a side panel closed, for <see cref="IRunicSidePanel.Closed"/>.</summary>
/// <remarks>Open: a minor release may add values. Experimental (<c>RUNICDESK104</c>).</remarks>
[Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public enum RunicSidePanelCloseReason
{
    /// <summary>The application closed it (<see cref="IRunicSidePanel.CloseAsync"/>), or its page did (<c>window.close()</c>).</summary>
    Closed,

    /// <summary>The Window closed.</summary>
    WindowClosed,

    /// <summary>The panel's page crashed twice, or the browser process ended.</summary>
    Crashed,
}

/// <summary>Provides the reason for <see cref="IRunicSidePanel.Closed"/>.</summary>
/// <remarks>Experimental (<c>RUNICDESK104</c>).</remarks>
[Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class RunicSidePanelClosedEventArgs : EventArgs
{
    /// <summary>Creates event data for <paramref name="reason"/>.</summary>
    /// <param name="reason">Why the panel closed.</param>
    public RunicSidePanelClosedEventArgs(RunicSidePanelCloseReason reason) => Reason = reason;

    /// <summary>Gets why the panel closed.</summary>
    public RunicSidePanelCloseReason Reason { get; }
}

/// <summary>
/// A Window's side panel, which <see cref="IRunicWindowPresentation.OpenSidePanelAsync"/> opened: a page of the Window
/// beside its page, on one edge by which the Window grew.
/// </summary>
/// <remarks>
/// <para>
/// The Window's page keeps its size and its place on the screen while the panel opens, closes, moves to the other edge or
/// changes its width. The panel's page is a second connection of the Window and shares its ViewModel tree, so the
/// ViewModel that opened the panel also serves it.
/// </para>
/// <para>
/// Disposing it closes it. Experimental (<c>RUNICDESK104</c>); see <c>docs/experimental.md</c>.
/// </para>
/// </remarks>
[Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public interface IRunicSidePanel : IAsyncDisposable
{
    /// <summary>Gets the edge the panel is on: <see cref="RunicSidePanelEdge.Left"/> or <see cref="RunicSidePanelEdge.Right"/>.</summary>
    RunicSidePanelEdge Edge { get; }

    /// <summary>Gets the panel's width, in the units of <see cref="RunicSidePanelOptions.Width"/>.</summary>
    int Width { get; }

    /// <summary>Gets whether the panel is open: <see langword="false"/> once it closed, for whatever reason.</summary>
    bool IsOpen { get; }

    /// <summary>Occurs once after the panel has closed, whatever closed it, in a turn of the Window's model context.</summary>
    /// <remarks>
    /// <see cref="IRunicWindowPresentation.SidePanel"/> is <see langword="null"/> by then. A handler added after the panel
    /// closed is not called; check <see cref="IsOpen"/> after adding one.
    /// </remarks>
    event EventHandler<RunicSidePanelClosedEventArgs>? Closed;

    /// <summary>Moves the panel to another edge: the Window moves by the panel's width, and its page stays where it is.</summary>
    /// <param name="edge">The new edge; <see cref="RunicSidePanelEdge.Start"/> and <see cref="RunicSidePanelEdge.End"/> follow the Window's layout direction.</param>
    /// <param name="cancellationToken">Cancels waiting for the Window.</param>
    /// <returns>
    /// <see langword="null"/> when the panel is on that edge now, also when it already was; otherwise why it stayed: the
    /// Window's state, room, position (<see cref="RunicSidePanelRefusal.PositionUnavailable"/>) or
    /// <see cref="RunicSidePanelRefusal.Closed"/> once the panel closed.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="edge"/> is not a defined value.</exception>
    ValueTask<RunicSidePanelRefusal?> MoveToEdgeAsync(RunicSidePanelEdge edge, CancellationToken cancellationToken = default);

    /// <summary>Changes the panel's width: the Window grows or shrinks on the panel's edge by the difference.</summary>
    /// <param name="width">The new width, at least <see cref="RunicSidePanelOptions.MinimumWidth"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for the Window.</param>
    /// <returns>
    /// <see langword="null"/> when the panel has the width now; otherwise why it kept its width, as for
    /// <see cref="MoveToEdgeAsync"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is smaller than <see cref="RunicSidePanelOptions.MinimumWidth"/>.</exception>
    ValueTask<RunicSidePanelRefusal?> SetWidthAsync(int width, CancellationToken cancellationToken = default);

    /// <summary>Moves the keyboard focus into the panel's page; does nothing once the panel closed.</summary>
    /// <param name="cancellationToken">Cancels waiting for the Window.</param>
    /// <returns>A task that completes when the panel has the focus.</returns>
    ValueTask FocusAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the panel: its page disconnects and the Window shrinks back. Closing a closed panel does nothing.</summary>
    /// <param name="cancellationToken">Cancels waiting for the Window; the close itself proceeds.</param>
    /// <returns>A task that completes when the panel has closed.</returns>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}
