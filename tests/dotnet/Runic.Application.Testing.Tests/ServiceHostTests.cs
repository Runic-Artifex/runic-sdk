using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// RunicWindowTestHost.Create resolves what the Desktop and CS-WebUI windows resolve
// from the window's scope, so a test needs no service lookups of its own.
internal sealed class ServiceHostTests
{
    [Test]
    public async Task ReactiveWindowSharesTheScopesModelContextAsync()
    {
        var services = new ServiceCollection();
        services.AddRunicReactiveModelContext();
        services.AddScoped<ContextShellViewModel>();
        services.AddScoped<ContextPageViewModel>();
        services.AddScoped<IRunicViewLocator, ContextPageViewLocator>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, ContextShellViewModel, IDisposable>>(
            _ => (transport, content, vm) => new ContextShellBridge(transport, vm, content: content));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();

        using var host = RunicWindowTestHost.Create<ContextShellViewModel>(scope.ServiceProvider);

        var shell = scope.ServiceProvider.GetRequiredService<ContextShellViewModel>();
        var context = scope.ServiceProvider.GetRequiredService<IRunicModelContext>();
        Require(ReferenceEquals(host.ViewModel, shell), "The host did not resolve the scope's root ViewModel.");
        Require(host.RootRoute == "contextShell" && host.Time is FakeTimeProvider,
            "The host did not derive its route or use the fake clock.");
        Require(RunicModelContextRegistry.Shared.TryGet(shell, out var bound) && ReferenceEquals(bound, context),
            "The host did not give the window the scope's model context.");
        (await host.Root.ExecuteAsync(vm => vm.OpenCommand)).ThrowIfFailed();
        var page = host.Root.View<ContextPageViewModel>(vm => vm.Main);
        (await page.ExecuteAsync(vm => vm.IncrementCommand)).ThrowIfFailed();
        Require(page.Snapshot().Read(vm => vm.Count) == 1, "A command on the scope's model context did not run.");
        shell.Main?.Dispose();
    }

    [Test]
    [DependsOn<WindowViewHostTests>(nameof(WindowViewHostTests.GeneratedRootHostPresentsAndReleasesEachBrowserViewAsync), ProceedOnFailure = true)]
    public async Task ToolkitWindowUsesTheRegisteredViewLocatorAsync()
    {
        var locator = new CountingViewLocator();
        await using var provider = ToolkitServices(locator).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        using var host = RunicWindowTestHost.Create<RootViewModel>(scope.ServiceProvider);

        host.Root.View<ChildViewModel>(vm => vm.Child).Mount();
        Require(locator.Located == 1, "The host did not present content through the registered View locator.");
        Require(scope.ServiceProvider.GetService<IRunicModelContext>() is null, "The toolkit window unexpectedly has a context.");
        await host.Root.ExecuteAsync(vm => vm.IncrementCommand);
        Require(host.ViewModel.Count == 1, "The resolved ViewModel's command did not run.");
    }

    [Test]
    [DependsOn<WindowViewHostTests>(nameof(WindowViewHostTests.GeneratedRootHostPresentsAndReleasesEachBrowserViewAsync), ProceedOnFailure = true)]
    public void ExplicitOptionsWinOverRegistrations()
    {
        var registered = new CountingViewLocator();
        var explicitLocator = new CountingViewLocator();
        using var provider = ToolkitServices(registered).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var clock = new FakeTimeProvider();

        using var host = RunicWindowTestHost.Create<RootViewModel>(scope.ServiceProvider, new RunicWindowTestHostOptions
        {
            RootRoute = "root",
            ViewLocator = explicitLocator,
            TimeProvider = clock,
            CreateId = () => "only",
        });

        host.Root.View<ChildViewModel>(vm => vm.Child).Mount();
        Require(explicitLocator.Located == 1 && registered.Located == 0, "The explicit View locator was not used.");
        Require(ReferenceEquals(host.Time, clock), "The explicit clock was not used.");
        Require(host.Root.Snapshot().Reference(vm => vm.Child)?.Id == "only", "The explicit id source was not used.");
    }

