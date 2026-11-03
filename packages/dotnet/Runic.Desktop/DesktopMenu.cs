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

    /// <summary>Gets whether a check or radio item shows its check mark now.</summary>
    /// <param name="item">The menu item; its <see cref="DesktopMenuItem.CommandId"/> and <see cref="DesktopMenuItem.CommandParameter"/> name the state.</param>
    /// <returns>
    /// <see langword="true"/> or <see langword="false"/>; <see langword="null"/>, like the default, keeps the item's
    /// declared <see cref="DesktopMenuItem.IsChecked"/>.
    /// </returns>
    /// <remarks>
    /// Runic Desktop asks it only for items declared with a non-null <see cref="DesktopMenuItem.IsChecked"/>, on the
    /// platform's UI thread, where it asks <see cref="CanExecute"/>: as a menu opens natively, and when the state of an
    /// in-page menu bar is built. Answer from state the command updates. The window's target answers first; the
    /// application's is asked when the window's answer is the declared state. Each item of a radio group is asked on its
    /// own, so answer <see langword="true"/> for exactly one of them.
    /// </remarks>
    bool? IsChecked(DesktopMenuItem item) => item.IsChecked;
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
