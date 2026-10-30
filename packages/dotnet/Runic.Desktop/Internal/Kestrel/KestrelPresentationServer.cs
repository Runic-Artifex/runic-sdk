using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Runic.Desktop.Internal.Kestrel;

internal sealed class KestrelPresentationServer(Action<IServiceCollection>? configureServices) : IPresentationServer
{
    [FeatureSwitchDefinition("Runic.Desktop.MinimalHost")]
    private static bool MinimalHost => AppContext.TryGetSwitch("Runic.Desktop.MinimalHost", out bool enabled) && enabled;

    private WebApplication? _application;

    public IServiceProvider Services =>
        _application?.Services ?? throw new InvalidOperationException("The presentation server has not started.");

    // ILLink 10.0 (dotnet/runtime#135913, fixed after 10.0.14) keeps X509Certificate2Collection's
    // IEnumerable<X509Certificate2>, reached through Kestrel's configuration loader, but strips the
    // interfaces and GetEnumerator of its uninstantiated base CollectionBase. A trimmed application then
    // fails to load the type with a TypeLoadException when Kestrel binds. NativeAOT is not affected.
    [DynamicDependency(DynamicallyAccessedMemberTypes.Interfaces, typeof(System.Collections.CollectionBase))]
    public async Task<int> StartAsync(IPAddress address, int port, Func<PresentationExchange, Task> dispatch, CancellationToken cancellationToken)
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
        application.Run(context => dispatch(new HttpContextExchange(context)));
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

// The application, not its embedded presentation server, owns process signals.
internal sealed class ProcessOwnedLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
