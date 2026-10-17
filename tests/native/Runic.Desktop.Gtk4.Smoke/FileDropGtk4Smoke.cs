using System.Text;
using Runic.Desktop;

#pragma warning disable CA1416 // The GTK 4 smoke runs on Linux.
// W315-006: a real X11 drag and drop from a GTK 4 drag source onto the GTK 4 WebKitGTK 6 window, driven through
// XTEST under Xvfb. The window reports the native paths with the page's routing label, the page still receives its
// drop event, and the window does not navigate to the dropped file.
internal static class FileDropGtk4Smoke
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
            await window.DispatchNativeAsync(_ =>
            {
                source = Gtk.Window.New();
                source.SetDefaultSize(120, 120);
                var label = Gtk.Label.New("drag me");
                var drag = Gtk.DragSource.New();
                drag.SetActions(Gdk.DragAction.Copy);
                drag.OnPrepare += (_, _) => Gdk.ContentProvider.NewForBytes("text/uri-list", GLib.Bytes.New(Encoding.UTF8.GetBytes(uriList)));
                label.AddController(drag);
                source.SetChild(label);
                source.Present();
            });
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
                await window.DispatchNativeAsync(_ => source?.Destroy());
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
#pragma warning restore CA1416
