using System.Runtime.InteropServices;
using Runic.Desktop;
using Runic.Platform;

// DesktopWindowOptions.ColorScheme in the WebView2 and WKWebView windows: for Dark and Light the page's
// prefers-color-scheme matches, and so does the window: its NSWindow.effectiveAppearance on macOS, its
// DWMWA_USE_IMMERSIVE_DARK_MODE title bar on Windows. A window without the option leaves both to the system. Run with
// --color-scheme on Windows or macOS.
internal static partial class ColorSchemeSmoke
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    internal static int Run(DesktopHostOptions hostOptions) => DesktopEventLoop.Run(hostOptions, RunAsync);

    private static async Task<int> RunAsync(DesktopHost host)
    {
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
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
            }
            finally { if (window.IsOpen) await window.CloseAsync(); }
        }
        Console.WriteLine("COLOR SCHEME DONE");
        return 0;
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
