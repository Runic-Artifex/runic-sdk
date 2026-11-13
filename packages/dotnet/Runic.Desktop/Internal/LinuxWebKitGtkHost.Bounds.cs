using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// DesktopWindow.SetBoundsAsync and SetMinimumSizeAsync, and the exact, visible and snapped parts of DesktopWindowState
// (#864, B1). GTK 3 reports and takes logical pixels already: the frame's position and the content size round-trip as
// they are, so the bounds are the state's values in one record.
internal sealed partial class LinuxWebKitGtkHost
{
    private const int GdkWindowStateTiledEdges = (1 << 9) | (1 << 11) | (1 << 13) | (1 << 15);

    // gdk_window_get_frame_extents: on X11 the frame the window manager put around the window, measured with
    // _NET_FRAME_EXTENTS where it publishes them, in logical pixels.
    private static readonly Lazy<nint> GdkWindowGetFrameExtents = new(static () => Gtk3Export("gdk_window_get_frame_extents"));
    private static readonly Lazy<nint> GtkWidgetGetWindow = new(static () => Gtk3Export("gtk_widget_get_window"));

    private static nint Gtk3Export(string name) =>
        NativeLibrary.TryLoad("libgtk-3.so.0", out var gtk) && NativeLibrary.TryGetExport(gtk, name, out var export) ? export : 0;

    // gtk_window_move and gtk_window_resize in one main-loop turn: GTK sends the window manager one configure request
    // with both, so the window is never shown at the new position with its old size.
    public ValueTask SetBoundsAsync(DesktopWindowBounds bounds, CancellationToken cancellationToken = default)
        => InvokeWindowAsync(() =>
        {
            var flags = Api.CanReportState ? Api.GetWindowStateFlags(_window) : 0;
            if ((flags & (GdkWindowStateIconified | GdkWindowStateMaximized | GdkWindowStateFullscreen)) != 0
                || Api.GtkWindowIsMaximized(_window))
            {
                throw WindowBoundsSupport.NotRestored();
            }
            if (bounds is { X: { } x, Y: { } y })
            {
                if (Api.IsWaylandDisplay()) throw WindowBoundsSupport.PositionUnavailable();
                Api.GtkWindowMove(_window, x, y);
            }
            Api.GtkWindowResize(_window, bounds.Width, bounds.Height);
        }, cancellationToken);

    // The size request of the window, as DesktopWindowOptions.MinimumWidth and MinimumHeight set it: GTK grows a smaller
    // window to it and keeps the user from making it smaller.
    public ValueTask SetMinimumSizeAsync(int width, int height, CancellationToken cancellationToken = default)
        => InvokeWindowAsync(() => Api.GtkWidgetSetSizeRequest(_window, width == 0 ? -1 : width, height == 0 ? -1 : height), cancellationToken);

    // GTK thread only, for UpdateState: the bounds, the frame the user sees (X11 only), and whether the desktop tiled the
    // window: GDK's tiled state, or on X11 a visible frame that fills a half or a quarter of its monitor's work area.
    private (DesktopWindowBounds Bounds, DesktopWindowBounds? Visible, bool Snapped) ReadExactBounds(
        int? x, int? y, int width, int height, int flags, bool maximized, bool fullScreen)
    {
        var bounds = new DesktopWindowBounds { X = x, Y = y, Width = width, Height = height, Scale = 1 };
        var visible = x is null ? null : ReadVisibleBounds();
        var tiled = (flags & (GdkWindowStateTiled | GdkWindowStateTiledEdges)) != 0;
        if (!tiled && visible is { X: { } visibleX, Y: { } visibleY }
            && WindowBoundsSupport.WorkAreaOf(new ScreenRect(visibleX, visibleY, visible.Width, visible.Height), Api.GetWorkAreas()) is { } area)
        {
            tiled = WindowBoundsSupport.LooksTiled(new ScreenRect(visibleX, visibleY, visible.Width, visible.Height), area, tolerance: 0, margin: 0);
        }
        return (bounds, visible, tiled && !maximized && !fullScreen);
    }

    private unsafe DesktopWindowBounds? ReadVisibleBounds()
    {
        if (GdkWindowGetFrameExtents.Value is not (var extents and not 0) || GtkWidgetGetWindow.Value is not (var getWindow and not 0)) return null;
        var gdkWindow = ((delegate* unmanaged[Cdecl]<nint, nint>)getWindow)(_window);
        if (gdkWindow == 0) return null;
        FrameRectangle frame;
        ((delegate* unmanaged[Cdecl]<nint, FrameRectangle*, void>)extents)(gdkWindow, &frame);
        return frame.Width <= 0 || frame.Height <= 0
            ? null
            : new DesktopWindowBounds { X = frame.X, Y = frame.Y, Width = frame.Width, Height = frame.Height, Scale = 1 };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameRectangle
    {
        internal int X;
        internal int Y;
        internal int Width;
        internal int Height;
    }
}
