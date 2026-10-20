using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Navigation;

namespace Runic.Application.Testing;

/// <summary>Creates a <see cref="RunicWindowTestHost{TViewModel}"/> from the application's services.</summary>
public static class RunicWindowTestHost
{
    /// <summary>
    /// Resolves the root ViewModel, its generated Bridge attachment, the View locator, the
    /// model context and the logger factory from <paramref name="services"/>, as the Desktop
    /// and CS-WebUI windows do, and creates a test host for them. By default the window uses
    /// a fake clock and sequential ids. Before it resolves the ViewModel it passes the logger factory to the
    /// registered MVVM adapters, as those windows do, so the ReactiveUI model-context scheduler's warnings reach the
    /// test's logs.
    /// </summary>
    /// <param name="services">
    /// The window's service scope, such as <c>scope.ServiceProvider</c> of a scope the test
    /// creates. The caller keeps ownership of the scope and the ViewModels it resolves.
    /// </param>
    /// <param name="options">
    /// The root route, clock and id source. A View locator, model context or logger factory
    /// set here is used instead of the registered one.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TViewModel"/> or its generated Bridge is not registered.
    /// </exception>
    public static RunicWindowTestHost<TViewModel> Create<TViewModel>(IServiceProvider services,
        RunicWindowTestHostOptions? options = null) where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new RunicWindowTestHostOptions();
        var loggerFactory = options.LoggerFactory ?? services.GetService<ILoggerFactory>();
        // As the Desktop and CS-WebUI hosts do, before the ViewModel creates its commands.
        RunicWindowOpeningObservers.Notify(services, loggerFactory);
        var viewModel = services.GetService<TViewModel>()
            ?? throw new InvalidOperationException(
                $"{typeof(TViewModel).FullName} is not registered. Register it as the application does, for example " +
                $"with services.AddScoped<{typeof(TViewModel).Name}>().");
        var attach = services.GetService<Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable>>()
            ?? throw new InvalidOperationException(
                $"No generated Bridge is registered for {typeof(TViewModel).FullName}. Call the application's " +
                "generated AddRunicViews() on the services, and declare a Runic Window for the ViewModel.");
        return new RunicWindowTestHost<TViewModel>(viewModel, attach, new RunicWindowTestHostOptions
        {
            RootRoute = options.RootRoute,
            ViewLocator = options.ViewLocator ?? services.GetService<IRunicViewLocator>(),
            OperationShutdown = options.OperationShutdown,
            ModelContext = options.ModelContext ?? services.GetService<IRunicModelContext>(),
            LoggerFactory = loggerFactory,
            TimeProvider = options.TimeProvider,
            CreateId = options.CreateId,
            IncludeFailureDetail = options.IncludeFailureDetail,
            PublicationTimeout = options.PublicationTimeout,
        });
    }
}
