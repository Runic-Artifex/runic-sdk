using DevLiveAssets;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;

var services = new ServiceCollection();
services.AddScoped<StatusViewModel>();
services.AddRunicBridges();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
// dev-smoke.mjs runs this through `dotnet runic dev` with RUNIC_APPLICATION_SERVE_ONLY=1 and opens the page in Chromium.
return RunicDesktopHost.Run(provider, new DesktopHostOptions { WaitForConnection = false }, async host =>
{
    await using var window = await host.OpenWindowAsync<StatusWindow>();
    await window.WaitForCloseAsync();
    return 0;
});
