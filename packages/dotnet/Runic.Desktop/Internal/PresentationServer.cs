using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Runic.Desktop.Internal.NativeServer;

namespace Runic.Desktop.Internal;

// The listener under a PresentationHostCore: it binds one address, hands every request to the host core's dispatch and
// owns the services the surfaces' scopes come from. Kestrel is the default; the experimental native server (X1)
// replaces it when the application references Runic.Desktop.NativeServer, which sets the Runic.Desktop.NativeServer
// runtime switch. The switch is a trimming feature switch, so a trimmed or NativeAOT application keeps one of the two.
// Requests reach the host core as a PresentationExchange, so nothing above this seam depends on either server's types.
internal interface IPresentationServer : IAsyncDisposable
{
    IServiceProvider Services { get; }

    // Binds the listener and starts dispatching. Returns the bound port.
    Task<int> StartAsync(IPAddress address, int port, Func<PresentationExchange, Task> dispatch, CancellationToken cancellationToken);
}

internal static class PresentationServers
{
    [FeatureSwitchDefinition("Runic.Desktop.NativeServer")]
    internal static bool UseNativeServer =>
        AppContext.TryGetSwitch("Runic.Desktop.NativeServer", out var enabled) && enabled;

    internal static IPresentationServer Create(Action<IServiceCollection>? configureServices) =>
        UseNativeServer
            ? new NativePresentationServer(configureServices)
            : new Kestrel.KestrelPresentationServer(configureServices);
}
