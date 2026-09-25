using FirstWindowDesktop;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;

var services = new ServiceCollection();
services.AddScoped<CounterViewModel>();
services.AddRunicBridges();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
var serveOnly = args.Contains("--serve-only", StringComparer.Ordinal);
var probeOwner = args.Contains("--probe-owner", StringComparer.Ordinal);
await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
{
    WaitForConnection = !serveOnly && !probeOwner,
});
var surfaceOptions = new DesktopSurfaceOptions
{
    RootFolder = Path.Combine(AppContext.BaseDirectory, "www"),
    Content = "index.html",
};

if (serveOnly)
{
    await using var scope = provider.CreateAsyncScope();
    await using var surface = await desktop.CreateSurfaceAsync(surfaceOptions);
    var transport = new DesktopBridgeTransport(surface);
    using var content = new WindowContentSession(transport);
    var viewModel = scope.ServiceProvider.GetRequiredService<CounterViewModel>();
    using var attachment = scope.ServiceProvider.GetRequiredService<
        Func<IBridgeTransport, WindowContentSession, CounterViewModel, IDisposable>>()(transport, content, viewModel);
    Console.WriteLine(surface.Url);
    Console.Out.Flush();
    await Console.In.ReadLineAsync();
    return;
}

await using var window = await provider.OpenDesktopWindowAsync<CounterWindow, CounterViewModel>(
    desktop, surfaceOptions, host => new CounterWindow(host),
    new DesktopWindowOptions { Browser = BrowserKind.Any, Width = 800, Height = 600 });
if (probeOwner)
{
    var close = await window.CloseAsync(TimeSpan.FromSeconds(2));
    await close.Completion;
    if (!close.Drained || close.RemainingOperations != 0 || CounterViewModel.Disposals != 1)
        throw new InvalidOperationException("The Desktop Window did not release its ViewModel scope cleanly.");
    Console.WriteLine("DESKTOP_VIEWS_OWNER_OK");
    return;
}
Console.WriteLine(window.Surface.Url);
window.Presentation.WaitForClose();
