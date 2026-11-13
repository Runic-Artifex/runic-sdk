using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Runic.Navigation;
using Runic.Platform;

namespace Runic.Application.Views;

/// <summary>
/// The Window whose DI scope resolved the service, as it appears on the desktop: a ViewModel brings it to the front,
/// minimizes, hides or shows it, makes it full-screen, marks its document edited, switches it between light and dark,
/// and reads whether it is active, minimized and visible. Take it in a constructor:
/// <code>
/// public sealed partial class InboxViewModel : ObservableObject
/// {
///     public InboxViewModel(IRunicWindowPresentation window)
///     {
///         _window = window;
///         // Poll less while the user works in another window.
///         window.ActivityChanged += (_, e) =&gt; _poll.Interval = e.Activity.IsActive ? Fast : Slow;
///     }
///
///     [RelayCommand]
///     private async Task HideToTrayAsync() =&gt; await _window.SetVisibleAsync(false);
/// }
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunicWindowServiceCollectionExtensions.AddRunicWindowService"/> registers it per scope, next to
/// <see cref="IRunicWindowContext"/>. It works on every host without naming one: Runic Desktop's embedded windows
/// (WebView2, WKWebView, WebKitGTK with GTK 3 or GTK 4), a WPF window through
/// <c>WpfPlatformServices.ForWindowPresentation</c>, and CS-WebUI as far as it can. <see cref="Features"/> says what
/// the host can do; a request it cannot do returns <see cref="PlatformResult{T}.Unavailable"/> with
/// <see cref="PlatformUnavailableReason.BackendUnavailable"/> instead of throwing. Before the Window opens, and in a
/// scope that no Window opened, requests report <see cref="PlatformUnavailableReason.OwnerUnavailable"/>; once it has
/// closed, <see cref="PlatformUnavailableReason.OwnerClosed"/>. Code outside the scope reads the same object from
/// <see cref="IRunicWindow.Presentation"/>.
/// </para>
/// <para>
/// The state changes (<see cref="ActivityChanged"/> and <see cref="INotifyPropertyChanged.PropertyChanged"/> for
/// <see cref="Activity"/>, <see cref="IsActive"/>, <see cref="IsMinimized"/>, <see cref="IsVisible"/> and
/// <see cref="ColorScheme"/>) are raised in a turn of the Window's model context, as a command runs, so a handler sets
/// ViewModel state directly. With CommunityToolkit, subscribe to the event; with ReactiveUI, use
/// <c>WhenAnyValue(x =&gt; x.IsActive)</c> on the service, or <see cref="RunicWindowPresentationExtensions.ObserveActivity"/>.
/// Where the host reports no activity (<see cref="Features"/> without <see cref="RunicWindowPresentationFeatures.Activity"/>),
/// the Window counts as active and visible (<see cref="RunicWindowActivity.Assumed"/>), so work that pauses while it is
/// inactive keeps running.
/// </para>
/// <para>
/// This is not a ViewModel activation hook (the dropped I-142): it reports the native window, not whether a View
/// shows the ViewModel. In tests, use <c>RunicTestWindowPresentation</c> from Runic.Application.Testing, which records
/// every request and lets the test set the activity.
/// </para>
/// </remarks>
public interface IRunicWindowPresentation : INotifyPropertyChanged
{
    /// <summary>Gets what the Window's host can do now; <see cref="RunicWindowPresentationFeatures.None"/> before it opens and after it closed.</summary>
    RunicWindowPresentationFeatures Features { get; }

    /// <summary>Gets whether the Window is active, minimized and visible, as last reported.</summary>
    RunicWindowActivity Activity { get; }

    /// <summary>Gets whether the Window has the desktop's keyboard focus: <see cref="RunicWindowActivity.IsActive"/> of <see cref="Activity"/>.</summary>
    bool IsActive { get; }

    /// <summary>Gets whether the Window is minimized: <see cref="RunicWindowActivity.IsMinimized"/> of <see cref="Activity"/>.</summary>
    bool IsMinimized { get; }

    /// <summary>Gets whether the Window is shown: <see cref="RunicWindowActivity.IsVisible"/> of <see cref="Activity"/>.</summary>
    bool IsVisible { get; }

    /// <summary>
    /// Gets the light or dark appearance the Window shows now, as Runic set it: its own scheme, the application's, or
    /// the last one set for every Window that shares it (<see cref="RunicWindowPresentationFeatures.PerWindowColorScheme"/>);
    /// <see cref="DesktopColorScheme.NoPreference"/> when it follows the system.
    /// </summary>
    /// <remarks><see cref="INotifyPropertyChanged.PropertyChanged"/> reports a change, also one that another Window or the application made.</remarks>
    DesktopColorScheme ColorScheme { get; }

    /// <summary>Occurs after <see cref="Activity"/> has changed, in a turn of the Window's model context.</summary>
    event EventHandler<RunicWindowActivityChangedEventArgs>? ActivityChanged;

