namespace Runic.Desktop.Internal;

// DesktopHost.SetStandardMenuTitles: the standard menus Runic added to the main menu take new titles while the
// application runs, for example after a language switch. Items are found by their action and the menus by the
// identifiers Runic gave them, never by their English titles; an application's own main menu keeps its titles.
internal sealed partial class MacOsWkWebViewHost
{
    // Main thread only: whether InstallMainMenu completed an empty main menu with the standard menus.
    private static bool s_completedMainMenu;

    // Sets the titles for the menus still to come, and retitles the open ones as soon as the main thread can.
    internal static void RequestStandardMenuTitles(DesktopStandardMenuTitles titles)
    {
        SetStandardMenuTitles(titles);
        if (!OperatingSystem.IsMacOS() || !Api.IsAvailable || !(IsMainThread || DesktopEventLoop.IsRunning)) return;
        _ = InvokeOnMainAsync(static () =>
        {
            var application = Api.SendNint(Api.GetClass("NSApplication"), "sharedApplication");
            if (s_menuTarget == 0 || Api.SendNint(application, "mainMenu") is not (not 0 and var mainMenu)) return;
            RetitleStandardMenus(mainMenu, Volatile.Read(ref HostSettings.Titles));
            // The standard File menu takes a declared File menu's title, or its own again.
            ApplyMenuBar(application);
        }, CancellationToken.None).AsTask();
    }

    // Main thread only.
    private static void RetitleStandardMenus(nint mainMenu, DesktopStandardMenuTitles titles)
    {
        var name = Api.Text(Api.SendNint(Api.SendNint(Api.GetClass("NSProcessInfo"), "processInfo"), "processName"));
        if (s_completedMainMenu)
        {
            var applicationMenu = Api.SendNint(mainMenu, "numberOfItems") > 0
                ? Api.SendNint(Api.SendNintNint(mainMenu, "itemAtIndex:", 0), "submenu") : 0;
            if (applicationMenu != 0)
            {
                RetitleItems(applicationMenu, MacOsMainMenu.ApplicationItems(titles), name);
            }
            if (IdentifiedSubmenu(mainMenu, MacOsMainMenu.EditIdentifier) is not 0 and var edit)
            {
                RetitleSubmenu(mainMenu, edit, titles.Edit);
                RetitleItems(edit, MacOsMainMenu.EditItems(titles), name);
            }
            if (IdentifiedSubmenu(mainMenu, FileIdentifier) is not 0 and var file)
            {
                RetitleItems(file, MacOsMainMenu.FileItems(titles), name);
                if (FindOpenRecent(file) is not 0 and var openRecent)
                {
                    Api.SetTitle(openRecent, Api.SendNint(openRecent, "submenu"), titles.OpenRecent);
                    // Its items, Clear Menu among them, are built again from the titles each time it opens.
                }
            }
            if (IdentifiedSubmenu(mainMenu, MacOsMainMenu.WindowIdentifier) is not 0 and var window)
            {
                RetitleSubmenu(mainMenu, window, titles.Window);
                RetitleItems(window, MacOsMainMenu.WindowItems(titles), name);
            }
        }
        // The zoom items go into any View menu, also an application's own.
        if (IdentifiedSubmenu(mainMenu, MacOsMainMenu.ViewIdentifier) is not 0 and var view)
        {
            RetitleSubmenu(mainMenu, view, titles.View);
        }
        if (Api.FindSubmenu(mainMenu, MacOsMainMenu.ViewIdentifier, MacOsMainMenu.ViewActions) is not 0 and var zoomMenu)
        {
            foreach (var item in MacOsPageZoom.Items(titles))
            {
                RetitleAction(zoomMenu, MacOsPageZoom.Selector(item.Command), item.Title);
            }
        }
    }

    private static void RetitleSubmenu(nint mainMenu, nint submenu, string title) =>
        Api.SetTitle(Api.SendNintNint(mainMenu, "itemAtIndex:", Api.IndexOfSubmenu(mainMenu, submenu)), submenu, title);

    private static void RetitleItems(nint menu, IEnumerable<MacOsMainMenu.Item> items, string name)
    {
        foreach (var item in items)
        {
            if (item.Action is { } action) RetitleAction(menu, action, DesktopStandardMenuTitles.Format(item.Title, name));
        }
    }

    // Every item of the menu (not its submenus) that sends the action.
    private static void RetitleAction(nint menu, string action, string title)
    {
        for (nint index = 0, count = Api.SendNint(menu, "numberOfItems"); index < count; index++)
        {
            var item = Api.SendNintNint(menu, "itemAtIndex:", index);
            if (Api.HasAction(item, action)) Api.SetTitle(item, 0, title);
        }
    }

    // The submenu of the top-level item with the identifier, or 0.
    private static nint IdentifiedSubmenu(nint mainMenu, string identifier)
    {
        for (nint index = 0, count = Api.SendNint(mainMenu, "numberOfItems"); index < count; index++)
        {
            var item = Api.SendNintNint(mainMenu, "itemAtIndex:", index);
            if (Api.Text(Api.SendNint(item, "identifier")) == identifier) return Api.SendNint(item, "submenu");
        }
        return 0;
    }
}
