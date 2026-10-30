using System.Runtime.InteropServices;
using Runic.Desktop;
using Runic.Desktop.Internal;
using Runic.Platform;
using Runic.Platform.MacOS;

// M5 (G-6) on the macOS runner: the document conventions of a Runic window and application. The represented file sets
// NSWindow.representedURL (the title bar's proxy icon) and clears it; the edited state toggles NSWindow.documentEdited;
// a noted document is in NSDocumentController.recentDocumentURLs and in the Open Recent submenu that
// MacOSDesktopOptions.OpenRecentMenu adds to the standard File menu (built and filled by Runic with public API), and
// choosing it there reaches the application as an open request of Runic.Platform.MacOS; Clear Menu and
// ClearRecentDocumentsAsync empty the list, and an empty menu disables Clear Menu. Run with --documents on macOS.
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal static partial class MacOsDocumentsSmoke
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    internal static void Run(DesktopHostOptions hostOptions)
    {
        var directory = Directory.CreateTempSubdirectory("runic-documents-");
        // A unique name: the recent documents of an unbundled process may outlive the run.
        var name = $"runic-smoke-{Guid.NewGuid():N}";
        var path = Path.Combine(directory.FullName, name + ".txt");
        File.WriteAllText(path, "Runic document conventions");
        try
        {
            var options = hostOptions with { MacOS = new MacOSDesktopOptions { OpenRecentMenu = true } };
            int result = DesktopEventLoop.Run(options, host => RunAsync(host, path, name));
            Expect(result == 0, $"the documents smoke returned {result}");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<int> RunAsync(DesktopHost host, string path, string name)
    {
        var opened = new TaskCompletionSource<DesktopOpenRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var requests = MacOSPlatformProvider.CreateOpenRequests(new DesktopOpenOptions([]) { IncludeFiles = true });
        requests.Opened += (_, request) => opened.TrySetResult(request);

        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Html("<!doctype html><html><head><script src=\"webui.js\"></script><title>Runic documents</title></head><body>documents</body></html>"),
        });
        var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 480, Height = 320 });

        await window.SetRepresentedFileAsync(path);
        var represented = await ReadAsync(window, handle => PathOf(Send(handle, Sel("representedURL"))));
        Expect(represented.EndsWith("/" + name + ".txt", StringComparison.Ordinal), $"representedURL is {represented}");
        await window.SetRepresentedFileAsync(null);
        Expect(await ReadAsync(window, handle => Send(handle, Sel("representedURL"))) == 0, "SetRepresentedFileAsync(null) removes representedURL");
        Pass("SetRepresentedFileAsync sets and removes NSWindow.representedURL (the proxy icon).");

        await window.SetDocumentEditedAsync(true);
        Expect(await ReadAsync(window, handle => Bool(handle, Sel("isDocumentEdited")) != 0), "SetDocumentEditedAsync(true) sets documentEdited");
        await window.SetDocumentEditedAsync(false);
        Expect(!await ReadAsync(window, handle => Bool(handle, Sel("isDocumentEdited")) != 0), "SetDocumentEditedAsync(false) clears documentEdited");
        Pass("SetDocumentEditedAsync toggles NSWindow.documentEdited (the close button's dot).");

        await host.ClearRecentDocumentsAsync();
        await host.NoteRecentDocumentAsync(path);
        await WaitAsync(async () => (await RecentAsync()).Any(recent => recent.EndsWith("/" + name + ".txt", StringComparison.Ordinal)),
            "the noted document in recentDocumentURLs");
        Pass("NoteRecentDocumentAsync adds the document to NSDocumentController.recentDocumentURLs.");

        // Runic's menu delegate fills Open Recent as the menu opens; the smoke asks it to, as menu tracking would.
        var menu = await ReadMenuAsync();
        var index = Array.FindIndex(menu, entry => entry.Title.Contains(name, StringComparison.Ordinal));
        Expect(index >= 0, $"Open Recent lists the noted document: {Describe(menu)}");
        Expect(menu[index] is { Enabled: true, Image: true, Represented: true }, $"the document's item is enabled, with an icon and its URL: {Describe(menu)}");
        Expect(menu[^1] is { Title: "Clear Menu", Enabled: true } && menu[^2].Title.Length == 0,
            $"Open Recent ends with a separator and an enabled Clear Menu: {Describe(menu)}");
        Pass($"Runic's Open Recent lists the noted document with its icon above Clear Menu ({Describe(menu)}).");

        await MacOsMainThread.InvokeAsync(() => Arg(OpenRecentMenu(), Sel("performActionForItemAtIndex:"), index), CancellationToken.None);
        var request = await opened.Task.WaitScaledAsync(StepTimeout);
        Expect(request.Items.Length == 1, $"the open request has one item: {string.Join(",", request.Items)}");
        var item = request.Items[0];
        Expect(item.IsFile && item.LocalPath.EndsWith("/" + name + ".txt", StringComparison.Ordinal), $"the open request is the document: {item}");
        Pass("choosing the document in Open Recent delivers its path as an open request.");

        // Clear Menu, as the user chooses it.
        await MacOsMainThread.InvokeAsync(() => Arg(OpenRecentMenu(), Sel("performActionForItemAtIndex:"), Send(OpenRecentMenu(), Sel("numberOfItems")) - 1),
            CancellationToken.None);
        await WaitAsync(async () => !(await RecentAsync()).Any(recent => recent.Contains(name, StringComparison.Ordinal)), "an empty recent documents list");
        menu = await ReadMenuAsync();
        Expect(menu is [{ Title: "Clear Menu", Enabled: false }], $"an empty Open Recent shows only a disabled Clear Menu: {Describe(menu)}");
        Pass("Clear Menu empties the recent documents, and an empty Open Recent disables it.");

        await host.NoteRecentDocumentAsync(path);
        await WaitAsync(async () => (await RecentAsync()).Any(recent => recent.Contains(name, StringComparison.Ordinal)), "the document noted again");
        await host.ClearRecentDocumentsAsync();
        await WaitAsync(async () => !(await RecentAsync()).Any(recent => recent.Contains(name, StringComparison.Ordinal)), "an empty recent documents list");
        Pass("ClearRecentDocumentsAsync empties the recent documents.");

        await window.CloseAsync();
        return 0;
    }

    private static async Task<T> ReadAsync<T>(DesktopWindow window, Func<nint, T> read)
    {
        T value = default!;
        await window.InvokeNativeAsync(handle => value = read(handle));
        return value;
    }

    private readonly record struct MenuEntry(string Title, bool Enabled, bool Image, bool Represented);

    private static string Describe(MenuEntry[] menu) => string.Join("|", menu.Select(static entry => entry.Enabled ? entry.Title : $"({entry.Title})"));

    // Fills Open Recent through its delegate, as AppKit does before the menu opens, and reads its items.
    private static async Task<MenuEntry[]> ReadMenuAsync()
    {
        MenuEntry[] entries = [];
        await MacOsMainThread.InvokeAsync(() =>
        {
            var menu = OpenRecentMenu();
            Expect(menu != 0, "the standard File menu has Open Recent");
            var menuDelegate = Send(menu, Sel("delegate"));
            Expect(menuDelegate != 0 && Bool(menuDelegate, Sel("respondsToSelector:"), Sel("menuNeedsUpdate:")) != 0, "Open Recent has Runic's menu delegate");
            Arg(menuDelegate, Sel("menuNeedsUpdate:"), menu);
            entries = [.. Enumerable.Range(0, (int)Send(menu, Sel("numberOfItems"))).Select(index => Arg(menu, Sel("itemAtIndex:"), index)).Select(item =>
                new MenuEntry(Text(Send(item, Sel("title"))), Bool(item, Sel("isEnabled")) != 0, Send(item, Sel("image")) != 0, Send(item, Sel("representedObject")) != 0))];
        }, CancellationToken.None);
        return entries;
    }

    private static async Task<string[]> RecentAsync()
    {
        string[] paths = [];
        await MacOsMainThread.InvokeAsync(() =>
        {
            var urls = Send(Send(Class("NSDocumentController"), Sel("sharedDocumentController")), Sel("recentDocumentURLs"));
            paths = [.. Enumerable.Range(0, (int)Send(urls, Sel("count"))).Select(index => PathOf(Arg(urls, Sel("objectAtIndex:"), index)))];
        }, CancellationToken.None);
        return paths;
    }

    // Main thread. The Open Recent submenu of the standard File menu.
    private static nint OpenRecentMenu()
    {
        var mainMenu = Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("mainMenu"));
        for (nint index = 0, count = Send(mainMenu, Sel("numberOfItems")); index < count; index++)
        {
            var file = Send(Arg(mainMenu, Sel("itemAtIndex:"), index), Sel("submenu"));
            if (file == 0) continue;
            for (nint child = 0, children = Send(file, Sel("numberOfItems")); child < children; child++)
            {
                var item = Arg(file, Sel("itemAtIndex:"), child);
                if (Text(Send(item, Sel("identifier"))) == "runic.menu.file.openRecent") return Send(item, Sel("submenu"));
            }
        }
        return 0;
    }

    private static string PathOf(nint url) => url == 0 ? "" : Text(Send(url, Sel("path")));

    private static async Task WaitAsync(Func<Task<bool>> check, string description, Func<string>? detail = null)
    {
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(StepTimeout);
        while (!await check())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Documents: expected {description}. {detail?.Invoke()}");
            await Task.Delay(100);
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Documents: " + message);
    }

    private static void Pass(string message) => Console.WriteLine("PASS " + message);

    private static string Text(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"))) ?? "";

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Arg(nint target, nint selector, nint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte Bool(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte Bool(nint target, nint selector, nint value);
}
