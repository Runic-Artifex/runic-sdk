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
    // The theme is the application's: every Window shows it, the page's prefers-color-scheme follows, and a new choice
    // switches the open Windows at once. "system" (NoPreference) follows the desktop.
    var settings = provider.GetRequiredService<SettingsStore>();
    await host.Desktop.SetColorSchemeAsync(settings.Load().ColorScheme);
    settings.Saved += (_, saved) => _ = host.Desktop.SetColorSchemeAsync(saved.ColorScheme).AsTask();
    // The first Window. Its ViewModel opens more with IRunicWindowService; the application ends when this one closes.
    await using var window = await host.OpenWindowAsync<ProjectWindow>(new RunicWindowOptions { Width = 640, Height = 420 });
    await window.WaitForCloseAsync();
    return 0;
});
