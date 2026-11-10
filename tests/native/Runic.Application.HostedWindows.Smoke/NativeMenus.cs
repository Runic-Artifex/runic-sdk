using System.Runtime.InteropServices;
using Runic.Desktop;

// What the native menu bars show (runic-host/1 menus/1), read from the toolkit itself rather than Runic's model: on
// GTK 3 and GTK 4 the menu bar widget at the top of the window, on macOS NSApp.mainMenu, which the key window decides.
// Each read runs on the window's native thread.
internal static partial class NativeMenus
{
    // The top-level titles of the window's own menu bar on GTK, or of the application menu bar on macOS (the
    // application menu first); empty when the window shows no bar.
    internal static async Task<string[]> TitlesAsync(DesktopWindow window, string toolkit)
    {
        string[] titles = [];
        await window.InvokeNativeAsync(handle => titles = toolkit switch
        {
            "gtk3" => Gtk3.Titles(handle),
            "gtk4" => Gtk4Titles(handle),
            "wkwebview" => Mac.Titles(),
            _ => [],
        });
        return titles;
    }

    // The titles of the items of the bar's menu with the title; macOS only.
    internal static async Task<string[]> MacItemsAsync(DesktopWindow window, string menu)
    {
        string[] items = [];
        await window.InvokeNativeAsync(_ => items = Mac.Items(menu));
        return items;
    }

    // Waits until the titles match, and returns the last ones read.
    internal static async Task<string[]> WaitAsync(DesktopWindow window, string toolkit, Func<string[], bool> match, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var titles = await TitlesAsync(window, toolkit);
        while (!match(titles) && !deadline.IsCancellationRequested)
        {
            await Task.Delay(100);
            titles = await TitlesAsync(window, toolkit);
        }
        return titles;
    }

    // GTK 4: the window's box holds the PopoverMenuBar above the web view.
    private static string[] Gtk4Titles(nint handle)
    {
        var window = (Gtk.Window)GObject.Internal.InstanceWrapper.WrapHandle<Gtk.Window>(handle, false);
        if (window.GetChild() is not Gtk.Box box || box.GetFirstChild() is not Gtk.PopoverMenuBar bar) return [];
        List<string> titles = [];
        for (var item = bar.GetFirstChild(); item is not null; item = item.GetNextSibling())
        {
            if (item.GetCssName() != "item") continue;
            for (var child = item.GetFirstChild(); child is not null; child = child.GetNextSibling())
            {
                if (child is Gtk.Label label)
                {
                    titles.Add(label.GetLabel());
                    break;
                }
            }
        }
        return [.. titles];
    }

    // GTK 3: the window's box holds the GtkMenuBar above the web view.
    private static unsafe partial class Gtk3
    {
        private const string Library = "libgtk-3.so.0";

        internal static string[] Titles(nint window)
        {
            var box = BinGetChild(window);
            if (!Is(box, BoxType()) || Children(box) is not [var bar, ..] || !Is(bar, MenuBarType())) return [];
            return [.. Children(bar).Select(static item => Marshal.PtrToStringUTF8(MenuItemGetLabel(item)) ?? "")];
        }

        private static List<nint> Children(nint container)
        {
            var list = ContainerGetChildren(container);
            List<nint> children = [];
            for (var node = list; node != 0; node = ((nint*)node)[1]) children.Add(((nint*)node)[0]);
            ListFree(list);
            return children;
        }

        private static bool Is(nint instance, nuint type) => instance != 0 && TypeCheckInstanceIsA(instance, type) != 0;

        [LibraryImport(Library, EntryPoint = "gtk_bin_get_child")] private static partial nint BinGetChild(nint bin);
        [LibraryImport(Library, EntryPoint = "gtk_container_get_children")] private static partial nint ContainerGetChildren(nint container);
        [LibraryImport(Library, EntryPoint = "g_list_free")] private static partial void ListFree(nint list);
        [LibraryImport(Library, EntryPoint = "g_type_check_instance_is_a")] private static partial int TypeCheckInstanceIsA(nint instance, nuint type);
        [LibraryImport(Library, EntryPoint = "gtk_box_get_type")] private static partial nuint BoxType();
        [LibraryImport(Library, EntryPoint = "gtk_menu_bar_get_type")] private static partial nuint MenuBarType();
        [LibraryImport(Library, EntryPoint = "gtk_menu_item_get_label")] private static partial nint MenuItemGetLabel(nint item);
    }

    // macOS: the application menu bar, NSApp.mainMenu, on the main thread.
    private static partial class Mac
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        internal static string[] Titles() => MainMenu() is not 0 and var menu ? [.. ItemsOf(menu).Select(static item => Text(Send(item, Sel("title"))))] : [];

        internal static string[] Items(string title)
        {
            if (MainMenu() is 0) return [];
            foreach (var item in ItemsOf(MainMenu()))
            {
                if (Text(Send(item, Sel("title"))) == title && Send(item, Sel("submenu")) is not 0 and var submenu)
                    return [.. ItemsOf(submenu).Select(static entry => Text(Send(entry, Sel("title"))))];
            }
            return [];
        }

        private static nint MainMenu() => Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("mainMenu"));

        private static IEnumerable<nint> ItemsOf(nint menu)
        {
            for (nint index = 0, count = Send(menu, Sel("numberOfItems")); index < count; index++)
                yield return Arg(menu, Sel("itemAtIndex:"), index);
        }

        private static string Text(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"))) ?? "";

        [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
        [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);
        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Arg(nint target, nint selector, nint value);
    }
}
