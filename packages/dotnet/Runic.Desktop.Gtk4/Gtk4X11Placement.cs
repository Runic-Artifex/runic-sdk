using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Gtk4;

// GTK 4 has no API for the position of a top-level window. On X11 the window is an X window that GDK exposes
// (gdk_x11_surface_get_xid), so Runic reads and sets its position through Xlib, as GTK 3's gtk_window_move and
// gtk_window_get_position did. Wayland has no global coordinates; there the compositor keeps placing every window.
// The GDK X11 exports exist only when GTK was built with its X11 backend, so every entry point is optional.
[SupportedOSPlatform("linux")]
internal static unsafe class Gtk4X11Placement
{
    private const int ConfigureNotify = 22;
    private const nuint XaCardinal = 6;

    private static readonly Lazy<Api?> Functions = new(Api.Load);

    // Set on the main thread once GTK has opened its display; null before that.
    private static bool? s_displayIsX11;

    // Whether GTK 4 positions windows in this process: on an X11 display, or before GTK has opened its display, when
    // GDK will choose X11. Validation runs before the display opens, so it uses the same backend order as GDK.
    internal static bool IsActive => s_displayIsX11 ?? (Functions.Value is not null && PrefersX11(
        Environment.GetEnvironmentVariable("GDK_BACKEND"),
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
        Environment.GetEnvironmentVariable("DISPLAY")));

    // GDK tries the backends GDK_BACKEND lists, in order ('*' for the default order), and by default Wayland before X11.
    internal static bool PrefersX11(string? backends, string? waylandDisplay, string? display)
    {
        foreach (var entry in (string.IsNullOrWhiteSpace(backends) ? "*" : backends).Split(','))
        {
            switch (entry.Trim())
            {
                case "x11":
                    return !string.IsNullOrEmpty(display);
                case "wayland":
                    if (!string.IsNullOrEmpty(waylandDisplay)) return false;
                    break;
                case "*":
                    if (!string.IsNullOrEmpty(waylandDisplay)) return false;
                    if (!string.IsNullOrEmpty(display)) return true;
                    break;
            }
        }
        return false;
    }

    // Main thread, after gtk_init_check opened the default display.
    internal static void DisplayOpened()
    {
        var api = Functions.Value;
        var display = api is null ? 0 : api.GdkDisplayGetDefault();
        s_displayIsX11 = api is not null && display != 0 && api.IsX11Display(display);
    }

    // GTK thread. The top-left corner of the window's visible frame in logical pixels, as GTK 3 reports it: the frame
    // the window manager put around the window or, for GTK's client-side decorations, the window without its shadow.
    internal static (int X, int Y)? GetPosition(nint surface)
    {
        if (!IsActive || surface == 0 || Functions.Value is not { } api || !api.IsX11Surface(surface)) return null;
        var display = api.XDisplay(surface);
        var window = api.Xid(surface);
        if (display == 0 || window == 0) return null;
        var frame = api.TopLevelAncestor(display, window, out var root);
        if (frame == 0 || !api.TranslateToRoot(display, frame, root, out var x, out var y)) return null;
        var (left, top) = api.FrameExtents(display, window);
        var scale = Math.Max(1, api.GdkSurfaceGetScaleFactor(surface));
        return ((int)Math.Floor((x + left) / (double)scale), (int)Math.Floor((y + top) / (double)scale));
    }

    // GTK thread. Moves the window so that GetPosition reports (x, y). A window manager receives it as a configure
    // request with the window's gravity, as it receives gtk_window_move of GTK 3.
    internal static bool Move(nint surface, int x, int y)
    {
        if (!IsActive || surface == 0 || Functions.Value is not { } api || !api.IsX11Surface(surface)) return false;
        var display = api.XDisplay(surface);
        var window = api.Xid(surface);
        if (display == 0 || window == 0) return false;
        var (left, top) = api.FrameExtents(display, window);
        var scale = Math.Max(1, api.GdkSurfaceGetScaleFactor(surface));
        api.XMoveWindow(display, window, checked(x * scale - left), checked(y * scale - top));
        api.XFlush(display);
        return true;
    }

