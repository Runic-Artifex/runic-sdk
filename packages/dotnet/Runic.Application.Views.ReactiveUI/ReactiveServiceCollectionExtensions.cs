// Shared by Runic.Application.Views.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.Views.ReactiveUI.Reactive.
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
    /// model-context main-thread scheduler (<see cref="ReactiveSchedulers.InstallMainThreadScheduler"/>),
    /// so ReactiveUI commands created without an output scheduler deliver their notifications on the model context
    /// that executes them. Existing application registrations are preserved. To opt out, set
    /// <c>RxSchedulers.MainThreadScheduler</c>, or call <c>WithMainThreadScheduler</c> on ReactiveUI's builder, before
    /// <c>AddRunicReactiveModelContext()</c>, or replace it afterwards.
    /// </summary>
    /// <remarks>
    /// The scheduler is registered as transient, and the provider returns one scheduler per context, so every
    /// resolution for one context gets the same instance. That also holds when the context is a singleton, such as
    /// a WPF dispatcher context, and a singleton ViewModel can inject it.
    /// The CS-WebUI, Desktop and WPF hosts bind a window's root ViewModel to the scoped context, the window binds
    /// each child ViewModel it presents, and a navigator binds the entries it owns, so ViewModels need no
    /// <see cref="RunicModelContextRegistry.Bind"/> call. A ViewModel bound to another context is rejected when
    /// presented. Dispose the service scope asynchronously to drain its owned context.
    /// An application that builds ReactiveUI with <c>RxAppBuilder</c> also calls
    /// <see cref="ReactiveUIBuilderExtensions.WithRunicModelContext"/> there, so its <c>BuildApp()</c> keeps the
    /// scheduler. When a Runic Desktop, CS-WebUI or test-host window opens, the installed scheduler logs through the
    /// application's <see cref="Microsoft.Extensions.Logging.ILoggerFactory"/>, and a scheduler that was kept or that
    /// replaced it is logged as Views event 1090 or 1091.
    /// </remarks>
    public static IServiceCollection AddRunicReactiveModelContext(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IRunicModelContext, RunicModelContext>();
        services.TryAddSingleton<IReactiveSchedulerProvider>(ReactiveSchedulerProvider.Instance);
        services.TryAddTransient<ModelScheduler>(provider => provider.GetRequiredService<IReactiveSchedulerProvider>()
            .For(provider.GetRequiredService<IRunicModelContext>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRunicWindowOpeningObserver, ReactiveSchedulerWindowObserver>());
        ReactiveSchedulers.InstallMainThreadScheduler();
        return services;
    }
}
