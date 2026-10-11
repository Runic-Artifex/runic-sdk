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
var serveOnly = args.Contains("--serve-only", StringComparer.Ordinal);
var probeOwner = args.Contains("--probe-owner", StringComparer.Ordinal);
var hostOptions = new DesktopHostOptions { WaitForConnection = !serveOnly && !probeOwner };

if (serveOnly)
{
    // Serves the authenticated surface without a window, for an external browser test.
    await using var desktop = await DesktopHost.StartAsync(hostOptions);
    await using var scope = provider.CreateAsyncScope();
    await using var surface = await desktop.CreateSurfaceAsync(new DesktopSurfaceOptions
    {
        Content = new DesktopContent.Directory(Path.Combine(AppContext.BaseDirectory, "www"), "index.html"),
    });
    var transport = new DesktopBridgeTransport(surface);
    var viewModel = scope.ServiceProvider.GetRequiredService<CounterViewModel>();
    using var content = new WindowContentSession(transport, rootModel: viewModel);
    using var attachment = scope.ServiceProvider.GetRequiredService<
        Func<IBridgeTransport, WindowContentSession, CounterViewModel, IDisposable>>()(transport, content, viewModel);
    Console.WriteLine(surface.Url);
    Console.Out.Flush();
    await Console.In.ReadLineAsync();
    return 0;
}

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
