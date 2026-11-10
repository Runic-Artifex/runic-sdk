namespace Runic.Desktop.Internal;

// Whether the menu item that runs now was chosen in the host's own native menu: a click, or the keyboard, in the window's
// menu bar, a global menu or the macOS application menu, not a shortcut matched from a key, the page or
// DesktopWindow.TryExecuteMenuItemAsync. A hosted window counts only such a choice as the user's work in the window
// (runic-host/1 window-services/1). The menu paths run their commands synchronously on the UI thread, inside the scope.
internal static class HostMenuChoice
{
    [ThreadStatic] private static bool t_current;

    // Whether the item that runs now on this thread was chosen in the host's native menu.
    internal static bool IsCurrent => t_current;

    // Runs the item's command path as a choice in the host's native menu.
    internal static T Run<T>(Func<T> run)
    {
        var previous = t_current;
        t_current = true;
        try { return run(); }
        finally { t_current = previous; }
    }

    internal static void Run(Action run) => _ = Run(() =>
    {
        run();
        return true;
    });
}
