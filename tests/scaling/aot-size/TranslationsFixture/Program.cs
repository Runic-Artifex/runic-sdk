using AotSize;
using AotSize.Models;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

// ../Fixture/Program.cs with Translations: the application's context in German and Runic's own catalog.
var services = new ServiceCollection();
FixtureModels.Register(services);
services.AddRunicViews();
services.AddRunicTranslations(options => options.InitialLocale = "de");
services.AddRunicViewsTranslations();

using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

return RunicCsWebUiHost.Run(provider, async host =>
{
    await using var window = await host.OpenWindowAsync<ShellWindow>();
    await window.WaitForCloseAsync();
    return 0;
});
