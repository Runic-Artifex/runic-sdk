using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// The monitors of Windows: EnumDisplayMonitors and GetMonitorInfoW for bounds, work area and the primary flag,
// GetDpiForMonitor for the scale, and the display configuration (QueryDisplayConfig) for an identifier that survives
// restarts and a name to show: the target's monitorDevicePath and monitorFriendlyDeviceName. Coordinates are physical
// virtual-screen pixels, read on a per-monitor DPI aware thread as the WebView2 window's thread is. It needs no window,
// so WPF windows use it too. Changes come from a hidden top-level window (broadcasts skip message-only windows) that
// listens for WM_DISPLAYCHANGE, the work area's WM_SETTINGCHANGE and WM_DPICHANGED.
internal sealed partial class WindowsMonitors : DesktopMonitorSource
{
    internal static WindowsMonitors Shared { get; } = new();

    private WindowsMonitors() { }

    // One monitor as Windows reports it, with its handle for the window host.
    internal readonly record struct Native(nint Handle, DesktopMonitor Monitor, ScreenRect Bounds, ScreenRect WorkArea, uint Dpi);

    protected override ValueTask<PlatformResult<ImmutableArray<DesktopMonitor>>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return ValueTask.FromResult<PlatformResult<ImmutableArray<DesktopMonitor>>>(
                new PlatformResult<ImmutableArray<DesktopMonitor>>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        try
        {
            var monitors = WithPerMonitorAwareness(Read);
            return ValueTask.FromResult<PlatformResult<ImmutableArray<DesktopMonitor>>>(
                new PlatformResult<ImmutableArray<DesktopMonitor>>.Success([.. monitors.Select(static monitor => monitor.Monitor)]));
        }
        catch (Exception error) when (error is Win32Exception or EntryPointNotFoundException or DllNotFoundException)
        {
            return ValueTask.FromResult<PlatformResult<ImmutableArray<DesktopMonitor>>>(new PlatformResult<ImmutableArray<DesktopMonitor>>.Failed(
                PlatformFailureCode.Other, error is Win32Exception win32
                    ? PlatformDiagnostic.FromWin32Error(win32.NativeErrorCode)
                    : new PlatformDiagnostic("Win32", 0, "The display functions are missing.")));
        }
    }

    // Runs on the caller's thread with per-monitor DPI awareness, so coordinates are physical pixels.
    internal static T WithPerMonitorAwareness<T>(Func<T> read)
    {
        var previous = NativeMethods.SetThreadDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2
        try { return read(); }
        finally { if (previous != 0) NativeMethods.SetThreadDpiAwarenessContext(previous); }
    }

    // The monitors, the primary one first. The calling thread must be per-monitor DPI aware.
    internal static unsafe List<Native> Read()
    {
        List<nint> handles = [];
        var list = GCHandle.Alloc(handles);
        try
        {
            if (!NativeMethods.EnumDisplayMonitors(0, 0, (nint)(delegate* unmanaged[Stdcall]<nint, nint, Rect*, nint, int>)&AddMonitor, GCHandle.ToIntPtr(list)))
                throw new Win32Exception("EnumDisplayMonitors failed.");
        }
        finally
        {
            list.Free();
        }
        var names = DisplayNames();
        List<Native> monitors = [];
        foreach (var handle in handles)
        {
            if (Describe(handle, names) is { } monitor) monitors.Add(monitor);
        }
        // A stable order: the primary monitor, then left to right and top to bottom.
        monitors.Sort(static (left, right) => left.Monitor.IsPrimary != right.Monitor.IsPrimary
            ? left.Monitor.IsPrimary ? -1 : 1
            : left.Bounds.X != right.Bounds.X ? left.Bounds.X.CompareTo(right.Bounds.X) : left.Bounds.Y.CompareTo(right.Bounds.Y));
        return monitors;
    }

