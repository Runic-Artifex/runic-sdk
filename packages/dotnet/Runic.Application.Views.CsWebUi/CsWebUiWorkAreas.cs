using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Desktop.Internal;

namespace Runic.Application.Views.CsWebUi;

// sdk#1149: the work areas of the monitors that exist now, in the units of WebUiWindow.TryGetPlacement, so a saved
// placement is kept on one of them (WindowPlacementClamp, shared as source with Runic Desktop). Windows: each monitor's
// rcWork in screen coordinates, read on any thread. Linux: GDK's monitor work areas on GTK's main loop, in GTK's logical
// pixels; Wayland reports each monitor's full area. Nothing elsewhere, or before GTK has a display.
internal static unsafe partial class CsWebUiWorkAreas
{
    internal static IReadOnlyList<ScreenRect> Read()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ReadWindows();
            if (OperatingSystem.IsLinux()) return Gtk.Read();
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
        }
        return [];
    }

    private static List<ScreenRect> ReadWindows()
    {
        var areas = new List<ScreenRect>();
        var handle = GCHandle.Alloc(areas);
        try { _ = EnumDisplayMonitors(0, null, &AddMonitor, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }
        return areas;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int AddMonitor(nint monitor, nint dc, Rect* bounds, nint data)
    {
        var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        if (GetMonitorInfoW(monitor, &info) != 0)
        {
            var areas = (List<ScreenRect>)GCHandle.FromIntPtr(data).Target!;
            areas.Add(new ScreenRect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
        }
        return 1;
    }

    // GDK is not thread-safe: the read runs on GTK's main loop (g_main_context_invoke), as CS-WebUI's placement does,
    // which runs it at once on the loop's own thread or when no thread runs the loop.
    private static class Gtk
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private static readonly Lazy<Api?> Loaded = new(Api.Load);

        internal static List<ScreenRect> Read()
        {
            if (Loaded.Value is not { } api) return [];
            var request = new Request();
            var handle = GCHandle.Alloc(request);
            api.MainContextInvoke(null, &RunOnMainLoop, (void*)GCHandle.ToIntPtr(handle));
            // A request the loop never runs keeps its handle; that happens only once the application stopped GTK's loop.
            return request.Done.Wait(Timeout) ? request.Areas : [];
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int RunOnMainLoop(void* data)
        {
            var handle = GCHandle.FromIntPtr((nint)data);
            var request = (Request)handle.Target!;
            handle.Free();
            try
            {
                var api = Loaded.Value;
                var display = api is null ? null : api.DisplayGetDefault();
                if (api is not null && display is not null)
                {
                    var count = api.GetMonitorCount(display);
                    for (var index = 0; index < count; index++)
                    {
                        var monitor = api.GetMonitor(display, index);
                        if (monitor is null) continue;
                        GdkRectangle area;
                        api.GetWorkArea(monitor, &area);
                        request.Areas.Add(new ScreenRect(area.X, area.Y, area.Width, area.Height));
                    }
                }
            }
            catch
            {
                // No managed exception may cross GTK's main loop.
                request.Areas.Clear();
            }
            finally
            {
                request.Done.Set();
            }
            return 0; // G_SOURCE_REMOVE
        }

        private sealed class Request
        {
            internal List<ScreenRect> Areas { get; } = [];

            internal ManualResetEventSlim Done { get; } = new();
        }

        private sealed class Api
        {
            internal delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, int>, void*, void> MainContextInvoke;
            internal delegate* unmanaged[Cdecl]<void*> DisplayGetDefault;
            internal delegate* unmanaged[Cdecl]<void*, int> GetMonitorCount;
            internal delegate* unmanaged[Cdecl]<void*, int, void*> GetMonitor;
            internal delegate* unmanaged[Cdecl]<void*, GdkRectangle*, void> GetWorkArea;

            // WebUI loads GTK 3 under this name; its handle also finds GLib and GDK, which it links.
            internal static Api? Load()
            {
                if (!NativeLibrary.TryLoad("libgtk-3.so.0", out var gtk)) return null;
                nint Export(string name) => NativeLibrary.TryGetExport(gtk, name, out var address) ? address : 0;
                var api = new Api
                {
                    MainContextInvoke = (delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, int>, void*, void>)Export("g_main_context_invoke"),
                    DisplayGetDefault = (delegate* unmanaged[Cdecl]<void*>)Export("gdk_display_get_default"),
                    GetMonitorCount = (delegate* unmanaged[Cdecl]<void*, int>)Export("gdk_display_get_n_monitors"),
                    GetMonitor = (delegate* unmanaged[Cdecl]<void*, int, void*>)Export("gdk_display_get_monitor"),
                    GetWorkArea = (delegate* unmanaged[Cdecl]<void*, GdkRectangle*, void>)Export("gdk_monitor_get_workarea"),
                };
                return api.MainContextInvoke is null || api.DisplayGetDefault is null || api.GetMonitorCount is null
                    || api.GetMonitor is null || api.GetWorkArea is null ? null : api;
            }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial int EnumDisplayMonitors(nint dc, Rect* clip, delegate* unmanaged[Stdcall]<nint, nint, Rect*, nint, int> callback, nint data);

    [LibraryImport("user32.dll")]
    private static partial int GetMonitorInfoW(nint monitor, MonitorInfo* info);
}
