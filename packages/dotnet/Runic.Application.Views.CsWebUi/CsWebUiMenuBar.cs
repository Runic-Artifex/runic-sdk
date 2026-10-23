using Microsoft.Extensions.Logging;
using Runic.Platform;

namespace Runic.Application.Views.Menus
{
    internal static partial class MenuBarLog
    {
        [LoggerMessage(EventId = 2102, EventName = "CsWebUiMenuCommandFailed", Level = LogLevel.Error,
            Message = "The menu command {CommandId} failed with {ErrorType}.")]
        private static partial void MenuCommandFailed(ILogger logger, Exception exception, string commandId, string errorType);

        internal static void CommandFailed(ILogger logger, Exception error, string commandId) =>
            MenuCommandFailed(logger, error, commandId, error.GetType().FullName ?? error.GetType().Name);
    }
}

namespace Runic.Application.Views.CsWebUi
{
    using Runic.Application.Views.Menus;

    // The menu bar of a RunicCsWebUiHost (D036.14). A CS-WebUI window has no native bar, so its page renders this one
    // through the in-page menu bar routes, which Runic Desktop shares (MenuBarPageRoutes), and the Window's root
    // ViewModel runs the items' commands (IRunicMenuCommandSource). There are no application commands.
    internal sealed class CsWebUiMenuBar : IMenuBarSource
    {
        private IReadOnlyList<DesktopMenu> _menus;

        internal CsWebUiMenuBar(IReadOnlyList<DesktopMenu> menus) => _menus = Check(menus);

        public IReadOnlyList<DesktopMenu> MenuBar => Volatile.Read(ref _menus);

        public event EventHandler? MenuBarChanged;

        public MenuCommandBinding? ApplicationCommands => null;

        internal void Set(IReadOnlyList<DesktopMenu> menus)
        {
            Volatile.Write(ref _menus, Check(menus));
            MenuBarChanged?.Invoke(this, EventArgs.Empty);
        }

        // The rules of DesktopHostOptions.MenuBar that make a bar unusable. Shortcuts a platform reserves are left out of
        // the page's matching by the planner, as on Runic Desktop.
        private static IReadOnlyList<DesktopMenu> Check(IReadOnlyList<DesktopMenu> menus)
        {
            ArgumentNullException.ThrowIfNull(menus);
            foreach (var role in new[] { DesktopMenuRole.Application, DesktopMenuRole.File, DesktopMenuRole.Help })
            {
                if (menus.Count(menu => menu?.Role == role) > 1)
                    throw new ArgumentException($"The menu bar has more than one {role} menu.", nameof(menus));
            }
            foreach (var menu in menus)
            {
                if (menu is null) throw new ArgumentException("The menu bar contains a null menu.", nameof(menus));
                CheckMenu(menu, "");
            }
            return [.. menus];
        }

        private static void CheckMenu(DesktopMenu menu, string path)
        {
            DesktopMenuRules.Title(menu.Title, path.Length == 0 ? "A menu" : $"A submenu of '{path}'");
            path = path.Length == 0 ? menu.Title : path + " ▸ " + menu.Title;
            DesktopMenuRules.AccessKey(menu.AccessKey, $"the menu '{path}'");
            if (menu.Items.IsDefault) throw new ArgumentException($"The menu '{path}' has no item list.");
            foreach (var entry in menu.Items)
            {
                switch (entry)
                {
                    case null:
                        throw new ArgumentException($"The menu '{path}' contains a null entry.");
                    case DesktopMenu submenu:
                        CheckMenu(submenu, path);
                        break;
                    case DesktopMenuItem item:
                        DesktopMenuRules.Title(item.Title, $"An item of '{path}'");
                        DesktopMenuRules.Item(item, $"'{path} ▸ {item.Title}'");
                        break;
                    default:
                        // A separator, or an entry kind of a later release that the bar does not show.
                        break;
                }
            }
        }
    }

    // A CS-WebUI window as the window of the in-page menu bar: the bar is always in the page while the host declares one,
    // and the page matches the items' shortcuts, because WebUI reports no keys to .NET. An item runs as on Runic Desktop:
    // its declaration, then the root ViewModel's command, asked CanExecute in its model turn before Execute.
    internal sealed class CsWebUiMenuBarPageWindow(CsWebUiMenuBar bar, MenuCommandBinding? commands) : IMenuBarPageWindow
    {
        public bool IsOpen => true;

        public string Placement => bar.MenuBar.Count > 0 ? "page" : "none";

        public bool HostShortcuts => false;

        public event EventHandler? PlacementChanged
        {
            add { }
            remove { }
        }

        public Task<bool[]> CanExecuteAsync(IReadOnlyList<DesktopMenuItem> items, CancellationToken cancellationToken)
        {
            var result = new bool[items.Count];
            for (var index = 0; index < items.Count; index++) result[index] = CanExecute(items[index]);
            return Task.FromResult(result);
        }

        public ValueTask<bool> TryExecuteAsync(DesktopMenuItem item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanExecute(item)) return ValueTask.FromResult(false);
            commands!.Execute(item);
            return ValueTask.FromResult(true);
        }

        public void SuspendHostShortcuts(bool suspended)
        {
        }

        private bool CanExecute(DesktopMenuItem item) => item.IsEnabled && commands is not null && commands.CanExecute(item);
    }
}
