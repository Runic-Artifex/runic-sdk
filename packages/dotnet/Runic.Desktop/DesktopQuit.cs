namespace Runic.Desktop;

/// <summary>Where a request to quit the application comes from (<see cref="DesktopQuitRequest.Source"/>).</summary>
/// <remarks>This set is open: a minor release may add values, so handle unknown ones as <see cref="Menu"/>.</remarks>
public enum DesktopQuitSource
{
    /// <summary>
    /// The application's Quit command: on macOS the Quit item of the application menu (Command and Q), or another
    /// sender of AppKit's <c>terminate:</c>, such as a status item's Quit.
    /// </summary>
    Menu,

    /// <summary>
    /// On macOS, Quit in the application's Dock menu, or another application that asks it to quit with the quit Apple
    /// event, such as a script.
    /// </summary>
    Dock,

    /// <summary>On macOS, logging out, restarting or shutting down: the system's quit Apple event, which names that reason.</summary>
    System,

    /// <summary>
    /// Ctrl+C, SIGTERM, SIGHUP or a closed Windows console, which
    /// <see cref="DesktopEventLoop.Run(DesktopHostOptions, Func{DesktopHost, Task{int}})"/> handles unless
    /// <see cref="DesktopHostOptions.HandleShutdownSignals"/> is <see langword="false"/>.
    /// </summary>
    Signal,
}

/// <summary>A request to quit the application, which <see cref="DesktopHostOptions.ConfirmQuitAsync"/> may refuse.</summary>
/// <param name="Source">Where the request comes from.</param>
public sealed record DesktopQuitRequest(DesktopQuitSource Source);

/// <summary>The data of <see cref="DesktopHost.ReopenRequested"/>.</summary>
public sealed class DesktopReopenEventArgs : EventArgs
{
    /// <summary>Creates the data of a reopen request.</summary>
    /// <param name="hasVisibleWindows">Whether the application has a visible window.</param>
    public DesktopReopenEventArgs(bool hasVisibleWindows) => HasVisibleWindows = hasVisibleWindows;

    /// <summary>
    /// Gets whether the application had a visible window when the user asked to reopen it; minimized windows and windows
    /// on another Space are not visible. Without one, an application typically opens or shows its main window.
    /// </summary>
    public bool HasVisibleWindows { get; }
}
