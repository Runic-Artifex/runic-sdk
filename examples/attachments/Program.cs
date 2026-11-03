using Attachments;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;

var services = new ServiceCollection();
services.AddScoped<ProfileViewModel>();
services.AddRunicViews();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

// RUNIC_APPLICATION_SERVE_ONLY=1 serves the surface without a window, for browser-smoke.mjs.
return RunicDesktopHost.Run(provider, new DesktopHostOptions(), new RunicDesktopHostOptions { MaximumUploadBytes = 50 * 1_048_576 },
    async host =>
    {
        await using var window = await host.OpenWindowAsync<ProfileWindow>(new RunicWindowOptions { Width = 640, Height = 640 });
        await window.WaitForCloseAsync();
        return 0;
    });
