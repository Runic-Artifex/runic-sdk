using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;

// sdk#896: a window the application opens itself, with DesktopSurface.OpenWindowAsync, as examples/first-window-desktop
// composes one, gets its DI scope's per-window services through DesktopPlatformServices.AttachWindowServices. A ViewModel
// that took IRunicWindowPresentation before the attach sets the window's scheme: the title bar (DWMWA_USE_IMMERSIVE_DARK_MODE
// on Windows, NSWindow.effectiveAppearance on macOS) and the page's prefers-color-scheme follow, as for a RunicDesktopHost
// Window; under GTK, whose scheme belongs to the process, the page follows. On Windows and macOS the page's session also
// receives the desktop's appearance (appearance/1). IRunicWindowContext.RequestCloseAsync closes the window.
internal static partial class DirectServicesSmoke
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    internal static int Run()
    {
        var options = new DesktopHostOptions
        {
            Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 },
            DiagnosticSink = static diagnostic => Console.Error.WriteLine($"Desktop diagnostic: {diagnostic.Category}/{diagnostic.Code}: {diagnostic.Message}"),
        };
        var result = DesktopEventLoop.Run(options, RunAsync);
        if (result == 0) Console.WriteLine("Runic direct services smoke passed.");
        return result;
    }

    private static async Task<int> RunAsync(DesktopHost host)
    {
        var collection = new ServiceCollection();
        collection.AddRunicWindowService();
        collection.AddScoped<DirectServicesViewModel>();
        await using var services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 480, Height = 360 });
        await using var scope = services.CreateAsyncScope();
        using var transport = new DesktopBridgeTransport(surface);
        using var content = new WindowContentSession(transport, new WindowContentSessionOptions());
        // The ViewModel takes its window's presentation before the window's services are attached.
        var viewModel = scope.ServiceProvider.GetRequiredService<DirectServicesViewModel>();
        await using var windowServices = DesktopPlatformServices.AttachWindowServices(window, scope.ServiceProvider, content);
        if (!ReferenceEquals(windowServices.Presentation, viewModel.Window))
            throw new InvalidOperationException("The ViewModel does not hold the scope's attached presentation.");
        if (!viewModel.Window.Features.HasFlag(RunicWindowPresentationFeatures.ColorScheme))
            throw new InvalidOperationException($"The direct window's presentation reports {viewModel.Window.Features}, without ColorScheme.");
        await ScriptAsync(surface, "return document.readyState;");
        foreach (var scheme in new[] { DesktopColorScheme.Dark, DesktopColorScheme.Light })
        {
            if (await viewModel.Window.SetColorSchemeAsync(scheme) is not PlatformResult<PlatformUnit>.Success)
                throw new InvalidOperationException($"IRunicWindowPresentation.SetColorSchemeAsync({scheme}) did not succeed.");
            await ExpectAsync(surface, window, scheme);
            if (viewModel.Window.ColorScheme != scheme)
                throw new InvalidOperationException($"The presentation reports {viewModel.Window.ColorScheme} after {scheme}.");
        }
        Console.WriteLine("PASS direct services: a ViewModel's IRunicWindowPresentation set the scheme of a window the application opened.");
        if (!OperatingSystem.IsLinux())
        {
            // appearance/1: the session of a loopback window follows the desktop's settings.
            for (var waited = 0; content.Appearance is null; waited += 100)
            {
                if (waited > 15_000) throw new InvalidOperationException("The direct window's session received no desktop appearance.");
                await Task.Delay(100);
            }
            Console.WriteLine($"PASS direct services: the page's session received the desktop's appearance ({content.Appearance.ColorScheme}).");
        }
        var context = scope.ServiceProvider.GetRequiredService<IRunicWindowContext>();
        if (!await context.RequestCloseAsync().AsTask().WaitAsync(StepTimeout))
            throw new InvalidOperationException("IRunicWindowContext.RequestCloseAsync did not close the window.");
        await window.WaitForCloseAsync().AsTask().WaitAsync(StepTimeout);
        for (var waited = 0; viewModel.Window.Activity != RunicWindowActivity.Closed; waited += 50)
        {
            if (waited > 5000) throw new InvalidOperationException("The presentation of the closed window does not report it closed.");
            await Task.Delay(50);
        }
        Console.WriteLine("PASS direct services: IRunicWindowContext.RequestCloseAsync closed the window, and the presentation reports it closed.");
        return 0;
    }

    private static async Task<string?> ScriptAsync(DesktopSurface surface, string script)
    {
        using var timeout = new CancellationTokenSource(StepTimeout);
        return await surface.ExecuteJavaScriptAsync(script, timeout.Token);
    }

    // The page's prefers-color-scheme, and on Windows and macOS the window, follow a change within a few seconds.
    private static async Task ExpectAsync(DesktopSurface surface, DesktopWindow window, DesktopColorScheme scheme)
    {
        var expectedDark = scheme == DesktopColorScheme.Dark ? "true" : "false";
        string? expectedNative = OperatingSystem.IsLinux() ? null
            : OperatingSystem.IsMacOS() ? scheme == DesktopColorScheme.Dark ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua"
            : scheme == DesktopColorScheme.Dark ? "1" : "0";
        string? dark = null, native = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            dark = await ScriptAsync(surface, "return String(matchMedia('(prefers-color-scheme: dark)').matches);");
            native = expectedNative is null ? null : await NativeAsync(window);
            if (dark == expectedDark && native == expectedNative) return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException(
            $"With {scheme} the page's prefers-color-scheme: dark is {dark} and the window reports {native}, expected {expectedDark} and {expectedNative}.");
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
        <script src="webui.js"></script><title>Runic direct services smoke</title></head>
        <body><main><h1>Direct services</h1></main></body></html>
        """;

    private sealed class DirectServicesViewModel(IRunicWindowPresentation window) : INotifyPropertyChanged
    {
        public IRunicWindowPresentation Window { get; } = window;
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }
}
