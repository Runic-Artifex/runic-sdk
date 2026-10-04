using DynamicDataExample;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.ReactiveUI;
using Runic.Desktop;

if (args.Contains("--benchmark", StringComparer.Ordinal))
{
    await Benchmarks.RunAsync();
    return;
}

var services = new ServiceCollection();
services.AddRunicReactiveModelContext();
services.AddSingleton<RowStore>();
services.AddScoped<RowsViewModel>();
services.AddRunicBridges();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
var serveOnly = args.Contains("--serve-only", StringComparer.Ordinal);
await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WaitForConnection = !serveOnly });
var surfaceOptions = new DesktopSurfaceOptions { RootFolder = Path.Combine(AppContext.BaseDirectory, "www"), Content = "index.html" };
if (serveOnly)
{
    await using var scope = provider.CreateAsyncScope();
    await using var surface = await desktop.CreateSurfaceAsync(surfaceOptions);
    var model = scope.ServiceProvider.GetRequiredService<RowsViewModel>();
    var transport = new DesktopBridgeTransport(surface);
    using var content = new WindowContentSession(transport, rootModel: model);
    using var attachment = scope.ServiceProvider.GetRequiredService<Func<IBridgeTransport, WindowContentSession, RowsViewModel, IDisposable>>()(transport, content, model);
    Console.WriteLine(surface.Url);
    await Console.In.ReadLineAsync();
    return;
}
await using var window = await provider.OpenDesktopWindowAsync<RowsWindow, RowsViewModel>(desktop, surfaceOptions,
    host => new RowsWindow(host), new DesktopWindowOptions { Width = 900, Height = 760 });
window.Presentation.WaitForClose();
