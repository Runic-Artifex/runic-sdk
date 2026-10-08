using System.Diagnostics.CodeAnalysis;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Runic.Navigation.Wpf;

/// <summary>Registers window navigation on the WPF dispatcher.</summary>
[Experimental(RunicNavigator.DiagnosticId)]
public static class RunicWpfNavigationServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="RunicNavigationServiceCollectionExtensions.AddRunicNavigation"/>, then replaces the model context
    /// with one singleton <see cref="DispatcherModelContext"/> and the navigator with one of
    /// <see cref="RunicWpfNavigationOptions.NavigatorLifetime"/>. With <see cref="RunicWpfNavigationOptions.MapView{TViewModel, TView}"/>
    /// or <see cref="RunicWpfNavigationOptions.UseViewNamingConvention"/>, it also registers a singleton
    /// <see cref="INavigationViewLocator"/>, replacing an earlier one.
    /// </summary>
    /// <remarks>
    /// The package assumes one UI thread. Without a configured <see cref="RunicWpfNavigationOptions.Dispatcher"/> and without
    /// <see cref="Application.Current"/>, resolving the model context throws <see cref="InvalidOperationException"/>.
    /// Dispose the provider asynchronously after the dispatcher stops; a synchronous dispose starts the navigator's
    /// disposal without waiting for it.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the options.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentException">
    /// The navigator lifetime is <see cref="ServiceLifetime.Transient"/>, or it is <see cref="ServiceLifetime.Scoped"/> with
    /// <see cref="RunicWpfNavigationOptions.CreateEntryScopes"/> set.
    /// </exception>
    public static IServiceCollection AddRunicWpfNavigation(this IServiceCollection services,
        Action<RunicWpfNavigationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new RunicWpfNavigationOptions();
        configure?.Invoke(options);
        var lifetime = options.NavigatorLifetime;
        if (lifetime is not (ServiceLifetime.Singleton or ServiceLifetime.Scoped))
            throw new ArgumentException("The navigator lifetime must be Singleton or Scoped.", nameof(configure));
        var entryScopes = options.CreateEntryScopes ?? lifetime == ServiceLifetime.Singleton;
        if (entryScopes && lifetime == ServiceLifetime.Scoped)
            throw new ArgumentException(
                "A scoped navigator can't create entry scopes: entry scopes are created from the root provider. Use a singleton navigator, or leave CreateEntryScopes off.",
                nameof(configure));

        var dispatcher = options.Dispatcher;
        var priority = options.Priority;
        services.AddRunicNavigation();
        services.Replace(ServiceDescriptor.Singleton<IRunicModelContext>(provider => new DispatcherModelContext(
            dispatcher ?? Application.Current?.Dispatcher ?? throw new InvalidOperationException(
                "AddRunicWpfNavigation needs Application.Current or RunicWpfNavigationOptions.Dispatcher when the model context is resolved."),
            priority, provider.GetService<ILogger<DispatcherModelContext>>())));
        services.Replace(new ServiceDescriptor(typeof(RunicNavigator), provider => new RunicNavigator(new RunicNavigatorOptions
        {
            ModelContext = provider.GetRequiredService<IRunicModelContext>(),
            Services = provider,
            CreateEntryScopes = entryScopes,
            LoggerFactory = provider.GetService<ILoggerFactory>(),
            TimeProvider = provider.GetService<TimeProvider>(),
        }), lifetime));
        if (options.Views.Count > 0 || options.ViewNamingConvention)
        {
            var locator = new NavigationViewLocator(new Dictionary<Type, Type>(options.Views), options.ViewNamingConvention);
            services.Replace(ServiceDescriptor.Singleton<INavigationViewLocator>(locator));
        }
        return services;
    }
}
