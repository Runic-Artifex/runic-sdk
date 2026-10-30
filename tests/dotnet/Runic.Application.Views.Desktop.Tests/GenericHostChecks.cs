using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using TUnit.Core;

// I-208 (D042): Runic has no configuration API; an application configures itself with Generic Host and hands its
// services to RunicDesktopHost.Run. This is the setup of docs/configuration.md: the content root is the application's
// directory, configuration files are not watched (#606), Runic Desktop keeps Ctrl+C and SIGTERM, hosted services run
// inside the host callback, StopApplication closes the Window, and the host is disposed after Run returns.
internal sealed class GenericHostChecks
{
    [Test, NotInParallel(Order = 8)]
    public async Task GenericHostServicesDriveTheDesktopHost()
    {
        var events = new List<string>();
        var settings = new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            Configuration = new ConfigurationManager(),
        };
        settings.Configuration.AddInMemoryCollection([new("hostBuilder:reloadConfigOnChange", "false")]);
        var builder = Host.CreateApplicationBuilder(settings);
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostLifetime, DesktopLifetime>();
        builder.Services.Configure<GreetingOptions>(builder.Configuration.GetSection("Greeting"));
        builder.Services.AddSingleton(events);
        builder.Services.AddHostedService<HostedProbe>();
        builder.Services.AddScoped<OrderedCollectionModel>();
        builder.Services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
            _ => (transport, model) => new OrderedCollectionBridge(transport, model));
        var host = builder.Build();

        var environment = host.Services.GetRequiredService<IHostEnvironment>();
        Require(environment.ContentRootPath == AppContext.BaseDirectory,
            $"The content root is '{environment.ContentRootPath}', not the application directory '{AppContext.BaseDirectory}'.");
        Require(host.Services.GetRequiredService<IOptions<GreetingOptions>>().Value.Text == "from appsettings.json",
            "appsettings.json next to the application was not read.");
        var files = ((IConfigurationRoot)host.Services.GetRequiredService<IConfiguration>()).Providers
            .OfType<FileConfigurationProvider>().ToList();
        Require(files.Count > 0 && files.All(static file => !file.Source.ReloadOnChange),
            $"A configuration file is watched for changes: {string.Join(", ", files.Select(static file => file.Source.Path))}.");
        Require(host.Services.GetRequiredService<IHostLifetime>() is DesktopLifetime,
            "Generic Host's ConsoleLifetime would cancel Ctrl+C and SIGTERM, which Runic Desktop handles.");

        var factory = new NativeDispatchFactory();
        var result = await Task.Run(() => RunicDesktopHost.Run(host.Services,
            new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false, HandleShutdownSignals = false },
            new RunicDesktopHostOptions
            {
                SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
                WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
            },
            async runic =>
            {
                await host.StartAsync();
                try
                {
                    await using var window = await runic.OpenWindowAsync<ProbeWindow>();
                    events.Add("window opened");
                    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
                    using var stopping = lifetime.ApplicationStopping.Register(() => _ = window.CloseAsync(TimeSpan.FromSeconds(5)).AsTask());
                    // A hosted service, or anything else holding IHostApplicationLifetime, asks the application to stop.
                    _ = Task.Run(lifetime.StopApplication);
                    await window.WaitForCloseAsync().AsTask().WaitScaledAsync(TimeSpan.FromSeconds(10));
                    events.Add("window closed");
                    return 3;
                }
                finally
                {
                    await host.StopAsync();
                }
            }));
        Require(result == 3, $"Run returned {result} instead of the application's result.");
        Require(events.SequenceEqual(["started", "window opened", "window closed", "stopped"]),
            $"The hosted service and the Window ran in the order {string.Join(", ", events)}.");
        host.Dispose();
        Require(events[^1] == "disposed", "Disposing the host after Run returned did not dispose the hosted service.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class GreetingOptions
    {
        public string Text { get; set; } = "";
    }

    // Starting and stopping belong to the host callback; Runic Desktop owns Ctrl+C, SIGTERM and SIGHUP.
    private sealed class DesktopLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class HostedProbe(List<string> events) : IHostedService, IDisposable
    {
        public Task StartAsync(CancellationToken cancellationToken) { events.Add("started"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { events.Add("stopped"); return Task.CompletedTask; }
        public void Dispose() => events.Add("disposed");
    }
}
