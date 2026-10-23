using System.Collections.Immutable;
using Runic.Platform;

namespace Runic.Desktop;

/// <summary>Creates platform-native embedded WebView presentations.</summary>
public interface IDesktopWindowHostFactory
{
    /// <summary>Gets whether the required platform runtime is available.</summary>
    bool IsSupported { get; }

    /// <summary>Gets the operations advertised by this provider before opening.</summary>
    DesktopWindowCapabilities Capabilities => DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.Focus |
        DesktopWindowCapabilities.Minimize | DesktopWindowCapabilities.Maximize |
        DesktopWindowCapabilities.Resize | DesktopWindowCapabilities.Move;

    /// <summary>Creates a new, initially closed host.</summary>
    IDesktopWindowHost Create();

    /// <summary>Reports the requested window options this provider cannot apply, without creating a window.</summary>
    /// <remarks>
    /// <see cref="DesktopHost.GetPresentationPreflight"/> and <see cref="DesktopHost.Validate"/> call this before a
    /// window opens. Return one diagnostic per option, with <see cref="DesktopDiagnostic.Option"/> naming the
    /// <see cref="DesktopWindowOptions"/> property: <see cref="DesktopDiagnosticSeverity.Error"/> when
    /// <see cref="IDesktopWindowHost.OpenAsync"/> rejects the option, or <see cref="DesktopDiagnosticSeverity.Warning"/>
    /// when the window ignores it. Keep the check cheap: do not load native libraries. The default reports none.
    /// </remarks>
    IReadOnlyList<DesktopDiagnostic> ValidateOptions(DesktopWindowHostOptions options) => [];

    /// <summary>Reports each missing prerequisite while <see cref="IsSupported"/> is <see langword="false"/>.</summary>
    /// <remarks>
    /// <see cref="DesktopPlatform.GetAvailability"/> and <see cref="DesktopHost.Validate"/> list these instead of a generic
    /// <c>custom-window-host-unavailable</c> diagnostic, so users see every missing native library or service at once.
    /// Keep the check cheap: do not load native libraries. The default reports none.
    /// </remarks>
    IReadOnlyList<DesktopDiagnostic> GetAvailabilityDiagnostics() => [];

    /// <summary>Gets the desktop's monitors in the units of this provider's windows, which <see cref="DesktopHost.Monitors"/> returns.</summary>
    /// <remarks>The default reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>.</remarks>
    IDesktopMonitors Monitors => DesktopMonitors.Unavailable(PlatformUnavailableReason.BackendUnavailable);
}

/// <summary>A window host factory whose windows need it to own the process main thread.</summary>
/// <remarks>
/// <see cref="DesktopEventLoop.Run(DesktopHostOptions, Func{DesktopHost, Task{int}})"/> hands the application to
/// <see cref="RunEventLoop"/> instead of its own loop when <see cref="IDesktopWindowHostFactory.IsSupported"/> is
/// <see langword="true"/>.
/// </remarks>
public interface IDesktopEventLoopWindowHostFactory : IDesktopWindowHostFactory
{
    /// <summary>Runs <paramref name="application"/> while the native toolkit owns the calling main thread.</summary>
    /// <param name="application">The application work. Windows from this factory can open while it runs.</param>
    /// <returns>The value returned by <paramref name="application"/>.</returns>
    int RunEventLoop(Func<Task<int>> application);

    /// <summary>Checks, on the thread that will run the loop, whether <see cref="RunEventLoop"/> can start.</summary>
    /// <remarks>
    /// <see cref="DesktopEventLoop.Run(DesktopHostOptions, Func{DesktopHost, Task{int}})"/> calls this right before
    /// <see cref="RunEventLoop"/>. Return the reasons the loop cannot run, for example that no display is available;
    /// the caller then reports them and runs the plain loop instead, and the factory should report
    /// <see cref="IDesktopWindowHostFactory.IsSupported"/> as <see langword="false"/> from then on. The default
    /// reports none.
    /// </remarks>
    /// <returns>The reasons the loop cannot run, or an empty list when it can.</returns>
    IReadOnlyList<DesktopDiagnostic> PrepareEventLoop() => [];
}

