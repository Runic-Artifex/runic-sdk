namespace Runic.Desktop.Internal;

// macOS document conventions (G-6): the window's represented file (the title bar's proxy icon and path menu) and edited
// state (the dot in the close button), and the application's recent documents, which NSDocumentController keeps for
// the Open Recent menu and the Dock. NSDocumentController stores its own bookmark of each noted URL, security-scoped in
// the App Sandbox, so the URL must be accessible when it is noted, as it is while a document lease is open.
internal sealed partial class MacOsWkWebViewHost
{
    // Whether a host asked for Open Recent (MacOSDesktopOptions.OpenRecentMenu); one main menu per process.
    private static int s_openRecentMenu;

    // Adds Open Recent as soon as the main thread can: now while the AppKit loop runs, else when the first window opens.
    internal static void RequestOpenRecentMenu()
    {
        Volatile.Write(ref s_openRecentMenu, 1);
        if (!OperatingSystem.IsMacOS() || !Api.IsAvailable || !(IsMainThread || DesktopEventLoop.IsRunning)) return;
        _ = InvokeOnMainAsync(static () =>
        {
            var application = Api.SendNint(Api.GetClass("NSApplication"), "sharedApplication");
            // Completes the main menu once; a later host adds Open Recent to the menu an earlier one completed.
            InstallMainMenu(application);
            if (Api.SendNint(application, "mainMenu") is not 0 and var mainMenu) InstallOpenRecent(mainMenu, Volatile.Read(ref s_titles));
            ApplyMenuBar(application);
        }, CancellationToken.None).AsTask();
    }

    internal ValueTask SetRepresentedFileAsync(string? path, CancellationToken cancellationToken) => InvokeAsync(() =>
    {
        // A nil URL removes the proxy icon.
        Api.SendVoidNint(_window, "setRepresentedURL:", path is null ? 0 : FileUrl(path));
    }, cancellationToken);

    internal ValueTask SetDocumentEditedAsync(bool edited, CancellationToken cancellationToken) =>
        InvokeAsync(() => Api.SendVoidBool(_window, "setDocumentEdited:", edited), cancellationToken);

    internal static ValueTask NoteRecentDocumentAsync(string path, CancellationToken cancellationToken) =>
        MacOsMainThread.InvokeAsync(() =>
        {
            if (DocumentController() is not 0 and var controller)
                Api.SendVoidNint(controller, "noteNewRecentDocumentURL:", FileUrl(path));
        }, cancellationToken);

    internal static ValueTask ClearRecentDocumentsAsync(CancellationToken cancellationToken) =>
        MacOsMainThread.InvokeAsync(() =>
        {
            if (DocumentController() is not 0 and var controller)
                Api.SendVoidNint(controller, "clearRecentDocuments:", 0);
        }, cancellationToken);

    // Main thread. An autoreleased file URL.
    private static nint FileUrl(string path)
    {
        var text = Api.CreateString(path);
        try
        {
            return Api.SendNintNint(Api.GetClass("NSURL"), "fileURLWithPath:", text);
        }
        finally
        {
            Api.SendVoid(text, "release");
        }
    }

    // Main thread. AppKit is loaded once a window opened or the Desktop event loop runs.
    private static nint DocumentController()
    {
        var type = Api.GetClass("NSDocumentController");
        return type == 0 ? 0 : Api.SendNint(type, "sharedDocumentController");
    }

    // Main thread, from InstallMainMenu. Adds Open Recent at the top of the standard File menu, followed by a separator:
    // a submenu that AppKit fills with the recent documents above Clear Menu.
    private static void InstallOpenRecent(nint mainMenu, DesktopStandardMenuTitles titles)
    {
        if (Volatile.Read(ref s_openRecentMenu) == 0) return;
        var file = Api.FindSubmenu(mainMenu, FileIdentifier, []);
        if (file == 0 || FindOpenRecent(file) != 0) return;
        var controller = DocumentController();
        if (controller == 0) return;
        Api.InsertItem(file, Api.SendNint(Api.GetClass("NSMenuItem"), "separatorItem"), 0);
        var recent = Api.InsertSubmenu(file, titles.OpenRecent, 0, MacOsMainMenu.OpenRecentIdentifier);
        var clear = Api.NewMenuItem(titles.ClearMenu, MacOsMainMenu.ClearRecentAction, "", 0, controller);
        Api.SendVoidNint(recent, "addItem:", clear);
        Api.SendVoid(clear, "release");
        // The menu name by which AppKit finds the recent documents menu, as a nib's Open Recent menu has it; there is
        // no other way to give a menu made in code this role.
        if (Api.RespondsToSelector(recent, "_setMenuName:"))
        {
            var name = Api.CreateString(MacOsMainMenu.RecentDocumentsMenuName);
            Api.SendVoidNint(recent, "_setMenuName:", name);
            Api.SendVoid(name, "release");
        }
    }

    // The Open Recent item of the standard File menu, or 0.
    internal static nint FindOpenRecent(nint fileMenu)
    {
        for (nint index = 0, count = Api.SendNint(fileMenu, "numberOfItems"); index < count; index++)
        {
            var item = Api.SendNintNint(fileMenu, "itemAtIndex:", index);
            if (Api.Text(Api.SendNint(item, "identifier")) == MacOsMainMenu.OpenRecentIdentifier) return item;
        }
        return 0;
    }
}
