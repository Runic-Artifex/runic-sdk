using Runic.Platform;

namespace Runic.Desktop;

/// <summary>Runs the commands of menu items for a window or for the application.</summary>
/// <remarks>
/// Runic Desktop calls both members on the platform's UI thread, while it shows a menu or matches a shortcut: keep
/// <see cref="CanExecute"/> cheap, for example by answering from state the command updates, and let
/// <see cref="Execute"/> hand the work to the application's own thread. An item whose
/// <see cref="DesktopMenuItem.IsEnabled"/> is <see langword="false"/> is disabled without asking. Runic Windows get one for
/// their root ViewModel through <c>IRunicMenuCommandSource</c>.
/// </remarks>
public interface IDesktopMenuCommandTarget
{
    /// <summary>Gets whether the item's command can run now; <see langword="false"/> also for a command this target does not handle.</summary>
    /// <param name="item">The menu item; its <see cref="DesktopMenuItem.CommandId"/> names the command.</param>
    bool CanExecute(DesktopMenuItem item);

    /// <summary>Runs the item's command, after <see cref="CanExecute"/> returned <see langword="true"/>.</summary>
    /// <param name="item">The menu item; its <see cref="DesktopMenuItem.CommandId"/> names the command.</param>
    void Execute(DesktopMenuItem item);
}

/// <summary>Where a window shows the menu bar of <see cref="DesktopHostOptions.MenuBar"/> (<see cref="DesktopWindowOptions.MenuBarPresentation"/>).</summary>
/// <remarks>This set is closed.</remarks>
public enum DesktopMenuBarPresentation
{
    /// <summary>
    /// The default: natively, in the macOS application menu bar, in the window on Windows, and on Linux in the desktop's
    /// global menu where one is shown, else in the window.
    /// </summary>
    Native,

    /// <summary>
    /// No native bar: the page renders <see cref="DesktopHost.MenuBar"/> and runs items with
    /// <see cref="DesktopWindow.TryExecuteMenuItemAsync"/>. The host keeps matching the items' shortcuts. On macOS the
    /// declared menus stay in the application menu bar, which every window shares, and the placement is
    /// <see cref="DesktopMenuBarPlacement.Global"/>.
    /// </summary>
    Page,

    /// <summary>
    /// No bar anywhere, for a window that offers its commands otherwise; the items' shortcuts still run. On macOS the
    /// declared menus stay in the application menu bar, which every window shares.
    /// </summary>
    None,
}

/// <summary>Where a window's menu bar is shown now (<see cref="DesktopWindow.MenuBarPlacement"/>).</summary>
/// <remarks>This set is open: a minor release may add values, so handle unknown ones as <see cref="None"/>.</remarks>
public enum DesktopMenuBarPlacement
{
    /// <summary>No native bar: the window is not open, has no native bar, or its presentation is <see cref="DesktopMenuBarPresentation.None"/>.</summary>
    None,

    /// <summary>In the window, below its title bar.</summary>
    Window,

    /// <summary>In a bar outside the window: the macOS application menu bar, or a Linux desktop's global menu.</summary>
    Global,

    /// <summary>In the page, which renders <see cref="DesktopHost.MenuBar"/> itself.</summary>
    Page,
}