    // The monitors, or none when Windows cannot list them.
    internal static List<Native> TryRead()
    {
        try { return Read(); }
        catch (Win32Exception) { return []; }
    }

    // The monitor with this identifier, or null when none is connected. The calling thread must be per-monitor DPI aware.
    internal static Native? Find(string id)
    {
        foreach (var monitor in TryRead())
        {
            if (monitor.Monitor.Id == id) return monitor;
        }
        return null;
    }

    // The identifier of a monitor handle, such as MonitorFromWindow's; null when it cannot be read.
    internal static string? IdOf(nint handle)
    {
        if (handle == 0 || !TryGetInfo(handle, out var info)) return null;
        var device = DeviceName(ref info);
        var cache = Volatile.Read(ref s_names);
        if (cache is null || !cache.ContainsKey(device))
        {
            cache = DisplayNames();
            Volatile.Write(ref s_names, cache);
        }
        return cache.TryGetValue(device, out var name) ? name.Id : device;
    }

    // Forgets the cached display configuration after a display change.
    internal static void Invalidate() => Volatile.Write(ref s_names, null);

    private static Dictionary<string, (string Id, string Name)>? s_names;

    private static Native? Describe(nint handle, Dictionary<string, (string Id, string Name)> names)
    {
        if (!TryGetInfo(handle, out var info)) return null;
        var device = DeviceName(ref info);
        var (id, name) = names.TryGetValue(device, out var known) ? known : (device, device);
        var bounds = ToScreenRect(info.Monitor);
        var work = ToScreenRect(info.Work);
        var dpi = Dpi(handle);
        var monitor = new DesktopMonitor(id, name)
        {
            Bounds = new DesktopRect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            WorkArea = new DesktopRect(work.X, work.Y, work.Width, work.Height),
            Scale = dpi / 96d,
            IsPrimary = (info.Flags & MonitorInfoPrimary) != 0,
        };
        return new Native(handle, monitor, bounds, work, dpi);
    }

