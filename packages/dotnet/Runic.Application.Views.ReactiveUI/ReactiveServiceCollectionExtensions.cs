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
    /// Adds a scoped model context, the scheduler provider and the scheduler of that context, and installs the
    /// model-context main-thread scheduler (<see cref="RunicReactiveSchedulerProvider.InstallMainThreadScheduler"/>),
    /// so ReactiveUI commands created without an output scheduler deliver their notifications on the model context
    /// that executes them. Existing application registrations are preserved.
    /// </summary>
    /// <remarks>
    /// The scheduler is registered as transient, and the provider returns one scheduler per context, so every
    /// resolution for one context gets the same instance. That also holds when the context is a singleton, such as
    /// a WPF dispatcher context, and a singleton ViewModel can inject it.
    /// The CS-WebUI, Desktop and WPF hosts bind a window's root ViewModel to the scoped context, the window binds
    /// each child ViewModel it presents, and a navigator binds the entries it owns, so ViewModels need no
    /// <see cref="RunicModelContextRegistry.Bind"/> call. A ViewModel bound to another context is rejected when
    /// presented. Dispose the service scope asynchronously to drain its owned context.
    /// </remarks>
    public static IServiceCollection AddRunicReactiveModelContext(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IRunicModelContext, RunicModelContext>();
        services.TryAddSingleton<IRunicReactiveSchedulerProvider, RunicReactiveSchedulerProvider>();
        services.TryAddTransient<ModelScheduler>(provider => provider.GetRequiredService<IRunicReactiveSchedulerProvider>()
            .For(provider.GetRequiredService<IRunicModelContext>()));
        RunicReactiveSchedulerProvider.InstallMainThreadScheduler();
        return services;
    }
}
