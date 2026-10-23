using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Runic.Desktop;

#pragma warning disable CA1416 // The GTK 4 smoke runs on Linux.
// W315-006: a real X11 drag and drop from a GTK 4 drag source onto the GTK 4 WebKitGTK 6 window, driven through
// XTEST under Xvfb. The window reports the native paths with the page's routing label, the page still receives its
// drop event, and the window does not navigate to the dropped file. Then B11's drag out and frame drops
// (LinuxDragOutSmoke).
internal static partial class FileDropGtk4Smoke
{
    private const string Page =
        """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>GTK4 file drop</title>
        <style>html,body{margin:0;height:100%} #target{position:fixed;inset:0;background:#cde}</style></head>
        <body><div id="target" data-runic-drop-target="folder-7">drop here</div>
        <script>window.__pageDrops = 0; addEventListener("drop", () => window.__pageDrops++);</script></body></html>
        """;

    internal static async Task RunAsync(DesktopHost host)
    {
        if (Pointer.Open() is not { } pointer)
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("XTEST (libXtst.so.6) is required for the GTK 4 file drop smoke in CI.");
            Console.WriteLine("GTK4 smoke: file drop skipped, XTEST is unavailable (set RUNIC_LIBXTST to libXtst.so.6).");
            return;
        }
        using var _ = pointer;
        var root = Directory.CreateTempSubdirectory("runic-drop-gtk4-");
        try
        {
            var file = Path.Combine(root.FullName, "dropped notes.txt");
            await File.WriteAllTextAsync(file, "dropped");
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "a folder")).FullName;
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 640, Height = 480 });
            if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.FileDrop))
                throw new InvalidOperationException("The GTK 4 window did not find WebKit's drop target.");
            var before = await surface.ExecuteJavaScriptAsync("return location.href;", TimeSpan.FromSeconds(10));
            var dropped = new TaskCompletionSource<DesktopFileDropEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.FilesDropped += (_, drop) => dropped.TrySetResult(drop);
            var uriList = new Uri(file).AbsoluteUri + "\r\n" + new Uri(folder).AbsoluteUri + "\r\n";
            Gtk.Window? source = null;
            // GTK 4 cannot place windows: without a window manager both appear at the origin, the newer on top.
            await window.InvokeNativeAsync(_ => source = CreateDragSource(uriList, Gdk.DragAction.Copy));
            try
            {
                await Task.Delay(700);
                await pointer.DragAsync((60, 60), (400, 300));
                var drop = await dropped.Task.WaitAsync(TimeSpan.FromSeconds(15));
                if (!drop.Paths.SequenceEqual([file, folder]) || drop.Target != "folder-7")
                    throw new InvalidOperationException($"The GTK 4 drop reported [{string.Join(", ", drop.Paths)}] with target '{drop.Target}'.");
                await Task.Delay(300);
                var pageDrops = await surface.ExecuteJavaScriptAsync("return String(window.__pageDrops);");
                var after = await surface.ExecuteJavaScriptAsync("return location.href;");
                if (pageDrops != "1" || after != before)
                    throw new InvalidOperationException($"The GTK 4 page saw {pageDrops} drop events and moved from {before} to {after}.");
                Console.WriteLine($"GTK4 smoke: file drop passed with [{string.Join(", ", drop.Paths)}], target '{drop.Target}', one page drop event, no navigation.");
            }
            finally
            {
                await window.InvokeNativeAsync(_ => source?.Destroy());
            }
            await window.CloseAsync();
            // GTK 4 places only Runic's windows (on X11): the page window to the right of the test windows, which
            // open at the origin.
            List<Gtk.Window> windows = [];
            Console.WriteLine($"GTK4 smoke: GTK {Gtk.Functions.GetMajorVersion()}.{Gtk.Functions.GetMinorVersion()}.{Gtk.Functions.GetMicroVersion()}.");
            await LinuxDragOutSmoke.RunAsync(host, pointer, [file, folder], new LinuxDragOutSmoke.Toolkit
            {
                Name = "GTK 4",
                MayLackDragOut = !Runic.Desktop.Gtk4.Gtk4WindowHost.DragOutSupported,
                PageWindow = new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 400, Height = 300, X = 300, Y = 0 },
                PageCenter = (500, 170),
                SameFrame = (400, 170),
                OtherFrame = (600, 170),
                CreateDropTarget = () => Remember(windows, DropTarget.Create(200)),
                DropPoint = (100, 100),
                NowherePoint = (150, 420),
                CreateDragSource = uris => Remember(windows, CreateDragSource(string.Concat(uris.Select(static uri => uri + "\r\n")),
                    Gdk.DragAction.Copy | Gdk.DragAction.Move)),
                SourcePoint = (60, 60),
                DestroyWindow = handle =>
                {
                    var match = windows.Find(item => item.Handle.DangerousGetHandle() == handle);
                    if (match is null) return;
                    windows.Remove(match);
                    match.Destroy();
                },
            });
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static nint Remember(List<Gtk.Window> windows, Gtk.Window window)
    {
        windows.Add(window);
        return window.Handle.DangerousGetHandle();
    }

    // A GTK 4 window at the origin that offers text/uri-list when dragged.
    private static Gtk.Window CreateDragSource(string uriList, Gdk.DragAction actions)
    {
        var source = Gtk.Window.New();
        source.SetDefaultSize(120, 120);
        var label = Gtk.Label.New("drag me");
        var drag = Gtk.DragSource.New();
        drag.SetActions(actions);
        drag.OnPrepare += (_, _) => Gdk.ContentProvider.NewForBytes("text/uri-list", GLib.Bytes.New(Encoding.UTF8.GetBytes(uriList)));
        label.AddController(drag);
        source.SetChild(label);
        source.Present();
        return source;
    }

    // A GTK 4 window at the origin whose GtkDropTarget takes a GdkFileList (GTK reads text/uri-list into one) with Copy
    // or Move and records the dropped files' URIs.
    private static unsafe partial class DropTarget
    {
        internal static Gtk.Window Create(int size)
        {
            var window = Gtk.Window.New();
            window.SetDefaultSize(size, size);
            window.SetChild(Gtk.Label.New("drop here"));
            var target = DropTargetNew(FileListGetType(), 1 | 2); // GDK_ACTION_COPY | GDK_ACTION_MOVE
            _ = SignalConnectData(target, "drop", (nint)(delegate* unmanaged[Cdecl]<nint, nint, double, double, nint, int>)&OnDrop, 0, 0, 0);
            WidgetAddController(window.Handle.DangerousGetHandle(), target);
            window.Present();
            return window;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int OnDrop(nint target, nint value, double x, double y, nint userData)
        {
            List<string> uris = [];
            var files = FileListGetFiles(ValueGetBoxed(value));
            for (var item = files; item != 0; item = ((nint*)item)[1])
            {
                var uri = FileGetUri(((nint*)item)[0]);
                uris.Add(Marshal.PtrToStringUTF8(uri)!);
                Free(uri);
            }
            SListFree(files);
            LinuxDragOutSmoke.RecordDrop([.. uris]);
            return 1;
        }

        [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_drop_target_new")] private static partial nint DropTargetNew(nuint type, int actions);
        [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_widget_add_controller")] private static partial void WidgetAddController(nint widget, nint controller);
        [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_file_list_get_type")] private static partial nuint FileListGetType();
        [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_file_list_get_files")] private static partial nint FileListGetFiles(nint list);
        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_get_boxed")] private static partial nint ValueGetBoxed(nint value);
        [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_file_get_uri")] private static partial nint FileGetUri(nint file);
        [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_slist_free")] private static partial void SListFree(nint list);
        [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_free")] private static partial void Free(nint memory);
        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nuint SignalConnectData(nint instance, string signal, nint handler, nint data, nint destroy, int flags);
    }
}
#pragma warning restore CA1416
