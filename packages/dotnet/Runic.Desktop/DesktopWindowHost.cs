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

/// <summary>
/// An optional native host that moves and resizes its window in one operation and changes its minimum size while it is
/// open.
/// </summary>
/// <remarks>
/// <para>
/// Runic Desktop finds it by type, like <see cref="IDesktopWindowStateHost"/>. Report
/// <see cref="DesktopWindowCapabilities.Bounds"/> and <see cref="DesktopWindowCapabilities.MinimumSize"/> in
/// <see cref="IDesktopWindowHost.Capabilities"/> for the operations the window supports; Runic Desktop calls a member only
/// with its flag. With <see cref="DesktopWindowCapabilities.Bounds"/>, report <see cref="DesktopWindowState.Bounds"/>
/// in the state in the units <see cref="SetBoundsAsync"/> takes, and <see cref="DesktopWindowState.VisibleBounds"/>
/// and <see cref="DesktopWindowState.IsSnapped"/> where the toolkit knows them.
/// </para>
/// <para>
/// A host without it, or without a flag, makes <see cref="DesktopWindow.SetBoundsAsync"/> and
/// <see cref="DesktopWindow.SetMinimumSizeAsync"/> fail with <c>window-capability-unavailable</c>.
/// </para>
/// </remarks>
public interface IDesktopWindowBoundsHost : IDesktopWindowHost
{
    /// <summary>Moves and resizes the window in one native operation, so it is never shown moved but not resized.</summary>
    /// <param name="bounds">
    /// The frame in the units of <see cref="DesktopWindowState.Bounds"/>, checked by Runic Desktop: a positive size, and
    /// <see cref="DesktopWindowBounds.X"/> and <see cref="DesktopWindowBounds.Y"/> both set or both
    /// <see langword="null"/>, which keeps the top-left corner where it is.
    /// </param>
    /// <param name="cancellationToken">Cancels the queued native work.</param>
    /// <returns>A task that completes when the window has been asked to take the bounds.</returns>
    /// <remarks>
    /// Fail with <see cref="InvalidOperationException"/> while the window is maximized, full-screen or minimized, and with
    /// a <see cref="DesktopException"/> coded <c>window-position-unavailable</c> for a position the toolkit cannot set.
    /// </remarks>
    ValueTask SetBoundsAsync(DesktopWindowBounds bounds, CancellationToken cancellationToken = default);

    /// <summary>Changes the window's minimum size, growing the window when it is smaller.</summary>
    /// <param name="width">The minimum width in the units of <see cref="DesktopWindowHostOptions.MinimumWidth"/>, or zero for none.</param>
    /// <param name="height">The minimum height in the units of <see cref="DesktopWindowHostOptions.MinimumHeight"/>, or zero for none.</param>
    /// <param name="cancellationToken">Cancels the queued native work.</param>
    /// <returns>A task that completes when the toolkit enforces the minimum.</returns>
    ValueTask SetMinimumSizeAsync(int width, int height, CancellationToken cancellationToken = default);
}

/// <summary>An optional native host that enters full screen and places its window on a chosen monitor.</summary>
/// <remarks>
/// Runic Desktop finds it by type, like <see cref="IDesktopWindowStateHost"/>. Report
/// <see cref="DesktopWindowCapabilities.FullScreen"/> and <see cref="DesktopWindowCapabilities.MonitorPlacement"/> in
/// <see cref="IDesktopWindowHost.Capabilities"/> for the operations the window supports; Runic Desktop calls a member only
/// with its flag. When opening, apply <see cref="DesktopWindowHostOptions.MonitorId"/> and a
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
    ValueTask SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default);

    /// <summary>Moves the window to a monitor, centered on its work area and shrunk to fit it.</summary>
    /// <param name="monitorId">The <see cref="DesktopMonitor.Id"/> of a connected monitor.</param>
    /// <param name="cancellationToken">Cancels the queued native work.</param>
    /// <returns>A task that completes when the window has moved; it fails with <see cref="ArgumentException"/> when no connected monitor has this identifier.</returns>
    ValueTask MoveToMonitorAsync(string monitorId, CancellationToken cancellationToken = default);
}

