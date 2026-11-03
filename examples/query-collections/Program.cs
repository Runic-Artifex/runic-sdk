using Microsoft.Extensions.DependencyInjection;
using QueryCollectionsExample;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.ReactiveUI;
using Runic.Desktop;

var services = new ServiceCollection();
services.AddRunicReactiveModelContext();
services.AddSingleton<ProductStore>();
services.AddScoped<CatalogViewModel>();
services.AddRunicBridges();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

// RUNIC_APPLICATION_SERVE_ONLY=1 serves the surface without opening a window, for a browser.
return RunicDesktopHost.Run(provider, new DesktopHostOptions(), async host =>
{
    await using var window = await host.OpenWindowAsync<CatalogWindow>(new RunicWindowOptions { Width = 960, Height = 760 });
    Console.WriteLine(((DesktopBridgeWindow<CatalogViewModel>)window.Host).Surface.Url.AbsoluteUri);
    await window.WaitForCloseAsync();
    return 0;
});