    /// <summary>
    /// Brings the Window to the front and gives it the keyboard focus: shows it when it is hidden and restores it when
    /// it is minimized.
    /// </summary>
    /// <param name="activation">
    /// The activation token or startup ID that lets the desktop grant focus, such as <c>ApplicationLaunch.PlatformContext</c>
    /// of a launch that a second instance forwarded, or <see langword="null"/>. Use it once, promptly.
    /// </param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns>
    /// <see cref="PlatformResult{T}.Success"/> when the desktop presented the Window. A desktop may refuse to take the
    /// focus from the application the user works in: Windows then flashes the taskbar button and reports
    /// <see cref="PlatformFailureCode.PermissionDenied"/>, and a Wayland compositor without an activation token only asks
    /// for attention.
    /// </returns>
    ValueTask<PlatformResult<PlatformUnit>> PresentAsync(DesktopActivationContext? activation = null, CancellationToken cancellationToken = default);

    /// <summary>Minimizes the Window to the taskbar or Dock.</summary>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns><see cref="PlatformResult{T}.Success"/> once the window was asked to minimize.</returns>
    ValueTask<PlatformResult<PlatformUnit>> MinimizeAsync(CancellationToken cancellationToken = default);

    /// <summary>Hides the Window without closing it, for example to a status icon, or shows it again.</summary>
    /// <param name="visible"><see langword="true"/> to show the Window; <see langword="false"/> to hide it.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns><see cref="PlatformResult{T}.Success"/> once the window was shown or hidden.</returns>
    /// <remarks>
    /// A hidden Window stays open: its page keeps running and its Bridge stays connected. Only the application can show
    /// it again, so keep a way back, such as a status icon whose command calls <see cref="PresentAsync"/>.
    /// </remarks>
    ValueTask<PlatformResult<PlatformUnit>> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default);

    /// <summary>Enters or leaves full screen.</summary>
    /// <param name="fullScreen"><see langword="true"/> to fill the Window's monitor; <see langword="false"/> to return to its bounds.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns><see cref="PlatformResult{T}.Success"/> once the window was asked to change.</returns>
    ValueTask<PlatformResult<PlatformUnit>> SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default);

    /// <summary>Marks whether the Window's document has unsaved changes, where the platform shows it: the dot in the macOS close button.</summary>
    /// <param name="edited"><see langword="true"/> while the document has unsaved changes.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns>
    /// <see cref="PlatformResult{T}.Success"/> once the window shows it; <see cref="PlatformUnavailableReason.BackendUnavailable"/>
    /// on Windows, Linux and in WPF, which have no such convention. Show unsaved changes in the page or title there.
    /// </returns>
    ValueTask<PlatformResult<PlatformUnit>> SetDocumentEditedAsync(bool edited, CancellationToken cancellationToken = default);

    /// <summary>Switches the Window between light and dark while it is open.</summary>
    /// <param name="colorScheme">
    /// <see cref="DesktopColorScheme.Light"/> or <see cref="DesktopColorScheme.Dark"/>, or
    /// <see cref="DesktopColorScheme.NoPreference"/> to follow the application's scheme, which by default follows the system.
    /// </param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns><see cref="PlatformResult{T}.Success"/> once the window and its page show the scheme.</returns>
    /// <remarks>
    /// <para>
    /// The page's <c>prefers-color-scheme</c> follows. Where <see cref="Features"/> lacks
    /// <see cref="RunicWindowPresentationFeatures.PerWindowColorScheme"/>, the scheme is not this Window's alone: on
    /// Linux the GTK windows share one scheme for the whole process, and on Windows the windows that share a WebView2
    /// profile share its scheme, so the call changes all of them and <see cref="ColorScheme"/> of each reports it.
    /// </para>
    /// <para>
    /// On Runic Desktop, <c>DesktopHost.SetColorSchemeAsync</c> switches the whole application; on such a shared scheme
    /// it wins over the Windows' own, whatever order they were set in.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="colorScheme"/> is not a defined value.</exception>
    ValueTask<PlatformResult<PlatformUnit>> SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default);

    /// <summary>Maximizes the Window (zooms it on macOS); a Window that already is stays maximized.</summary>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns>
    /// <see cref="PlatformResult{T}.Success"/> once the window was asked to maximize; <see cref="Placement"/> then reports it.
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/> where <see cref="Features"/> lacks
    /// <see cref="RunicWindowPresentationFeatures.Maximize"/>, <see cref="PlatformFailureCode.PermissionDenied"/> when a
    /// host application's policy refused it, and <see cref="PlatformFailureCode.Conflict"/> for a kiosk window, which stays
    /// full-screen.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A full-screen Window leaves full screen first, a minimized one is restored maximized, and a hidden one stays hidden
    /// and shows maximized. An open side panel stays open. Windows activates a window that it maximizes, as it does when
    /// the user maximizes one; macOS animates the zoom, and a Linux window manager decides when the window is maximized.
    /// </para>
    /// <para>
    /// The default implementation, for a presentation of 0.18 or earlier, reports
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>.
    /// </para>
    /// </remarks>
    ValueTask<PlatformResult<PlatformUnit>> MaximizeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(RunicWindowPlacement.Unsupported);
    }

    /// <summary>
    /// Returns the Window to its normal bounds: leaves full screen, un-maximizes and un-minimizes it; a normal Window stays
    /// as it is.
    /// </summary>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns>
    /// <see cref="PlatformResult{T}.Success"/> once the window was asked to restore, with the results of
    /// <see cref="MaximizeAsync"/> otherwise.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The Window ends at its normal bounds, the ones <see cref="Placement"/> reports, also when it was minimized while
    /// maximized, where the platform itself would bring it back maximized; <see cref="PresentAsync"/> keeps that
    /// behaviour. A hidden Window stays hidden and shows at its normal bounds. Windows restores a visible maximized window
    /// without activating it, but activates one that it brings back from minimized. On Wayland an application is not told
    /// that its window is minimized, so only <see cref="PresentAsync"/> brings it back there.
    /// </para>
    /// <para>The default implementation reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>.</para>
    /// </remarks>
    ValueTask<PlatformResult<PlatformUnit>> RestoreAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(RunicWindowPlacement.Unsupported);
    }

    /// <summary>
    /// Gives the open Window a placement: the bounds it restores to and whether it is maximized or full-screen, as a
    /// placement saved from <see cref="Placement"/> gives it.
    /// </summary>
    /// <param name="placement">
    /// The placement, in the units of <see cref="Placement"/> (see <see cref="RunicWindowPlacement"/>).
    /// <see cref="RunicWindowPlacement.X"/> and <see cref="RunicWindowPlacement.Y"/> both <see langword="null"/> keep the
    /// position; a <see cref="RunicWindowPlacement.Width"/> or <see cref="RunicWindowPlacement.Height"/> of zero or less
    /// keeps that dimension.
    /// </param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns>
    /// <see cref="PlatformResult{T}.Success"/> once the window was asked to take the placement, with the results of
    /// <see cref="MaximizeAsync"/> otherwise. Where the platform cannot position a window (Wayland), a placement with a
    /// position reports <see cref="PlatformUnavailableReason.BackendUnavailable"/> and changes nothing: pass one without
    /// <see cref="RunicWindowPlacement.X"/> and <see cref="RunicWindowPlacement.Y"/>, as <see cref="Placement"/> reports there.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A maximized, minimized, full-screen or hidden Window takes the bounds as the ones it restores to, and the
    /// placement's state when it is next shown or restored. The bounds are kept on a monitor that exists now: bounds off
    /// every monitor's work area move onto the nearest one, and bounds larger than it shrink to fit, so
    /// <see cref="Placement"/> can report other bounds than the ones passed. A host application's policy can also change
    /// them. With a side panel open, the placement is that of the Window's page, without the panel.
    /// </para>
    /// <para>
    /// Windows activates a window that the placement maximizes or brings back from minimized.
    /// The default implementation reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Only one of <see cref="RunicWindowPlacement.X"/> and <see cref="RunicWindowPlacement.Y"/> is set.</exception>
    ValueTask<PlatformResult<PlatformUnit>> SetPlacementAsync(RunicWindowPlacement placement, CancellationToken cancellationToken = default)
    {
        RunicWindowPlacement.Validate(placement);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(RunicWindowPlacement.Unsupported);
    }

    /// <summary>
    /// Gets the Window's placement as last reported: the bounds it restores to and whether it is maximized or full-screen;
    /// <see langword="null"/> where the host reports none (<see cref="Features"/> without
    /// <see cref="RunicWindowPresentationFeatures.Placement"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Save it when the Window closes and pass it to <see cref="SetPlacementAsync"/> to put the Window back where it was.
    /// On Runic Desktop it is the window's <c>DesktopWindowState.Placement</c>, converted with
    /// <c>ToRunicPlacement()</c> of Runic.Application.Views.Desktop, so a placement saved by either reads in the other.
    /// </para>
    /// <para>
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> reports a change in a turn of the Window's model context. While
    /// the user drags or resizes the Window the changes are combined, so a handler reads the latest placement. The value
    /// stays readable after the Window closed. The default implementation returns <see langword="null"/>.
    /// </para>
    /// </remarks>
    RunicWindowPlacement? Placement => null;

    /// <summary>
    /// Opens a page of the Window beside its page, growing the Window by the panel's width on one edge, so its page keeps
    /// its size and its place on the screen.
    /// </summary>
    /// <param name="options">The edge, width and page of the panel.</param>
    /// <param name="cancellationToken">Cancels waiting for the panel's page; a cancelled open leaves the Window unchanged.</param>
    /// <returns>
    /// <see cref="RunicSidePanelResult.Opened"/> with the panel, also <see cref="SidePanel"/> from then on, or
    /// <see cref="RunicSidePanelResult.Refused"/> with the reason, after which the Window is unchanged and the ViewModel
    /// shows the content another way, such as an overlay of its page.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The panel's page is a second connection of the Window and binds to the same ViewModel tree, for example to a
    /// <c>SidePanel</c> property of the root ViewModel; its address and history are its own. Only Runic Desktop's own
    /// windows on Windows (WebView2) open one in 0.19; every other presentation refuses with
    /// <see cref="RunicSidePanelRefusal.Unsupported"/>: macOS and Linux for now, an installed browser, a serve-only
    /// application, a custom window host, WPF, CS-WebUI and a hosted application's Window or region.
    /// <see cref="RunicWindowPresentationFeatures.SidePanel"/> in <see cref="Features"/> says the host has one; the
    /// Window's state can still refuse (maximized, full-screen, snapped, minimized, no room). Once the Window closed,
    /// the result is <see cref="RunicSidePanelRefusal.Closed"/>.
    /// </para>
    /// <para>
    /// The default implementation, for a presentation of 0.18 or earlier, refuses with
    /// <see cref="RunicSidePanelRefusal.Unsupported"/>. Experimental (<c>RUNICDESK104</c>); see <c>docs/experimental.md</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The width is smaller than <see cref="RunicSidePanelOptions.MinimumWidth"/>, the edge is not defined, or the load
    /// timeout is not positive.
    /// </exception>
    /// <exception cref="ArgumentException">The path is empty, absolute, or leaves the Window's page.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    ValueTask<RunicSidePanelResult> OpenSidePanelAsync(RunicSidePanelOptions options, CancellationToken cancellationToken = default)
    {
        RunicSidePanelOptions.Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<RunicSidePanelResult>(new RunicSidePanelResult.Refused(RunicSidePanelRefusal.Unsupported));
    }

    /// <summary>Gets the Window's open side panel, or <see langword="null"/>.</summary>
    /// <remarks>
    /// Set when <see cref="OpenSidePanelAsync"/> returns the panel; <see langword="null"/> again in the turn of the Window's
    /// model context that raises the panel's <see cref="IRunicSidePanel.Closed"/>, and
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> reports both changes. The default implementation returns
    /// <see langword="null"/>. Experimental (<c>RUNICDESK104</c>).
    /// </remarks>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    IRunicSidePanel? SidePanel => null;
}

