using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// B10, D036.9: the GTK 3 window's bar in the desktop's global menu (LinuxGlobalMenu). A window with the Native
// presentation that is not a kiosk exports its bar once it is realized, and LinuxDesktopOptions.GlobalMenu allows it.
// On X11 it is registered by its XID; on Wayland its wl_surface gets org_kde_kwin_appmenu (WaylandAppMenu). GDK 3
// destroys the wl_surface when the window is hidden and creates another when it is shown, so the address is released
// on unmap and set again on map. While the global menu shows the bar, the in-window GtkMenuBar is hidden, not
// destroyed; a frameless window has none.
internal sealed partial class LinuxWebKitGtkHost
{
    private static readonly Lazy<Gtk3X11?> X11Api = new(Gtk3X11.Load);
    private static readonly Lazy<Gtk3Wayland?> WaylandApi = new(Gtk3Wayland.Load);

    // GTK thread only: the window's global menu, and whether it shows the bar now.
    private LinuxGlobalMenu? _globalMenu;
    private volatile bool _globalPlacement;
    // GTK thread only: a Wayland window's org_kde_kwin_appmenu, once the menu is exported and the compositor offers it.
    private WaylandAppMenu? _appMenu;

    public event EventHandler? MenuBarPlacementChanged;


    // GTK thread, once the window is realized.
    private unsafe void StartGlobalMenu(WebUiEmbeddedHostOptions options)
    {
        if (!GtkMenus.UsesGlobalMenu(options.MenuBarPresentation, options.Kiosk, options.GlobalMenu)) return;
        if (Api.IsWaylandDisplay())
        {
            if (WaylandApi.Value is not { } wayland || wayland.Display(_window) == 0) return;
            Api.Connect(_window, "map", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnMappedForAppMenu, this, after: true);
            Api.Connect(_window, "unmap", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnUnmappedForAppMenu, this);
            _globalMenu = LinuxGlobalMenu.Start(new GlobalMenuWindow(this), xid: null, _plan);
            return;
        }
        if (X11Api.Value is not { } x11 || x11.Window(_window) is not { Xid: not 0 } window || window.Xid > uint.MaxValue) return;
        _globalMenu = LinuxGlobalMenu.Start(new GlobalMenuWindow(this), (uint)window.Xid, _plan);
    }

    // GTK thread: the menu is exported; sets its address on the window's wl_surface. False when the compositor does not
    // offer org_kde_kwin_appmenu.
    private bool AnnotateWayland(string serviceName, string objectPath)
    {
        if (!IsOpen || _globalMenu is null || WaylandApi.Value is not { } wayland) return false;
        _appMenu ??= WaylandAppMenu.Create(wayland.Library, wayland.Display(_window));
        if (_appMenu is not { } appMenu) return false;
        appMenu.SetAddress(serviceName, objectPath, wayland.Surface(_window));
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMappedForAppMenu(nint window, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost { IsOpen: true, _appMenu: { } appMenu } && WaylandApi.Value is { } wayland)
                appMenu.Attach(wayland.Surface(window));
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUnmappedForAppMenu(nint window, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost { _appMenu: { } appMenu }) appMenu.Detach();
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    // GTK thread: the bar went into the global menu or came back.
    private void OnGlobalPlacement(bool global)
    {
        if (!IsOpen) return;
        _globalPlacement = global;
        if (_menuBar != 0)
        {
            if (global) Api.GtkWidgetHide(_menuBar);
            else Api.GtkWidgetShowAll(_menuBar);
        }
        MenuBarPlacementChanged?.Invoke(this, EventArgs.Empty);
    }

    // GTK thread, when the window is destroyed.
    private void StopGlobalMenu()
    {
        var menu = _globalMenu;
        _globalMenu = null;
        _globalPlacement = false;
        _appMenu?.Dispose();
        _appMenu = null;
        if (menu is not null) _ = menu.DisposeAsync().AsTask();
    }

    private sealed class GlobalMenuWindow(LinuxWebKitGtkHost host) : ILinuxGlobalMenuWindow
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken) => Dispatcher.InvokeAsync(() =>
        {
            if (host.IsOpen) action();
        }, cancellationToken);

        public bool CanExecute(DesktopMenuItem item) => host.CanExecuteMenuItem(item);

        public void Execute(DesktopMenuItem item) => host.RunMenuItem(item);

        public void PlacementChanged(bool global) => host.OnGlobalPlacement(global);

        public bool Annotate(string serviceName, string objectPath)
        {
            if (Api.IsWaylandDisplay()) return host.AnnotateWayland(serviceName, objectPath);
            if (X11Api.Value is { } x11 && x11.Window(host._window) is { Xid: not 0 } window)
                LinuxGlobalMenu.X11.Annotate(window.Display, window.Xid, serviceName, objectPath);
            return true;
        }

