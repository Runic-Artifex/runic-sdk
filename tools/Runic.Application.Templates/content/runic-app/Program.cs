#if (host == "desktop")
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views.Desktop;
#if (viewModels == "reactiveui")
using Runic.Application.Views.ReactiveUI;
#endif
using Runic.Desktop;
using RunicWindowApp;

var services = new ServiceCollection();
#if (viewModels == "reactiveui")
services.AddRunicReactiveModelContext();
#endif
services.AddScoped<WorkspaceViewModel>();
services.AddScoped<WelcomeViewModel>();
services.AddScoped<CounterViewModel>();
services.AddRunicViews();

await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true
});

await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
{
    // Linux embeds WebKitGTK 4.1 through GTK 3. Windows uses WebView2 and macOS WKWebView.
    Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 },
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
#else
using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views.CsWebUi;
#if (viewModels == "reactiveui")
using Runic.Application.Views.ReactiveUI;
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
