using System;
using Microsoft.Extensions.DependencyInjection;

namespace Runic.Application;

/// <summary>Provides the bridge session factory emitted with the generated application composition.</summary>
public static class RunicApplicationBridgeCompositionRegistry
{
    private static Action<IServiceCollection>? _configureServices;
    private static Func<IServiceProvider, object>? _createSession;

    /// <summary>Registers the application's generated bridge-session factory.</summary>
    public static void Register(
        Action<IServiceCollection> configureServices,
        Func<IServiceProvider, object> createSession)
    {
        ArgumentNullException.ThrowIfNull(configureServices);
        ArgumentNullException.ThrowIfNull(createSession);
        if (System.Threading.Interlocked.CompareExchange(ref _createSession, createSession, null) is not null)
        {
            throw new InvalidOperationException("The application bridge composition has already been registered.");
        }
        _configureServices = configureServices;
    }

    internal static void ConfigureServices(IServiceCollection services) =>
        System.Threading.Volatile.Read(ref _configureServices)?.Invoke(services);

    /// <summary>Creates the generated bridge session, if the application declared one.</summary>
    public static object? CreateSession(IServiceProvider services) => _createSession?.Invoke(services);
}
