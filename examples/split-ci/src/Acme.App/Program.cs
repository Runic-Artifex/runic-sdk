using Acme.App;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;

await using var provider = new ServiceCollection().AddAcmeApp().BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true
});

// WebView2 on Windows, WebKitGTK 4.1 on Linux, WKWebView on macOS.
var options = new DesktopHostOptions { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } };
var hostOptions = new RunicDesktopHostOptions
{
    ContentSecurityPolicy = "default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'"
};
return RunicDesktopHost.Run(provider, options, hostOptions, RunAsync);

static async Task<int> RunAsync(IRunicWindowHost host)
{
    await using var window = await host.OpenWindowAsync<CounterWindow>(new RunicWindowOptions { Width = 480, Height = 320 });
    await window.WaitForCloseAsync();
    return 0;
}
