using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Runic.Navigation;

/// <summary>Registers the window navigator.</summary>
public static class RunicNavigationServiceCollectionExtensions
{
    /// <summary>
    /// Adds one <see cref="IRunicModelContext"/> per service scope, unless one is registered, so the
    /// window's content session shares it, and one <see cref="RunicNavigator"/> per scope built from that
    /// context, the scope's provider, the <see cref="ILoggerFactory"/> and the <see cref="TimeProvider"/>.
    /// Dispose the scope asynchronously; the navigator is disposed before the context it depends on.
    /// It also registers <see cref="NavigationEntryContext"/> so that resolving it from the container, for example as a
    /// constructor parameter of a service, throws an <see cref="InvalidOperationException"/> that explains where it is
    /// available: only content the navigator creates receives its entry's context.
    /// </summary>
    public static IServiceCollection AddRunicNavigation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddTransient<NavigationEntryContext>(static _ => throw new InvalidOperationException(
            "NavigationEntryContext can't be resolved from the container. Only content that the navigator constructs receives it, "
            + "as a constructor parameter of NavigationTarget.Create<T>() and PushAsync<T>, ReplaceAsync<T>, ResetAsync<T> and "
            + "PushForResult<T, TResult>, or as the second argument of a NavigationTarget.Create(factory). Pass it on from that "
            + "content to the objects that need it. "
            + "Content created with NavigationTarget.Own or Borrow receives it in INavigationInitialize.InitializeAsync."));
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
