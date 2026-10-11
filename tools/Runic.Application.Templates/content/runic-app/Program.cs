using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
#if (desktopHost)
using Runic.Application.Views.Desktop;
using Runic.Desktop;
#if (gtk4)
using Runic.Desktop.Gtk4;
#endif
#else
using Runic.Application.Views.CsWebUi;
#endif
using RunicWindowApp;

await using var provider = new ServiceCollection().AddWorkspace().BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true
});

#if (desktopHost)
// Fallbacks and limitations, such as a missing WebKitGTK runtime, are written to standard error.
#if (gtk4)
// Linux embeds WebKitGTK 6 through GTK 4. Windows uses WebView2 and macOS WKWebView.
var options = new DesktopHostOptions { DiagnosticSink = ReportDiagnostic }.WithGtk4();
#else
// Linux embeds WebKitGTK 4.1 through GTK 3. Windows uses WebView2 and macOS WKWebView.
var options = new DesktopHostOptions
{
    Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 },
    DiagnosticSink = ReportDiagnostic,
};
#endif

// Opens Windows natively with the embedded WebView, falling back to an installed browser, on the event loop
// this platform and backend need. Call it before any await. RunicCsWebUiHost.Run(provider, RunAsync), from
// Runic.Application.Views.CsWebUi, runs the same application on CS-WebUI.
return RunicDesktopHost.Run(provider, options, RunAsync);
#else
// Opens Windows in an installed browser in app mode, with the platform WebView as a fallback, while the main
// thread waits for WebUI. RunicDesktopHost.Run(provider, RunAsync), from Runic.Application.Views.Desktop, runs the same
// application in a native window.
return RunicCsWebUiHost.Run(provider, RunAsync);
#endif

// The application is the same on every host: open the Window, then wait until it closes.
static async Task<int> RunAsync(IRunicWindowHost host)
{
    await using var window = await host.OpenWindowAsync<WorkspaceWindow>(new RunicWindowOptions { Width = 1000, Height = 700 });
    await window.WaitForCloseAsync();
    return 0;
}
#if (desktopHost)

static void ReportDiagnostic(DesktopDiagnostic diagnostic) =>
    Console.Error.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message} {diagnostic.Remediation}".TrimEnd());
#endif
