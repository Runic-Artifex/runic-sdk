using System.Globalization;
using System.Runtime.InteropServices;
using Runic.Desktop;

// W315-017 on a CI runner: the embedded WebView2 window follows a live change of the monitor's scale, which reaches it
// as the same WM_DPICHANGED that moving to a monitor with another scale sends. The runner has one virtual monitor, so
// a move between two monitors is not covered here. The smoke raises the resolution to 1920x1080, where Windows offers
// 100 to 175 %, opens the window at 100 % and switches to 150 % and 175 % while it is open: the window must keep its
// size in device-independent pixels, grow in physical pixels, and the page must report the new devicePixelRatio and
// fill the client area. It changes the session's display settings, so it runs only with RUNIC_TEST_DISPLAY_SCALING=1
// (CI), and restores the scale and resolution before it exits.
internal static partial class WindowsDpiSmoke
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    // Small enough to fit the 1080-pixel work area at 175 %, where Windows would shrink a taller suggested rectangle.
    private const int Width = 640;
    private const int Height = 400;

    internal static async Task RunAsync(DesktopHostOptions hostOptions)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The DPI smoke changes Windows display scaling.");
        if (Environment.GetEnvironmentVariable("RUNIC_TEST_DISPLAY_SCALING") != "1")
            throw new InvalidOperationException("The DPI smoke changes the session's display settings; set RUNIC_TEST_DISPLAY_SCALING=1 on a disposable desktop.");
        var resolution = CurrentResolution();
        uint originalDpi = MonitorDpi();
        Console.WriteLine($"DPI smoke: display {resolution.Width}x{resolution.Height} at {originalDpi} DPI.");
        try
        {
            if (resolution.Width < 1920)
            {
                int changed = SetResolution(1920, 1080);
                var current = CurrentResolution();
                Console.WriteLine($"DPI smoke: display {current.Width}x{current.Height} after asking for 1920x1080 (ChangeDisplaySettingsEx returned {changed}).");
                if (changed != 0)
                    throw new InvalidOperationException("The display does not accept 1920x1080, where Windows offers scales up to 175 %.");
            }
            await SetScaleAsync(96);
            await using var host = await DesktopHost.StartAsync(hostOptions);
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = Width, Height = Height });
            using var trace = DpiChangeTrace.Start(window.NativeHandle);
            var baseline = await ExpectAsync(surface, window, 96, null);
            foreach (uint dpi in new uint[] { 144, 168, 96 })
            {
                await SetScaleAsync(dpi);
                await ExpectAsync(surface, window, dpi, baseline);
            }
            await window.CloseAsync();
            Console.WriteLine("PASS Windows per-monitor DPI: the WebView2 window followed 100 -> 150 -> 175 -> 100 % live.");
        }
        finally
        {
            await SetScaleAsync(originalDpi, required: false);
            if (CurrentResolution() != resolution) _ = SetResolution(resolution.Width, resolution.Height);
        }
    }

    // Window is the window's rectangle in physical pixels, for the log; Check does not compare it.
    private sealed record Sample(uint Dpi, int ClientWidth, int ClientHeight, int StateWidth, double PixelRatio, double InnerWidth, string Window);

    // Waits until the window reports the DPI, then checks its sizes against the 100 % baseline.
    private static async Task<Sample> ExpectAsync(DesktopSurface surface, DesktopWindow window, uint dpi, Sample? baseline)
    {
        double scale = dpi / 96.0;
        var started = DateTime.UtcNow;
        Sample? sample = null;
        string problem = "no sample";
        while (DateTime.UtcNow - started < Deadline)
        {
            sample = await ReadAsync(surface, window);
            problem = Check(sample, dpi, scale, baseline);
            if (problem.Length == 0)
            {
                Console.WriteLine($"DPI smoke: {dpi} DPI after {(DateTime.UtcNow - started).TotalMilliseconds:0} ms: {sample}.");
                return sample;
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"At {dpi} DPI: {problem}; last sample {sample}.");
    }

    private static string Check(Sample sample, uint dpi, double scale, Sample? baseline)
    {
        if (sample.Dpi != dpi) return $"the window reports {sample.Dpi} DPI";
        if (Math.Abs(sample.PixelRatio - scale) > 0.01) return $"the page reports devicePixelRatio {sample.PixelRatio}";
        // The WebView fills the client area: its CSS width times the ratio is the client width in physical pixels.
        if (Math.Abs(sample.InnerWidth * sample.PixelRatio - sample.ClientWidth) > 2 * scale) return "the page does not fill the client area";
        if (baseline is null) return "";
        // WM_DPICHANGED's suggested rectangle keeps the size in device-independent pixels.
        if (Math.Abs(sample.ClientWidth - baseline.ClientWidth * scale) > 3 * scale || Math.Abs(sample.ClientHeight - baseline.ClientHeight * scale) > 3 * scale)
            return $"the client area is {sample.ClientWidth}x{sample.ClientHeight}, not the baseline scaled by {scale}";
        if (Math.Abs((int)sample.StateWidth - (int)baseline.StateWidth) > 2) return $"the reported width changed to {sample.StateWidth} DIPs";
        return "";
    }

    private static async Task<Sample> ReadAsync(DesktopSurface surface, DesktopWindow window)
    {
        string page = await surface.ExecuteJavaScriptAsync("return devicePixelRatio + ' ' + innerWidth;", Deadline);
        var parts = page.Split(' ');
        nint handle = window.NativeHandle;
        // The host window is per-monitor aware; read it with the same awareness to get physical pixels.
        nint previous = SetThreadDpiAwarenessContext(-4);
        try
        {
            _ = GetClientRect(handle, out var client);
            _ = GetWindowRect(handle, out var frame);
            return new(GetDpiForWindow(handle), client.Right - client.Left, client.Bottom - client.Top, window.State?.Width ?? 0,
                double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture), Describe(frame));
        }
        finally { _ = SetThreadDpiAwarenessContext(previous); }
    }

    // SPI_SETLOGICALDPIOVERRIDE and SPI_GETLOGICALDPIOVERRIDE are undocumented. Windows Settings and common display
    // tools use them to pick a scale step relative to the recommended one, which is 100 % at 1920x1080 on the runner,
    // in the order of ScalePercents. Test-only code.
    private const uint SpiGetLogicalDpiOverride = 0x009E;
    private const uint SpiSetLogicalDpiOverride = 0x009F;
    private static readonly uint[] ScalePercents = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    // Goes to the scale in one step (#1346). Walking the steps in order passes other scales: 96 -> 120 -> 144 DPI sends
    // the window two WM_DPICHANGED two seconds apart. Twice on win-arm64 the window ended at 1.2 times its 96-DPI size
    // in both directions (746x424 client, 768x480 frame) instead of 1.5 times: 144/120 applied to the 96-DPI size, as
    // if the second suggested rectangle was made from a window that had not yet taken the first one's size. No clamp
    // to the display or work area gives the same factor in both directions. One step per scale sends one WM_DPICHANGED,
    // the change the smoke checks. The search in order stays as the fallback where the step table does not apply; it
    // logs each step, and DpiChangeTrace logs what the window got.
    private static async Task SetScaleAsync(uint dpi, bool required = true)
    {
        if (MonitorDpi() == dpi) return;
        if (DirectStep(dpi) is { } direct && await TryStepAsync(direct, dpi)) return;
        foreach (int step in new[] { 0, 1, 2, 3, -1, -2, 4, 5 })
        {
            if (await TryStepAsync(step, dpi)) return;
        }
        if (required) throw new InvalidOperationException($"Windows does not offer {dpi} DPI at this resolution; it is at {MonitorDpi()} DPI.");
    }

    private static async Task<bool> TryStepAsync(int step, uint dpi)
    {
        _ = SystemParametersInfo(SpiSetLogicalDpiOverride, step, 0, 1);
        for (int i = 0; i < 20 && MonitorDpi() != dpi; i++) await Task.Delay(100);
        uint reached = MonitorDpi();
        Console.WriteLine($"DPI smoke: scale step {step} for {dpi} DPI gave {reached} DPI.");
        return reached == dpi;
    }

    // The step of the scale relative to the recommended one, from the current step and scale; null when unknown.
    private static unsafe int? DirectStep(uint dpi)
    {
        int current = 0;
        if (SystemParametersInfo(SpiGetLogicalDpiOverride, 0, (nint)(&current), 0) == 0) return null;
        int from = Array.IndexOf(ScalePercents, MonitorDpi() * 100 / 96);
        int to = Array.IndexOf(ScalePercents, dpi * 100 / 96);
        return from < 0 || to < 0 ? null : current + to - from;
    }

    private static uint MonitorDpi()
    {
        nint previous = SetThreadDpiAwarenessContext(-4);
        try
        {
            _ = GetDpiForMonitor(MonitorFromPoint(0, 1), 0, out uint x, out _);
            return x;
        }
        finally { _ = SetThreadDpiAwarenessContext(previous); }
    }

    internal static (int Width, int Height) CurrentResolution()
    {
        var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
        _ = EnumDisplaySettings(0, -1, ref mode);
        return (mode.PelsWidth, mode.PelsHeight);
    }

    internal static int SetResolution(int width, int height)
    {
        var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
        _ = EnumDisplaySettings(0, -1, ref mode);
        mode.PelsWidth = width;
        mode.PelsHeight = height;
        mode.Fields = 0x80000 | 0x100000; // DM_PELSWIDTH | DM_PELSHEIGHT
        return ChangeDisplaySettingsEx(0, ref mode, 0, 0, 0);
    }

    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script>
        <title>Runic DPI</title><style>html,body{margin:0;height:100%}</style></head><body>dpi</body></html>
        """;

    private static string Describe(Rect rect) => $"({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) {rect.Right - rect.Left}x{rect.Bottom - rect.Top}";

    // #1346: logs each WM_DPICHANGED the window gets, from hooks on the window's thread: the new DPI, the suggested
    // rectangle, the window's rectangle before and after its window procedure handled it, and the monitor's area and
    // work area. It also logs each later change of the window's size and each display change, so a clamp to the
    // display or work area, a suggestion made from an old rectangle and a later resize can be told apart.
    private sealed unsafe partial class DpiChangeTrace : IDisposable
    {
        private const uint WmDpiChanged = 0x02E0, WmWindowPosChanged = 0x0047, WmDisplayChange = 0x007E;
        private const int WhCallWndProc = 4, WhCallWndProcReturn = 12;
        // The smoke opens one window; the hook procedures are static.
        private static nint s_window;
        private static (int Width, int Height) s_size;
        private readonly nint _call, _return;

        private DpiChangeTrace(nint call, nint @return) => (_call, _return) = (call, @return);

        public static DpiChangeTrace Start(nint window)
        {
            s_window = window;
            Rect frame;
            // Physical pixels, as the window's own per-monitor aware thread reads them in the hooks.
            nint previous = SetThreadDpiAwarenessContext(-4);
            try { _ = GetWindowRect(window, out frame); }
            finally { _ = SetThreadDpiAwarenessContext(previous); }
            s_size = (frame.Right - frame.Left, frame.Bottom - frame.Top);
            uint thread = GetWindowThreadProcessId(window, out _);
            // A thread of this process with a hook procedure in it: no module handle (SetWindowsHookEx).
            nint call = SetWindowsHookEx(WhCallWndProc, (nint)(delegate* unmanaged<int, nint, nint, nint>)&OnCall, 0, thread);
            nint @return = SetWindowsHookEx(WhCallWndProcReturn, (nint)(delegate* unmanaged<int, nint, nint, nint>)&OnReturn, 0, thread);
            Log($"tracing the window's DPI changes ({(call != 0 && @return != 0 ? "hooks set" : $"a hook failed, error {Marshal.GetLastPInvokeError()}")}); window {Describe(frame)}, {MonitorAreas(window)}");
            return new(call, @return);
        }

        public void Dispose()
        {
            if (_call != 0) _ = UnhookWindowsHookEx(_call);
            if (_return != 0) _ = UnhookWindowsHookEx(_return);
            s_window = 0;
        }

        // CWPSTRUCT and CWPRETSTRUCT.
        [StructLayout(LayoutKind.Sequential)]
        private struct CallInfo { public nint LParam, WParam; public uint Message; public nint Window; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ReturnInfo { public nint Result, LParam, WParam; public uint Message; public nint Window; }

        [UnmanagedCallersOnly]
        private static nint OnCall(int code, nint wParam, nint lParam)
        {
            try
            {
                var info = (CallInfo*)lParam;
                if (code >= 0 && info->Window == s_window && s_window != 0 && info->Message == WmDpiChanged)
                {
                    _ = GetWindowRect(info->Window, out var frame);
                    Log($"WM_DPICHANGED to {(uint)info->WParam & 0xFFFF} DPI: suggested {Describe(*(Rect*)info->LParam)}; window before {Describe(frame)}; {MonitorAreas(info->Window)}");
                }
            }
            catch (Exception exception)
            {
                Log($"hook failed: {exception.Message}");
            }
            return CallNextHookEx(0, code, wParam, lParam);
        }

        [UnmanagedCallersOnly]
        private static nint OnReturn(int code, nint wParam, nint lParam)
        {
            try
            {
                var info = (ReturnInfo*)lParam;
                if (code >= 0 && info->Window == s_window && s_window != 0)
                {
                    switch (info->Message)
                    {
                        case WmDpiChanged:
                            _ = GetWindowRect(info->Window, out var frame);
                            _ = GetClientRect(info->Window, out var client);
                            s_size = (frame.Right - frame.Left, frame.Bottom - frame.Top);
                            Log($"WM_DPICHANGED handled: window after {Describe(frame)}, client {client.Right}x{client.Bottom}, window DPI {GetDpiForWindow(info->Window)}");
                            break;
                        case WmWindowPosChanged:
                            _ = GetWindowRect(info->Window, out var moved);
                            var size = (moved.Right - moved.Left, moved.Bottom - moved.Top);
                            if (size == s_size) break;
                            s_size = size;
                            Log($"window resized to {Describe(moved)} at {GetDpiForWindow(info->Window)} DPI");
                            break;
                        case WmDisplayChange:
                            Log($"WM_DISPLAYCHANGE: {(int)info->LParam & 0xFFFF}x{((int)info->LParam >> 16) & 0xFFFF}; {MonitorAreas(info->Window)}");
                            break;
                    }
                }
            }
            catch (Exception exception)
            {
                Log($"hook failed: {exception.Message}");
            }
            return CallNextHookEx(0, code, wParam, lParam);
        }

        private static string MonitorAreas(nint window)
        {
            var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
            return GetMonitorInfo(MonitorFromWindow(window, 2), ref info) == 0
                ? "monitor unknown"
                : $"monitor {Describe(info.Monitor)}, work area {Describe(info.Work)}";
        }

        private static void Log(string message) => Console.WriteLine($"DPI smoke: {message}.");

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }

        [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static partial nint SetWindowsHookEx(int hook, nint procedure, nint module, uint thread);
        [LibraryImport("user32.dll")] private static partial int UnhookWindowsHookEx(nint hook);
        [LibraryImport("user32.dll")] private static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
        [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint window, out uint process);
        [LibraryImport("user32.dll")] private static partial nint MonitorFromWindow(nint window, uint flags);
        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static partial int GetMonitorInfo(nint monitor, ref MonitorInfo info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    // DEVMODEW with the display fields; the two name buffers are 32 UTF-16 code units each.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DevMode
    {
        public fixed char DeviceName[32];
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        public fixed char FormName[32];
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [LibraryImport("user32.dll")] private static partial nint SetThreadDpiAwarenessContext(nint context);
    [LibraryImport("user32.dll")] private static partial uint GetDpiForWindow(nint window);
    [LibraryImport("user32.dll")] private static partial int GetClientRect(nint window, out Rect rect);
    [LibraryImport("user32.dll")] private static partial int GetWindowRect(nint window, out Rect rect);
    [LibraryImport("user32.dll")] private static partial nint MonitorFromPoint(long point, uint flags);
    [LibraryImport("shcore.dll")] private static partial int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static partial int SystemParametersInfo(uint action, int parameter, nint value, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW")] private static partial int EnumDisplaySettings(nint device, int mode, ref DevMode devMode);
    [LibraryImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW")] private static partial int ChangeDisplaySettingsEx(nint device, ref DevMode devMode, nint window, uint flags, nint parameter);
}
