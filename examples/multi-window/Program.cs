using Microsoft.Extensions.DependencyInjection;
using MultiWindow;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;

await using var provider = new ServiceCollection().AddProject().BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true,
});

return RunicDesktopHost.Run(provider, async host =>
{
    // The first Window. Its ViewModel opens more with IRunicWindowService; the application ends when this one closes.
    await using var window = await host.OpenWindowAsync<ProjectWindow>(new RunicWindowOptions { Width = 640, Height = 420 });
    await window.WaitForCloseAsync();
    return 0;
});
