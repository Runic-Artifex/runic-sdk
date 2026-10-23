using System.Runtime.InteropServices;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// B10, D036.9: the GTK 3 window's bar in the desktop's global menu (LinuxGlobalMenu). On X11, a window with the Native
// presentation that is not a kiosk exports its bar once it is realized, and LinuxDesktopOptions.GlobalMenu allows it.
// While the global menu shows it, the in-window GtkMenuBar is hidden, not destroyed; a frameless window has none.
internal sealed partial class LinuxWebKitGtkHost
{
    private static readonly Lazy<Gtk3X11?> X11Api = new(Gtk3X11.Load);

    // GTK thread only: the window's global menu, and whether it shows the bar now.
    private LinuxGlobalMenu? _globalMenu;
    private volatile bool _globalPlacement;

    public event EventHandler? MenuBarPlacementChanged;


    // GTK thread, once the window is realized.
    private void StartGlobalMenu(WebUiEmbeddedHostOptions options)
    {
        if (!GtkMenus.UsesGlobalMenu(options.MenuBarPresentation, options.Kiosk, options.GlobalMenu) || Api.IsWaylandDisplay()
            || X11Api.Value is not { } x11 || x11.Window(_window) is not { Xid: not 0 } window || window.Xid > uint.MaxValue)
            return;
        _globalMenu = LinuxGlobalMenu.Start(new GlobalMenuWindow(this), (uint)window.Xid, _plan);
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

        public void Annotate(string serviceName, string objectPath)
        {
            if (X11Api.Value is { } x11 && x11.Window(host._window) is { Xid: not 0 } window)
                LinuxGlobalMenu.X11.Annotate(window.Display, window.Xid, serviceName, objectPath);
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
}
