using System.Diagnostics.CodeAnalysis;
using Runic.Desktop.Internal;

namespace Runic.Desktop;

// sdk#864, P2: the side panel (side-panel/DESIGN.md §3.1; D062.3–D062.10), experimental under RUNICDESK104 (D062.7).
public sealed partial class DesktopWindow
{
    // Serializes opening the panel, which waits for its page to load, without holding the surface's window gate meanwhile.
    private readonly SemaphoreSlim _sidePanelGate = new(1, 1);
    private DesktopSidePanel? _sidePanel;

    /// <summary>Gets the open side panel, or <see langword="null"/>.</summary>
    /// <remarks>
    /// Set when <see cref="OpenSidePanelAsync"/> opened one, and <see langword="null"/> again once it closed, before its
    /// <see cref="DesktopSidePanel.Closed"/> event. Experimental (<c>RUNICDESK104</c>).
    /// </remarks>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public DesktopSidePanel? SidePanel => Volatile.Read(ref _sidePanel);

    /// <summary>
    /// Opens a page of this window's surface beside the window's page, growing the window by the panel's width on one
    /// edge, in one native operation.
    /// </summary>
    /// <param name="options">The edge, width and page of the panel.</param>
    /// <param name="cancellationToken">Cancels waiting for the panel's page; a cancelled open leaves the window unchanged.</param>
    /// <returns>
    /// <see cref="DesktopSidePanelResult.Opened"/> with the panel, or <see cref="DesktopSidePanelResult.Refused"/> with the
    /// reason, after which the window is unchanged and the application shows the content another way, such as in an
    /// overlay of its page.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The window creates the panel's view hidden, from its page's profile, loads <see cref="DesktopSidePanelOptions.Path"/>
    /// and then grows: on the right edge by widening, on the left edge by moving left and widening, so the page keeps its
    /// size and its position on the screen and is not laid out again. The panel's page is a second connection of the
    /// window, with a credential of its own, so it binds to the same ViewModel tree as the page. It is
    /// not granted <c>navigation-path/1</c>: its address and history are its own, and its Back does not navigate the
    /// window. It may show hosted regions. Files dropped on it are refused. <c>window.close()</c> in it closes the panel,
    /// never the window, and its title is not the window's.
    /// </para>
    /// <para>
    /// While the panel is open, <see cref="DesktopWindowState.Bounds"/> and <see cref="DesktopWindowState.VisibleBounds"/>
    /// are the whole window, panel included, while <see cref="DesktopWindowState.Placement"/> excludes the panel: a window
    /// restored from it opens at its page's size, and the application decides whether to open the panel again. The
    /// minimum width grows by the panel's. The user may maximize the window or make it full-screen: the panel keeps its
    /// width and the page takes the rest.
    /// </para>
    /// <para>
    /// The panel opens only in a window of its own that a built-in host shows: WebView2 on Windows and WKWebView on
    /// macOS. Every other presentation refuses with <see cref="DesktopSidePanelRefusal.Unsupported"/>, as Linux does
    /// for now. The
    /// other refusals are the window's state (<see cref="CheckSidePanel"/>), a panel that is open already, and a page that
    /// did not load. Experimental (<c>RUNICDESK104</c>); see <c>docs/experimental.md</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The width is smaller than <see cref="DesktopSidePanelOptions.MinimumWidth"/>, the edge is not defined, or the load
    /// timeout is not positive.
    /// </exception>
    /// <exception cref="ArgumentException">The path is empty, absolute, or leaves the surface.</exception>
    /// <exception cref="ObjectDisposedException">The window is closed or was replaced.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public async ValueTask<DesktopSidePanelResult> OpenSidePanelAsync(DesktopSidePanelOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateSidePanel(options.Edge, options.Width);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Path, nameof(options));
        if (options.LoadTimeout <= TimeSpan.Zero || options.LoadTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.LoadTimeout, "The load timeout must be positive.");
        }
        ObjectDisposedException.ThrowIf(!IsOpen, this);
        await _sidePanelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(!IsOpen, this);
            if (Browser != BrowserKind.Embedded || !_engine.SupportsSidePanel) return Refused(DesktopSidePanelRefusal.Unsupported);
            if (SidePanel is not null) return Refused(DesktopSidePanelRefusal.AlreadyOpen);
            var url = _engine.SidePanelUrl(options.Path)
                ?? throw new ArgumentException($"The side panel's path '{options.Path}' must be a relative path inside the surface.", nameof(options));
            var left = DesktopWindowBounds.ResolvesToLeft(options.Edge, LayoutDirection);
            // From the last report; the host checks again on its thread, before and after loading the panel's page.
            if (_engine.CheckSidePanel(left, options.Width) is { } refusal) return Refused(refusal);

            _surface.OpenSidePanelSlot();
            var panel = new DesktopSidePanel(this);
            WebUiSidePanelOpening opening;
            try
            {
                var script = _surface.SidePanelDocumentStartScript
                    ?? throw new InvalidOperationException("The surface's side panel slot closed while the panel opened.");
                opening = await _engine.OpenSidePanelAsync(
                    new WebUiSidePanelRequest(url, script, left, options.Width, options.LoadTimeout, panel.OnNativeClosed),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException) when (!IsOpen)
            {
                _surface.CloseSidePanelSlot();
                throw new ObjectDisposedException(nameof(DesktopWindow), "The window closed while the side panel opened.");
            }
            catch
            {
                _surface.CloseSidePanelSlot();
                throw;
            }
            if (opening.Panel is not { } native)
            {
                _surface.CloseSidePanelSlot();
                return Refused(opening.Refusal ?? DesktopSidePanelRefusal.Unsupported);
            }
            panel.Attach(native);
            Volatile.Write(ref _sidePanel, panel);
            // The window closed while the panel opened: the host reported the close before the panel was published.
            if (!panel.IsOpen) OnSidePanelClosed(panel);
            return new DesktopSidePanelResult.Opened(panel);
        }
        finally
        {
            _sidePanelGate.Release();
        }

        static DesktopSidePanelResult Refused(DesktopSidePanelRefusal reason) => new DesktopSidePanelResult.Refused(reason);
    }

    /// <summary>
    /// Returns whether <see cref="OpenSidePanelAsync"/> would open a panel now, from the window's last reported
    /// <see cref="State"/> and <see cref="Activity"/>: <see langword="null"/> when it would, otherwise the refusal it would
    /// give.
    /// </summary>
    /// <param name="edge">The edge; <see cref="DesktopWindowEdge.Start"/> and <see cref="DesktopWindowEdge.End"/> follow <see cref="LayoutDirection"/>.</param>
    /// <param name="width">The panel's width, as <see cref="DesktopSidePanelOptions.Width"/>.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Cheap and synchronous, for showing or hiding a command that opens the panel. It checks everything except whether
    /// the panel's page loads: <see cref="DesktopSidePanelRefusal.Unsupported"/>, <see cref="DesktopSidePanelRefusal.AlreadyOpen"/>,
    /// the window's state (minimized or hidden, full-screen, maximized, snapped), the left edge on a platform without
    /// positions, and room in the monitor's work area. The window can change before the panel opens, so the result of
    /// <see cref="OpenSidePanelAsync"/> decides. Experimental (<c>RUNICDESK104</c>).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="edge"/> is not defined, or <paramref name="width"/> is smaller than <see cref="DesktopSidePanelOptions.MinimumWidth"/>.</exception>
    /// <exception cref="ObjectDisposedException">The window is closed or was replaced.</exception>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public DesktopSidePanelRefusal? CheckSidePanel(DesktopWindowEdge edge, int width)
    {
        ValidateSidePanel(edge, width);
        ObjectDisposedException.ThrowIf(!IsOpen, this);
        if (Browser != BrowserKind.Embedded || !_engine.SupportsSidePanel) return DesktopSidePanelRefusal.Unsupported;
        if (SidePanel is not null) return DesktopSidePanelRefusal.AlreadyOpen;
        return _engine.CheckSidePanel(DesktopWindowBounds.ResolvesToLeft(edge, LayoutDirection), width);
    }

    private static void ValidateSidePanel(DesktopWindowEdge edge, int width)
    {
        if (!Enum.IsDefined(edge)) throw new ArgumentOutOfRangeException(nameof(edge), edge, "The edge is not defined.");
        if (width < DesktopSidePanelOptions.MinimumWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width,
                $"A side panel is at least {DesktopSidePanelOptions.MinimumWidth} device-independent pixels wide.");
        }
    }

    internal Microsoft.Extensions.Logging.ILogger? Logger => _engine.Logger;

    // The panel closed, for whatever reason: the window forgets it and the surface revokes its connection slot.
    internal void OnSidePanelClosed(DesktopSidePanel panel)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref _sidePanel, null, panel), panel) || !panel.IsAttached)
        {
            _surface.CloseSidePanelSlot();
        }
    }

    // The window closed: its host destroyed the panel's view with it, and reports that too, unless it already went.
    private void CloseSidePanelWithWindow() => Volatile.Read(ref _sidePanel)?.OnNativeClosed(DesktopSidePanelCloseReason.WindowClosed);
}
