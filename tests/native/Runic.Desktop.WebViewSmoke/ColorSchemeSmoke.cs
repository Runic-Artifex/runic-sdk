using System.Runtime.InteropServices;
using Runic.Desktop;
using Runic.Platform;

// DesktopWindowOptions.ColorScheme in the WebView2 and WKWebView windows: for Dark and Light the page's
// prefers-color-scheme matches, and so does the window: its NSWindow.effectiveAppearance on macOS, its
// DWMWA_USE_IMMERSIVE_DARK_MODE title bar on Windows. A window without the option leaves both to the system. B2: an open
// window switches with DesktopWindow.SetColorSchemeAsync, and with DesktopHost.SetColorSchemeAsync while it has no
// scheme of its own. S3: DesktopWindow.SetLayoutDirectionAsync mirrors the open WebView2 window (WS_EX_LAYOUTRTL) and
// returns it; WKWebView reports no LayoutDirection capability. Run with --color-scheme on Windows or macOS.
internal static partial class ColorSchemeSmoke
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    internal static int Run(DesktopHostOptions hostOptions) => DesktopEventLoop.Run(hostOptions, RunAsync);

    private static async Task<int> RunAsync(DesktopHost host)
    {
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        if (OperatingSystem.IsLinux()) return await RunGtkAsync(host, surface);
        foreach (var scheme in new[] { DesktopColorScheme.Dark, DesktopColorScheme.Light })
        {
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded,
                Width = 480,
                Height = 360,
                ColorScheme = scheme,
            });
            try
            {
                var dark = await surface.ExecuteJavaScriptAsync("return String(matchMedia('(prefers-color-scheme: dark)').matches);", StepTimeout);
                if (dark != (scheme == DesktopColorScheme.Dark ? "true" : "false"))
                    throw new InvalidOperationException($"With ColorScheme {scheme} the page's prefers-color-scheme: dark is {dark}.");
                var native = await NativeAsync(window);
                var expected = OperatingSystem.IsMacOS()
                    ? scheme == DesktopColorScheme.Dark ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua"
                    : scheme == DesktopColorScheme.Dark ? "1" : "0";
                if (native != expected)
                    throw new InvalidOperationException($"With ColorScheme {scheme} the window reports {native}, expected {expected}.");
                Console.WriteLine($"PASS color scheme: {scheme}: the page's prefers-color-scheme and the window ({native}) follow it.");
            }
            finally { if (window.IsOpen) await window.CloseAsync(); }
        }
        await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            Width = 480,
            Height = 360,
            ColorScheme = DesktopColorScheme.Dark,
        }))
        {
            try
            {
                await ExpectAsync(surface, window, DesktopColorScheme.Dark, "as it opened");
                await window.SetColorSchemeAsync(DesktopColorScheme.Light);
                await ExpectAsync(surface, window, DesktopColorScheme.Light, "after DesktopWindow.SetColorSchemeAsync");
                // Without its own scheme the window follows the application's.
                await window.SetColorSchemeAsync(DesktopColorScheme.NoPreference);
                await host.SetColorSchemeAsync(DesktopColorScheme.Dark);
                await ExpectAsync(surface, window, DesktopColorScheme.Dark, "after DesktopHost.SetColorSchemeAsync");
                await host.SetColorSchemeAsync(DesktopColorScheme.Light);
                await ExpectAsync(surface, window, DesktopColorScheme.Light, "after a second DesktopHost.SetColorSchemeAsync");
                await host.SetColorSchemeAsync(DesktopColorScheme.NoPreference);
                Console.WriteLine("PASS color scheme: the open window and its page switched at run time.");
            }
            finally { if (window.IsOpen) await window.CloseAsync(); }
        }
        await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 480, Height = 360 }))
        {
            try
            {
                await surface.ExecuteJavaScriptAsync("return document.readyState;", StepTimeout);
                if (OperatingSystem.IsMacOS())
                {
                    string? appearance = null;
                    await window.InvokeNativeAsync(handle => appearance = Send(handle, Sel("appearance")) == 0 ? "nil" : "set");
                    if (appearance != "nil") throw new InvalidOperationException("Without ColorScheme the macOS window has its own appearance.");
                }
                Console.WriteLine("PASS color scheme: without the option the window follows the system.");
                await LayoutDirectionAsync(window);
            }
            finally { if (window.IsOpen) await window.CloseAsync(); }
        }
        Console.WriteLine("COLOR SCHEME DONE");
        return 0;
    }

    private static async Task LayoutDirectionAsync(DesktopWindow window)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (window.Capabilities.HasFlag(DesktopWindowCapabilities.LayoutDirection))
                throw new InvalidOperationException("The WKWebView window reports DesktopWindowCapabilities.LayoutDirection.");
            try
            {
                await window.SetLayoutDirectionAsync(DesktopLayoutDirection.RightToLeft);
                throw new InvalidOperationException("SetLayoutDirectionAsync succeeded in the WKWebView window.");
            }
            catch (DesktopException error) when (error.Code == "window-capability-unavailable") { }
            Console.WriteLine("PASS layout direction: the WKWebView window reports no run-time direction.");
            return;
        }
        foreach (var (direction, mirrored) in new[] { (DesktopLayoutDirection.RightToLeft, true), (DesktopLayoutDirection.Default, false) })
        {
            await window.SetLayoutDirectionAsync(direction);
            var extendedStyle = 0L;
            await window.InvokeNativeAsync(handle => extendedStyle = GetWindowLongPtr(handle, -20));
            if (((extendedStyle & 0x00400000) != 0) != mirrored || window.LayoutDirection != direction)
                throw new InvalidOperationException($"After SetLayoutDirectionAsync({direction}) the window's extended style is 0x{extendedStyle:X}.");
        }
        Console.WriteLine("PASS layout direction: SetLayoutDirectionAsync mirrors the open WebView2 window and returns it.");
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial long GetWindowLongPtr(nint window, int index);

    // GTK has no per-window scheme: DesktopHost.SetColorSchemeAsync sets the process's prefer-dark setting, and WebKitGTK's
    // prefers-color-scheme follows it. Run with --color-scheme under Xvfb.
    private static async Task<int> RunGtkAsync(DesktopHost host, DesktopSurface surface)
    {
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 480, Height = 360 });
        if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.ColorScheme))
            throw new InvalidOperationException("The GTK window did not advertise its color scheme.");
        await surface.ExecuteJavaScriptAsync("return document.readyState;", StepTimeout);
        var system = await surface.ExecuteJavaScriptAsync("return String(matchMedia('(prefers-color-scheme: dark)').matches);", StepTimeout);
        foreach (var scheme in new[] { DesktopColorScheme.Dark, DesktopColorScheme.Light, DesktopColorScheme.Dark })
        {
            await host.SetColorSchemeAsync(scheme);
            await ExpectPageAsync(surface, scheme, "after DesktopHost.SetColorSchemeAsync");
        }
        await window.SetColorSchemeAsync(DesktopColorScheme.Light);
        await ExpectPageAsync(surface, DesktopColorScheme.Light, "after DesktopWindow.SetColorSchemeAsync");
        if (window.Capabilities.HasFlag(DesktopWindowCapabilities.PerWindowColorScheme) || window.ColorScheme != DesktopColorScheme.Light)
            throw new InvalidOperationException($"The GTK window reported a scheme of its own, or {window.ColorScheme}.");
        // NoPreference resets GTK's settings, so the page follows the desktop again: here the scheme it had before.
        await host.SetColorSchemeAsync(DesktopColorScheme.NoPreference);
        await ExpectPageAsync(surface, system == "true" ? DesktopColorScheme.Dark : DesktopColorScheme.Light, "after NoPreference");
        Console.WriteLine("PASS color scheme: the GTK window's page switched with the application's scheme and returned to the system's.");
        Console.WriteLine("COLOR SCHEME DONE");
        return 0;
    }

    private static async Task ExpectPageAsync(DesktopSurface surface, DesktopColorScheme scheme, string when)
    {
        var expected = scheme == DesktopColorScheme.Dark ? "true" : "false";
        string? dark = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            dark = await surface.ExecuteJavaScriptAsync("return String(matchMedia('(prefers-color-scheme: dark)').matches);", StepTimeout);
            if (dark == expected) return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException($"{when}: with {scheme} the page's prefers-color-scheme: dark is {dark}, expected {expected}.");
    }

    // The page's prefers-color-scheme and the window follow a change within a few seconds.
    private static async Task ExpectAsync(DesktopSurface surface, DesktopWindow window, DesktopColorScheme scheme, string when)
    {
        var expectedDark = scheme == DesktopColorScheme.Dark ? "true" : "false";
        var expectedNative = OperatingSystem.IsMacOS()
            ? scheme == DesktopColorScheme.Dark ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua"
            : scheme == DesktopColorScheme.Dark ? "1" : "0";
        string? dark = null, native = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            dark = await surface.ExecuteJavaScriptAsync("return String(matchMedia('(prefers-color-scheme: dark)').matches);", StepTimeout);
            native = await NativeAsync(window);
            if (dark == expectedDark && native == expectedNative) return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException(
            $"{when}: with {scheme} the page's prefers-color-scheme: dark is {dark} and the window reports {native}, expected {expectedDark} and {expectedNative}.");
    }

    // macOS: [[window effectiveAppearance] name]. Windows: DWMWA_USE_IMMERSIVE_DARK_MODE of the top-level window.
    private static async Task<string> NativeAsync(DesktopWindow window)
    {
        string? result = null;
        await window.InvokeNativeAsync(handle =>
        {
            if (OperatingSystem.IsMacOS())
            {
                var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
                try
                {
                    var name = Send(Send(handle, Sel("effectiveAppearance")), Sel("name"));
                    result = name == 0 ? "" : Marshal.PtrToStringUTF8(Send(name, Sel("UTF8String")));
                }
                finally { Send(pool, Sel("drain")); }
            }
            else
            {
                var hr = DwmGetWindowAttribute(handle, 20, out var value, sizeof(int));
                result = hr < 0 ? $"DwmGetWindowAttribute failed with 0x{hr:X8}" : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        });
        return result ?? "";
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);

    private const string Page = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="color-scheme" content="light dark">
        <script src="webui.js"></script><title>Runic color scheme smoke</title></head>
        <body><main><h1>Color scheme</h1></main></body></html>
        """;
}