/// <summary>What a Window's host can do through <see cref="IRunicWindowPresentation"/>.</summary>
/// <remarks>Open: a later minor release can add values, so test for the flags you use.</remarks>
[Flags]
public enum RunicWindowPresentationFeatures
{
    /// <summary>Nothing: the Window has not opened, has closed, or its host presents no native window.</summary>
    None = 0,

    /// <summary><see cref="IRunicWindowPresentation.PresentAsync"/> brings the Window to the front.</summary>
    Present = 1 << 0,

    /// <summary><see cref="IRunicWindowPresentation.MinimizeAsync"/> minimizes the Window.</summary>
    Minimize = 1 << 1,

    /// <summary><see cref="IRunicWindowPresentation.SetVisibleAsync"/> hides and shows the Window.</summary>
    Visibility = 1 << 2,

    /// <summary><see cref="IRunicWindowPresentation.SetFullScreenAsync"/> enters and leaves full screen.</summary>
    FullScreen = 1 << 3,

    /// <summary><see cref="IRunicWindowPresentation.SetDocumentEditedAsync"/> shows unsaved changes in the window's frame.</summary>
    DocumentEdited = 1 << 4,

    /// <summary><see cref="IRunicWindowPresentation.SetColorSchemeAsync"/> switches the Window between light and dark.</summary>
    ColorScheme = 1 << 5,

