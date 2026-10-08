// Shared by Runic.Navigation.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Navigation.ReactiveUI.Reactive.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
#if SYSTEM_REACTIVE
using ModelScheduler = System.Reactive.Concurrency.IScheduler;
#else
using ModelScheduler = ReactiveUI.Primitives.Concurrency.ISequencer;
#endif

#if SYSTEM_REACTIVE
namespace Runic.Navigation.ReactiveUI.Reactive;
#else
namespace Runic.Navigation.ReactiveUI;
#endif

/// <summary>Registers ReactiveUI scheduling for a scoped Runic model graph.</summary>
public static class ReactiveServiceCollectionExtensions
{
    /// <summary>
    /// Adds a scoped model context, the scheduler provider and the command-output scheduler
    /// of that context. Existing application registrations are preserved.
    /// The scheduler is registered as transient, and the provider returns one scheduler per
    /// context, so every resolution for one context gets the same instance. That also holds
    /// when the context is a singleton, such as a WPF dispatcher context, and a singleton
    /// ViewModel can inject it.
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
        services.TryAddTransient<ModelScheduler>(provider => provider.GetRequiredService<IRunicReactiveSchedulerProvider>()
            .For(provider.GetRequiredService<IRunicModelContext>()));
        return services;
    }
}