/// <summary>An optional native host exposing its owning dispatcher to platform services.</summary>
public interface IDesktopNativeDispatchWindowHost : IDesktopWindowHost
{
    /// <summary>Gets whether <see cref="InvokeNativeAsync"/> can run work on the window's owning thread.</summary>
    bool SupportsNativeDispatch { get; }

    /// <summary>Gets whether the caller is executing on the thread that owns the native window.</summary>
    /// <returns><see langword="true"/> on the owning thread; otherwise <see langword="false"/>.</returns>
    bool CheckNativeAccess();

    /// <summary>Runs <paramref name="action"/> on the thread that owns the native window.</summary>
    /// <param name="action">The native work to run.</param>
    /// <param name="cancellationToken">Prevents the work from running if it has not started.</param>
    /// <returns>A task that completes after <paramref name="action"/> has run, with any exception it threw.</returns>
    /// <remarks>Cancellation must not interrupt a running callback; the returned task completes when it finishes.</remarks>
    ValueTask InvokeNativeAsync(Action action, CancellationToken cancellationToken = default);
}

/// <summary>An optional native host that reports its window's bounds, maximized and full-screen state.</summary>
/// <remarks>
/// Runic Desktop reads <see cref="State"/> when <see cref="StateChanged"/> is raised and passes it to
/// <see cref="DesktopWindow.State"/> and <see cref="DesktopWindow.StateChanged"/>. Report
/// <see cref="DesktopWindowCapabilities.State"/> in <see cref="IDesktopWindowHost.Capabilities"/> and apply
/// <see cref="DesktopWindowHostOptions.Placement"/> when opening. Use the units of the host's own
/// <see cref="IDesktopWindowHost.MoveAsync"/> and <see cref="IDesktopWindowHost.ResizeAsync"/>.
/// </remarks>
public interface IDesktopWindowStateHost : IDesktopWindowHost
{
    /// <summary>Gets the window's current state, or <see langword="null"/> before the window has reported one.</summary>
    /// <remarks>Keep the last state after the window closes. Called from any thread; keep it cheap.</remarks>
    DesktopWindowState? State { get; }

    /// <summary>Occurs after <see cref="State"/> has changed. It may be raised on the window's owning thread.</summary>
    event EventHandler? StateChanged;
}

/// <summary>An optional native host that enters full screen and places its window on a chosen monitor.</summary>
/// <remarks>
/// Runic Desktop finds it by type, like <see cref="IDesktopWindowStateHost"/>. Report
/// <see cref="DesktopWindowCapabilities.FullScreen"/> and <see cref="DesktopWindowCapabilities.MonitorPlacement"/> in
/// <see cref="IDesktopWindowHost.Capabilities"/> for the operations the window supports; Runic Desktop calls a member only
/// with its flag. When opening, apply <see cref="DesktopWindowHostOptions.Monitor"/> and a
/// <see cref="DesktopWindowPlacement.FullScreen"/> placement, and report <see cref="DesktopWindowState.IsFullScreen"/>,
/// <see cref="DesktopWindowState.MonitorId"/> and <see cref="DesktopWindowPlacement.FullScreen"/> in the state. Monitor
/// identifiers are those of <see cref="IDesktopWindowHostFactory.Monitors"/>.
/// </remarks>
public interface IDesktopPlacementWindowHost : IDesktopWindowHost
{
    /// <summary>Enters or leaves full screen; does nothing when the window already is or is not full-screen.</summary>
    /// <param name="fullScreen"><see langword="true"/> to enter full screen; <see langword="false"/> to leave it.</param>
    /// <param name="cancellationToken">Cancels the queued native work.</param>
    /// <returns>A task that completes when the window has been asked to change.</returns>
    ValueTask SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken);

    /// <summary>Moves the window to a monitor, centered on its work area and shrunk to fit it.</summary>
    /// <param name="monitorId">The <see cref="DesktopMonitor.Id"/> of a connected monitor.</param>
    /// <param name="cancellationToken">Cancels the queued native work.</param>
    /// <returns>A task that completes when the window has moved; it fails with <see cref="ArgumentException"/> when no connected monitor has this identifier.</returns>
    ValueTask MoveToMonitorAsync(string monitorId, CancellationToken cancellationToken);
}