    [Test]
    public void MissingRegistrationsNameTheFix()
    {
        using var empty = new ServiceCollection().BuildServiceProvider();
        var missingModel = Catch(() => RunicWindowTestHost.Create<RootViewModel>(empty));
        Require(missingModel is InvalidOperationException && missingModel.Message.Contains(typeof(RootViewModel).FullName!, StringComparison.Ordinal)
            && missingModel.Message.Contains("AddScoped<RootViewModel>", StringComparison.Ordinal),
            $"A missing ViewModel did not name the registration: {missingModel}");

        using var withoutBridge = new ServiceCollection().AddScoped<RootViewModel>().BuildServiceProvider();
        using var scope = withoutBridge.CreateScope();
        var missingBridge = Catch(() => RunicWindowTestHost.Create<RootViewModel>(scope.ServiceProvider));
        Require(missingBridge is InvalidOperationException && missingBridge.Message.Contains("AddRunicViews()", StringComparison.Ordinal),
            $"A missing generated Bridge did not name AddRunicViews(): {missingBridge}");
        Require(Catch(() => RunicWindowTestHost.Create<RootViewModel>(null!)) is ArgumentNullException,
            "A null service provider was accepted.");
    }


    // W260-015: Create passes the application's ILoggerFactory to the ReactiveUI model-context scheduler, as the
    // Desktop and CS-WebUI windows do, so work scheduled outside a model turn reaches the test's logs as event 1043.
    [Test]
    public async Task CreatePassesTheLoggerFactoryToTheReactiveSchedulerAsync()
    {
        var logs = new SchedulerLogCapture();
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(logs);
        services.AddRunicReactiveModelContext();
        services.AddScoped<ContextShellViewModel>();
        services.AddScoped<ContextPageViewModel>();
        services.AddScoped<IRunicViewLocator, ContextPageViewLocator>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, ContextShellViewModel, IDisposable>>(
            _ => (transport, content, vm) => new ContextShellBridge(transport, vm, content: content));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        Require(ReactiveSchedulers.InstallMainThreadScheduler(), "The model-context scheduler is not installed.");

        using (RunicWindowTestHost.Create<ContextShellViewModel>(scope.ServiceProvider))
            await TestHostOffTurnViewModel.PushOutsideTurnAsync();
        Require(logs.Entries.Count(static entry => entry is { Id: 1043, Level: Microsoft.Extensions.Logging.LogLevel.Warning }
                && entry.Source == typeof(TestHostOffTurnViewModel).FullName) == 1,
            $"Event 1043 did not reach the test's logs as a Warning: {string.Join(", ", logs.Entries)}");
        scope.ServiceProvider.GetRequiredService<ContextShellViewModel>().Main?.Dispose();
    }

    private sealed record SchedulerLogEntry(int Id, Microsoft.Extensions.Logging.LogLevel Level, string? Source);

    private sealed class SchedulerLogCapture : Microsoft.Extensions.Logging.ILoggerFactory, Microsoft.Extensions.Logging.ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<SchedulerLogEntry> Entries { get; } = new();
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) => throw new NotSupportedException();
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;
        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(new(eventId.Id, logLevel,
                (state as IEnumerable<KeyValuePair<string, object?>>)?.FirstOrDefault(static pair => pair.Key == "Source").Value as string));
    }
    private static ServiceCollection ToolkitServices(IRunicViewLocator locator)
    {
        var services = new ServiceCollection();
        services.AddScoped<RootViewModel>();
        services.AddSingleton(locator);
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, RootViewModel, IDisposable>>(
            _ => (transport, content, vm) => new RootBridge(transport, vm, content: content));
        return services;
    }

    private static Exception? Catch(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CountingViewLocator : IRunicViewLocator
    {
        public int Located { get; private set; }

        public TView Locate<TView, TViewModel>() where TView : class, IRunicView where TViewModel : class
        {
            Located++;
            return new TestViewLocator().Locate<TView, TViewModel>();
        }
    }
}