    /// <summary>
    /// <see cref="IRunicWindowPresentation.Activity"/> follows the window. Without it the Window counts as active and
    /// visible (<see cref="RunicWindowActivity.Assumed"/>).
    /// </summary>
    Activity = 1 << 6,

    /// <summary>
    /// <see cref="IRunicWindowPresentation.SetColorSchemeAsync"/> changes this Window only. Without it, but with
    /// <see cref="ColorScheme"/>, the scheme belongs to more than this Window and a call changes all of them: on Linux
    /// every GTK window of the application, on Windows every window that shares the WebView2 profile
    /// (<c>DesktopWindowOptions.ProfilePath</c>).
    /// </summary>
    PerWindowColorScheme = 1 << 7,

    /// <summary>
    /// A Window opened with this Window as its <see cref="RunicWindowOptions.Owner"/> belongs to it, and can be window-modal
    /// to it (<see cref="RunicWindowOptions.Modality"/>). Without it, such a Window opens on its own.
    /// </summary>
    OwnedWindows = 1 << 8,

    /// <summary>
    /// <see cref="IRunicWindowPresentation.OpenSidePanelAsync"/> can open a side panel: the host has one, though the
    /// Window's state can still refuse it. Experimental (<c>RUNICDESK104</c>).
    /// </summary>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    SidePanel = 1 << 9,

    /// <summary>
    /// <see cref="IRunicWindowPresentation.MaximizeAsync"/> maximizes the Window and
    /// <see cref="IRunicWindowPresentation.RestoreAsync"/> returns it to its normal bounds.
    /// </summary>
    Maximize = 1 << 10,

    /// <summary>
    /// <see cref="IRunicWindowPresentation.SetPlacementAsync"/> places the Window, and
    /// <see cref="IRunicWindowPresentation.Placement"/> reports where it is.
    /// </summary>
    Placement = 1 << 11,
}