/// <summary>An optional native host that shows the menu bar and context menus.</summary>
/// <remarks>
/// <para>
/// Runic Desktop finds it by type and calls <see cref="SetMenuBarAsync"/> once the window has opened and again after
/// every <see cref="DesktopHost.SetMenuBar"/>, whatever <see cref="DesktopWindowHostOptions.MenuBarPresentation"/> says:
/// the host shows the bar only for <see cref="DesktopMenuBarPresentation.Native"/>, but matches the items' shortcuts
/// for every presentation, after the page has seen the key and left it alone. Report
/// <see cref="DesktopWindowCapabilities.MenuBar"/> and <see cref="DesktopWindowCapabilities.ContextMenu"/> in
/// <see cref="IDesktopWindowHost.Capabilities"/> for what the host provides, and in
/// <see cref="IDesktopWindowHostFactory.Capabilities"/> so validation knows it before a window opens.
/// </para>
/// <para>
/// Run a chosen item or matched shortcut through <see cref="MenuCommands"/>: ask
/// <see cref="IDesktopMenuCommandTarget.CanExecute"/> and call <see cref="IDesktopMenuCommandTarget.Execute"/> on the
/// window's UI thread, and show an item enabled while its declaration and <c>CanExecute</c> allow it. The shared rules
/// are the macOS bar's: <see cref="DesktopMenuItem.IsChecked"/> and <see cref="DesktopMenuItem.RadioGroup"/> as
/// declared, a disabled menu disables its entries, and the standard shortcuts of the platform stay the platform's.
/// </para>
/// </remarks>
public interface IDesktopMenuWindowHost : IDesktopWindowHost
{
    /// <summary>
    /// Gets or sets the commands of the window's menu items: Runic Desktop sets a target that asks the window's
    /// <see cref="DesktopWindow.MenuCommands"/> and then <see cref="DesktopHost.MenuCommands"/>, and logs a target that
    /// throws (event 3014). <see langword="null"/> until Runic Desktop sets it.
    /// </summary>
    IDesktopMenuCommandTarget? MenuCommands { get; set; }

    /// <summary>Gets where the window shows its menu bar now; <see cref="DesktopMenuBarPlacement.None"/> before it opens and after it closes.</summary>
    /// <remarks>Called from any thread; keep it cheap.</remarks>
    DesktopMenuBarPlacement MenuBarPlacement { get; }

    /// <summary>Occurs after <see cref="MenuBarPlacement"/> has changed, for example when a global menu bar appeared or went away. It may be raised on the window's owning thread.</summary>
    event EventHandler? MenuBarPlacementChanged;

    /// <summary>Replaces the window's menus with <paramref name="menus"/>, checked by <see cref="DesktopHost"/>; empty removes them.</summary>
    /// <param name="menus">The menus of <see cref="DesktopHost.MenuBar"/>, in declaration order.</param>
    /// <param name="cancellationToken">Cancels the queued native work.</param>
    /// <returns>A task that completes when the window shows the menus.</returns>
    ValueTask SetMenuBarAsync(IReadOnlyList<DesktopMenu> menus, CancellationToken cancellationToken = default);

