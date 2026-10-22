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
            if (resolution.Width < 1920 && SetResolution(1920, 1080) != 0)
                throw new InvalidOperationException("The display does not accept 1920x1080, where Windows offers scales up to 175 %.");
            await SetScaleAsync(96);
            await using var host = await DesktopHost.StartAsync(hostOptions);
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = Width, Height = Height });
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

    private sealed record Sample(uint Dpi, int ClientWidth, int ClientHeight, int StateWidth, double PixelRatio, double InnerWidth);

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
            return new(GetDpiForWindow(handle), client.Right - client.Left, client.Bottom - client.Top, window.State?.Width ?? 0,
                double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
        }
        finally { _ = SetThreadDpiAwarenessContext(previous); }
    }

    // SPI_SETLOGICALDPIOVERRIDE is undocumented. Windows Settings and common display tools use it to pick a scale
    // step relative to the recommended one, which is 100 % at 1920x1080 on the runner. Test-only code.
    private const uint SpiSetLogicalDpiOverride = 0x009F;

    private static async Task SetScaleAsync(uint dpi, bool required = true)
    {
        if (MonitorDpi() == dpi) return;
        foreach (int step in new[] { 0, 1, 2, 3, -1, -2, 4, 5 })
        {
            _ = SystemParametersInfo(SpiSetLogicalDpiOverride, step, 0, 1);
            for (int i = 0; i < 20 && MonitorDpi() != dpi; i++) await Task.Delay(100);
            if (MonitorDpi() == dpi) return;
        }
        if (required) throw new InvalidOperationException($"Windows does not offer {dpi} DPI at this resolution; it is at {MonitorDpi()} DPI.");
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
    [LibraryImport("user32.dll")] private static partial nint MonitorFromPoint(long point, uint flags);
    [LibraryImport("shcore.dll")] private static partial int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static partial int SystemParametersInfo(uint action, int parameter, nint value, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW")] private static partial int EnumDisplaySettings(nint device, int mode, ref DevMode devMode);
    [LibraryImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW")] private static partial int ChangeDisplaySettingsEx(nint device, ref DevMode devMode, nint window, uint flags, nint parameter);
}
