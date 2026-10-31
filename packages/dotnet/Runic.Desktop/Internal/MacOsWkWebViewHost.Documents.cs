using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
            if (Api.SendNint(application, "mainMenu") is not 0 and var mainMenu) InstallOpenRecent(mainMenu, Volatile.Read(ref HostSettings.Titles));
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
    // a submenu that Runic's menu delegate fills from the recent documents each time it opens (public API only).
    private static void InstallOpenRecent(nint mainMenu, DesktopStandardMenuTitles titles)
    {
        if (Volatile.Read(ref s_openRecentMenu) == 0) return;
        var file = Api.FindSubmenu(mainMenu, FileIdentifier, []);
        if (file == 0 || FindOpenRecent(file) != 0) return;
        var recentDelegate = RecentMenuDelegate();
        if (recentDelegate == 0) return;
        Api.InsertItem(file, Api.SendNint(Api.GetClass("NSMenuItem"), "separatorItem"), 0);
        var recent = Api.InsertSubmenu(file, titles.OpenRecent, 0, MacOsMainMenu.OpenRecentIdentifier);
        // Runic enables the items itself: Clear Menu only while there is something to clear.
        Api.SendVoidBool(recent, "setAutoenablesItems:", false);
        // NSMenu's delegate is weak; the delegate lives for the process.
        Api.SendVoidNint(recent, "setDelegate:", recentDelegate);
        FillRecentMenu(recent);
    }

    private static nint s_recentMenuDelegate;

    private static unsafe nint RecentMenuDelegate()
    {
        if (s_recentMenuDelegate != 0) return s_recentMenuDelegate;
        var type = Api.GetClass("RunicDesktopRecentDocumentsMenu");
        if (type == 0)
        {
            type = Api.AllocateClassPair(Api.GetClass("NSObject"), "RunicDesktopRecentDocumentsMenu");
            if (type == 0) return 0;
            Api.AddMethod(type, "menuNeedsUpdate:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&RecentMenuNeedsUpdate, "v@:@");
            Api.AddMethod(type, MacOsMainMenu.OpenRecentAction, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OpenRecentDocument, "v@:@");
            Api.RegisterClassPair(type);
        }
        return s_recentMenuDelegate = Api.SendNint(Api.SendNint(type, "alloc"), "init");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RecentMenuNeedsUpdate(nint self, nint selector, nint menu)
    {
        try { FillRecentMenu(menu); }
        catch { /* Never unwind through AppKit. */ }
    }

    // A recent document's item: its URL is the item's represented object.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OpenRecentDocument(nint self, nint selector, nint sender)
    {
        try
        {
            if (sender != 0 && Api.SendNint(sender, "representedObject") is not 0 and var url) MacOsApplication.OpenFileUrls([url]);
        }
        catch { /* Never unwind through AppKit. */ }
    }

    // Main thread. Replaces the submenu's items: the recent documents, most recent first, with the Finder's display
    // name and the file's icon as AppKit's own menu shows them, a separator, and Clear Menu.
    internal static void FillRecentMenu(nint menu)
    {
        var controller = DocumentController();
        if (controller == 0) return;
        Api.SendVoid(menu, "removeAllItems");
        var urls = Api.SendNint(controller, "recentDocumentURLs");
        var count = urls == 0 ? 0 : (int)Api.SendNint(urls, "count");
        var documents = new List<(nint Url, string Path, string Name)>(count);
        var files = Api.SendNint(Api.GetClass("NSFileManager"), "defaultManager");
        for (var index = 0; index < count; index++)
        {
            var url = Api.SendNintNint(urls, "objectAtIndex:", index);
            var path = Api.SendNint(url, "path");
            if (path == 0) continue;
            documents.Add((url, Api.Text(path), Api.Text(Api.SendNintNint(files, "displayNameAtPath:", path))));
        }
        var workspace = Api.SendNint(Api.GetClass("NSWorkspace"), "sharedWorkspace");
        foreach (var (url, path, name) in documents)
        {
            var item = Api.NewMenuItem(MacOsMainMenu.RecentDocumentTitle(name, path, documents.Select(static document => (document.Path, document.Name))),
                MacOsMainMenu.OpenRecentAction, "", 0, s_recentMenuDelegate);
            Api.SendVoidNint(item, "setRepresentedObject:", url);
            var text = Api.CreateString(path);
            var icon = Api.SendNintNint(workspace, "iconForFile:", text);
            Api.SendVoid(text, "release");
            if (icon != 0)
            {
                // A copy: the workspace may hand out a shared image.
                var small = Api.SendNint(icon, "copy");
                Api.SendVoidSize(small, "setSize:", new CGSize(16, 16));
                Api.SendVoidNint(item, "setImage:", small);
                Api.SendVoid(small, "release");
            }
            Api.SendVoidNint(menu, "addItem:", item);
            Api.SendVoid(item, "release");
        }
        if (documents.Count > 0) Api.SendVoidNint(menu, "addItem:", Api.SendNint(Api.GetClass("NSMenuItem"), "separatorItem"));
        var clear = Api.NewMenuItem(Volatile.Read(ref HostSettings.Titles).ClearMenu, MacOsMainMenu.ClearRecentAction, "", 0, controller);
        Api.SendVoidBool(clear, "setEnabled:", documents.Count > 0);
        Api.SendVoidNint(menu, "addItem:", clear);
        Api.SendVoid(clear, "release");
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
