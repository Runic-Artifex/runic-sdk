using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;
using SharedLibraryApp;
using SharedUi;

var services = new ServiceCollection();
services.AddScoped<ShellViewModel>();
services.AddScoped<AboutViewModel>();
services.AddScoped<SupportViewModel>();
// The application's composition registers the Bridges of its own and SharedUi's ViewModels, and SharedUi's Views.
services.AddRunicViews();

using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true
});

return RunicCsWebUiHost.Run(provider, async host =>
{
    await using var window = await host.OpenWindowAsync<ShellWindow>();
    await window.WaitForCloseAsync();
    return 0;
});