/// <summary>An optional native host whose spell checker takes languages.</summary>
/// <remarks>
/// Runic Desktop finds it by type, like <see cref="IDesktopWindowStateHost"/>, and calls
/// <see cref="SetSpellCheckingLanguagesAsync"/> after <see cref="DesktopHost.SetSpellCheckingLanguagesAsync"/>, and after
/// the window opened when the languages changed while it was opening. When opening, apply
/// <see cref="DesktopWindowHostOptions.SpellCheckingLanguages"/>. A host whose checker has no languages, such as
/// WebView2, does not implement it.
/// </remarks>
public interface IDesktopSpellCheckingWindowHost : IDesktopWindowHost
{
    /// <summary>Changes the languages the window's spell checker checks.</summary>
    /// <param name="languages">BCP 47 language tags, checked by <see cref="DesktopHost"/>; empty for the user's languages.</param>
    /// <param name="cancellationToken">Cancels the queued native work.</param>
    /// <returns>A task that completes when the checker uses the languages.</returns>
    ValueTask SetSpellCheckingLanguagesAsync(IReadOnlyList<string> languages, CancellationToken cancellationToken = default);
}

/// <summary>An optional native host that reports whether its window is active, minimized and visible.</summary>
/// <remarks>
/// Runic Desktop finds it by type, like <see cref="IDesktopWindowStateHost"/>, reads <see cref="Activity"/> when
/// <see cref="ActivityChanged"/> is raised, and passes it to <see cref="DesktopWindow.Activity"/> and
/// <see cref="DesktopWindow.ActivityChanged"/>. Report <see cref="DesktopWindowCapabilities.Activity"/> in
/// <see cref="IDesktopWindowHost.Capabilities"/>, and raise the event when the window gains or loses the keyboard focus,
/// is minimized or restored, and is shown or hidden.
/// </remarks>
public interface IDesktopActivityWindowHost : IDesktopWindowHost
{
    /// <summary>Gets the window's current activity, or <see langword="null"/> before the window has reported one.</summary>
    /// <remarks>Called from any thread; keep it cheap. After the window closed, report it inactive and not visible.</remarks>
    DesktopWindowActivity? Activity { get; }

    /// <summary>Occurs after <see cref="Activity"/> has changed. It may be raised on the window's owning thread.</summary>
    event EventHandler? ActivityChanged;
}

/// <summary>An optional native host that changes its window's light or dark appearance while it is open.</summary>
/// <remarks>
/// Runic Desktop finds it by type and calls <see cref="SetColorSchemeAsync"/> after
/// <see cref="DesktopWindow.SetColorSchemeAsync"/> and <see cref="DesktopHost.SetColorSchemeAsync"/>. Report
/// <see cref="DesktopWindowCapabilities.ColorScheme"/> in <see cref="IDesktopWindowHost.Capabilities"/>. The page's
/// <c>prefers-color-scheme</c> should follow, as it does for <see cref="DesktopWindowHostOptions.ColorScheme"/>.
/// </remarks>
public interface IDesktopColorSchemeWindowHost : IDesktopWindowHost
{
    /// <summary>Changes the window's appearance.</summary>
    /// <param name="colorScheme">
    /// The scheme to show, already resolved: the window's own, else the application's
    /// (<see cref="DesktopHost.ColorScheme"/>). <see cref="DesktopColorScheme.NoPreference"/> follows the system.
    /// </param>
    /// <param name="cancellationToken">Cancels the queued native work.</param>
    /// <returns>A task that completes when the window and its page show the scheme.</returns>
    ValueTask SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default);
}

/// <summary>An optional native host that changes the direction of its window's native parts while it is open.</summary>
/// <remarks>
/// Runic Desktop finds it by type and calls <see cref="SetLayoutDirectionAsync"/> after
/// <see cref="DesktopWindow.SetLayoutDirectionAsync"/>. Report <see cref="DesktopWindowCapabilities.LayoutDirection"/> in
/// <see cref="IDesktopWindowHost.Capabilities"/>. The page keeps its own <c>dir</c>, as for
/// <see cref="DesktopWindowHostOptions.LayoutDirection"/>.
/// </remarks>
public interface IDesktopLayoutDirectionWindowHost : IDesktopWindowHost
{
    /// <summary>Changes the direction of the window, its menu bar and the other native parts it draws.</summary>
    /// <param name="layoutDirection">
    /// The direction to show. <see cref="DesktopLayoutDirection.Default"/> returns to the platform's.
    /// </param>
    /// <param name="cancellationToken">Cancels the queued native work.</param>
    /// <returns>A task that completes when the window shows the direction.</returns>
    ValueTask SetLayoutDirectionAsync(DesktopLayoutDirection layoutDirection, CancellationToken cancellationToken = default);
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
    /// Gets the light or dark appearance of the window and its page, from <see cref="DesktopWindowOptions.ColorScheme"/>;
    /// <see cref="DesktopColorScheme.NoPreference"/> follows the system.
    /// </summary>
    /// <remarks>
    /// A host that applies it sets the page's <c>prefers-color-scheme</c> and, where it can, its window's appearance. A
    /// host that cannot reports <c>color-scheme-unsupported</c> from <see cref="IDesktopWindowHostFactory.ValidateOptions"/>.
    /// </remarks>
    public DesktopColorScheme ColorScheme { get; init; }

