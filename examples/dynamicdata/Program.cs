using DynamicDataExample;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.ReactiveUI;
using Runic.Desktop;

if (args.Contains("--benchmark", StringComparer.Ordinal))
{
    await Benchmarks.RunAsync();
    return 0;
}

var services = new ServiceCollection();
services.AddRunicReactiveModelContext();
services.AddSingleton<RowStore>();
services.AddScoped<RowsViewModel>();
services.AddRunicBridges();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
var serveOnly = args.Contains("--serve-only", StringComparer.Ordinal);
var hostOptions = new DesktopHostOptions
{
    WaitForConnection = !serveOnly,
    Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 },
};
if (serveOnly)
{
    await using var desktop = await DesktopHost.StartAsync(hostOptions);
    var surfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Directory(Path.Combine(AppContext.BaseDirectory, "www"), "index.html") };
    await using var scope = provider.CreateAsyncScope();
    await using var surface = await desktop.CreateSurfaceAsync(surfaceOptions);
    var model = scope.ServiceProvider.GetRequiredService<RowsViewModel>();
    var transport = new DesktopBridgeTransport(surface);
    using var content = new WindowContentSession(transport, rootModel: model);
    using var attachment = scope.ServiceProvider.GetRequiredService<Func<IBridgeTransport, WindowContentSession, RowsViewModel, IDisposable>>()(transport, content, model);
    Console.WriteLine(surface.Url);
    await Console.In.ReadLineAsync();
    return 0;
}
return RunicDesktopHost.Run(provider, hostOptions, async host =>
{
    await using var window = await host.OpenWindowAsync<RowsWindow>(new RunicWindowOptions { Width = 900, Height = 760 });
    await window.WaitForCloseAsync();
    return 0;
});