/// <summary>
/// A Window's placement: the bounds it restores to (when it is neither maximized nor full-screen) and whether it is
/// maximized or full-screen, for <see cref="IRunicWindowPresentation.Placement"/> and
/// <see cref="IRunicWindowPresentation.SetPlacementAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// The values use the units of the Window's host, so a placement read from a Window and saved places a Window of the same
/// host where it was; save it per host rather than convert it:
/// </para>
/// <list type="bullet">
/// <item><description>On Runic Desktop, those of <c>DesktopWindowPlacement</c> as <c>DesktopWindowState.Placement</c>
/// reports it: on Windows <see cref="X"/> and <see cref="Y"/> are physical pixels of the virtual screen and
/// <see cref="Width"/> and <see cref="Height"/> device-independent pixels of the outer frame; on macOS points, the
/// top-left corner of the frame and the content size; on Linux logical pixels, the frame's corner and the content size,
/// and no position on Wayland. Runic.Application.Views.Desktop converts with <c>ToRunicPlacement()</c> and
/// <c>ToDesktopPlacement()</c>.</description></item>
/// <item><description>In WPF, device-independent pixels for all four values: <c>Window.Left</c>, <c>Top</c>,
/// <c>Width</c> and <c>Height</c> of the restored window.</description></item>
/// <item><description>For a host application's window, the host's units.</description></item>
/// </list>
/// <para>An open side panel is not part of the bounds: they are those of the Window's page.</para>
/// </remarks>
public sealed record RunicWindowPlacement
{
    internal static readonly PlatformResult<PlatformUnit> Unsupported =
        new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);

    /// <summary>Gets the horizontal position of the restored Window, or <see langword="null"/> where there is none or to keep it.</summary>
    /// <remarks>Set together with <see cref="Y"/>.</remarks>
    public int? X { get; init; }

    /// <summary>Gets the vertical position of the restored Window, or <see langword="null"/> where there is none or to keep it.</summary>
    /// <remarks>Set together with <see cref="X"/>.</remarks>
    public int? Y { get; init; }

    /// <summary>Gets the width of the restored Window; zero or less keeps the current width.</summary>
    public int Width { get; init; }

    /// <summary>Gets the height of the restored Window; zero or less keeps the current height.</summary>
    public int Height { get; init; }

    /// <summary>Gets whether the Window is maximized (zoomed on macOS).</summary>
    public bool Maximized { get; init; }

    /// <summary>Gets whether the Window is full-screen.</summary>
    public bool FullScreen { get; init; }

    // The checks of IRunicWindowPresentation.SetPlacementAsync, for every presentation.
    internal static void Validate(RunicWindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (placement.X is null != placement.Y is null)
            throw new ArgumentException("Set both X and Y of the placement, or neither to keep the Window's position.", nameof(placement));
    }
}

/// <summary>Whether a Window is active, minimized and visible, for <see cref="IRunicWindowPresentation.Activity"/>.</summary>
/// <param name="IsActive">Whether the Window has the desktop's keyboard focus.</param>
/// <param name="IsMinimized">Whether the Window is minimized.</param>
/// <param name="IsVisible">Whether the Window is shown: not hidden and not minimized, though other windows may cover it.</param>
public sealed record RunicWindowActivity(bool IsActive, bool IsMinimized, bool IsVisible)
{
    /// <summary>Active and visible: what a Window reports when its host cannot tell, and before it opens.</summary>
    public static RunicWindowActivity Assumed { get; } = new(IsActive: true, IsMinimized: false, IsVisible: true);

    /// <summary>Neither active, minimized nor visible: what a closed Window reports.</summary>
    public static RunicWindowActivity Closed { get; } = new(IsActive: false, IsMinimized: false, IsVisible: false);
}

/// <summary>Provides the previous and new activity for <see cref="IRunicWindowPresentation.ActivityChanged"/>.</summary>
/// <param name="previous">The activity before the change.</param>
/// <param name="activity">The new activity.</param>
public sealed class RunicWindowActivityChangedEventArgs(RunicWindowActivity previous, RunicWindowActivity activity) : EventArgs
{
    /// <summary>Gets the activity before the change.</summary>
    public RunicWindowActivity Previous { get; } = previous ?? throw new ArgumentNullException(nameof(previous));

    /// <summary>Gets the new activity.</summary>
    public RunicWindowActivity Activity { get; } = activity ?? throw new ArgumentNullException(nameof(activity));
}

/// <summary>Observes a Window's activity without an MVVM library.</summary>
public static class RunicWindowPresentationExtensions
{
    /// <summary>
    /// Returns the Window's activity as an observable sequence: the current value when subscribed, then each change, in
    /// the turns that raise <see cref="IRunicWindowPresentation.ActivityChanged"/>.
    /// </summary>
    /// <remarks>
    /// A plain <see cref="IObservable{T}"/>, so ReactiveUI composes it with either flavor, for example to pause a timer:
    /// <code>
    /// window.ObserveActivity().Select(activity =&gt; activity.IsActive).DistinctUntilChanged().Subscribe(...)
    /// </code>
    /// Disposing the subscription removes its handler. The sequence never completes; it ends with the Window's scope.
    /// </remarks>
    /// <param name="presentation">The Window's presentation.</param>
    public static IObservable<RunicWindowActivity> ObserveActivity(this IRunicWindowPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        return new ActivityObservable(presentation);
    }

