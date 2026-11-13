using AotSize;
using AotSize.Models;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

// The shell is the one window type the application opens, so the host window's own generics cost the same at every K.
// The shell and the K fixture ViewModels (ViewModels.g.cs) are registered and bridged, which roots each generated Bridge.
var services = new ServiceCollection();
FixtureModels.Register(services);
services.AddRunicViews();

using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

return RunicCsWebUiHost.Run(provider, async host =>
{
    await using var window = await host.OpenWindowAsync<ShellWindow>();
    await window.WaitForCloseAsync();
    return 0;
});

