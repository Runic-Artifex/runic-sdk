using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Runic.Desktop.Internal.NativeServer;

namespace Runic.Desktop.Internal;

// The listener under a PresentationHostCore: it binds one address, hands every request to the host core's dispatch and
// owns the services the surfaces' scopes come from. Kestrel is the default; the experimental native server (X1)
// replaces it when the application references Runic.Desktop.NativeServer, which sets the Runic.Desktop.NativeServer
// runtime switch. The switch is a trimming feature switch, so a trimmed or NativeAOT application keeps one of the two.
internal interface IPresentationServer : IAsyncDisposable
{
    IServiceProvider Services { get; }

    // Binds the listener and starts dispatching. Returns the bound port.
    Task<int> StartAsync(IPAddress address, int port, RequestDelegate dispatch, CancellationToken cancellationToken);
}

internal static class PresentationServers
{
    [FeatureSwitchDefinition("Runic.Desktop.NativeServer")]
    internal static bool UseNativeServer =>
        AppContext.TryGetSwitch("Runic.Desktop.NativeServer", out var enabled) && enabled;

    internal static IPresentationServer Create(Action<IServiceCollection>? configureServices) =>
        UseNativeServer
            ? new NativePresentationServer(configureServices)
            : new KestrelPresentationServer(configureServices);
}

internal sealed class KestrelPresentationServer(Action<IServiceCollection>? configureServices) : IPresentationServer
{
    [FeatureSwitchDefinition("Runic.Desktop.MinimalHost")]
    private static bool MinimalHost => AppContext.TryGetSwitch("Runic.Desktop.MinimalHost", out bool enabled) && enabled;

    private WebApplication? _application;

    public IServiceProvider Services =>
        _application?.Services ?? throw new InvalidOperationException("The presentation server has not started.");

    public async Task<int> StartAsync(IPAddress address, int port, RequestDelegate dispatch, CancellationToken cancellationToken)
    {
        // The server reads no files. The default content root is the working directory,
        // often the user's home, and reloading configuration from it would put a recursive
        // file watcher (one inotify watch per directory on Linux) on that tree.
        var hostOptions = new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = ["--hostBuilder:reloadConfigOnChange=false"],
        };
        var builder = MinimalHost
            ? WebApplication.CreateEmptyBuilder(hostOptions)
            : WebApplication.CreateSlimBuilder(hostOptions);
        if (MinimalHost) builder.WebHost.UseKestrelCore().UseSockets();
        builder.Logging.ClearProviders();
        // The default console lifetime cancels SIGINT, SIGQUIT and SIGTERM and only
        // stops this embedded server, so the application would ignore them.
        builder.Services.AddSingleton<IHostLifetime, ProcessOwnedLifetime>();
        configureServices?.Invoke(builder.Services);
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, port));

        var application = builder.Build();
        application.UseWebSockets();
        application.Run(dispatch);
        await application.StartAsync(cancellationToken).ConfigureAwait(false);

        var addresses = application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        var bound = addresses?.SingleOrDefault()
            ?? throw new InvalidOperationException("Kestrel did not publish a listening address.");
        _application = application;
        return new Uri(bound, UriKind.Absolute).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is not { } application)
        {
            return;
        }
        _application = null;
        try
        {
            await application.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await application.DisposeAsync().ConfigureAwait(false);
        }
    }
}
