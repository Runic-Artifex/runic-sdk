#if (desktopHost)
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views.Desktop;
#if (viewModels == "reactiveui")
using Runic.Navigation.ReactiveUI;
#endif
using Runic.Desktop;
#if (gtk4)
using Runic.Desktop.Gtk4;
#endif
using RunicWindowApp;

var services = new ServiceCollection();
#if (viewModels == "reactiveui")
services.AddRunicReactiveModelContext();
#endif
services.AddScoped<WorkspaceViewModel>();
services.AddScoped<WelcomeViewModel>();
services.AddScoped<CounterViewModel>();
services.AddRunicViews();
// Per-window file dialogs, clipboard and file launcher from the provider for the running backend. A
// ViewModel can take IFileDialogs, ITextClipboard or IWindowPlatformServices in its constructor; they
// bind to the native window when it opens and report OwnerUnavailable until then.
services.AddRunicPlatformServices();

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

// Runs the application on the event loop this platform and backend need. Call it before any await.
return DesktopEventLoop.Run(options, async desktop =>
{
    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
    {
        ValidateScopes = true,
        ValidateOnBuild = true
    });
    await using var window = await provider.OpenDesktopWindowAsync<WorkspaceWindow, WorkspaceViewModel>(
        desktop,
        new DesktopSurfaceOptions { Content = new DesktopContent.Directory(Path.Combine(AppContext.BaseDirectory, "www"), "index.html") },
        host => new WorkspaceWindow(host),
        new DesktopWindowOptions
        {
            // Prefer a native window with the embedded WebView; fall back to an installed browser.
            Browser = BrowserKind.Embedded,
            PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
            Width = 1000,
            Height = 700,
        });
    window.Presentation.WaitForClose();
    return 0;
});

static void ReportDiagnostic(DesktopDiagnostic diagnostic) =>
    Console.Error.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message} {diagnostic.Remediation}".TrimEnd());
#else
using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views.CsWebUi;
#if (viewModels == "reactiveui")
using Runic.Navigation.ReactiveUI;
#endif
using RunicWindowApp;

var services = new ServiceCollection();
#if (viewModels == "reactiveui")
services.AddRunicReactiveModelContext();
#endif
services.AddScoped<WorkspaceViewModel>();
services.AddScoped<WelcomeViewModel>();
services.AddScoped<CounterViewModel>();
services.AddRunicViews();
// Registers IFileDialogs, ITextClipboard and IWindowPlatformServices so ViewModels resolve on CS-WebUI.
// CS-WebUI has no native window owner, so they report OwnerUnavailable; use --host desktop for native
// dialogs and the clipboard.
services.AddRunicPlatformServices();

using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true
});

// The window releases asynchronously, before WebUI cleans up its native state.
await using (var window = provider.OpenWindow<WorkspaceWindow, WorkspaceViewModel>(host => new WorkspaceWindow(host)))
{
    window.SetRootFolder(Path.Combine(AppContext.BaseDirectory, "www"));
    window.Show("index.html");
    WebUiApplication.Wait();
}
WebUiApplication.Clean();
#endif
