using AotSize;
using AotSize.Models;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;

// The CS-WebUI fixture's application (../Fixture/Program.cs) on the Runic.Desktop host: the shell is the one window it
// opens. measure.mjs publishes it with K = 0 only.
var services = new ServiceCollection();
FixtureModels.Register(services);
services.AddRunicViews();

using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

return RunicDesktopHost.Run(provider, async host =>
{
    await using var window = await host.OpenWindowAsync<ShellWindow>();
    await window.WaitForCloseAsync();
    return 0;
});