    /// <summary>Shows a context menu and completes with the chosen item, or <see langword="null"/> when it was dismissed.</summary>
    /// <param name="items">The entries, already checked against the context-menu rules.</param>
    /// <param name="options">Where the menu opens: <see cref="DesktopContextMenuOptions.X"/> and <see cref="DesktopContextMenuOptions.Y"/> in CSS pixels of the page's viewport, or at the pointer.</param>
    /// <param name="cancellationToken">Closes the menu; the task then completes as cancelled.</param>
    /// <returns>The chosen item, one of <paramref name="items"/>, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Runic Desktop calls it only when <see cref="IDesktopWindowHost.Capabilities"/> includes
    /// <see cref="DesktopWindowCapabilities.ContextMenu"/>. A new context menu of the window dismisses the open one,
    /// which completes with <see langword="null"/>, as does a menu open while the window closes.
    /// </remarks>
    ValueTask<DesktopMenuItem?> ShowContextMenuAsync(ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Hosts one Desktop surface in a platform-native window.</summary>
/// <remarks>
/// Runic Desktop creates one host per opened window, calls its members from arbitrary threads, and disposes it
/// after it closes. Implementations marshal native work to the thread that owns the window.
/// </remarks>
public interface IDesktopWindowHost : IAsyncDisposable
{
    /// <summary>Whether user close requests invoke the configured CloseRequested callback instead of closing.</summary>
    bool SupportsCloseConfirmation => false;

    /// <summary>
    /// Whether the host runs <see cref="DesktopWindowHostOptions.DocumentStartScript"/> in every frame's
    /// document before its page scripts. The surface then withholds session credentials from fetchable scripts.
    /// </summary>
    bool SupportsDocumentStartScript => false;

    /// <summary>Gets operations supported by the native window.</summary>
    DesktopWindowCapabilities Capabilities => DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.Focus |
        DesktopWindowCapabilities.Minimize | DesktopWindowCapabilities.Maximize |
        DesktopWindowCapabilities.Resize | DesktopWindowCapabilities.Move;

    /// <summary>Occurs once after the native window has closed, whether the user or <see cref="CloseAsync"/> closed it.</summary>
    /// <remarks>It may be raised on the window's owning thread; Runic Desktop then disposes the host.</remarks>
    event EventHandler? Closed;

    /// <summary>Gets whether the native window is open.</summary>
    bool IsOpen { get; }

    /// <summary>Gets the platform-native top-level window handle, or zero when the window is not open.</summary>
    /// <remarks>
    /// Report the handle kind of the toolkit, such as an <c>HWND</c> or a <c>GtkWindow*</c>, and document it for the
    /// host's users; <see cref="DesktopWindow.NativeHandle"/> lists the built-in hosts' kinds.
    /// </remarks>
    nint NativeHandle { get; }

    /// <summary>Creates and shows the native window and navigates its WebView to <paramref name="url"/>.</summary>
    /// <param name="url">The surface URL to present.</param>
    /// <param name="options">The initial window state.</param>
    /// <param name="cancellationToken">Cancels opening.</param>
    /// <returns>A task that completes when the window exists and navigation has started.</returns>
    /// <remarks>A failed or cancelled open must release any native resources it created; Runic Desktop then disposes the host.</remarks>
    ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default);

    /// <summary>Navigates the open window's WebView to <paramref name="url"/>.</summary>
    /// <param name="url">The URL to load.</param>
    /// <param name="cancellationToken">Cancels the navigation request.</param>
    /// <returns>A task that completes when navigation has started.</returns>
    ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default);

    /// <summary>Closes the native window without invoking <see cref="DesktopWindowHostOptions.CloseRequested"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the window to close.</param>
    /// <returns>A task that completes when the window has closed.</returns>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>Activates the window and gives it keyboard focus.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the window has been focused.</returns>
    ValueTask FocusAsync(CancellationToken cancellationToken = default);

    /// <summary>Minimizes the window.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the window has been minimized.</returns>
    ValueTask MinimizeAsync(CancellationToken cancellationToken = default);

    /// <summary>Maximizes the window, or restores it when it is already maximized.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the window state has changed.</returns>
    ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default);

    /// <summary>Resizes the window.</summary>
    /// <param name="width">The new width in pixels.</param>
    /// <param name="height">The new height in pixels.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the window has been resized.</returns>
    ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default);

    /// <summary>Moves the window to a screen position.</summary>
    /// <param name="x">The new horizontal position in pixels.</param>
    /// <param name="y">The new vertical position in pixels.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the window has been moved.</returns>
    ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default);

    /// <summary>Shows or hides the window without closing it.</summary>
    /// <param name="visible"><see langword="true"/> to show the window; <see langword="false"/> to hide it.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the visibility has changed.</returns>
    ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default);

    /// <summary>Starts a user-driven native move of the window, as when the page drags a frameless window.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the move has started.</returns>
    /// <remarks>
    /// Runic Desktop ignores a faulted task, so a host that cannot move the window may return a task faulted with
    /// <see cref="NotSupportedException"/>. Do not throw synchronously: that ends the session's receive loop.
    /// </remarks>
    ValueTask BeginMoveAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the screen reader to announce <paramref name="message"/> for the open window.</summary>
    /// <param name="message">The text to speak.</param>
    /// <param name="priority">Whether the message waits for what the screen reader is saying or interrupts it.</param>
    /// <param name="cancellationToken">Cancels the queued request.</param>
    /// <returns>A task that completes when the request has been passed to the platform's accessibility API.</returns>
    /// <remarks>
    /// Runic Desktop calls it only when <see cref="Capabilities"/> includes
    /// <see cref="DesktopWindowCapabilities.Announcements"/>. The default throws <see cref="NotSupportedException"/>.
    /// </remarks>
    ValueTask AnnounceAsync(string message, DesktopAnnouncementPriority priority, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("This window host cannot make announcements."));
}