        public void Report(DesktopDiagnostic diagnostic) => host._options?.ReportDiagnostic?.Invoke(diagnostic);
    }

    // GDK 3's X11 window of a realized GtkWindow; the exports exist only when GTK was built with its X11 backend.
    private sealed unsafe class Gtk3X11
    {
        private readonly delegate* unmanaged[Cdecl]<nint, nint> _widgetGetWindow;
        private readonly delegate* unmanaged[Cdecl]<nuint> _x11WindowGetType;
        private readonly delegate* unmanaged[Cdecl]<nint, nuint> _x11WindowGetXid;
        private readonly delegate* unmanaged[Cdecl]<nint, nint> _windowGetDisplay;
        private readonly delegate* unmanaged[Cdecl]<nint, nint> _x11DisplayGetXDisplay;
        private readonly delegate* unmanaged[Cdecl]<nint, nuint, int> _typeCheckInstanceIsA;

        private Gtk3X11(nint gtk, nint gobject)
        {
            _widgetGetWindow = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(gtk, "gtk_widget_get_window");
            _x11WindowGetType = (delegate* unmanaged[Cdecl]<nuint>)NativeLibrary.GetExport(gtk, "gdk_x11_window_get_type");
            _x11WindowGetXid = (delegate* unmanaged[Cdecl]<nint, nuint>)NativeLibrary.GetExport(gtk, "gdk_x11_window_get_xid");
            _windowGetDisplay = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(gtk, "gdk_window_get_display");
            _x11DisplayGetXDisplay = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(gtk, "gdk_x11_display_get_xdisplay");
            _typeCheckInstanceIsA = (delegate* unmanaged[Cdecl]<nint, nuint, int>)NativeLibrary.GetExport(gobject, "g_type_check_instance_is_a");
        }

        internal static Gtk3X11? Load()
        {
            try
            {
                return OperatingSystem.IsLinux() && NativeLibrary.TryLoad("libgtk-3.so.0", out var gtk)
                    && NativeLibrary.TryLoad("libgobject-2.0.so.0", out var gobject)
                    && NativeLibrary.TryGetExport(gtk, "gdk_x11_window_get_xid", out _)
                    ? new Gtk3X11(gtk, gobject)
                    : null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }

        // GTK thread. The X display and window of a realized top-level on X11; zeros otherwise.
        internal (nint Display, nuint Xid) Window(nint widget)
        {
            if (widget == 0) return default;
            var window = _widgetGetWindow(widget);
            if (window == 0 || _typeCheckInstanceIsA(window, _x11WindowGetType()) == 0) return default;
            var display = _windowGetDisplay(window);
            return (display == 0 ? 0 : _x11DisplayGetXDisplay(display), _x11WindowGetXid(window));
        }
    }

    // GDK 3's Wayland display and surface of a GtkWindow; the exports exist only when GTK was built with its Wayland
    // backend.
    private sealed unsafe class Gtk3Wayland
    {
        private readonly delegate* unmanaged[Cdecl]<nint, nint> _widgetGetWindow;
        private readonly delegate* unmanaged[Cdecl]<nint, nint> _widgetGetDisplay;
        private readonly delegate* unmanaged[Cdecl]<nuint> _waylandWindowGetType;
        private readonly delegate* unmanaged[Cdecl]<nint, nint> _waylandWindowGetSurface;
        private readonly delegate* unmanaged[Cdecl]<nuint> _waylandDisplayGetType;
        private readonly delegate* unmanaged[Cdecl]<nint, nint> _waylandDisplayGetDisplay;
        private readonly delegate* unmanaged[Cdecl]<nint, nuint, int> _typeCheckInstanceIsA;

        private Gtk3Wayland(nint gtk, nint gobject)
        {
            Library = gtk;
            _widgetGetWindow = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(gtk, "gtk_widget_get_window");
            _widgetGetDisplay = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(gtk, "gtk_widget_get_display");
            _waylandWindowGetType = (delegate* unmanaged[Cdecl]<nuint>)NativeLibrary.GetExport(gtk, "gdk_wayland_window_get_type");
            _waylandWindowGetSurface = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(gtk, "gdk_wayland_window_get_wl_surface");
            _waylandDisplayGetType = (delegate* unmanaged[Cdecl]<nuint>)NativeLibrary.GetExport(gtk, "gdk_wayland_display_get_type");
            _waylandDisplayGetDisplay = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(gtk, "gdk_wayland_display_get_wl_display");
            _typeCheckInstanceIsA = (delegate* unmanaged[Cdecl]<nint, nuint, int>)NativeLibrary.GetExport(gobject, "g_type_check_instance_is_a");
        }

        // libgtk-3, which links libgdk-3 and libwayland-client.
        internal nint Library { get; }

        internal static Gtk3Wayland? Load()
        {
            try
            {
                return OperatingSystem.IsLinux() && NativeLibrary.TryLoad("libgtk-3.so.0", out var gtk)
                    && NativeLibrary.TryLoad("libgobject-2.0.so.0", out var gobject)
                    && NativeLibrary.TryGetExport(gtk, "gdk_wayland_window_get_wl_surface", out _)
                    ? new Gtk3Wayland(gtk, gobject)
                    : null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }

        // GTK thread. The wl_display of the widget's GDK display on Wayland; zero otherwise.
        internal nint Display(nint widget)
        {
            if (widget == 0) return 0;
            var display = _widgetGetDisplay(widget);
            return display == 0 || _typeCheckInstanceIsA(display, _waylandDisplayGetType()) == 0 ? 0 : _waylandDisplayGetDisplay(display);
        }

        // GTK thread. The wl_surface of a realized, shown top-level on Wayland; zero otherwise.
        internal nint Surface(nint widget)
        {
            if (widget == 0) return 0;
            var window = _widgetGetWindow(widget);
            return window == 0 || _typeCheckInstanceIsA(window, _waylandWindowGetType()) == 0 ? 0 : _waylandWindowGetSurface(window);
        }
    }
}