    /// <summary>
    /// Gets whether a <see cref="DesktopMenuBarPresentation.Native"/> bar may go into the Linux desktop's global menu,
    /// from <see cref="LinuxDesktopOptions.GlobalMenu"/>; <see langword="true"/> by default.
    /// </summary>
    /// <remarks>
    /// A Linux host implementing <see cref="IDesktopMenuWindowHost"/> that can export its bar applies it and reports
    /// <see cref="DesktopMenuBarPlacement.Global"/> while the global menu shows the bar; others ignore it.
    /// </remarks>
    public bool GlobalMenu { get; init; } = true;

    /// <summary>
    /// Gets the native handle of the window that owns this one, from <see cref="DesktopWindowOptions.Owner"/>, or zero
    /// for a window of its own. It is the kind <see cref="IDesktopWindowHost.NativeHandle"/> reports, such as a
    /// <c>GtkWindow*</c>.
    /// </summary>
    /// <remarks>
    /// A host reporting <see cref="DesktopWindowCapabilities.Ownership"/> keeps the window above its owner, as a
    /// toolkit's transient or owned window does. Use the handle only on the window's UI thread, and only while opening
    /// and closing the window. Runic Desktop passes zero to a host without the capability.
    /// </remarks>
    public nint OwnerHandle { get; init; }

    /// <summary>Gets whether the window blocks its owner, from <see cref="DesktopWindowOptions.Modality"/>.</summary>
    /// <remarks>
    /// With <see cref="DesktopWindowModality.Window"/>, a host reporting <see cref="DesktopWindowCapabilities.Ownership"/>
    /// makes the owner take no input, and ignore the user's close, while the window is shown (a hidden one does not
    /// block it), and leaves the application's other windows usable. Several windows may be modal to one owner at once: the owner takes input again when the last
    /// of them closed. It is <see cref="DesktopWindowModality.None"/> when <see cref="OwnerHandle"/> is zero.
    /// </remarks>
    public DesktopWindowModality Modality { get; init; }

    /// <summary>Gets the monitor to open on, from <see cref="DesktopWindowOptions.MonitorId"/>; <see langword="null"/> when a placement's position or the platform decides.</summary>
    /// <remarks>A host implementing <see cref="IDesktopPlacementWindowHost"/> applies it; others ignore it.</remarks>
    public string? MonitorId { get; init; }

    /// <summary>Gets the direction of the window's native parts, from <see cref="DesktopWindowOptions.LayoutDirection"/>.</summary>
    public DesktopLayoutDirection LayoutDirection { get; init; }

    /// <summary>Gets the BCP 47 language of the web view's own text, from <see cref="DesktopWindowOptions.Language"/>, or <see langword="null"/>.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// Gets whether the page's fields are spell-checked, from <see cref="DesktopWindowOptions.SpellChecking"/>;
    /// <see langword="true"/> by default.
    /// </summary>
    /// <remarks>
    /// Runic Desktop applies <see langword="false"/> through <see cref="DocumentStartScript"/>, which then sets
    /// <c>spellcheck="false"</c> on every document's root element, so a host that runs that script need do nothing
    /// more. A host with a spell checker that is off until it is turned on, such as WebKitGTK's, turns it on.
    /// </remarks>
    public bool SpellChecking { get; init; } = true;

    /// <summary>
    /// Gets the BCP 47 languages the spell checker checks, from <see cref="DesktopHost.SpellCheckingLanguages"/>; empty
    /// for the user's languages.
    /// </summary>
    /// <remarks>A host implementing <see cref="IDesktopSpellCheckingWindowHost"/> applies them; others ignore them.</remarks>
    public IReadOnlyList<string> SpellCheckingLanguages { get; init; } = [];

