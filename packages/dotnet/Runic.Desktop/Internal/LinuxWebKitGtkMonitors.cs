using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// Monitors, full screen and placement on a monitor for the GTK 3 window. GDK's monitors on the GTK thread, in GDK's
// application (logical) pixels, the window's units; the identifier is the connector (gdk_screen_get_monitor_plug_name,
// such as HDMI-1). Changes come from the display's monitor-added and monitor-removed, the screen's monitors-changed and
// each monitor's notify::workarea and notify::scale-factor. Reading or watching keeps the GTK loop running meanwhile.
internal sealed partial class LinuxWebKitGtkHost
{
    internal static IDesktopMonitors Monitors { get; } = new MonitorSource();

    // The monitor index for gtk_window_fullscreen_on_monitor, from DesktopWindowOptions.Monitor; -1 for none.
    private int _fullScreenMonitor = -1;

    private readonly record struct ChosenMonitor(int Index, DesktopMonitor Monitor);

    // GTK thread only.
    private static ChosenMonitor? FindMonitor(string id)
    {
        foreach (var (handle, monitor) in Api.ReadMonitors())
        {
            if (monitor.Id == id) return new ChosenMonitor(Api.MonitorIndex(handle), monitor);
        }
        return null;
    }

    // GTK thread only.
    private static string? MonitorIdOf(nint handle)
    {
        if (handle == 0) return null;
        foreach (var (candidate, monitor) in Api.ReadMonitors())
        {
            if (candidate == handle) return monitor.Id;
        }
        return null;
    }

