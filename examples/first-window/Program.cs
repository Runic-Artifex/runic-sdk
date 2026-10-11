using FirstWindow;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

var services = new ServiceCollection();
services.AddScoped<CounterViewModel>();
services.AddRunicViews();

using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true
});

// The main thread waits for WebUI while the application runs; Run cleans up WebUI's native state at the end.
return RunicCsWebUiHost.Run(provider, async host =>
{
    await using var window = await host.OpenWindowAsync<CounterWindow>();
    await window.WaitForCloseAsync();
    return 0;
});