    /// <summary>
    /// Gets the callback that reports an event of the open window, such as a crashed web process, to
    /// <see cref="DesktopHostOptions.DiagnosticSink"/> and the Desktop logger (event 3010). The host may call it on any
    /// thread, including the window's own; a sink that throws is logged, not rethrown.
    /// </summary>
    public Action<DesktopDiagnostic>? DiagnosticSink { get; init; }

    // The Bridge handshake log of the presentation (WebUiEmbeddedHostOptions.StartupProgress), for Runic's own hosts.
    internal Action<string>? StartupProgress { get; init; }

    // The connection deadline's startup signals (WebUiEmbeddedHostOptions.StartupSignal), for Runic's own hosts.
    internal Action<Internal.WebViewStartupSignal>? StartupSignal { get; init; }

    /// <summary>
    /// Gets the key of the data store that is not persisted this window uses, or <see langword="null"/>, the default, for
    /// the host's usual data store.
    /// </summary>
    /// <remarks>
    /// Runic Desktop sets it for the windows of a hosted application (the Runic host protocol, <c>runic-host/1</c>): their
    /// cookies, storage and caches must stay in memory, shared by the windows of the same key and by no other window, so
    /// the hosted application's server never receives the host application's cookies and two hosted applications never
    /// share storage. Runic Desktop gives a key only to a host that implements
    /// <see cref="IDesktopEphemeralDataStoreWindowHost"/>; a hosted window fails to open on any other. Experimental with the
    /// rest of the protocol; see <c>docs/experimental.md</c>.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public string? EphemeralDataStore { get; init; }
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
/// <summary>
/// A window host that honours <see cref="DesktopWindowHostOptions.EphemeralDataStore"/>: it opens a window with a key in a
/// data store that is not persisted, which only the windows of the same key share.
/// </summary>
/// <remarks>
/// Runic Desktop shows a hosted application's window (the Runic host protocol, <c>runic-host/1</c>) only in a window host
/// that implements it, since the window's page would otherwise share the host application's cookies and storage.
/// Runic.Desktop.Gtk4 implements it with an ephemeral WebKitNetworkSession for each key. Experimental with the rest of the
/// protocol; see <c>docs/experimental.md</c>.
/// </remarks>
[System.Diagnostics.CodeAnalysis.Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
#pragma warning disable CA1040 // The interface is the host's declaration that it honours the option.
public interface IDesktopEphemeralDataStoreWindowHost : IDesktopWindowHost;
#pragma warning restore CA1040

// sdk#864, P4: a first-party custom window host that shows the side panel (Runic.Desktop.Gtk4), through the same seam as
// the built-in hosts (IWebUiEmbeddedHost.SupportsSidePanel, CheckSidePanel, OpenSidePanelAsync). Other custom window hosts
// refuse the panel (D062.8).
internal interface IDesktopSidePanelWindowHost : IDesktopWindowHost
{
    bool SupportsSidePanel { get; }

    DesktopSidePanelRefusal? CheckSidePanel(bool left, int width);

    ValueTask<Internal.WebUiSidePanelOpening> OpenSidePanelAsync(Internal.WebUiSidePanelRequest request, CancellationToken cancellationToken);
}

internal interface IDesktopPageAnnouncementHost : IDesktopWindowHost
{
    Func<string, DesktopAnnouncementPriority, CancellationToken, Task<bool>>? AnnounceInPage { set; }
}

internal sealed class DesktopWindowHostAdapter : IWebUiEmbeddedHost
{
    private readonly IDesktopWindowHost _host;

    internal bool AnnouncesInPage => _host is IDesktopPageAnnouncementHost;

    internal const string EphemeralDataStoreUnavailableCode = "hosted-data-store-unavailable";

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
        if (_host is IDesktopActivityWindowHost activityHost)
        {
            activityHost.ActivityChanged += OnActivityChanged;
        }
    }

    public event EventHandler? ActivityChanged;

    public DesktopWindowActivity? Activity => _host is IDesktopActivityWindowHost activityHost ? activityHost.Activity : null;

    public ValueTask SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default) =>
        _host is IDesktopColorSchemeWindowHost schemeHost
            ? schemeHost.SetColorSchemeAsync(colorScheme, cancellationToken)
            : ValueTask.FromException(new NotSupportedException("This window host cannot change its color scheme."));