/// <summary>Describes the immutable initial state of an embedded window host.</summary>
/// <remarks>Runic Desktop derives these values from <see cref="DesktopWindowOptions"/>.</remarks>
public sealed record DesktopWindowHostOptions
{
    /// <summary>When set, suppress user close requests and invoke this callback. CloseAsync must bypass it.</summary>
    public Action? CloseRequested { get; init; }

    /// <summary>Gets the initial window width in pixels. Defaults to 800.</summary>
    public int Width { get; init; } = 800;

    /// <summary>Gets the initial window height in pixels. Defaults to 600.</summary>
    public int Height { get; init; } = 600;

    /// <summary>Gets the minimum window width in pixels, or <see langword="null"/> for no minimum.</summary>
    public int? MinimumWidth { get; init; }

    /// <summary>Gets the minimum window height in pixels, or <see langword="null"/> for no minimum.</summary>
    public int? MinimumHeight { get; init; }

    /// <summary>Gets the initial horizontal screen position in pixels, or <see langword="null"/> to let the platform choose.</summary>
    public int? X { get; init; }

    /// <summary>Gets the initial vertical screen position in pixels, or <see langword="null"/> to let the platform choose.</summary>
    public int? Y { get; init; }

    /// <summary>Gets whether the window opens centered on the screen.</summary>
    public bool Centered { get; init; }

    /// <summary>
    /// Gets the saved placement the window restores, from <see cref="DesktopWindowOptions.Placement"/>, or
    /// <see langword="null"/>. <see cref="Width"/>, <see cref="Height"/>, <see cref="X"/> and <see cref="Y"/> already
    /// hold its values.
    /// </summary>
    /// <remarks>
    /// A host reporting <see cref="DesktopWindowCapabilities.State"/> keeps the restored window inside one of the
    /// current monitors' work areas, shrinking it to fit, and opens it maximized when
    /// <see cref="DesktopWindowPlacement.Maximized"/> is set and <see cref="Kiosk"/> is not.
    /// </remarks>
    public DesktopWindowPlacement? Placement { get; init; }

    /// <summary>Gets whether the user can resize the window. Defaults to <see langword="true"/>.</summary>
    public bool Resizable { get; init; } = true;

    /// <summary>Gets whether the window opens without a title bar and border.</summary>
    public bool Frameless { get; init; }

    /// <summary>Gets whether the window has a transparent background where the page does not paint.</summary>
    public bool Transparent { get; init; }

    /// <summary>Gets whether the window is created without being shown.</summary>
    public bool Hidden { get; init; }

    /// <summary>Gets whether the window opens full-screen without decorations.</summary>
    public bool Kiosk { get; init; }

    /// <summary>Gets whether the presentation uses a high-contrast theme, already resolved from the system setting when unset.</summary>
    public bool HighContrast { get; init; }

    /// <summary>Gets the path of the window icon file, or <see langword="null"/> for the default icon.</summary>
    public string? IconFile { get; init; }

    /// <summary>Gets the WebView profile or user-data directory, or <see langword="null"/> for the host's default.</summary>
    public string? ProfilePath { get; init; }

    /// <summary>Gets additional WebView command-line arguments as one unsplit string: <see cref="DesktopWindowOptions.BrowserArguments"/>.</summary>
    public string? BrowserArguments { get; init; }

    /// <summary>Gets the sensitive permissions the host may grant, only to the origin of the presented URL.</summary>
    public DesktopPermissionGrant AllowedPermissions { get; init; }

