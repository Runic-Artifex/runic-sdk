using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using SidePanelSample;

var services = new ServiceCollection();
services.AddSidePanelSample();
services.AddRunicViews();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

// RUNIC_APPLICATION_SERVE_ONLY=1 serves the surface without a window, for browser-smoke.mjs: there the Window cannot
// grow (Unsupported), so the page shows the debug panel as an overlay.
return RunicDesktopHost.Run(provider, new DesktopHostOptions(), new RunicDesktopHostOptions(),
    async host =>
    {
        await using var window = await host.OpenWindowAsync<MainWindow>(new RunicWindowOptions { Width = 860, Height = 640 });
        await window.WaitForCloseAsync();
        return 0;
    });
