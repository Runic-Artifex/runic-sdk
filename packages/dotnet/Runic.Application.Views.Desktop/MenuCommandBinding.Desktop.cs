using Microsoft.Extensions.Logging;
using Runic.Application.Views.Menus;
using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Menus
{
    // On Runic Desktop the binding is the target of DesktopWindow.MenuCommands and DesktopHost.MenuCommands.
    internal sealed partial class MenuCommandBinding : IDesktopMenuCommandTarget;

    internal static class MenuBarLog
    {
        internal static void CommandFailed(ILogger logger, Exception error, string commandId) =>
            Runic.Application.Views.Desktop.DesktopLog.MenuCommandFailed(logger, error, commandId,
                error.GetType().FullName ?? error.GetType().Name);
    }
}

namespace Runic.Application.Views.Desktop
{
    // The in-page menu bar of a Runic Desktop window (MenuBarPageRoutes): DesktopHost's bar and application commands.
    internal sealed class DesktopMenuBarSource(DesktopHost host) : IMenuBarSource
    {
        public IReadOnlyList<DesktopMenu> MenuBar => host.MenuBar;

        public event EventHandler? MenuBarChanged
        {
            add => host.MenuBarChanged += value;
            remove => host.MenuBarChanged -= value;
        }

        public MenuCommandBinding? ApplicationCommands => host.MenuCommands as MenuCommandBinding;
    }

    // A DesktopWindow as the window of an in-page menu bar.
    internal sealed class DesktopMenuBarPageWindow(DesktopWindow window, DesktopMenuBarPresentation presentation) : IMenuBarPageWindow
    {
        public bool IsOpen => window.IsOpen;

        // A Page window whose host reports no placement, such as an installed browser or a window host without a native
        // bar, renders it in the page; macOS keeps the application bar (D036.13).
        public string Placement => window.MenuBarPlacement switch
        {
            DesktopMenuBarPlacement.Window => "window",
            DesktopMenuBarPlacement.Global => "global",
            DesktopMenuBarPlacement.Page => "page",
            _ => presentation == DesktopMenuBarPresentation.Page ? "page" : "none",
        };

        public bool HostShortcuts => window.Browser == BrowserKind.Embedded && window.Capabilities.HasFlag(DesktopWindowCapabilities.MenuBar);

        public event EventHandler? PlacementChanged
        {
            add => window.MenuBarPlacementChanged += value;
            remove => window.MenuBarPlacementChanged -= value;
        }

        public Task<bool[]> CanExecuteAsync(IReadOnlyList<DesktopMenuItem> items, CancellationToken cancellationToken) =>
            window.CanExecuteMenuItemsAsync(items, cancellationToken);

        public ValueTask<bool> TryExecuteAsync(DesktopMenuItem item, CancellationToken cancellationToken) =>
            window.TryExecuteMenuItemAsync(item, cancellationToken);

        public void SuspendHostShortcuts(bool suspended) => window.SuspendHostMenuShortcuts(suspended);
    }
}