    /// <summary>
    /// Gets what the window does with navigation away from the application, new windows, downloads and developer tools
    /// (<see cref="DesktopWindowOptions.WebContent"/>). Runic Desktop resolves the Development default, so
    /// <see cref="DesktopWebContentPolicy.DevTools"/> is never <see langword="null"/> here.
    /// </summary>
    /// <remarks>
    /// A host that can intercept them applies it to top-level navigations, new-window requests and downloads: the
    /// application's origins are the presented URL's origin and <see cref="AdditionalOrigins"/>.
    /// </remarks>
    public DesktopWebContentPolicy WebContent { get; init; } = SafeWebContent;

    // The default for hosts given no policy: the safe defaults with developer tools off.
    internal static DesktopWebContentPolicy SafeWebContent { get; } = DesktopWebContentPolicy.Default with { DevTools = false };

    /// <summary>
    /// Gets the application's origins besides the presented URL's: the surface's own origin and its
    /// <see cref="DesktopSecurityPolicy.AdditionalOrigins"/>.
    /// </summary>
    public IReadOnlyList<Uri> AdditionalOrigins { get; init; } = [];

    /// <summary>
    /// Gets the script a host reporting <see cref="IDesktopWindowHost.SupportsDocumentStartScript"/> must add before
    /// its first navigation. It carries session credentials: do not log, persist, or expose it to page content.
    /// </summary>
    public string? DocumentStartScript { get; init; }

    /// <summary>Gets where the window shows the menu bar, from <see cref="DesktopWindowOptions.MenuBarPresentation"/>.</summary>
    /// <remarks>A host implementing <see cref="IDesktopMenuWindowHost"/> applies it; others ignore it.</remarks>
    public DesktopMenuBarPresentation MenuBarPresentation { get; init; }

    /// <summary>
    /// Gets whether a <see cref="DesktopMenuBarPresentation.Native"/> bar may go into the Linux desktop's global menu,
    /// from <see cref="LinuxDesktopOptions.GlobalMenu"/>; <see langword="true"/> by default.
    /// </summary>
    /// <remarks>
    /// A Linux host implementing <see cref="IDesktopMenuWindowHost"/> that can export its bar applies it and reports
    /// <see cref="DesktopMenuBarPlacement.Global"/> while the global menu shows the bar; others ignore it.
    /// </remarks>
    public bool GlobalMenu { get; init; } = true;

    /// <summary>Gets the monitor to open on, from <see cref="DesktopWindowOptions.Monitor"/>; <see langword="null"/> when a placement's position or the platform decides.</summary>
    /// <remarks>A host implementing <see cref="IDesktopPlacementWindowHost"/> applies it; others ignore it.</remarks>
    public string? Monitor { get; init; }

    /// <summary>Gets the direction of the window's native parts, from <see cref="DesktopWindowOptions.LayoutDirection"/>.</summary>
    public DesktopLayoutDirection LayoutDirection { get; init; }

    /// <summary>Gets the BCP 47 language of the web view's own text, from <see cref="DesktopWindowOptions.Language"/>, or <see langword="null"/>.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// Gets the callback that reports an event of the open window, such as a crashed web process, to
    /// <see cref="DesktopHostOptions.DiagnosticSink"/> and the Desktop logger (event 3010). The host may call it on any
    /// thread, including the window's own; a sink that throws is logged, not rethrown.
    /// </summary>
    public Action<DesktopDiagnostic>? DiagnosticSink { get; init; }

    // The Bridge handshake log of the presentation (WebUiEmbeddedHostOptions.StartupProgress), for Runic's own hosts.
    internal Action<string>? StartupProgress { get; init; }
}

internal sealed class DesktopWindowHostFactoryAdapter(IDesktopWindowHostFactory factory) : IWebUiEmbeddedHostFactory
{
    public bool IsSupported => factory.IsSupported;

    public IWebUiEmbeddedHost Create() => new DesktopWindowHostAdapter(factory.Create());
}

// A window host inside a window of its own toolkit, whose announcements its page makes while the page has the
// keyboard focus (I-203): RunicWebView in WPF, where WpfPlatformServices announces for the WPF window. Runic Desktop
// gives it the window's in-page announcer (WebUiWindow.AnnounceInPageAsync), which reports false when no page is
// connected, and prepares the page's live regions when a page connects.
internal interface IDesktopPageAnnouncementHost : IDesktopWindowHost
{
    Func<string, DesktopAnnouncementPriority, CancellationToken, Task<bool>>? AnnounceInPage { set; }
}

