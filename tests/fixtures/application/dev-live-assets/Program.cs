using DevLiveAssets;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;

var services = new ServiceCollection();
services.AddScoped<StatusViewModel>();
services.AddRunicBridges();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
// webview2-debug-smoke.mjs gives the Window browser arguments of its own, as an application's
// DesktopWindowOptions.BrowserArguments, and checks that WebView2 still applies them next to the template's
// WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS launch profile. Unset, the Window has none, as the template's.
var hostOptions = new RunicDesktopHostOptions
{
    WindowOptions = RunicDesktopHost.DefaultWindowOptions with
    {
        BrowserArguments = Environment.GetEnvironmentVariable("DEV_LIVE_ASSETS_BROWSER_ARGUMENTS"),
    },
};
// dev-smoke.mjs runs this through `dotnet runic dev` with RUNIC_APPLICATION_SERVE_ONLY=1 and opens the page in Chromium.
return RunicDesktopHost.Run(provider, new DesktopHostOptions { WaitForConnection = false }, hostOptions, async host =>
{
    await using var window = await host.OpenWindowAsync<StatusWindow>();
    await window.WaitForCloseAsync();
    return 0;
});