    private static ScreenRect ToScreenRect(DesktopRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private static ScreenRect CenterIn(int width, int height, ScreenRect area)
    {
        width = Math.Max(1, Math.Min(width, area.Width));
        height = Math.Max(1, Math.Min(height, area.Height));
        return new ScreenRect(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
    }

    // GTK thread only.
    private void EnterFullScreen()
    {
        if (_fullScreenMonitor >= 0 && Api.CanPlaceOnMonitor) Api.GtkWindowFullscreenOnMonitor(_window, _fullScreenMonitor);
        else Api.GtkWindowFullscreen(_window);
    }

    public ValueTask SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default)
        => InvokeWindowAsync(() =>
        {
            if (_options?.Kiosk == true || !Api.CanUseFullScreen) return;
            if (fullScreen) EnterFullScreen();
            else Api.GtkWindowUnfullscreen(_window);
        }, cancellationToken);

    public ValueTask MoveToMonitorAsync(string monitorId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(monitorId);
        return InvokeWindowAsync(() =>
        {
            if (FindMonitor(monitorId) is not { } target)
                throw new ArgumentException($"No connected monitor has the identifier '{monitorId}'.", nameof(monitorId));
            _fullScreenMonitor = target.Index;
            var flags = Api.GetWindowStateFlags(_window);
            if ((flags & GdkWindowStateFullscreen) != 0)
            {
                Api.GtkWindowFullscreenOnMonitor(_window, target.Index);
                return;
            }
            var maximized = (flags & GdkWindowStateMaximized) != 0 || Api.GtkWindowIsMaximized(_window);
            if (maximized) Api.GtkWindowUnmaximize(_window);
            var (width, height) = Api.GtkWindowGetSize(_window);
            var area = CenterIn(width, height, ToScreenRect(target.Monitor.WorkArea));
            if (area.Width != width || area.Height != height) Api.GtkWindowResize(_window, area.Width, area.Height);
            Api.GtkWindowMove(_window, area.X, area.Y);
            if (maximized) Api.GtkWindowMaximize(_window);
        }, cancellationToken);
    }

    private sealed class MonitorSource : DesktopMonitorSource
    {
        protected override async ValueTask<PlatformResult<ImmutableArray<DesktopMonitor>>> ReadAsync(CancellationToken cancellationToken)
        {
            if (!IsSupported || !Api.CanListMonitors || !await AcquireAsync(cancellationToken).ConfigureAwait(false))
                return new PlatformResult<ImmutableArray<DesktopMonitor>>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            try
            {
                ImmutableArray<DesktopMonitor> monitors = [];
                await Dispatcher.InvokeAsync(() => monitors = [.. Api.ReadMonitors().Select(static monitor => monitor.Monitor)], cancellationToken)
                    .ConfigureAwait(false);
                return new PlatformResult<ImmutableArray<DesktopMonitor>>.Success(monitors);
            }
            finally
            {
                await Dispatcher.ReleaseAsync().ConfigureAwait(false);
            }
        }

        protected override IDisposable Subscribe(Action changed)
        {
            ScreenWatchers[changed] = 0;
            lock (WatchGate)
            {
                if (s_watchCount++ == 0) s_watch = s_watch.ContinueWith(static _ => StartWatchAsync(), TaskScheduler.Default).Unwrap();
            }
            return new Unsubscriber(changed);
        }

        private sealed class Unsubscriber(Action changed) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                ScreenWatchers.TryRemove(changed, out _);
                lock (WatchGate)
                {
                    if (--s_watchCount == 0) s_watch = s_watch.ContinueWith(static _ => StopWatchAsync(), TaskScheduler.Default).Unwrap();
                }
            }
        }
    }

    // Starts the GTK loop for the monitors, as opening a window does; false where GTK cannot start, such as without a
    // display or after the process chose GTK 4.
    private static async Task<bool> AcquireAsync(CancellationToken cancellationToken)
    {
        try
        {
            LinuxDesktopRuntime.ClaimBackend(LinuxEmbeddedBackend.Gtk3WebKit41);
            await Dispatcher.AcquireAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static readonly ConcurrentDictionary<Action, byte> ScreenWatchers = new();
    private static readonly object WatchGate = new();
    private static int s_watchCount;
    private static Task s_watch = Task.CompletedTask;
    private static bool s_watchHoldsLoop;
    // GTK thread only: the display and the monitors whose signals are connected.
    private static nint s_watchedDisplay;
    private static readonly HashSet<nint> WatchedMonitors = [];

    private static async Task StartWatchAsync()
    {
        if (!IsSupported || !Api.CanListMonitors || !await AcquireAsync(CancellationToken.None).ConfigureAwait(false)) return;
        s_watchHoldsLoop = true;
        try
        {
            await Dispatcher.InvokeAsync(ConnectMonitorSignals, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The loop stopped meanwhile; the watch reads again on its next signal.
        }
    }

    private static async Task StopWatchAsync()
    {
        if (!s_watchHoldsLoop) return;
        s_watchHoldsLoop = false;
        await Dispatcher.ReleaseAsync().ConfigureAwait(false);
    }

    // GTK thread only. The display and screen signals once, and each monitor's notify signals once.
    private static unsafe void ConnectMonitorSignals()
    {
        var display = Api.DefaultDisplay();
        if (display == 0) return;
        if (s_watchedDisplay != display)
        {
            s_watchedDisplay = display;
            Api.ConnectStatic(display, "monitor-added", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnMonitorAdded);
            Api.ConnectStatic(display, "monitor-removed", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnMonitorsChanged);
            if (Api.DefaultScreen() is var screen and not 0)
            {
                Api.ConnectStatic(screen, "monitors-changed", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnScreenChanged);
                Api.ConnectStatic(screen, "size-changed", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnScreenChanged);
            }
        }
        foreach (var (monitor, _) in Api.ReadMonitors()) WatchMonitor(monitor);
    }

    private static unsafe void WatchMonitor(nint monitor)
    {
        if (monitor == 0 || !WatchedMonitors.Add(monitor)) return;
        foreach (var signal in (string[])["notify::workarea", "notify::geometry", "notify::scale-factor"])
        {
            Api.ConnectStatic(monitor, signal, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnMonitorsChanged);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMonitorAdded(nint display, nint monitor, nint data)
    {
        try
        {
            WatchMonitor(monitor);
            NotifyWatchers();
        }
        catch
        {
            // Never unwind through GTK.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMonitorsChanged(nint instance, nint argument, nint data)
    {
        try { NotifyWatchers(); }
        catch { } // Never unwind through GTK.
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnScreenChanged(nint screen, nint data)
    {
        try { NotifyWatchers(); }
        catch { } // Never unwind through GTK.
    }

    private static void NotifyWatchers()
    {
        foreach (var watcher in ScreenWatchers.Keys)
        {
            try { watcher(); }
            catch { } // A watcher only releases its signal.
        }
    }
}