internal sealed class DesktopWindowHostAdapter : IWebUiEmbeddedHost
{
    private readonly IDesktopWindowHost _host;

    internal bool AnnouncesInPage => _host is IDesktopPageAnnouncementHost;

    internal void AttachPageAnnouncer(Func<string, DesktopAnnouncementPriority, CancellationToken, Task<bool>> announce)
    {
        if (_host is IDesktopPageAnnouncementHost pageHost) pageHost.AnnounceInPage = announce;
    }

    internal DesktopWindowHostAdapter(IDesktopWindowHost host)
    {
        _host = host;
        _host.Closed += OnClosed;
        if (_host is IDesktopWindowStateHost stateHost)
        {
            stateHost.StateChanged += OnStateChanged;
        }
        if (_host is IDesktopFileDropWindowHost dropHost)
        {
            dropHost.FilesDropped += OnFilesDropped;
        }
        if (_host is IDesktopMenuWindowHost menuHost)
        {
            menuHost.MenuBarPlacementChanged += OnMenuBarPlacementChanged;
        }
    }

    public ValueTask<DesktopDragEffects> StartDragAsync(DesktopDragData data, CancellationToken cancellationToken = default) =>
        _host is IDesktopDragSourceWindowHost dragHost
            ? dragHost.StartDragAsync(data, cancellationToken)
            : ValueTask.FromException<DesktopDragEffects>(new NotSupportedException("This window host starts no drags."));

