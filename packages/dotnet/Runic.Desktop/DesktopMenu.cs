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