    // GTK thread. A monitor's work area on X11; null on Wayland, which reports none.
    internal static ScreenRect? MonitorWorkArea(nint monitor) =>
        s_displayIsX11 == true && Functions.Value is { } api ? api.MonitorWorkArea(monitor) : null;

    // GTK thread. The work areas of the monitors in logical pixels, or their geometry where X11 reports no work area.
    internal static IReadOnlyList<ScreenRect> GetWorkAreas()
    {
        List<ScreenRect> areas = [];
        if (Gdk.Display.GetDefault()?.GetMonitors() is not { } monitors) return areas;
        var api = Functions.Value;
        for (uint index = 0; index < monitors.GetNItems(); index++)
        {
            if (monitors.GetObject(index) is not Gdk.Monitor monitor) continue;
            monitor.GetGeometry(out var geometry);
            var area = new ScreenRect(geometry.X, geometry.Y, geometry.Width, geometry.Height);
            if (api?.MonitorWorkArea(monitor.Handle.DangerousGetHandle()) is { Width: > 0, Height: > 0 } workArea) area = workArea;
            areas.Add(area);
        }
        return areas;
    }

    // GTK thread. Calls onMoved on the GTK thread for every ConfigureNotify of the surface's X window, which an X server
    // sends when the window moves and a reparenting window manager sends when it moves the frame (ICCCM 4.1.5).
    // Returns the connection to pass to Disconnect, or zero.
    internal static (nint Display, nuint Handler) ConnectMoved(nint surface, Action onMoved)
    {
        if (!IsActive || surface == 0 || Functions.Value is not { } api || !api.IsX11Surface(surface)) return default;
        var window = api.Xid(surface);
        var display = api.GdkSurfaceGetDisplay(surface);
        if (window == 0 || display == 0) return default;
        var context = GCHandle.Alloc(new MoveWatch(window, onMoved));
        var handler = api.SignalConnectData(display, "xevent",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnXEvent, GCHandle.ToIntPtr(context),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Release, 0);
        if (handler == 0)
        {
            context.Free();
            return default;
        }
        return (display, handler);
    }

    internal static void Disconnect((nint Display, nuint Handler) connection)
    {
        if (connection.Handler != 0 && Functions.Value is { } api) api.SignalHandlerDisconnect(connection.Display, connection.Handler);
    }