    public ValueTask SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default) =>
        _host is IDesktopPlacementWindowHost placementHost
            ? placementHost.SetFullScreenAsync(fullScreen, cancellationToken)
            : ValueTask.FromException(new NotSupportedException("This window host cannot enter full screen."));

    public ValueTask MoveToMonitorAsync(string monitorId, CancellationToken cancellationToken = default) =>
        _host is IDesktopPlacementWindowHost placementHost
            ? placementHost.MoveToMonitorAsync(monitorId, cancellationToken)
            : ValueTask.FromException(new NotSupportedException("This window host cannot place its window on a monitor."));

    public event EventHandler<DesktopFileDropEventArgs>? FilesDropped;

    public event EventHandler? MenuBarPlacementChanged;

    public IDesktopMenuCommandTarget? MenuCommands
    {
        get => _host is IDesktopMenuWindowHost menuHost ? menuHost.MenuCommands : null;
        set
        {
            if (_host is IDesktopMenuWindowHost menuHost) menuHost.MenuCommands = value;
        }
    }

    public DesktopMenuBarPlacement MenuBarPlacement =>
        _host is IDesktopMenuWindowHost menuHost ? menuHost.MenuBarPlacement : DesktopMenuBarPlacement.None;

    public ValueTask SetMenuBarAsync(IReadOnlyList<DesktopMenu> menus, CancellationToken cancellationToken = default) =>
        _host is IDesktopMenuWindowHost menuHost ? menuHost.SetMenuBarAsync(menus, cancellationToken) : ValueTask.CompletedTask;

    public ValueTask<DesktopMenuItem?> ShowContextMenuAsync(ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions options,
        CancellationToken cancellationToken = default) =>
        _host is IDesktopMenuWindowHost menuHost
            ? menuHost.ShowContextMenuAsync(items, options, cancellationToken)
            : ValueTask.FromException<DesktopMenuItem?>(new NotSupportedException("This window host shows no context menus."));

    public event EventHandler? Closed;

    public event EventHandler? StateChanged;

    public DesktopWindowState? State => _host is IDesktopWindowStateHost stateHost ? stateHost.State : null;

    public bool IsOpen => _host.IsOpen;

    public bool SupportsCloseConfirmation => _host.SupportsCloseConfirmation;
    public bool SupportsDocumentStartScript => _host.SupportsDocumentStartScript;
    public DesktopWindowCapabilities Capabilities => _host.Capabilities;
    public bool SupportsNativeDispatch => _host is IDesktopNativeDispatchWindowHost { SupportsNativeDispatch: true };
    public bool CheckNativeAccess() => _host is IDesktopNativeDispatchWindowHost native && native.CheckNativeAccess();
    public ValueTask InvokeNativeAsync(Action action, CancellationToken cancellationToken) =>
        _host is IDesktopNativeDispatchWindowHost native
            ? native.InvokeNativeAsync(action, cancellationToken)
            : ValueTask.FromException(new NotSupportedException("This host does not expose native dispatch."));

    public nint NativeHandle => _host.NativeHandle;

    public ValueTask ShowAsync(
        Uri url,
        WebUiEmbeddedHostOptions options,
        CancellationToken cancellationToken = default) =>
        _host.OpenAsync(url, new DesktopWindowHostOptions
        {
            CloseRequested = options.CloseRequested,
            Width = (int)options.Width,
            Height = (int)options.Height,
            MinimumWidth = (int?)options.MinimumWidth,
            MinimumHeight = (int?)options.MinimumHeight,
            X = options.X,
            Y = options.Y,
            Centered = options.Centered,
            Placement = options.Placement,
            Resizable = options.Resizable,
            Frameless = options.Frameless,
            Transparent = options.Transparent,
            Hidden = options.Hidden,
            Kiosk = options.Kiosk,
            HighContrast = options.HighContrast,
            IconFile = options.IconFile,
            ProfilePath = options.ProfilePath,
            BrowserArguments = options.BrowserArguments,
            AllowedPermissions = options.AllowedPermissions,
            WebContent = options.WebContent,
            AdditionalOrigins = options.AdditionalOrigins,
            DocumentStartScript = options.DocumentStartScript,
            DiagnosticSink = options.ReportDiagnostic,
            StartupProgress = options.StartupProgress,
            MenuBarPresentation = options.MenuBarPresentation,
            GlobalMenu = options.GlobalMenu,
            Monitor = options.Monitor,
            LayoutDirection = options.LayoutDirection,
            Language = options.Language,
        }, cancellationToken);

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) =>
        _host.NavigateAsync(url, cancellationToken);

    public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
        _host.CloseAsync(cancellationToken);

    public ValueTask FocusAsync(CancellationToken cancellationToken = default) =>
        _host.FocusAsync(cancellationToken);

    public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) =>
        _host.MinimizeAsync(cancellationToken);

    public ValueTask MaximizeAsync(CancellationToken cancellationToken = default) =>
        _host.ToggleMaximizedAsync(cancellationToken);

    public ValueTask SetSizeAsync(uint width, uint height, CancellationToken cancellationToken = default) =>
        _host.ResizeAsync(checked((int)width), checked((int)height), cancellationToken);

    public ValueTask SetPositionAsync(int x, int y, CancellationToken cancellationToken = default) =>
        _host.MoveAsync(x, y, cancellationToken);

    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        _host.SetVisibleAsync(visible, cancellationToken);

    public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) =>
        _host.BeginMoveAsync(cancellationToken);

    public ValueTask AnnounceAsync(string message, DesktopAnnouncementPriority priority, CancellationToken cancellationToken = default) =>
        _host.AnnounceAsync(message, priority, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _host.Closed -= OnClosed;
        if (_host is IDesktopWindowStateHost stateHost)
        {
            stateHost.StateChanged -= OnStateChanged;
        }
        if (_host is IDesktopFileDropWindowHost dropHost)
        {
            dropHost.FilesDropped -= OnFilesDropped;
        }
        if (_host is IDesktopMenuWindowHost menuHost)
        {
            menuHost.MenuBarPlacementChanged -= OnMenuBarPlacementChanged;
        }
        await _host.DisposeAsync().ConfigureAwait(false);
    }

    private void OnClosed(object? sender, EventArgs eventArgs) => Closed?.Invoke(this, eventArgs);

    private void OnStateChanged(object? sender, EventArgs eventArgs) => StateChanged?.Invoke(this, eventArgs);

    private void OnFilesDropped(object? sender, DesktopFileDropEventArgs eventArgs) => FilesDropped?.Invoke(this, eventArgs);

    private void OnMenuBarPlacementChanged(object? sender, EventArgs eventArgs) => MenuBarPlacementChanged?.Invoke(this, eventArgs);
}
