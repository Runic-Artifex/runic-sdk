using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.ReactiveUI;
using Runic.Desktop;
using WidgetTool;

// `WidgetTool [name]` runs on its own; `WidgetTool hosted <name>` waits for a Runic host (examples/hosted-windows explains
// the opt-in).
var hosted = args is ["hosted", ..];
var name = (hosted ? args.Skip(1) : args).FirstOrDefault() ?? "Service";

var services = new ServiceCollection();
services.AddRunicReactiveModelContext();
services.AddSingleton(TimeProvider.System);
services.AddSingleton(new WidgetOptions(name));
services.AddScoped<WidgetViewModel>();
services.AddRunicViews();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

if (hosted)
{
    try
    {
        return RunicDesktopHost.Run(provider, new DesktopHostOptions(),
            new RunicDesktopHostOptions
            {
                Guest = RunicGuestOptions.FromStandardInput() with
                {
                    ApplicationName = $"{name} widget",
                    // Trusted component mode: the widget's code runs in the dashboard's page, and the dashboard's page holds
                    // the widget's token. Allow it only for the hosts of your own suite. The name is what the host says
                    // about itself: a filter, not a security boundary. Every other host shows the widget in a frame.
                    AllowComponentEmbedding = host => host.Name == "Hosted components dashboard",
                    // CI's Windows runner runs elevated: the dashboard's --elevated runs the pair (examples/hosted-region).
                    AllowElevatedHost = true,
                },
            },
            RunWindowAsync);
    }
    catch (RunicHostedApplicationException error)
    {
        Console.Error.WriteLine($"The widget runs hosted only when a Runic application starts it ({error.Code}): {error.Message}");
        return 1;
    }
}

return RunicDesktopHost.Run(provider, RunWindowAsync);

static async Task<int> RunWindowAsync(RunicDesktopHost host)
{
    await using var window = await host.OpenWindowAsync<WidgetWindow>(new RunicWindowOptions { Width = 320, Height = 200 });
    await window.WaitForCloseAsync();
    return 0;
}