    private sealed record MoveWatch(nuint Window, Action OnMoved);

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnXEvent(nint display, nint xevent, nint context)
    {
        try
        {
            // XConfigureEvent on LP64: int type at 0, Window event at 32, Window window at 40.
            if (xevent != 0 && *(int*)xevent == ConfigureNotify
                && GCHandle.FromIntPtr(context).Target is MoveWatch watch && *(nuint*)(xevent + 40) == watch.Window)
            {
                watch.OnMoved();
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GDK.
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void Release(nint context, nint closure) => GCHandle.FromIntPtr(context).Free();

    private sealed class Api
    {
        private readonly delegate* unmanaged[Cdecl]<nint> _gdkDisplayGetDefault;
        private readonly delegate* unmanaged[Cdecl]<nuint> _gdkX11DisplayGetType;
        private readonly delegate* unmanaged[Cdecl]<nuint> _gdkX11SurfaceGetType;
        private readonly delegate* unmanaged[Cdecl]<nint, nuint> _gdkX11SurfaceGetXid;
        private readonly delegate* unmanaged[Cdecl]<nint, nint> _gdkX11DisplayGetXDisplay;
        private readonly delegate* unmanaged[Cdecl]<nint, nint> _gdkSurfaceGetDisplay;
        private readonly delegate* unmanaged[Cdecl]<nint, int> _gdkSurfaceGetScaleFactor;
        private readonly delegate* unmanaged[Cdecl]<nint, GdkRectangle*, void> _gdkX11MonitorGetWorkarea;
        private readonly delegate* unmanaged[Cdecl]<nint, nuint, int> _gTypeCheckInstanceIsA;
        private readonly delegate* unmanaged[Cdecl]<nint, byte*, nint, nint, nint, int, nuint> _gSignalConnectData;
        private readonly delegate* unmanaged[Cdecl]<nint, nuint, void> _gSignalHandlerDisconnect;
        private readonly delegate* unmanaged[Cdecl]<nint, nuint, int, int, int> _xMoveWindow;
        private readonly delegate* unmanaged[Cdecl]<nint, nuint, nuint*, nuint*, nuint**, uint*, int> _xQueryTree;
        private readonly delegate* unmanaged[Cdecl]<nint, nuint, nuint, int, int, int*, int*, nuint*, int> _xTranslateCoordinates;
        private readonly delegate* unmanaged[Cdecl]<nint, byte*, int, nuint> _xInternAtom;
        private readonly delegate* unmanaged[Cdecl]<nint, nuint, nuint, nint, nint, int, nuint, nuint*, int*, nuint*, nuint*, byte**, int> _xGetWindowProperty;
        private readonly delegate* unmanaged[Cdecl]<nint, int> _xFree;
        private readonly delegate* unmanaged[Cdecl]<nint, int> _xFlush;

        private Api(nint gtk, nint gobject, nint x11)
        {
            _gdkDisplayGetDefault = (delegate* unmanaged[Cdecl]<nint>)NativeLibrary.GetExport(gtk, "gdk_display_get_default");
            _gdkX11DisplayGetType = (delegate* unmanaged[Cdecl]<nuint>)NativeLibrary.GetExport(gtk, "gdk_x11_display_get_type");
            _gdkX11SurfaceGetType = (delegate* unmanaged[Cdecl]<nuint>)NativeLibrary.GetExport(gtk, "gdk_x11_surface_get_type");
            _gdkX11SurfaceGetXid = (delegate* unmanaged[Cdecl]<nint, nuint>)NativeLibrary.GetExport(gtk, "gdk_x11_surface_get_xid");
            _gdkX11DisplayGetXDisplay = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(gtk, "gdk_x11_display_get_xdisplay");
            _gdkSurfaceGetDisplay = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(gtk, "gdk_surface_get_display");
            _gdkSurfaceGetScaleFactor = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(gtk, "gdk_surface_get_scale_factor");
            _gdkX11MonitorGetWorkarea = NativeLibrary.TryGetExport(gtk, "gdk_x11_monitor_get_workarea", out var workArea)
                ? (delegate* unmanaged[Cdecl]<nint, GdkRectangle*, void>)workArea
                : null;
            _gTypeCheckInstanceIsA = (delegate* unmanaged[Cdecl]<nint, nuint, int>)NativeLibrary.GetExport(gobject, "g_type_check_instance_is_a");
            _gSignalConnectData = (delegate* unmanaged[Cdecl]<nint, byte*, nint, nint, nint, int, nuint>)NativeLibrary.GetExport(gobject, "g_signal_connect_data");
            _gSignalHandlerDisconnect = (delegate* unmanaged[Cdecl]<nint, nuint, void>)NativeLibrary.GetExport(gobject, "g_signal_handler_disconnect");
            _xMoveWindow = (delegate* unmanaged[Cdecl]<nint, nuint, int, int, int>)NativeLibrary.GetExport(x11, "XMoveWindow");
            _xQueryTree = (delegate* unmanaged[Cdecl]<nint, nuint, nuint*, nuint*, nuint**, uint*, int>)NativeLibrary.GetExport(x11, "XQueryTree");
            _xTranslateCoordinates = (delegate* unmanaged[Cdecl]<nint, nuint, nuint, int, int, int*, int*, nuint*, int>)NativeLibrary.GetExport(x11, "XTranslateCoordinates");
            _xInternAtom = (delegate* unmanaged[Cdecl]<nint, byte*, int, nuint>)NativeLibrary.GetExport(x11, "XInternAtom");
            _xGetWindowProperty = (delegate* unmanaged[Cdecl]<nint, nuint, nuint, nint, nint, int, nuint, nuint*, int*, nuint*, nuint*, byte**, int>)NativeLibrary.GetExport(x11, "XGetWindowProperty");
            _xFree = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(x11, "XFree");
            _xFlush = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(x11, "XFlush");
        }

        // Null without GTK's X11 backend or without Xlib, which that backend links.
        internal static Api? Load()
        {
            try
            {
                if (!NativeLibrary.TryLoad(Gtk4NativeLibraries.Gtk, out var gtk)
                    || !NativeLibrary.TryGetExport(gtk, "gdk_x11_surface_get_xid", out _)
                    || !NativeLibrary.TryLoad("libgobject-2.0.so.0", out var gobject)
                    || !NativeLibrary.TryLoad("libX11.so.6", out var x11))
                {
                    return null;
                }
                return new Api(gtk, gobject, x11);
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }

        internal nint GdkDisplayGetDefault() => _gdkDisplayGetDefault();

        internal bool IsX11Display(nint display) => _gTypeCheckInstanceIsA(display, _gdkX11DisplayGetType()) != 0;

        internal bool IsX11Surface(nint surface) => _gTypeCheckInstanceIsA(surface, _gdkX11SurfaceGetType()) != 0;

        internal nuint Xid(nint surface) => _gdkX11SurfaceGetXid(surface);

        internal nint GdkSurfaceGetDisplay(nint surface) => _gdkSurfaceGetDisplay(surface);

        internal nint XDisplay(nint surface) => _gdkSurfaceGetDisplay(surface) is var display and not 0 ? _gdkX11DisplayGetXDisplay(display) : 0;

        internal int GdkSurfaceGetScaleFactor(nint surface) => _gdkSurfaceGetScaleFactor(surface);

        internal ScreenRect? MonitorWorkArea(nint monitor)
        {
            if (_gdkX11MonitorGetWorkarea is null || monitor == 0) return null;
            GdkRectangle area;
            _gdkX11MonitorGetWorkarea(monitor, &area);
            return new ScreenRect(area.X, area.Y, area.Width, area.Height);
        }

        internal void XMoveWindow(nint display, nuint window, int x, int y) => _xMoveWindow(display, window, x, y);

        internal void XFlush(nint display) => _xFlush(display);

        // The ancestor of the window that is a child of the root window: the window manager's frame, or the window itself.
        internal nuint TopLevelAncestor(nint display, nuint window, out nuint root)
        {
            root = 0;
            for (var depth = 0; depth < 16; depth++)
            {
                nuint windowRoot, parent;
                nuint* children;
                uint count;
                if (_xQueryTree(display, window, &windowRoot, &parent, &children, &count) == 0) return 0;
                if (children is not null) _xFree((nint)children);
                root = windowRoot;
                if (parent == 0 || parent == windowRoot) return window;
                window = parent;
            }
            return 0;
        }

        internal bool TranslateToRoot(nint display, nuint window, nuint root, out int x, out int y)
        {
            int rootX, rootY;
            nuint child;
            var translated = _xTranslateCoordinates(display, window, root, 0, 0, &rootX, &rootY, &child) != 0;
            (x, y) = (rootX, rootY);
            return translated;
        }

        // GTK's client-side decorations draw a shadow inside the X window and publish its width as _GTK_FRAME_EXTENTS
        // (left, right, top, bottom); the visible frame starts inside it.
        internal (int Left, int Top) FrameExtents(nint display, nuint window)
        {
            nuint atom;
            fixed (byte* name = "_GTK_FRAME_EXTENTS\0"u8) atom = _xInternAtom(display, name, 1);
            if (atom == 0) return (0, 0);
            nuint actualType, count, remaining;
            int format;
            byte* data = null;
            if (_xGetWindowProperty(display, window, atom, 0, 4, 0, XaCardinal, &actualType, &format, &count, &remaining, &data) != 0)
                return (0, 0);
            try
            {
                // Format-32 properties come back as an array of C longs.
                return data is not null && format == 32 && count == 4
                    ? ((int)((nint*)data)[0], (int)((nint*)data)[2])
                    : (0, 0);
            }
            finally
            {
                if (data is not null) _xFree((nint)data);
            }
        }

        internal nuint SignalConnectData(nint instance, string signal, nint handler, nint data, nint destroy, int flags)
        {
            var name = Marshal.StringToCoTaskMemUTF8(signal);
            try
            {
                return _gSignalConnectData(instance, (byte*)name, handler, data, destroy, flags);
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
            }
        }

        internal void SignalHandlerDisconnect(nint instance, nuint handler) => _gSignalHandlerDisconnect(instance, handler);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdkRectangle
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
    }
}