    private sealed class ActivityObservable(IRunicWindowPresentation presentation) : IObservable<RunicWindowActivity>
    {
        public IDisposable Subscribe(IObserver<RunicWindowActivity> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            void OnChanged(object? sender, RunicWindowActivityChangedEventArgs args) => observer.OnNext(args.Activity);
            presentation.ActivityChanged += OnChanged;
            observer.OnNext(presentation.Activity);
            return new Subscription(() => presentation.ActivityChanged -= OnChanged);
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

// The presentation of one Window, which its scope and IRunicWindow.Presentation share. The host attaches its own
// presentation once the native window exists (RunicWindowOpening.UsePresentation); this one forwards requests to it,
// and raises its changes in the Window's model turns. Runic.Application.Testing wraps its recording fake in it, so a
// test sees the same delivery, OwnerClosed and handler isolation as an application.
internal sealed class RunicWindowPresentation : IRunicWindowPresentation, IDisposable
{
    // A presentation attached to backend at once, for the test fake.
    internal static RunicWindowPresentation Wrap(IRunicWindowPresentation backend, Func<IRunicModelContext?> context, Func<ILogger> logger)
    {
        var presentation = new RunicWindowPresentation();
        presentation.Attach(backend, context, logger);
        return presentation;
    }

    // The scope that resolved it releases it, after its Window closed.
    public void Dispose() => Close();

    // Changes waiting to be applied outside a model context, one at a time.
    private readonly Queue<Action> _serial = new();
    private bool _applying;
    private static readonly PlatformResult<PlatformUnit> NotOpen =
        new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
    private static readonly PlatformResult<PlatformUnit> Closed =
        new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);

    private readonly object _gate = new();
    private IRunicWindowPresentation? _backend;
    private Func<IRunicModelContext?>? _context;
    private Func<ILogger>? _logger;
    private RunicWindowActivity _activity = RunicWindowActivity.Assumed;
    private DesktopColorScheme _colorScheme;
    private bool _closed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<RunicWindowActivityChangedEventArgs>? ActivityChanged;

    public RunicWindowPresentationFeatures Features =>
        Volatile.Read(ref _closed) ? RunicWindowPresentationFeatures.None : Volatile.Read(ref _backend)?.Features ?? RunicWindowPresentationFeatures.None;

    public RunicWindowActivity Activity => Volatile.Read(ref _activity);

    public bool IsActive => Activity.IsActive;

    public bool IsMinimized => Activity.IsMinimized;

    public bool IsVisible => Activity.IsVisible;

    public DesktopColorScheme ColorScheme => Volatile.Read(ref _backend)?.ColorScheme ?? _colorScheme;

    // Called by the host once, while it opens the Window. context and logger are read when a change arrives.
    internal void Attach(IRunicWindowPresentation backend, Func<IRunicModelContext?> context, Func<ILogger> logger)
    {
        ArgumentNullException.ThrowIfNull(backend);
        lock (_gate)
        {
            if (_backend is not null) throw new InvalidOperationException("The Window already has a presentation.");
            if (_closed) return;
            _context = context;
            _logger = logger;
            Volatile.Write(ref _backend, backend);
            backend.ActivityChanged += OnBackendActivityChanged;
            backend.PropertyChanged += OnBackendPropertyChanged;
        }
        Deliver(backend.Features.HasFlag(RunicWindowPresentationFeatures.Activity) ? backend.Activity : RunicWindowActivity.Assumed);
        if (backend.Placement is { } placement) OnPlacement(placement);
    }

    // The Window was released: requests report OwnerClosed, and the activity is Closed.
    internal void Close()
    {
        IRunicWindowPresentation? backend;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            backend = _backend;
        }
        if (backend is not null)
        {
            backend.ActivityChanged -= OnBackendActivityChanged;
            backend.PropertyChanged -= OnBackendPropertyChanged;
        }
        // Also for a Window whose host attached no presentation, such as a serve-only one.
        Deliver(RunicWindowActivity.Closed);
    }