    internal static uint Dpi(nint monitor)
    {
        if (monitor != 0 && NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out _) >= 0 && dpiX != 0) return dpiX;
        return 96;
    }

    private static unsafe bool TryGetInfo(nint handle, out MonitorInfoEx info)
    {
        info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
        return NativeMethods.GetMonitorInfo(handle, ref info);
    }

    private static unsafe string DeviceName(ref MonitorInfoEx info)
    {
        fixed (char* device = info.Device) return new string(device);
    }

    private static ScreenRect ToScreenRect(Rect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    // GDI device names (\\.\DISPLAY1) to their target's device path and friendly name, from the active display
    // configuration. The first target of a cloned source names it. Empty when the configuration cannot be read.
    private static unsafe Dictionary<string, (string Id, string Name)> DisplayNames()
    {
        Dictionary<string, (string Id, string Name)> names = new(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (NativeMethods.GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0) return names;
            var paths = new PathInfo[pathCount];
            var modes = new byte[checked((int)modeCount * ModeInfoSize)];
            int result;
            fixed (PathInfo* pathPointer = paths)
            fixed (byte* modePointer = modes)
            {
                result = NativeMethods.QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, pathPointer, ref modeCount, modePointer, 0);
            }
            // The configuration changed between the two calls.
            if (result == ErrorInsufficientBuffer) continue;
            if (result != 0) return names;
            for (var index = 0; index < pathCount; index++)
            {
                var path = paths[index];
                var source = new SourceDeviceName
                {
                    Header = new DeviceInfoHeader
                    {
                        Type = DeviceInfoGetSourceName,
                        Size = (uint)sizeof(SourceDeviceName),
                        AdapterId = path.SourceAdapterId,
                        Id = path.SourceId,
                    },
                };
                if (NativeMethods.DisplayConfigGetDeviceInfo(&source.Header) != 0) continue;
                var device = new string(source.GdiDeviceName);
                if (names.ContainsKey(device)) continue;
                var target = new TargetDeviceName
                {
                    Header = new DeviceInfoHeader
                    {
                        Type = DeviceInfoGetTargetName,
                        Size = (uint)sizeof(TargetDeviceName),
                        AdapterId = path.TargetAdapterId,
                        Id = path.TargetId,
                    },
                };
                if (NativeMethods.DisplayConfigGetDeviceInfo(&target.Header) != 0) continue;
                var devicePath = new string(target.MonitorDevicePath);
                var friendly = new string(target.MonitorFriendlyDeviceName);
                names[device] = (devicePath.Length > 0 ? devicePath : device, friendly.Length > 0 ? friendly : device);
            }
            return names;
        }
        return names;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int AddMonitor(nint monitor, nint deviceContext, Rect* bounds, nint context)
    {
        if (GCHandle.FromIntPtr(context).Target is List<nint> handles) handles.Add(monitor);
        return 1;
    }

    // The change signal: one hidden window on its own thread while anything watches.
    private static readonly object s_watchGate = new();
    private static readonly List<Action> s_watchers = [];
    private static nint s_watchWindow;
    private static Thread? s_watchThread;

    protected override IDisposable Subscribe(Action changed)
    {
        if (!OperatingSystem.IsWindows()) return new Subscription(null);
        lock (s_watchGate)
        {
            s_watchers.Add(changed);
            if (s_watchThread is null)
            {
                var ready = new ManualResetEventSlim();
                s_watchThread = new Thread(() => RunWatch(ready)) { IsBackground = true, Name = "Runic Desktop monitors" };
                s_watchThread.Start();
                ready.Wait();
            }
        }
        return new Subscription(changed);
    }

    private sealed class Subscription(Action? changed) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (changed is null || Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (s_watchGate)
            {
                s_watchers.Remove(changed);
                if (s_watchers.Count == 0 && s_watchWindow != 0)
                {
                    NativeMethods.PostMessage(s_watchWindow, WmClose, 0, 0);
                }
            }
        }
    }

    private static readonly string WatchClassName = $"Runic.Desktop.Monitors.{Environment.ProcessId}";
    private static ushort s_watchClass;

    private static unsafe void RunWatch(ManualResetEventSlim ready)
    {
        nint window = 0;
        try
        {
            NativeMethods.SetThreadDpiAwarenessContext(-4);
            if (s_watchClass == 0)
            {
                var name = Marshal.StringToHGlobalUni(WatchClassName);
                try
                {
                    var windowClass = new WindowClass
                    {
                        Size = (uint)sizeof(WindowClass),
                        WindowProcedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WatchProcedure,
                        Instance = NativeMethods.GetModuleHandle(null),
                        ClassName = name,
                    };
                    s_watchClass = NativeMethods.RegisterClassEx(in windowClass);
                }
                finally
                {
                    Marshal.FreeHGlobal(name);
                }
            }
            // A top-level popup that is never shown: it receives the broadcasts a message-only window does not.
            window = NativeMethods.CreateWindowEx(WsExToolWindow, WatchClassName, "", WsPopup, 0, 0, 0, 0, 0, 0,
                NativeMethods.GetModuleHandle(null), 0);
            lock (s_watchGate) s_watchWindow = window;
        }
        finally
        {
            ready.Set();
        }
        if (window == 0)
        {
            lock (s_watchGate) s_watchThread = null;
            return;
        }
        while (NativeMethods.GetMessage(out var message, 0, 0, 0) > 0)
        {
            NativeMethods.DispatchMessage(in message);
        }
        lock (s_watchGate)
        {
            s_watchWindow = 0;
            s_watchThread = null;
            // A watcher that subscribed while the window closed starts a new one.
            if (s_watchers.Count > 0)
            {
                var again = new ManualResetEventSlim();
                s_watchThread = new Thread(() => RunWatch(again)) { IsBackground = true, Name = "Runic Desktop monitors" };
                s_watchThread.Start();
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WatchProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WmDisplayChange:
            case WmDpiChanged:
            case WmSettingChange when wParam == SpiSetWorkArea:
                Invalidate();
                Action[] watchers;
                lock (s_watchGate) watchers = [.. s_watchers];
                foreach (var watcher in watchers)
                {
                    try { watcher(); }
                    catch { } // Never unwind through the window procedure.
                }
                return message == WmSettingChange ? 0 : NativeMethods.DefWindowProc(window, message, wParam, lParam);
            case WmClose:
                NativeMethods.DestroyWindow(window);
                return 0;
            case WmDestroy:
                NativeMethods.PostQuitMessage(0);
                return 0;
            default:
                return NativeMethods.DefWindowProc(window, message, wParam, lParam);
        }
    }

    private const uint WmDestroy = 0x0002, WmClose = 0x0010, WmSettingChange = 0x001A, WmDisplayChange = 0x007E, WmDpiChanged = 0x02E0;
    private const nuint SpiSetWorkArea = 0x002F;
    private const uint WsPopup = 0x80000000, WsExToolWindow = 0x00000080;
    private const uint MonitorInfoPrimary = 1;
    private const uint QdcOnlyActivePaths = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int ModeInfoSize = 64;
    private const int DeviceInfoGetSourceName = 1, DeviceInfoGetTargetName = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct MonitorInfoEx
    {
        internal uint Size;
        internal Rect Monitor;
        internal Rect Work;
        internal uint Flags;
        internal fixed char Device[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        internal uint LowPart;
        internal int HighPart;
    }

    // DISPLAYCONFIG_PATH_INFO: the source (20 bytes), the target (48 bytes) and flags.
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct PathInfo
    {
        [FieldOffset(0)] internal Luid SourceAdapterId;
        [FieldOffset(8)] internal uint SourceId;
        [FieldOffset(20)] internal Luid TargetAdapterId;
        [FieldOffset(28)] internal uint TargetId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        internal int Type;
        internal uint Size;
        internal Luid AdapterId;
        internal uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct SourceDeviceName
    {
        internal DeviceInfoHeader Header;
        internal fixed char GdiDeviceName[32];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct TargetDeviceName
    {
        internal DeviceInfoHeader Header;
        internal uint Flags;
        internal int OutputTechnology;
        internal ushort EdidManufactureId;
        internal ushort EdidProductCodeId;
        internal uint ConnectorInstance;
        internal fixed char MonitorFriendlyDeviceName[64];
        internal fixed char MonitorDevicePath[128];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        internal uint Size;
        internal uint Style;
        internal nint WindowProcedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal nint MenuName;
        internal nint ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal nint Window;
        internal uint Value;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X;
        internal int Y;
        internal uint Private;
    }

    private static unsafe partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        internal static partial nint SetThreadDpiAwarenessContext(nint context);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool EnumDisplayMonitors(nint deviceContext, nint clip, nint callback, nint data);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

        [LibraryImport("shcore.dll")]
        internal static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

        [LibraryImport("user32.dll")]
        internal static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

        [LibraryImport("user32.dll")]
        internal static partial int QueryDisplayConfig(uint flags, ref uint pathCount, PathInfo* paths, ref uint modeCount, byte* modes, nint topology);

        [LibraryImport("user32.dll")]
        internal static partial int DisplayConfigGetDeviceInfo(DeviceInfoHeader* packet);

        [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
        internal static partial ushort RegisterClassEx(in WindowClass windowClass);

        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        internal static partial nint CreateWindowEx(uint extendedStyle, string className, string windowName, uint style, int x, int y,
            int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
        internal static partial nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyWindow(nint window);

        [LibraryImport("user32.dll")]
        internal static partial void PostQuitMessage(int exitCode);

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

        [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
        internal static partial int GetMessage(out Message message, nint window, uint minimum, uint maximum);

        [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
        internal static partial nint DispatchMessage(in Message message);

        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint GetModuleHandle(string? moduleName);
    }
}
