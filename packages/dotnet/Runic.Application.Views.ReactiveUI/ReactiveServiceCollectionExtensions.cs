// Shared by Runic.Application.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.ReactiveUI.Reactive.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Runic.Navigation;
#if SYSTEM_REACTIVE
using ModelScheduler = System.Reactive.Concurrency.IScheduler;
#else
using ModelScheduler = ReactiveUI.Primitives.Concurrency.ISequencer;
#endif

#if SYSTEM_REACTIVE
namespace Runic.Application.Views.ReactiveUI.Reactive;
#else
namespace Runic.Application.Views.ReactiveUI;
#endif

/// <summary>Registers ReactiveUI scheduling for a scoped Runic model graph.</summary>
public static class ReactiveServiceCollectionExtensions
{
    /// <summary>
    /// Adds one model context and command-output scheduler per service scope,
    /// plus the scheduler provider. Existing application registrations are preserved.
    /// The CS-WebUI and Desktop hosts bind a window's root ViewModel to the scoped
    /// context; bind independently presented children to it with
    /// <see cref="RunicModelContextRegistry.Bind"/> before exposing them.
    /// Dispose the service scope asynchronously to drain its owned context.
    /// </summary>
    public static IServiceCollection AddRunicReactiveModelContext(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IRunicModelContext, RunicModelContext>();
        services.TryAddSingleton<IRunicReactiveSchedulerProvider, RunicReactiveSchedulerProvider>();
        services.TryAddScoped<ModelScheduler>(provider => provider.GetRequiredService<IRunicReactiveSchedulerProvider>()
            .For(provider.GetRequiredService<IRunicModelContext>()));
        return services;
    }
}