    public ValueTask<PlatformResult<PlatformUnit>> PresentAsync(DesktopActivationContext? activation = null, CancellationToken cancellationToken = default) =>
        Forward(backend => backend.PresentAsync(activation, cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> MinimizeAsync(CancellationToken cancellationToken = default) =>
        Forward(backend => backend.MinimizeAsync(cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        Forward(backend => backend.SetVisibleAsync(visible, cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default) =>
        Forward(backend => backend.SetFullScreenAsync(fullScreen, cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetDocumentEditedAsync(bool edited, CancellationToken cancellationToken = default) =>
        Forward(backend => backend.SetDocumentEditedAsync(edited, cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(colorScheme)) throw new ArgumentOutOfRangeException(nameof(colorScheme));
        return Forward(backend => backend.SetColorSchemeAsync(colorScheme, cancellationToken), cancellationToken);
    }

    // sdk#897, W1b (window-api-0.19/DESIGN.md §3.2): maximize, restore and placement go to the host's presentation.
    public ValueTask<PlatformResult<PlatformUnit>> MaximizeAsync(CancellationToken cancellationToken = default) =>
        Forward(backend => backend.MaximizeAsync(cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> RestoreAsync(CancellationToken cancellationToken = default) =>
        Forward(backend => backend.RestoreAsync(cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetPlacementAsync(RunicWindowPlacement placement, CancellationToken cancellationToken = default)
    {
        RunicWindowPlacement.Validate(placement);
        return Forward(backend => backend.SetPlacementAsync(placement, cancellationToken), cancellationToken);
    }

    // The placement the ViewModel was last told of; the latest one the host reported waits in _reportedPlacement for one
    // pending turn, so the changes of a drag reach the ViewModel combined.
    private RunicWindowPlacement? _placement;
    private RunicWindowPlacement? _reportedPlacement;
    private bool _placementPending;

    public RunicWindowPlacement? Placement => Volatile.Read(ref _placement);

    private void OnPlacement(RunicWindowPlacement? placement)
    {
        lock (_gate)
        {
            _reportedPlacement = placement;
            if (_placementPending) return;
            _placementPending = true;
        }
        InTurn(() =>
        {
            RunicWindowPlacement? latest;
            lock (_gate)
            {
                _placementPending = false;
                latest = _reportedPlacement;
            }
            if (Equals(_placement, latest)) return;
            Volatile.Write(ref _placement, latest);
            Raise(nameof(Placement));
        });
    }

    // sdk#864, P5: the backend's panel, wrapped so that its Closed and the change of SidePanel arrive in a model turn.
    private ModelSidePanel? _sidePanel;

    public IRunicSidePanel? SidePanel => Volatile.Read(ref _sidePanel);

    public async ValueTask<RunicSidePanelResult> OpenSidePanelAsync(RunicSidePanelOptions options, CancellationToken cancellationToken = default)
    {
        RunicSidePanelOptions.Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _closed)) return new RunicSidePanelResult.Refused(RunicSidePanelRefusal.Closed);
        // A Window whose host attached no presentation, such as a serve-only one, has no native window to grow.
        if (Volatile.Read(ref _backend) is not { } backend) return new RunicSidePanelResult.Refused(RunicSidePanelRefusal.Unsupported);
        var result = await backend.OpenSidePanelAsync(options, cancellationToken).ConfigureAwait(false);
        return result is RunicSidePanelResult.Opened opened ? new RunicSidePanelResult.Opened(Track(opened.Panel)) : result;
    }

    // The backend's panel, wrapped once: SidePanel is set at once, so the command that opened it reads it, and
    // PropertyChanged follows in a turn.
    private ModelSidePanel Track(IRunicSidePanel inner)
    {
        ModelSidePanel panel;
        lock (_gate)
        {
            if (_sidePanel is { } current && ReferenceEquals(current.Inner, inner)) return current;
            panel = new ModelSidePanel(this, inner);
            Volatile.Write(ref _sidePanel, panel);
        }
        panel.Start();
        InTurn(() => Raise(nameof(SidePanel)));
        return panel;
    }

    private void OnSidePanelClosed(ModelSidePanel panel, RunicSidePanelCloseReason reason) => InTurn(() =>
    {
        bool current;
        lock (_gate)
        {
            current = ReferenceEquals(_sidePanel, panel);
            if (current) Volatile.Write(ref _sidePanel, null);
        }
        if (current) Raise(nameof(SidePanel));
        panel.RaiseClosed(reason);
    });

    // IRunicSidePanel of the Window's scope: forwards to the backend's panel and raises its Closed in a model turn.
    private sealed class ModelSidePanel(RunicWindowPresentation presentation, IRunicSidePanel inner) : IRunicSidePanel
    {
        private readonly object _gate = new();
        private EventHandler<RunicSidePanelClosedEventArgs>? _closedHandlers;
        private int _closing;
        private bool _raised;

        internal IRunicSidePanel Inner => inner;

        public RunicSidePanelEdge Edge => inner.Edge;

        public int Width => inner.Width;

        public bool IsOpen => inner.IsOpen;

        public event EventHandler<RunicSidePanelClosedEventArgs>? Closed
        {
            add { lock (_gate) if (!_raised) _closedHandlers += value; }
            remove { lock (_gate) _closedHandlers -= value; }
        }

        internal void Start()
        {
            inner.Closed += OnInnerClosed;
            // Closed before the handler was added: a backend does not call a handler added after the close.
            if (!inner.IsOpen) OnClosed(RunicSidePanelCloseReason.Closed);
        }

        private void OnInnerClosed(object? sender, RunicSidePanelClosedEventArgs args) => OnClosed(args.Reason);

        private void OnClosed(RunicSidePanelCloseReason reason)
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0) return;
            inner.Closed -= OnInnerClosed;
            presentation.OnSidePanelClosed(this, reason);
        }

        internal void RaiseClosed(RunicSidePanelCloseReason reason)
        {
            EventHandler<RunicSidePanelClosedEventArgs>? handlers;
            lock (_gate)
            {
                _raised = true;
                handlers = _closedHandlers;
                _closedHandlers = null;
            }
            if (handlers is null) return;
            var args = new RunicSidePanelClosedEventArgs(reason);
            foreach (EventHandler<RunicSidePanelClosedEventArgs> handler in handlers.GetInvocationList())
            {
                try { handler(this, args); }
                catch (Exception error) when (error is not OutOfMemoryException) { presentation.HandlerFailed(error, nameof(Closed)); }
            }
        }

        public ValueTask<RunicSidePanelRefusal?> MoveToEdgeAsync(RunicSidePanelEdge edge, CancellationToken cancellationToken = default)
        {
            RunicSidePanelOptions.ValidateEdge(edge);
            cancellationToken.ThrowIfCancellationRequested();
            return IsOpen ? inner.MoveToEdgeAsync(edge, cancellationToken) : ValueTask.FromResult<RunicSidePanelRefusal?>(RunicSidePanelRefusal.Closed);
        }

        public ValueTask<RunicSidePanelRefusal?> SetWidthAsync(int width, CancellationToken cancellationToken = default)
        {
            RunicSidePanelOptions.ValidateWidth(width);
            cancellationToken.ThrowIfCancellationRequested();
            return IsOpen ? inner.SetWidthAsync(width, cancellationToken) : ValueTask.FromResult<RunicSidePanelRefusal?>(RunicSidePanelRefusal.Closed);
        }

        public ValueTask FocusAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return IsOpen ? inner.FocusAsync(cancellationToken) : ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
            IsOpen ? inner.CloseAsync(cancellationToken) : ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => CloseAsync(CancellationToken.None);
    }

    private ValueTask<PlatformResult<PlatformUnit>> Forward(Func<IRunicWindowPresentation, ValueTask<PlatformResult<PlatformUnit>>> request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _closed)) return ValueTask.FromResult(Closed);
        return Volatile.Read(ref _backend) is { } backend ? request(backend) : ValueTask.FromResult(NotOpen);
    }

    private void OnBackendActivityChanged(object? sender, RunicWindowActivityChangedEventArgs args)
    {
        if (!Volatile.Read(ref _closed)) Deliver(args.Activity);
    }

    private void OnBackendPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Volatile.Read(ref _closed) || sender is not IRunicWindowPresentation backend) return;
        // A panel the backend opened without this presentation, such as through DesktopWindow; its close reports itself.
        if (args.PropertyName == nameof(SidePanel))
        {
            if (backend.SidePanel is { } panel) Track(panel);
            return;
        }
        if (args.PropertyName == nameof(Placement))
        {
            OnPlacement(backend.Placement);
            return;
        }
        if (args.PropertyName != nameof(ColorScheme)) return;
        var colorScheme = backend.ColorScheme;
        InTurn(() =>
        {
            if (_colorScheme == colorScheme) return;
            _colorScheme = colorScheme;
            Raise(nameof(ColorScheme));
        });
    }

    // The change is applied in a turn of the Window's model context, so a handler reads and writes ViewModel state as a
    // command does. Turns run in order, so the last change wins.
    private void Deliver(RunicWindowActivity activity) => InTurn(() =>
    {
        var previous = _activity;
        if (Equals(previous, activity)) return;
        Volatile.Write(ref _activity, activity);
        if (previous.IsActive != activity.IsActive) Raise(nameof(IsActive));
        if (previous.IsMinimized != activity.IsMinimized) Raise(nameof(IsMinimized));
        if (previous.IsVisible != activity.IsVisible) Raise(nameof(IsVisible));
        Raise(nameof(Activity));
        if (ActivityChanged is not { } handlers) return;
        var args = new RunicWindowActivityChangedEventArgs(previous, activity);
        foreach (EventHandler<RunicWindowActivityChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception error) when (error is not OutOfMemoryException) { HandlerFailed(error, nameof(ActivityChanged)); }
        }
    });

    // Always a turn of its own after the current one, also when the host reports the change from inside a turn, as a
    // request a command made completes. Without a model context, or once it closed, changes are applied in order on
    // the reporting thread, and a change a handler causes waits for that handler instead of running inside it.
    private void InTurn(Action change)
    {
        var context = _context?.Invoke();
        if (context is not null && context.TryPost(change)) return;
        lock (_serial)
        {
            _serial.Enqueue(change);
            if (_applying) return;
            _applying = true;
        }
        while (true)
        {
            Action next;
            lock (_serial)
            {
                if (!_serial.TryDequeue(out next!))
                {
                    _applying = false;
                    return;
                }
            }
            next();
        }
    }

    private void Raise(string property)
    {
        if (PropertyChanged is not { } handlers) return;
        var args = new PropertyChangedEventArgs(property);
        foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception error) when (error is not OutOfMemoryException) { HandlerFailed(error, nameof(PropertyChanged)); }
        }
    }

    private void HandlerFailed(Exception error, string handler) =>
        ViewsLog.WindowPresentationHandlerFailed(_logger?.Invoke() ?? TraceFallbackLogger.Instance, error, handler,
            error.GetType().FullName ?? error.GetType().Name);
}
