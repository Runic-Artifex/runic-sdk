using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Gtk4;

// GDK 4's Wayland display and surface of a window, for the global menu's org_kde_kwin_appmenu (WaylandAppMenu). The
// exports exist only when GTK was built with its Wayland backend, so every entry point is optional.
[SupportedOSPlatform("linux")]
internal static unsafe class Gtk4Wayland
{
    private static readonly Lazy<Api?> Functions = new(Api.Load);

    // libgtk-4, which links libwayland-client; zero without the Wayland backend.
    internal static nint Library => Functions.Value?.Library ?? 0;

    // GTK thread. The wl_display and wl_surface of a realized surface on Wayland; zeros on X11.
    internal static (nint Display, nint Surface) Window(nint surface)
    {
        if (surface == 0 || Functions.Value is not { } api || api.IsInstance(surface, api.WaylandSurfaceGetType()) == 0) return default;
        var display = api.SurfaceGetDisplay(surface);
        return display == 0 || api.IsInstance(display, api.WaylandDisplayGetType()) == 0
            ? default
            : (api.WaylandDisplayGetDisplay(display), api.WaylandSurfaceGetSurface(surface));
    }

    private sealed class Api
    {
        private Api(nint gtk, nint gobject)
        {
            Library = gtk;
            WaylandSurfaceGetType = (delegate* unmanaged[Cdecl]<nuint>)NativeLibrary.GetExport(gtk, "gdk_wayland_surface_get_type");
            WaylandSurfaceGetSurface = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(gtk, "gdk_wayland_surface_get_wl_surface");
            WaylandDisplayGetType = (delegate* unmanaged[Cdecl]<nuint>)NativeLibrary.GetExport(gtk, "gdk_wayland_display_get_type");
            WaylandDisplayGetDisplay = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(gtk, "gdk_wayland_display_get_wl_display");
            SurfaceGetDisplay = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(gtk, "gdk_surface_get_display");
            IsInstance = (delegate* unmanaged[Cdecl]<nint, nuint, int>)NativeLibrary.GetExport(gobject, "g_type_check_instance_is_a");
        }

        internal nint Library { get; }
        internal delegate* unmanaged[Cdecl]<nuint> WaylandSurfaceGetType { get; }
        internal delegate* unmanaged[Cdecl]<nint, nint> WaylandSurfaceGetSurface { get; }
        internal delegate* unmanaged[Cdecl]<nuint> WaylandDisplayGetType { get; }
        internal delegate* unmanaged[Cdecl]<nint, nint> WaylandDisplayGetDisplay { get; }
        internal delegate* unmanaged[Cdecl]<nint, nint> SurfaceGetDisplay { get; }
        internal delegate* unmanaged[Cdecl]<nint, nuint, int> IsInstance { get; }

        internal static Api? Load()
        {
            try
            {
                return OperatingSystem.IsLinux() && NativeLibrary.TryLoad(Gtk4NativeLibraries.Gtk, out var gtk)
                    && NativeLibrary.TryLoad("libgobject-2.0.so.0", out var gobject)
                    && NativeLibrary.TryGetExport(gtk, "gdk_wayland_surface_get_wl_surface", out _)
                    ? new Api(gtk, gobject)
                    : null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }
    }
}
