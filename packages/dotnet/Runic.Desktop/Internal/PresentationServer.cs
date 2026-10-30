using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Runic.Desktop.Internal.NativeServer;

namespace Runic.Desktop.Internal;

// The listener under a PresentationHostCore: it binds one address, hands every request to the host core's dispatch and
// owns the services the surfaces' scopes come from. Runic.Desktop carries no server of its own and no ASP.NET Core
// reference (X2): the application references one server package. Runic.Desktop.NativeServer sets the
// Runic.Desktop.NativeServer runtime switch; Runic.Desktop.Kestrel is ASP.NET Core Kestrel, found by name so that
// Runic.Desktop does not reference it. The switch is a trimming feature switch, so a trimmed or NativeAOT application
// keeps one server. Requests reach the host core as a PresentationExchange, so nothing above this seam depends on
// either server's types.
internal interface IPresentationServer : IAsyncDisposable
{
    IServiceProvider Services { get; }

    // Binds the listener and starts dispatching. Returns the bound port.
    Task<int> StartAsync(IPAddress address, int port, Func<PresentationExchange, Task> dispatch, CancellationToken cancellationToken);
}

// Implemented by a server assembly that Runic.Desktop loads by name.
internal abstract class PresentationServerFactory
{
    internal abstract IPresentationServer Create(Action<IServiceCollection>? configureServices);
}

internal static class PresentationServers
{
    internal const string KestrelFactoryType = "Runic.Desktop.Kestrel.KestrelPresentationServerFactory, Runic.Desktop.Kestrel";

    [FeatureSwitchDefinition("Runic.Desktop.NativeServer")]
    internal static bool UseNativeServer =>
        AppContext.TryGetSwitch("Runic.Desktop.NativeServer", out var enabled) && enabled;

    internal static IPresentationServer Create(Action<IServiceCollection>? configureServices) =>
        UseNativeServer
            ? new NativePresentationServer(configureServices)
            : CreateKestrel(configureServices);

    // The factory's implicit constructor is public (its class is internal).
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, "Runic.Desktop.Kestrel.KestrelPresentationServerFactory", "Runic.Desktop.Kestrel")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The factory's constructor is kept by the DynamicDependency above.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "The factory's constructor is kept by the DynamicDependency above.")]
    // An application without Runic.Desktop.Kestrel (RunicDesktopServer=None) has no assembly to resolve; the lookup then
    // reports presentation-server-unavailable.
    [UnconditionalSuppressMessage("Trimming", "IL2035", Justification = "Runic.Desktop.Kestrel is optional.")]
    private static IPresentationServer CreateKestrel(Action<IServiceCollection>? configureServices)
    {
        Type? type;
        try
        {
            type = Type.GetType(KestrelFactoryType, throwOnError: false);
        }
        catch (Exception exception) when (exception is FileLoadException or BadImageFormatException)
        {
            type = null;
        }
        if (type is null || Activator.CreateInstance(type) is not PresentationServerFactory factory)
        {
            throw new DesktopException(
                DesktopErrorCategory.Unavailable,
                "presentation-server-unavailable",
                "Runic Desktop has no presentation server. Reference the Runic.Desktop.Kestrel package (ASP.NET Core Kestrel) " +
                "or the Runic.Desktop.NativeServer package in the application (docs/migration-1.0.md).",
                retryable: false);
        }
        return factory.Create(configureServices);
    }
}
