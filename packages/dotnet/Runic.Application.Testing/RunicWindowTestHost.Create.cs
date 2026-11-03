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
    /// <remarks>
    /// <para>
    /// <paramref name="services"/> needs what the application's window scope has: the ViewModel itself (for example
    /// <c>services.AddScoped&lt;ShellViewModel&gt;()</c>) and its generated Bridge, which the application's generated
    /// <c>AddRunicViews()</c> registers. Build them with the application's own registration method, so the test runs the
    /// same composition. The View locator, model context (for example from <c>AddRunicReactiveModelContext()</c> or
    /// <c>AddRunicNavigation()</c>) and logger factory are used when registered.
    /// </para>
    /// <para>
    /// For a ViewModel the test builds by hand, pass it to
    /// <see cref="Create{TViewModel}(IServiceProvider, TViewModel, RunicWindowTestHostOptions?)"/> instead of registering it.
    /// </para>
    /// </remarks>
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
                $"with services.AddScoped<{typeof(TViewModel).Name}>(), or pass a ViewModel you built to " +
                "RunicWindowTestHost.Create(services, viewModel).");
        return Create(services, viewModel, options, loggerFactory);
    }

    /// <summary>
    /// Creates a test host for a ViewModel the test built, with its generated Bridge attachment, the View locator, the
    /// model context and the logger factory from <paramref name="services"/>, as
    /// <see cref="Create{TViewModel}(IServiceProvider, RunicWindowTestHostOptions?)"/> does. The ViewModel does not need
    /// to be registered.
    /// </summary>
    /// <remarks>
    /// <code>
    /// await using var services = new ServiceCollection().AddRunicViews().BuildServiceProvider();
    /// var shell = new ShellViewModel(new InMemoryNotes());
    /// using var host = RunicWindowTestHost.Create(services, shell);
    /// </code>
    /// The MVVM adapters receive the logger factory before this call returns, but after the test built the ViewModel,
    /// so a ReactiveUI command it created earlier does not report a scheduler that is not Runic's.
    /// </remarks>
    /// <param name="services">The services with the application's generated <c>AddRunicViews()</c> registrations.</param>
    /// <param name="viewModel">The root ViewModel; the caller keeps ownership.</param>
    /// <param name="options">The root route, clock and id source, as for the other overload.</param>
    /// <exception cref="InvalidOperationException">No generated Bridge is registered for <typeparamref name="TViewModel"/>.</exception>
    public static RunicWindowTestHost<TViewModel> Create<TViewModel>(IServiceProvider services, TViewModel viewModel,
        RunicWindowTestHostOptions? options = null) where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(viewModel);
        options ??= new RunicWindowTestHostOptions();
        var loggerFactory = options.LoggerFactory ?? services.GetService<ILoggerFactory>();
        RunicWindowOpeningObservers.Notify(services, loggerFactory);
        return Create(services, viewModel, options, loggerFactory);
    }

    /// <summary>
    /// Opens <typeparamref name="TWindow"/> headless, as a host opens it: in a new scope of <paramref name="services"/>,
    /// with its close guard, and with the application's <see cref="IRunicWindowService"/> bound to the test, so the
    /// Windows its ViewModels open, find, activate and open for a result are test Windows too.
    /// </summary>
    /// <remarks>
    /// <code>
    /// await using var services = new ServiceCollection().AddMyApplication().BuildServiceProvider();
    /// var editor = await RunicWindowTestHost.CreateWindowAsync&lt;EditorWindow&gt;(services);
    /// await editor.Root&lt;EditorViewModel&gt;().SetAsync(x =&gt; x.Text, "unsaved");
    /// Assert.False(await editor.RequestUserCloseAsync());   // CanCloseAsync kept it open
    /// </code>
    /// <para>
    /// <paramref name="services"/> is the application's root provider, built with its own registration method: the
    /// Windows' ViewModels, the generated <c>AddRunicViews()</c> and <c>AddRunicWindowService()</c>. Each Window opens
    /// in its own scope, which the test host releases when the Window is released. Every Window shares one clock (a
    /// <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/> unless <paramref name="hostOptions"/> name another)
    /// and one id source. Each Window gets a <see cref="RunicTestWindowPresentation"/>, or the one its scope registers.
    /// </para>
    /// <para>
    /// Dispose the result to close and release every Window. <see cref="RunicTestWindow.OpenedWindows"/> lists the
    /// Windows opened after it and <see cref="RunicTestWindow.WaitForOpenedWindowAsync{TWindow}"/> waits for one.
    /// </para>
    /// </remarks>
    /// <param name="services">The application's root services.</param>
    /// <param name="options">The Window's options, as for <c>OpenWindowAsync</c>.</param>
    /// <param name="hostOptions">The clock, id source, View locator, model context and logger factory of every Window.</param>
    /// <param name="cancellationToken">Cancels opening.</param>
    /// <returns>The open Window.</returns>
    /// <exception cref="RunicWindowConfigurationException">The Window's ViewModel or Bridge is not registered.</exception>
    public static async Task<RunicTestWindow> CreateWindowAsync<TWindow>(IServiceProvider services, RunicWindowOptions? options = null,
        RunicWindowTestHostOptions? hostOptions = null, CancellationToken cancellationToken = default)
        where TWindow : class, IRunicWindow, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        var application = new RunicTestWindowApplication(services, hostOptions ?? new RunicWindowTestHostOptions());
        try
        {
            var window = await RunicWindowHostExtensions.OpenWindowAsync<TWindow>(application, options, cancellationToken)
                .ConfigureAwait(false);
            return application.Find(window);
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static RunicWindowTestHost<TViewModel> Create<TViewModel>(IServiceProvider services, TViewModel viewModel,
        RunicWindowTestHostOptions options, ILoggerFactory? loggerFactory) where TViewModel : class
    {
        var attach = services.GetService<Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable>>()
            ?? throw new InvalidOperationException(
                $"No generated Bridge is registered for {typeof(TViewModel).FullName}. Call the application's " +
                "generated AddRunicViews() on the services, and declare a Runic Window for the ViewModel.");
        var host = new RunicWindowTestHost<TViewModel>(viewModel, attach, new RunicWindowTestHostOptions
        {
            RootRoute = options.RootRoute,
            ViewLocator = options.ViewLocator ?? services.GetService<IRunicViewLocator>(),
            OperationShutdown = options.OperationShutdown,
            ModelContext = options.ModelContext ?? services.GetService<IRunicModelContext>(),
            LoggerFactory = loggerFactory,
            TimeProvider = options.TimeProvider,
            CreateId = options.CreateId,
            IncludeFailureDetail = options.IncludeFailureDetail,
            MaximumInputBytes = options.MaximumInputBytes,
            MaximumUploadBytes = options.MaximumUploadBytes,
            PublicationTimeout = options.PublicationTimeout,
        })
        { Services = services, Platform = services.GetService<RunicTestPlatform>() };
        // IRunicConnections of these services follows the test window, as OpenWindowAsync binds a Window scope's.
        // A root provider that validates scopes refuses the scoped service; such services have no window scope to bind.
        try { (services.GetService(typeof(RunicConnections)) as RunicConnections)?.Bind(host.Content); }
        catch (InvalidOperationException) { }
        return host;
    }
}
