using FirstWindowDesktop;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.ReactiveUI;
using Runic.Desktop;

var services = new ServiceCollection();
services.AddRunicReactiveModelContext();
services.AddScoped<CounterViewModel>();
services.AddRunicBridges();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
var probeOwner = args.Contains("--probe-owner", StringComparer.Ordinal);
var hostOptions = new DesktopHostOptions { WaitForConnection = !probeOwner };

// RUNIC_APPLICATION_SERVE_ONLY=1 makes the host serve the authenticated surface without opening a window,
// for an external browser test (browser-smoke.mjs).
return RunicDesktopHost.Run(provider, hostOptions, async host =>
{
    await using var window = await host.OpenWindowAsync<CounterWindow>(new RunicWindowOptions { Width = 800, Height = 600 });
    if (probeOwner)
    {
        var close = await window.CloseAsync(TimeSpan.FromSeconds(2));
        await close.Completion;
        if (!close.Drained || close.RemainingOperations != 0 || CounterViewModel.Disposals != 1)
            throw new InvalidOperationException("The Desktop Window did not release its ViewModel scope cleanly.");
        Console.WriteLine("DESKTOP_VIEWS_OWNER_OK");
        return 0;
    }
    // The Desktop adapter behind the host-neutral Window exposes the surface and native presentation.
    Console.WriteLine(((DesktopBridgeWindow<CounterViewModel>)window.Host).Surface.Url);
    await window.WaitForCloseAsync();
    return 0;
});
