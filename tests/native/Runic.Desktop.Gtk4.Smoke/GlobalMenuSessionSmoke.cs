using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Desktop;
using Runic.Desktop.Gtk4;
using Runic.Platform;

// --global-menu-session gtk3|gtk4 <log> (B10 PR 8, D036.9) on a real desktop session, for nixos/global-menu: a window
// with MenuSmoke's bar that stays open and writes what happens to <log>, one line each:
// - "open <toolkit> <x11|wayland> placement=<placement>", then "placement <placement>" for every change;
// - "surface <hex>" (the window's wl_surface) or "xid <hex>";
// - "executed <command> <parameter>" when the global menu (or the window's bar) runs an item.
// The VM test drives it with files next to the log: <log>.hide hides the window and shows it again, then logs
// "shown surface <hex>" (or "shown xid <hex>"); <log>.quit closes it and logs "closed". The menu view's side (kded's
// registrar, KWin's window state, plasmashell reading the menu) is checked by the VM test.
[SupportedOSPlatform("linux")]
internal static partial class GlobalMenuSessionSmoke
{
    private sealed class Target(Action<string> log) : IDesktopMenuCommandTarget
    {
        public bool CanExecute(DesktopMenuItem item) => item.CommandId is "file.save" or "view.zoom" or "view.wrap";
        public void Execute(DesktopMenuItem item) => log($"executed {item.CommandId} {item.CommandParameter ?? "-"}");
    }

    internal static int Run(string toolkit, string logPath)
    {
        var options = toolkit switch
        {
            "gtk3" => new DesktopHostOptions { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } },
            "gtk4" => new DesktopHostOptions().WithGtk4(),
            _ => throw new ArgumentException($"Unknown toolkit '{toolkit}'; use gtk3 or gtk4.", nameof(toolkit)),
        };
        options = options with { WaitForConnection = true, MenuBar = MenuSmoke.Bar };
        var gate = new Lock();
        void Log(string line)
        {
            lock (gate) File.AppendAllText(logPath, line + "\n");
            Console.WriteLine(line);
        }
        return DesktopEventLoop.Run(options, async host =>
        {
            try
            {
                return await RunAsync(host, toolkit, logPath, Log);
            }
            catch (Exception error)
            {
                Log("failed " + error.Message.ReplaceLineEndings(" "));
                Console.Error.WriteLine(error);
                throw;
            }
        });
    }

    private static async Task<int> RunAsync(DesktopHost host, string toolkit, string logPath, Action<string> log)
    {
        if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is null)
            throw new InvalidOperationException("Global menu session: no session bus.");
        var page = $"<!doctype html><html><head><title>Runic Global Menu {toolkit}</title><script src=\"webui.js\"></script></head><body>global menu {toolkit}</body></html>";
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(page) });
        var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 640, Height = 400 });
        window.MenuCommands = new Target(log);
        window.MenuBarPlacementChanged += (_, _) => log("placement " + window.MenuBarPlacement);
        var wayland = await NativeAsync(window, handle => Surface(toolkit, handle)) != 0;
        log($"open {toolkit} {(wayland ? "wayland" : "x11")} placement={window.MenuBarPlacement}");
        log(await HandleAsync(window, toolkit, wayland));
        while (true)
        {
            await Task.Delay(200);
            if (File.Exists(logPath + ".quit"))
            {
                File.Delete(logPath + ".quit");
                await window.CloseAsync();
                log("closed");
                return 0;
            }
            if (!File.Exists(logPath + ".hide")) continue;
            File.Delete(logPath + ".hide");
            await window.SetVisibleAsync(false);
            log("hidden");
            await Task.Delay(1500);
            await window.SetVisibleAsync(true);
            await window.FocusAsync();
            await Task.Delay(500);
            log("shown " + await HandleAsync(window, toolkit, wayland));
        }
    }

    private static async Task<string> HandleAsync(DesktopWindow window, string toolkit, bool wayland) => wayland
        ? $"surface {await NativeAsync(window, handle => Surface(toolkit, handle)):x}"
        : $"xid {await NativeAsync(window, handle => (nint)Xid(toolkit, handle)):x}";

    private static async Task<nint> NativeAsync(DesktopWindow window, Func<nint, nint> read)
    {
        nint value = 0;
        await window.InvokeNativeAsync(handle => value = read(handle));
        return value;
    }

    // The window's wl_surface on Wayland, zero on X11.
    private static nint Surface(string toolkit, nint window)
    {
        if (toolkit == "gtk3")
        {
            var gdkWindow = Gtk3.WidgetGetWindow(window);
            return gdkWindow != 0 && Gtk3.TypeCheckInstanceIsA(gdkWindow, Gtk3.WaylandWindowType()) != 0 ? Gtk3.WaylandWindowGetSurface(gdkWindow) : 0;
        }
        var gdkSurface = ((Gtk.Window)GObject.Internal.InstanceWrapper.WrapHandle<Gtk.Window>(window, false)).GetSurface()?.Handle.DangerousGetHandle() ?? 0;
        return gdkSurface != 0 && Gtk3.TypeCheckInstanceIsA(gdkSurface, Gtk4.WaylandSurfaceType()) != 0 ? Gtk4.WaylandSurfaceGetSurface(gdkSurface) : 0;
    }

    private static ulong Xid(string toolkit, nint window) => toolkit == "gtk3"
        ? Gtk3.X11WindowGetXid(Gtk3.WidgetGetWindow(window))
        : Gtk4.X11SurfaceGetXid(((Gtk.Window)GObject.Internal.InstanceWrapper.WrapHandle<Gtk.Window>(window, false)).GetSurface()!.Handle.DangerousGetHandle());

    private static partial class Gtk3
    {
        private const string Library = "libgtk-3.so.0";
        [LibraryImport(Library, EntryPoint = "gtk_widget_get_window")] internal static partial nint WidgetGetWindow(nint widget);
        [LibraryImport(Library, EntryPoint = "gdk_x11_window_get_xid")] internal static partial ulong X11WindowGetXid(nint window);
        [LibraryImport(Library, EntryPoint = "gdk_wayland_window_get_type")] internal static partial nuint WaylandWindowType();
        [LibraryImport(Library, EntryPoint = "gdk_wayland_window_get_wl_surface")] internal static partial nint WaylandWindowGetSurface(nint window);
        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_type_check_instance_is_a")] internal static partial int TypeCheckInstanceIsA(nint instance, nuint type);
    }

    private static partial class Gtk4
    {
        private const string Library = "libgtk-4.so.1";
        [LibraryImport(Library, EntryPoint = "gdk_x11_surface_get_xid")] internal static partial ulong X11SurfaceGetXid(nint surface);
        [LibraryImport(Library, EntryPoint = "gdk_wayland_surface_get_type")] internal static partial nuint WaylandSurfaceType();
        [LibraryImport(Library, EntryPoint = "gdk_wayland_surface_get_wl_surface")] internal static partial nint WaylandSurfaceGetSurface(nint surface);
    }
}
