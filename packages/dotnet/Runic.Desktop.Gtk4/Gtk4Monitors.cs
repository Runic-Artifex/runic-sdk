using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Desktop.Internal;
using Runic.Platform;

namespace Runic.Desktop.Gtk4;

// The monitors of GTK 4: Gdk.Display.GetMonitors() on the GTK thread, in logical pixels, the window's units. The
// identifier is the connector (gdk_monitor_get_connector, such as HDMI-1), the name the monitor's description. The
// scale is gdk_monitor_get_scale (fractional, GTK 4.14) or else the integer scale factor. The work area comes from
// gdk_x11_monitor_get_workarea on X11 and equals the geometry on Wayland, which reports none; the primary monitor is
// X11's (gdk_x11_display_get_primary_monitor), and Wayland has none. Changes come from the list's items-changed and
// each monitor's notify signals.
[SupportedOSPlatform("linux")]
internal static class Gtk4Monitors
{
    internal static IDesktopMonitors Source { get; } = new MonitorSource();

    private static readonly Lazy<Natives> Functions = new(Natives.Load);

    // A monitor of the default display with its GdkMonitor. GTK thread only.
    internal readonly record struct Entry(Gdk.Monitor Handle, DesktopMonitor Monitor);

    internal static List<Entry> Read()
    {
        List<Entry> entries = [];
        if (Gdk.Display.GetDefault() is not { } display || display.GetMonitors() is not { } list) return entries;
        var primary = Functions.Value.PrimaryMonitor(display.Handle.DangerousGetHandle());
        HashSet<string> ids = [];
        for (uint index = 0; index < list.GetNItems(); index++)
        {
            if (list.GetObject(index) is not Gdk.Monitor monitor) continue;
            var native = monitor.Handle.DangerousGetHandle();
            monitor.GetGeometry(out var geometry);
            var bounds = new DesktopRect(geometry.X, geometry.Y, geometry.Width, geometry.Height);
            var work = Gtk4X11Placement.MonitorWorkArea(native) is { Width: > 0, Height: > 0 } area
                ? new DesktopRect(area.X, area.Y, area.Width, area.Height)
                : bounds;
            var connector = monitor.GetConnector();
            var id = !string.IsNullOrEmpty(connector) ? connector : $"monitor-{index}";
            if (!ids.Add(id)) id = $"{id}-{index}";
            var name = Describe(monitor);
            entries.Add(new Entry(monitor, new DesktopMonitor(id, string.IsNullOrWhiteSpace(name) ? id : name)
            {
                Bounds = bounds,
                WorkArea = work,
                Scale = Functions.Value.Scale(native) is > 0 and var scale ? scale : Math.Max(1, monitor.GetScaleFactor()),
                IsPrimary = primary != 0 && primary == native,
            }));
        }
        var first = entries.FindIndex(static entry => entry.Monitor.IsPrimary);
        if (first > 0)
        {
            var entry = entries[first];
            entries.RemoveAt(first);
            entries.Insert(0, entry);
        }
        return entries;
    }