    public ValueTask SetLayoutDirectionAsync(DesktopLayoutDirection layoutDirection, CancellationToken cancellationToken = default) =>
        _host is IDesktopLayoutDirectionWindowHost directionHost
            ? directionHost.SetLayoutDirectionAsync(layoutDirection, cancellationToken)
            : ValueTask.FromException(new NotSupportedException("This window host cannot change its layout direction."));

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

    public ValueTask SetBoundsAsync(DesktopWindowBounds bounds, CancellationToken cancellationToken = default) =>
        _host is IDesktopWindowBoundsHost boundsHost
            ? boundsHost.SetBoundsAsync(bounds, cancellationToken)
            : ValueTask.FromException(new NotSupportedException("This window host cannot set its bounds in one operation."));

    public ValueTask SetMinimumSizeAsync(int width, int height, CancellationToken cancellationToken = default) =>
        _host is IDesktopWindowBoundsHost boundsHost
            ? boundsHost.SetMinimumSizeAsync(width, height, cancellationToken)
            : ValueTask.FromException(new NotSupportedException("This window host cannot change its minimum size."));

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
        options.EphemeralDataStore is not null && _host is not IDesktopEphemeralDataStoreWindowHost
            ? ValueTask.FromException(new DesktopException(DesktopErrorCategory.Unavailable, EphemeralDataStoreUnavailableCode,
                "The custom window host cannot give a hosted application's window a data store of its own, so it cannot show it."))
            : _host.OpenAsync(url, new DesktopWindowHostOptions
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
            StartupSignal = options.StartupSignal,
            EphemeralDataStore = options.EphemeralDataStore,
            MenuBarPresentation = options.MenuBarPresentation,
            ColorScheme = options.ColorScheme,
            GlobalMenu = options.GlobalMenu,
            MonitorId = options.MonitorId,
            LayoutDirection = options.LayoutDirection,
            Language = options.Language,
            SpellChecking = options.SpellChecking,
            SpellCheckingLanguages = options.SpellCheckingLanguages,
            OwnerHandle = options.OwnerHandle,
            Modality = options.Modality,
        }, cancellationToken);

    public bool SupportsSidePanel => _host is IDesktopSidePanelWindowHost { SupportsSidePanel: true };

    public DesktopSidePanelRefusal? CheckSidePanel(bool left, int width) =>
        _host is IDesktopSidePanelWindowHost panelHost ? panelHost.CheckSidePanel(left, width) : DesktopSidePanelRefusal.Unsupported;

    public ValueTask<Internal.WebUiSidePanelOpening> OpenSidePanelAsync(Internal.WebUiSidePanelRequest request,
        CancellationToken cancellationToken = default) =>
        _host is IDesktopSidePanelWindowHost panelHost
            ? panelHost.OpenSidePanelAsync(request, cancellationToken)
            : ValueTask.FromResult(Internal.WebUiSidePanelOpening.Refused(DesktopSidePanelRefusal.Unsupported));

    public ValueTask SetSpellCheckingLanguagesAsync(IReadOnlyList<string> languages, CancellationToken cancellationToken = default) =>
        _host is IDesktopSpellCheckingWindowHost spellingHost
            ? spellingHost.SetSpellCheckingLanguagesAsync(languages, cancellationToken)
            : ValueTask.CompletedTask;

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
        if (_host is IDesktopActivityWindowHost activityHost)
        {
            activityHost.ActivityChanged -= OnActivityChanged;
        }
        await _host.DisposeAsync().ConfigureAwait(false);
    }

    private void OnClosed(object? sender, EventArgs eventArgs) => Closed?.Invoke(this, eventArgs);

    private void OnStateChanged(object? sender, EventArgs eventArgs) => StateChanged?.Invoke(this, eventArgs);

    private void OnFilesDropped(object? sender, DesktopFileDropEventArgs eventArgs) => FilesDropped?.Invoke(this, eventArgs);

    private void OnMenuBarPlacementChanged(object? sender, EventArgs eventArgs) => MenuBarPlacementChanged?.Invoke(this, eventArgs);

    private void OnActivityChanged(object? sender, EventArgs eventArgs) => ActivityChanged?.Invoke(this, eventArgs);
}
