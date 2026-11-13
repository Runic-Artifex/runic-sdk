using CheckTool;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.ReactiveUI;
using Runic.Desktop;
using Runic.Platform;

var services = new ServiceCollection();
services.AddRunicReactiveModelContext();
services.AddScoped<CheckViewModel>();
services.AddRunicViews();
// The Window's message box (IDesktopMessageBoxes). Hosted, it is the host's, owned by the window that shows the check.
services.AddRunicPlatformServices();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

// Hosted mode is the check's choice: only its own `hosted` parameter accepts a host (examples/hosted-windows explains
// the opt-in). The host decides whether the Window shows in a window of its own or in a region of the host's page; the
// check does the same in both.
if (args is ["hosted"])
{
    try
    {
        return RunicDesktopHost.Run(provider, new DesktopHostOptions(),
            new RunicDesktopHostOptions
            {
                Guest = RunicGuestOptions.FromStandardInput() with
                {
                    ApplicationName = "System check",
                    // The check needs administrator rights where the installer has them (README, "Elevated"): it accepts
                    // an elevated host, and Runic then checks that its parent is that host, elevated and the same user.
                    // Unelevated, this changes nothing.
                    AllowElevatedHost = true,
                },
            },
            RunWindowAsync);
    }
    catch (RunicHostedApplicationException error)
    {
        // Started with `hosted` but not by a Runic host, such as from a terminal.
        Console.Error.WriteLine($"The check runs hosted only when a Runic application starts it ({error.Code}): {error.Message}");
        return 1;
    }
}

// On its own, the check opens its own window.
return RunicDesktopHost.Run(provider, RunWindowAsync);

static async Task<int> RunWindowAsync(RunicDesktopHost host)
{
    // In a region the open completes as soon as the host decided; the page loads when a region attaches the Window. The
    // page's title names the window, or in a region the frame (Frontend/src/app.ts).
    await using var window = await host.OpenWindowAsync<CheckWindow>(new RunicWindowOptions { Width = 520, Height = 360 });
    await window.ViewModel.StartAsync();
    await window.WaitForCloseAsync();
    return 0;
}