    // gdk_monitor_get_description is GTK 4.10; the manufacturer and model before it.
    private static string? Describe(Gdk.Monitor monitor)
    {
        try
        {
            if (monitor.GetDescription() is { Length: > 0 } description) return description;
        }
        catch (EntryPointNotFoundException)
        {
        }
        return string.Join(' ', new[] { monitor.GetManufacturer(), monitor.GetModel() }.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    // GTK thread only.
    internal static Entry? Find(string id)
    {
        foreach (var entry in Read())
        {
            if (entry.Monitor.Id == id) return entry;
        }
        return null;
    }

    // The identifier of the monitor that holds most of the surface, or null. GTK thread only.
    internal static string? IdOf(Gdk.Surface? surface)
    {
        if (surface is null || Gdk.Display.GetDefault() is not { } display || display.GetMonitorAtSurface(surface) is not { } monitor) return null;
        var native = monitor.Handle.DangerousGetHandle();
        foreach (var entry in Read())
        {
            if (entry.Handle.Handle.DangerousGetHandle() == native) return entry.Monitor.Id;
        }
        return null;
    }

    private sealed class MonitorSource : DesktopMonitorSource
    {
        protected override async ValueTask<PlatformResult<ImmutableArray<DesktopMonitor>>> ReadAsync(CancellationToken cancellationToken)
        {
            // GTK 4 runs only inside DesktopEventLoop.Run(options.WithGtk4(), …).
            if (!Gtk4Application.IsRunning)
                return new PlatformResult<ImmutableArray<DesktopMonitor>>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            ImmutableArray<DesktopMonitor> monitors = [];
            try
            {
                await Gtk4Dispatcher.Instance.InvokeAsync(() => monitors = [.. Read().Select(static entry => entry.Monitor)], cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return new PlatformResult<ImmutableArray<DesktopMonitor>>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            }
            return new PlatformResult<ImmutableArray<DesktopMonitor>>.Success(monitors);
        }

        protected override IDisposable Subscribe(Action changed)
        {
            Watchers[changed] = 0;
            if (Gtk4Application.IsRunning)
            {
                _ = ConnectAsync();
            }
            return new Unsubscriber(changed);
        }

        private static async Task ConnectAsync()
        {
            try
            {
                await Gtk4Dispatcher.Instance.InvokeAsync(ConnectSignals, CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // GTK stopped meanwhile.
            }
        }

        private sealed class Unsubscriber(Action changed) : IDisposable
        {
            public void Dispose() => Watchers.TryRemove(changed, out _);
        }
    }

    private static readonly ConcurrentDictionary<Action, byte> Watchers = new();
    // GTK thread only: the monitor list and the monitors whose signals are connected. They stay connected for the
    // process; a callback without watchers does nothing.
    private static nint s_watchedList;
    private static readonly HashSet<nint> WatchedMonitors = [];

    private static unsafe void ConnectSignals()
    {
        if (Gdk.Display.GetDefault()?.GetMonitors() is not { } list) return;
        var native = list.Handle.DangerousGetHandle();
        if (s_watchedList != native)
        {
            s_watchedList = native;
            Functions.Value.Connect(native, "items-changed", (nint)(delegate* unmanaged[Cdecl]<nint, uint, uint, uint, nint, void>)&OnItemsChanged);
        }
        foreach (var entry in Read()) Watch(entry.Handle.Handle.DangerousGetHandle());
    }

    private static unsafe void Watch(nint monitor)
    {
        if (monitor == 0 || !WatchedMonitors.Add(monitor)) return;
        foreach (var signal in (string[])["notify::geometry", "notify::scale-factor", "notify::scale"])
        {
            Functions.Value.Connect(monitor, signal, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnMonitorChanged);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnItemsChanged(nint list, uint position, uint removed, uint added, nint data)
    {
        try
        {
            if (added > 0)
            {
                foreach (var entry in Read()) Watch(entry.Handle.Handle.DangerousGetHandle());
            }
            Notify();
        }
        catch
        {
            // Never unwind through GTK.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMonitorChanged(nint monitor, nint property, nint data)
    {
        try { Notify(); }
        catch { } // Never unwind through GTK.
    }

    private static void Notify()
    {
        foreach (var watcher in Watchers.Keys)
        {
            try { watcher(); }
            catch { } // A watcher only releases its signal.
        }
    }

    // The exports GirCore cannot call on every supported GTK: gdk_monitor_get_scale (4.14) and the X11 primary monitor.
    private sealed unsafe class Natives
    {
        private delegate* unmanaged[Cdecl]<nint, double> _scale;
        private delegate* unmanaged[Cdecl]<nint, nint> _primary;
        private delegate* unmanaged[Cdecl]<nuint> _x11DisplayType;
        private delegate* unmanaged[Cdecl]<nint, nuint, int> _isA;
        private delegate* unmanaged[Cdecl]<nint, byte*, nint, nint, nint, int, nuint> _connect;

        internal static Natives Load()
        {
            var natives = new Natives();
            if (!NativeLibrary.TryLoad(Gtk4NativeLibraries.Gtk, out var gtk)) return natives;
            if (NativeLibrary.TryGetExport(gtk, "gdk_monitor_get_scale", out var scale)) natives._scale = (delegate* unmanaged[Cdecl]<nint, double>)scale;
            if (NativeLibrary.TryGetExport(gtk, "gdk_x11_display_get_primary_monitor", out var primary)
                && NativeLibrary.TryGetExport(gtk, "gdk_x11_display_get_type", out var type))
            {
                natives._primary = (delegate* unmanaged[Cdecl]<nint, nint>)primary;
                natives._x11DisplayType = (delegate* unmanaged[Cdecl]<nuint>)type;
            }
            if (NativeLibrary.TryLoad("libgobject-2.0.so.0", out var gobject))
            {
                if (NativeLibrary.TryGetExport(gobject, "g_type_check_instance_is_a", out var isA)) natives._isA = (delegate* unmanaged[Cdecl]<nint, nuint, int>)isA;
                if (NativeLibrary.TryGetExport(gobject, "g_signal_connect_data", out var connect))
                    natives._connect = (delegate* unmanaged[Cdecl]<nint, byte*, nint, nint, nint, int, nuint>)connect;
            }
            return natives;
        }

        internal double Scale(nint monitor) => _scale is null || monitor == 0 ? 0 : _scale(monitor);

        internal nint PrimaryMonitor(nint display) =>
            _primary is null || _isA is null || display == 0 || _isA(display, _x11DisplayType()) == 0
                ? 0
                : _primary(display);

        internal void Connect(nint instance, string signal, nint callback)
        {
            if (_connect is null || instance == 0) return;
            var name = Marshal.StringToCoTaskMemUTF8(signal);
            try { _ = _connect(instance, (byte*)name, callback, 0, 0, 0); }
            finally { Marshal.FreeCoTaskMem(name); }
        }
    }
}
