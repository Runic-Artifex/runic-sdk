using System.Diagnostics.CodeAnalysis;
using Runic.Desktop.Internal;

namespace Runic.Desktop;

/// <summary>What <see cref="DesktopWindow.OpenSidePanelAsync"/> opens: a page of the window's surface beside its page.</summary>
/// <remarks>Experimental (<c>RUNICDESK104</c>); see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record DesktopSidePanelOptions
{
    /// <summary>The smallest <see cref="Width"/>, in device-independent pixels.</summary>
    public const int MinimumWidth = 120;

    /// <summary>
    /// Gets the edge of the window that grows: <see cref="DesktopWindowEdge.End"/>, the default, is the right edge of a
    /// left-to-right window and the left edge of a right-to-left one (<see cref="DesktopWindow.LayoutDirection"/>).
    /// </summary>
    public DesktopWindowEdge Edge { get; init; } = DesktopWindowEdge.End;

    /// <summary>
    /// Gets the panel's width in the units of <see cref="DesktopWindow.ResizeAsync"/>: device-independent pixels on
    /// Windows, points on macOS, logical pixels on Linux. At least <see cref="MinimumWidth"/>; a width over half the
    /// monitor's work area is refused with <see cref="DesktopSidePanelRefusal.NoRoom"/>.
    /// </summary>
    /// <remarks>
    /// The window converts it once into native units with its scale and grows by exactly that many, so closing the panel
    /// gives the window back its exact bounds (<see cref="DesktopSidePanel.CloseAsync"/>).
    /// </remarks>
    public required int Width { get; init; }

    /// <summary>
    /// Gets the page the panel shows: a path relative to the address the window's page opened with, such as
    /// <c>side-panel</c>, <c>/side-panel</c> (the same: a leading <c>/</c> stays inside the surface),
    /// <c>side-panel?tab=log</c> or <c>#/side-panel</c> for a hash router.
    /// </summary>
    /// <remarks>
    /// The panel's page is another page of the same surface: same origin, same profile and storage, and a connection of
    /// its own to the same window, so it binds to the window's ViewModel tree. It does not follow or drive the window's
    /// navigation path. An absolute URL, a scheme or a path that leaves the surface (<c>..</c>) is refused with
    /// <see cref="ArgumentException"/>.
    /// </remarks>
    public required string Path { get; init; }

    /// <summary>
    /// Gets how long the panel's page may take to load before the window grows; ten seconds by default. A page that
    /// does not load in time refuses the panel with <see cref="DesktopSidePanelRefusal.LoadFailed"/>, and the window does
    /// not change.
    /// </summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>The outcome of <see cref="DesktopWindow.OpenSidePanelAsync"/>: <see cref="Opened"/> or <see cref="Refused"/>.</summary>
/// <remarks>Closed: these two cases are all there are. Experimental (<c>RUNICDESK104</c>).</remarks>
[Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public abstract record DesktopSidePanelResult
{
    private DesktopSidePanelResult()
    {
    }

    /// <summary>The panel opened and the window grew.</summary>
    /// <param name="Panel">The open panel, also <see cref="DesktopWindow.SidePanel"/>.</param>
    public sealed record Opened(DesktopSidePanel Panel) : DesktopSidePanelResult;

    /// <summary>The panel did not open, and the window did not change.</summary>
    /// <param name="Reason">Why; the application shows its content another way, such as an overlay.</param>
    public sealed record Refused(DesktopSidePanelRefusal Reason) : DesktopSidePanelResult;
}

/// <summary>Why a side panel did not open or change.</summary>
/// <remarks>
/// Open: a minor release may add values; handle an unknown value as <see cref="Unsupported"/>. Experimental
/// (<c>RUNICDESK104</c>).
/// </remarks>
[Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public enum DesktopSidePanelRefusal
{
    /// <summary>
    /// The presentation has no native window of its own to grow, or its host has no side panel: an installed browser, a
    /// custom window host (<see cref="IDesktopWindowHostFactory"/>, also WPF's <c>RunicWebView</c>), a hosted
    /// application's window (<c>runic-host/1</c>) or region, a surface that admits several clients, and, until its
    /// engine supports it, the Linux windows.
    /// </summary>
    Unsupported,

    /// <summary>The window is maximized; widening it would leave the screen.</summary>
    Maximized,

    /// <summary>The window is full-screen or a kiosk.</summary>
    FullScreen,

    /// <summary>The desktop arranged the window against a screen edge or in a layout (<see cref="DesktopWindowState.IsSnapped"/>).</summary>
    Snapped,

    /// <summary>The window is minimized or hidden.</summary>
    Minimized,

    /// <summary>
    /// The window's visible frame, grown by the panel, would leave its monitor's work area, or the panel is wider than
    /// half of it. The window is not moved to make room, since its page would move on the screen.
    /// </summary>
    NoRoom,

    /// <summary>The left edge needs the window to move, and the platform lets no application position its windows (Wayland).</summary>
    PositionUnavailable,

    /// <summary>The window already shows a side panel (<see cref="DesktopWindow.SidePanel"/>).</summary>
    AlreadyOpen,

    /// <summary>The panel's page did not load within <see cref="DesktopSidePanelOptions.LoadTimeout"/>, or failed to load.</summary>
    LoadFailed,
}

/// <summary>Why a side panel closed, for <see cref="DesktopSidePanel.Closed"/>.</summary>
/// <remarks>Open: a minor release may add values. Experimental (<c>RUNICDESK104</c>).</remarks>
[Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public enum DesktopSidePanelCloseReason
{
    /// <summary>The application closed it (<see cref="DesktopSidePanel.CloseAsync"/>), or its page did (<c>window.close()</c>).</summary>
    Closed,

    /// <summary>The window closed.</summary>
    WindowClosed,

    /// <summary>The panel's page crashed twice, or the browser process ended.</summary>
    Crashed,
}

/// <summary>Provides the reason for <see cref="DesktopSidePanel.Closed"/>.</summary>
/// <remarks>Experimental (<c>RUNICDESK104</c>).</remarks>
[Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class DesktopSidePanelClosedEventArgs : EventArgs
{
    /// <summary>Creates event data for <paramref name="reason"/>.</summary>
    /// <param name="reason">Why the panel closed.</param>
    public DesktopSidePanelClosedEventArgs(DesktopSidePanelCloseReason reason) => Reason = reason;

    /// <summary>Gets why the panel closed.</summary>
    public DesktopSidePanelCloseReason Reason { get; }
}

/// <summary>
/// A page of a window's surface shown beside the window's page, in a strip on one edge by which the window grew
/// (<see cref="DesktopWindow.OpenSidePanelAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// The window's page keeps its size and its position on the screen while the panel opens, closes, moves to the other edge
/// or changes its width: the window grows or shrinks on the panel's edge in one native operation. When the user resizes
/// the window the panel keeps its width and the page takes the rest, also when the window is maximized or full-screen.
/// </para>
/// <para>
/// Disposing it closes it. Experimental (<c>RUNICDESK104</c>); see <c>docs/experimental.md</c>.
/// </para>
/// </remarks>
[Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class DesktopSidePanel : IAsyncDisposable
{
    private readonly DesktopWindow _window;
    private IWebUiSidePanel? _native;
    private readonly object _gate = new();
    private EventHandler<DesktopSidePanelClosedEventArgs>? _closedHandlers;
    private int _closed;

    // Created before the host opens the panel, so the host can report a close at any time (OnNativeClosed); the host's
    // panel is attached once it opened.
    internal DesktopSidePanel(DesktopWindow window) => _window = window;

    internal void Attach(IWebUiSidePanel native) => Volatile.Write(ref _native, native);

    internal bool IsAttached => Volatile.Read(ref _native) is not null;

    private IWebUiSidePanel Native => Volatile.Read(ref _native) ?? throw new ObjectDisposedException(nameof(DesktopSidePanel));

    /// <summary>Gets the edge the panel is on: <see cref="DesktopWindowEdge.Left"/> or <see cref="DesktopWindowEdge.Right"/>.</summary>
    public DesktopWindowEdge Edge => Native.IsLeft ? DesktopWindowEdge.Left : DesktopWindowEdge.Right;

    /// <summary>Gets the panel's width in device-independent pixels (points on macOS, logical pixels on Linux).</summary>
    public int Width => Native.Width;

    /// <summary>Gets whether the panel is open: <see langword="false"/> once it closed, for whatever reason.</summary>
    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    /// <summary>Occurs once after the panel has closed, whatever closed it.</summary>
    /// <remarks>
    /// Raised on the thread pool, never on the window's native thread, after <see cref="IsOpen"/> became
    /// <see langword="false"/> and the window shrank. A handler added after the panel closed is not called; check
    /// <see cref="IsOpen"/> after adding one. A handler that throws is logged (event 3041,
    /// <c>SidePanelClosedHandlerFailed</c>) and does not stop the other handlers.
    /// </remarks>
    public event EventHandler<DesktopSidePanelClosedEventArgs>? Closed
    {
        add { lock (_gate) _closedHandlers += value; }
        remove { lock (_gate) _closedHandlers -= value; }
    }

    /// <summary>Moves the panel to the other edge: the window moves by the panel's width, and its page stays where it is on the screen.</summary>
    /// <param name="edge">The new edge; <see cref="DesktopWindowEdge.Start"/> and <see cref="DesktopWindowEdge.End"/> follow <see cref="DesktopWindow.LayoutDirection"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for the window.</param>
    /// <returns>
    /// <see langword="null"/> when the panel is on that edge now, also when it already was; otherwise why it stayed, for
    /// the same reasons as <see cref="DesktopWindow.OpenSidePanelAsync"/>: the window is maximized, full-screen,
    /// snapped or minimized, the moved frame would leave the work area, or the platform cannot position windows.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="edge"/> is not a defined value.</exception>
    /// <exception cref="ObjectDisposedException">The panel is closed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask<DesktopSidePanelRefusal?> MoveToEdgeAsync(DesktopWindowEdge edge, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(edge)) throw new ArgumentOutOfRangeException(nameof(edge));
        ObjectDisposedException.ThrowIf(!IsOpen, this);
        var left = DesktopWindowBounds.ResolvesToLeft(edge, _window.LayoutDirection);
        return left == Native.IsLeft ? ValueTask.FromResult<DesktopSidePanelRefusal?>(null) : Native.MoveAsync(left, cancellationToken);
    }

    /// <summary>
    /// Changes the panel's width: the window grows or shrinks on the panel's edge by the difference, and its page keeps
    /// its size and position. Applications that let the user resize the panel offer their own control and call this.
    /// </summary>
    /// <param name="width">The new width, in the units of <see cref="DesktopSidePanelOptions.Width"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for the window.</param>
    /// <returns>
    /// <see langword="null"/> when the panel has the width now; otherwise why it kept its width, for the same reasons as
    /// <see cref="MoveToEdgeAsync"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is smaller than <see cref="DesktopSidePanelOptions.MinimumWidth"/>.</exception>
    /// <exception cref="ObjectDisposedException">The panel is closed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask<DesktopSidePanelRefusal?> SetWidthAsync(int width, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, DesktopSidePanelOptions.MinimumWidth);
        ObjectDisposedException.ThrowIf(!IsOpen, this);
        return width == Native.Width ? ValueTask.FromResult<DesktopSidePanelRefusal?>(null) : Native.SetWidthAsync(width, cancellationToken);
    }

    /// <summary>Moves the keyboard focus into the panel's page, to its focused element or its first one.</summary>
    /// <param name="cancellationToken">Cancels waiting for the window.</param>
    /// <returns>A task that completes when the panel's view has the focus.</returns>
    /// <remarks>
    /// Tab past the last element of the window's page focuses the panel's first one, and Shift+Tab before the first
    /// focuses the panel's last one; the panel hands the focus back the same way. This does not activate the window.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The panel is closed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask FocusAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(!IsOpen, this);
        return Native.FocusAsync(cancellationToken);
    }

    /// <summary>Closes the panel: its page disconnects and the window shrinks back.</summary>
    /// <param name="cancellationToken">Cancels waiting for the window; the close itself proceeds.</param>
    /// <returns>A task that completes when the panel has closed.</returns>
    /// <remarks>
    /// When the user did not move or resize the window while the panel was open, the window gets back exactly the bounds
    /// it had before; otherwise its docked edge moves back by the panel's width. A maximized or full-screen window keeps
    /// its frame, the page takes the whole window, and the bounds it restores to shrink by the panel's width. Closing a
    /// closed panel does nothing.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (!IsOpen) return;
        try
        {
            await Native.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (!_window.IsOpen)
        {
            // The window closed meanwhile, and the panel with it.
        }
        // The host reports the close (OnNativeClosed); a window that closed meanwhile reported it already.
        OnNativeClosed(DesktopSidePanelCloseReason.Closed);
    }

    /// <summary>Closes the panel, like <see cref="CloseAsync"/>.</summary>
    /// <returns>A task that completes when the panel has closed.</returns>
    public ValueTask DisposeAsync() => CloseAsync(CancellationToken.None);

    // The host closed the panel, on any thread (its window thread included): once, with the first reason.
    internal void OnNativeClosed(DesktopSidePanelCloseReason reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _window.OnSidePanelClosed(this);
        ThreadPool.UnsafeQueueUserWorkItem(static state => state.Panel.RaiseClosed(state.Reason), (Panel: this, Reason: reason), preferLocal: false);
    }

    private void RaiseClosed(DesktopSidePanelCloseReason reason)
    {
        EventHandler<DesktopSidePanelClosedEventArgs>? handlers;
        lock (_gate) handlers = _closedHandlers;
        if (handlers is null) return;
        var args = new DesktopSidePanelClosedEventArgs(reason);
        foreach (EventHandler<DesktopSidePanelClosedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (_window.Logger is { } logger)
                    DesktopLog.SidePanelClosedHandlerFailed(logger, error, DesktopLog.ErrorType(error));
                else
                    System.Diagnostics.Trace.TraceError($"A DesktopSidePanel.Closed handler failed with {error.GetType().FullName}.");
            }
        }
    }
}
