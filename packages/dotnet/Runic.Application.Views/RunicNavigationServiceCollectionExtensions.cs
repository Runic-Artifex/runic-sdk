using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

/// <summary>Registers the experimental window navigator.</summary>
[Experimental(RunicNavigator.DiagnosticId)]
public static class RunicNavigationServiceCollectionExtensions
{
    /// <summary>
    /// Adds one <see cref="IRunicModelContext"/> per service scope, unless one is registered, so the
    /// window's content session shares it, and one <see cref="RunicNavigator"/> per scope built from that
    /// context, the scope's provider, the <see cref="ILoggerFactory"/> and the <see cref="TimeProvider"/>.
    /// Dispose the scope asynchronously; the navigator is disposed before the context it depends on.
    /// </summary>
    public static IServiceCollection AddRunicNavigation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IRunicModelContext>(provider =>
            new RunicModelContext(provider.GetService<ILogger<RunicModelContext>>()));
        services.TryAddScoped(provider => new RunicNavigator(new RunicNavigatorOptions
        {
            ModelContext = provider.GetRequiredService<IRunicModelContext>(),
            Services = provider,
            LoggerFactory = provider.GetService<ILoggerFactory>(),
            TimeProvider = provider.GetService<TimeProvider>(),
        }));
        return services;
    }
}
