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
var browserArguments = Environment.GetEnvironmentVariable("DEV_LIVE_ASSETS_BROWSER_ARGUMENTS");
var hostOptions = new RunicDesktopHostOptions
{
    WindowOptions = RunicDesktopHost.DefaultWindowOptions with { BrowserArguments = browserArguments },
};
// DEV_LIVE_ASSETS_TRACE=1: what the process received and every Desktop diagnostic (such as a fallback from WebView2), on
// standard error for webview2-debug-smoke.mjs's failure detail.
var trace = Environment.GetEnvironmentVariable("DEV_LIVE_ASSETS_TRACE") == "1";
if (trace)
{
    Console.Error.WriteLine($"[dev-live-assets] process {Environment.ProcessId}; " +
        $"WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS={Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS") ?? "(unset)"}; " +
        $"DOTNET_ENVIRONMENT={Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "(unset)"}; BrowserArguments={browserArguments ?? "(none)"}");
}
var desktopOptions = new DesktopHostOptions
{
    WaitForConnection = false,
    DiagnosticSink = trace ? diagnostic => Console.Error.WriteLine($"[dev-live-assets] diagnostic: {diagnostic}") : null,
};
// dev-smoke.mjs runs this through `dotnet runic dev` with RUNIC_APPLICATION_SERVE_ONLY=1 and opens the page in Chromium.
return RunicDesktopHost.Run(provider, desktopOptions, hostOptions, async host =>
{
    await using var window = await host.OpenWindowAsync<StatusWindow>();
    if (trace) Console.Error.WriteLine("[dev-live-assets] window opened");
    await window.WaitForCloseAsync();
    return 0;
});
