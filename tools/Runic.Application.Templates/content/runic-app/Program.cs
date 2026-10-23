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
// Fallbacks and limitations, such as a missing WebKitGTK runtime, are written to standard error and to
// DiagnosticsLog.Path, since a Windows application has no console.
#if (gtk4)
// Linux embeds WebKitGTK 6 through GTK 4, with the application ID that the desktop entry is named after. Windows uses
// WebView2 and macOS WKWebView.
var options = new DesktopHostOptions { DiagnosticSink = ReportDiagnostic }.WithGtk4(ApplicationIdentity.Id);
#else
// Linux embeds WebKitGTK 4.1 through GTK 3. Windows uses WebView2 and macOS WKWebView.
var options = new DesktopHostOptions
{
    Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 },
    DiagnosticSink = ReportDiagnostic,
};
#endif

// Outside Development, the Window's pages get a Content-Security-Policy: scripts and connections only from the
// application itself, no string evaluation, no plugins, no foreign <base> and no framing. The Views Bridge needs
// nothing more: its state arrives as data. Development (dotnet runic dev and its development server) sends none.
var developing = string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);
#if (frontend == "sveltekit")
// SvelteKit starts the page with an inline script. vite.config.ts makes it add a policy of its own that allows
// only that script by its hash; the browser enforces both policies. style-src-attr allows exactly the style attribute
// of SvelteKit's route announcer (root.svelte in @sveltejs/kit 3.0.0) by its hash; update it with SvelteKit.
const string contentSecurityPolicy = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src-attr 'unsafe-hashes' 'sha256-S8qMpvofolR8Mpjy4kQvEm7m1q8clzU4dfDH0AmvZjo='; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
#elif (frontend == "angular")
// Angular adds component styles as <style> elements.
const string contentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
#else
const string contentSecurityPolicy = "default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
#endif
var hostOptions = new RunicDesktopHostOptions { ContentSecurityPolicy = developing ? null : contentSecurityPolicy };

// Opens Windows natively with the embedded WebView, falling back to an installed browser, on the event loop
// this platform and backend need. Call it before any await. RunicCsWebUiHost.Run(provider, RunAsync), from
// Runic.Application.Views.CsWebUi, runs the same application on CS-WebUI.
return RunicDesktopHost.Run(provider, options, hostOptions, RunAsync);
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
    DiagnosticsLog.Write($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message} {diagnostic.Remediation}".TrimEnd());
#endif
