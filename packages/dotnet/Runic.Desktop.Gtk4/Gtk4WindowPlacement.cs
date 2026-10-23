using System.Runtime.Versioning;
using Runic.Desktop.Internal;
using Runic.Platform;

namespace Runic.Desktop.Gtk4;

// Full screen and placement on a monitor for the GTK 4 window (IDesktopPlacementWindowHost). Full screen works on X11
// and Wayland, also on a chosen monitor (gtk_window_fullscreen_on_monitor); moving to a monitor needs X11, where Runic
// positions windows through Xlib (Gtk4X11Placement).
[SupportedOSPlatform("linux")]
internal sealed partial class Gtk4WindowHost
{
    // GTK thread only: the monitor for full screen, from DesktopWindowOptions.MonitorId or MoveToMonitorAsync.
    private Gdk.Monitor? _fullScreenMonitor;

    private static ScreenRect ToScreenRect(DesktopRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private static ScreenRect CenterIn(int width, int height, ScreenRect area)
    {
        width = Math.Max(1, Math.Min(width, area.Width));
        height = Math.Max(1, Math.Min(height, area.Height));
        return new ScreenRect(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
    }

    // GTK thread only.
    private void EnterFullScreen(Gtk.Window window)
    {
        if (_fullScreenMonitor is { } monitor && monitor.IsValid()) window.FullscreenOnMonitor(monitor);
        else window.Fullscreen();
    }

    public ValueTask SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken) =>
        new(Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            if (_options?.Kiosk == true) return;
            var window = GetWindow();
            if (fullScreen) EnterFullScreen(window);
            else window.Unfullscreen();
        }, cancellationToken));

    public ValueTask MoveToMonitorAsync(string monitorId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(monitorId);
        return new(Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            if (Gtk4Monitors.Find(monitorId) is not { } target)
                throw new ArgumentException($"No connected monitor has the identifier '{monitorId}'.", nameof(monitorId));
            var window = GetWindow();
            _fullScreenMonitor = target.Handle;
            if (window.IsFullscreen())
            {
                window.FullscreenOnMonitor(target.Handle);
                return;
            }
            if (!Gtk4X11Placement.IsActive)
                throw new NotSupportedException("GTK 4 positions top-level windows only on X11; on Wayland the compositor places them.");
            var maximized = window.IsMaximized();
            if (maximized) window.Unmaximize();
            window.GetDefaultSize(out var width, out var height);
            if (width <= 0 || height <= 0) (width, height) = (window.GetWidth(), window.GetHeight());
            var area = CenterIn(width, height, ToScreenRect(target.Monitor.WorkArea));
            if (area.Width != width || area.Height != height) window.SetDefaultSize(area.Width, area.Height);
            if (!window.IsVisible())
            {
                _pendingPosition = (area.X, area.Y);
            }
            else
            {
                Gtk4X11Placement.Move(SurfaceOf(window), area.X, area.Y);
            }
            if (maximized) window.Maximize();
            QueueStateUpdate();
        }, cancellationToken));
    }
}
