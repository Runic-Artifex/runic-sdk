using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReactiveUI.Primitives.Concurrency;

namespace Runic.Application.Views.ReactiveUI;

/// <summary>Registers ReactiveUI scheduling for a scoped Runic model graph.</summary>
public static class ReactiveServiceCollectionExtensions
{
    /// <summary>
    /// Adds one model context and command-output sequencer per service scope,
    /// plus the scheduler provider. Existing application registrations are preserved.
    /// Bind the root and independently presented children to the injected context
    /// with <see cref="RunicModelContextRegistry.Bind"/> before exposing them.
    /// Dispose the service scope asynchronously to drain its owned context.
    /// </summary>
    public static IServiceCollection AddRunicReactiveModelContext(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IRunicModelContext, RunicModelContext>();
        services.TryAddSingleton<IRunicReactiveSchedulerProvider, RunicReactiveSchedulerProvider>();
        services.TryAddScoped<ISequencer>(provider => provider.GetRequiredService<IRunicReactiveSchedulerProvider>()
            .For(provider.GetRequiredService<IRunicModelContext>()));
        return services;
    }
}
