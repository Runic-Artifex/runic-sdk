using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// D018.3: one Window class and one open API for every host. These run the host-neutral half of
// OpenWindowAsync and ValidateWindow against a recording host; the Desktop and CS-WebUI adapters
// have their own checks (tests/dotnet/Runic.Application.Views.Desktop.Tests, cswebui-window-probes).
// The former runner's order: HostProbeViewModel.Created and ScopeProbe.Live are static, Services() resets them, and
// the validation case, which builds its own provider, relies on the earlier cases having reset them.
internal sealed class RunicWindowHostTests
{
    [Test, NotInParallel(Order = 1)]
    public async Task OpenResolvesTheViewModelInANewScopeAndAssignsIt()
    {
        await using var provider = Services(bridge: true).BuildServiceProvider();
        var host = new RecordingHost(provider);
        var notOpen = new HostProbeWindow();
        Require(Catch(() => _ = notOpen.Host) is InvalidOperationException && Catch(() => _ = notOpen.ViewModel) is InvalidOperationException,
            "A Window that never opened exposed a host or ViewModel.");
        await notOpen.DisposeAsync();

        var options = new RunicWindowOptions { Width = 640, Height = 480, StartPage = "app.html" };
        HostProbeViewModel firstModel;
        await using (var first = await host.OpenWindowAsync<HostProbeWindow>(options))
        await using (var second = await host.OpenWindowAsync<HostProbeWindow>())
        {
            firstModel = first.ViewModel;
            Require(ReferenceEquals(first.DataContext, first.ViewModel) && ReferenceEquals(first.Host, host.Opened[0].Window),
                "OpenWindowAsync did not assign the scoped ViewModel and the host's window.");
            Require(!ReferenceEquals(first.ViewModel, second.ViewModel) && !ReferenceEquals(first.ViewModel.Scope, second.ViewModel.Scope),
                "Two Windows shared a DI scope.");
            Require(ReferenceEquals(host.Opened[0].Options, options) && host.Opened[1].Options.StartPage == "index.html"
                && host.Opened[1].Options.Width is null,
                "The host did not receive the Window's options, or the default options.");
            Require(host.Opened.All(static item => item.Attached is { Disposed: false }),
                "The host could not attach the registered Bridge.");
            Require(!firstModel.Scope.Disposed, "A Window's scope was released while it was open.");
        }
        Require(firstModel.Scope.Disposed && host.Opened.All(static item => item.Attached!.Disposed),
            "Disposing the Window did not release its scope and Bridge.");
    }

    [Test, NotInParallel(Order = 2)]
    public async Task MissingBridgeFailsBeforeTheHostOrViewModel()
    {
        var log = new LogCapture();
        await using var provider = Services(bridge: false).AddSingleton<ILoggerFactory>(log).BuildServiceProvider();
        var host = new RecordingHost(provider);
        var error = await CatchAsync(() => host.OpenWindowAsync<HostProbeWindow>().AsTask());
        Require(error is RunicWindowConfigurationException { Diagnostics: [{ Code: RunicWindowConfigurationException.BridgeNotRegisteredCode }] } failure
            && failure.Message.Contains("AddRunicViews()", StringComparison.Ordinal),
            $"A missing Bridge did not fail with the shared configuration exception: {error}");
        Require(host.Opened.Count == 0 && HostProbeViewModel.Created == 0,
            "A missing Bridge reached the host or constructed the ViewModel.");
        Require(log.Entries.Count == 1 && log.Entries.Single() is { Category: RunicViewsTelemetry.LogCategory, Level: LogLevel.Error }
            && log.Entries.Single().EventId is { Id: 1050, Name: "WindowRegistrationMissing" },
            "A missing Bridge was not logged once as event 1050 WindowRegistrationMissing.");
    }

    [Test, NotInParallel(Order = 3)]
    public async Task OpaqueContainerReportsTheMissingBridgeAndReleasesTheScope()
    {
        await using var provider = Services(bridge: false).BuildServiceProvider();
        var host = new RecordingHost(new OpaqueServices(provider));
        Require(host.ValidateWindow<HostProbeWindow>().Diagnostics.Count == 0,
            "A container without IServiceProviderIsService was checked by validation.");
        var error = await CatchAsync(() => host.OpenWindowAsync<HostProbeWindow>().AsTask());
        Require(error is RunicWindowConfigurationException { Diagnostics: [{ Code: RunicWindowConfigurationException.BridgeNotRegisteredCode }] },
            $"Opening on an opaque container did not report the missing Bridge: {error}");
        Require(host.Opened.Count == 0 && HostProbeViewModel.Created == 0 && ScopeProbe.Live == 0,
            "Opening on an opaque container reached the host, constructed the ViewModel or kept the scope.");
    }

    [Test, NotInParallel(Order = 4)]
    public void ValidationReturnsOneResultWithRegistrationOptionAndHostDiagnostics()
    {
        using var empty = new ServiceCollection().BuildServiceProvider();
        var host = new RecordingHost(empty)
        {
            Diagnostics = [new("host-warning", "The host warns.") { Severity = RunicWindowDiagnosticSeverity.Warning }],
        };
        var result = host.ValidateWindow<HostProbeWindow>(new RunicWindowOptions { Width = 0 });
        Require(!result.IsValid
            && result.Errors.Select(static item => item.Code).SequenceEqual([RunicWindowConfigurationException.BridgeNotRegisteredCode, RunicWindowConfigurationException.OptionInvalidCode])
            && result.Errors[1].Option == "RunicWindowOptions.Width"
            && result.Warnings.Select(static item => item.Code).Order().SequenceEqual(["viewmodel-not-registered"]),
            $"Validation did not return the registration and option diagnostics: {string.Join(", ", result.Diagnostics)}");
        Require(host.Opened.Count == 0 && HostProbeViewModel.Created == 0, "Validation opened a window or constructed a ViewModel.");
        Require(Catch(result.ThrowIfInvalid) is RunicWindowConfigurationException { Diagnostics.Count: 2 },
            "ThrowIfInvalid did not throw the shared exception with the errors.");

        using var provider = Services(bridge: true).BuildServiceProvider();
        var valid = new RecordingHost(provider) { Diagnostics = host.Diagnostics }.ValidateWindow<HostProbeWindow>();
        Require(valid.IsValid && valid.Diagnostics.Single().Code == "host-warning",
            "Validation did not pass the host's own diagnostics through.");
        valid.ThrowIfInvalid();
    }

    [Test, NotInParallel(Order = 5)]
    public async Task InvalidOptionsAndCancellationStopBeforeTheHost()
    {
        await using var provider = Services(bridge: true).BuildServiceProvider();
        var host = new RecordingHost(provider);
        Require(await CatchAsync(() => host.OpenWindowAsync<HostProbeWindow>(new RunicWindowOptions { Height = -1 }).AsTask())
            is ArgumentOutOfRangeException { ParamName: "Height" },
            "A negative height reached the host.");
        Require(await CatchAsync(() => host.OpenWindowAsync<HostProbeWindow>(new RunicWindowOptions { StartPage = " " }).AsTask())
            is ArgumentException { ParamName: "StartPage" },
            "An empty start page reached the host.");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Require(await CatchAsync(() => host.OpenWindowAsync<HostProbeWindow>(cancellationToken: cancelled.Token).AsTask())
            is OperationCanceledException,
            "A cancelled open did not throw OperationCanceledException.");
        Require(host.Opened.Count == 0 && HostProbeViewModel.Created == 0 && ScopeProbe.Live == 0,
            "Invalid options or a cancelled open created a scope, ViewModel or window.");
    }

    [Test, NotInParallel(Order = 6)]
    public async Task HostCancellationReleasesTheScope()
    {
        await using var provider = Services(bridge: true).BuildServiceProvider();
        using var cancel = new CancellationTokenSource();
        var host = new RecordingHost(provider) { BlockOpen = true };
        var open = host.OpenWindowAsync<HostProbeWindow>(cancellationToken: cancel.Token).AsTask();
        await host.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(ScopeProbe.Live == 1, "The Window's scope did not exist while the host opened it.");
        await cancel.CancelAsync();
        Require(await CatchAsync(() => open.WaitAsync(TimeSpan.FromSeconds(5))) is OperationCanceledException,
            "Cancelling a host open did not cancel OpenWindowAsync.");
        Require(ScopeProbe.Live == 0, "A cancelled host open kept the Window's scope.");
    }

    [Test, NotInParallel(Order = 7)]
    public async Task LifecycleForwardsCancellationTokensToTheHost()
    {
        await using var provider = Services(bridge: true).BuildServiceProvider();
        var host = new RecordingHost(provider);
        await using var window = await host.OpenWindowAsync<HostProbeWindow>();
        var bridge = host.Opened.Single().Window;

        using var stop = new CancellationTokenSource();
        var wait = window.WaitForCloseAsync(stop.Token).AsTask();
        Require(!wait.IsCompleted, "WaitForCloseAsync completed before the window closed.");
        await stop.CancelAsync();
        Require(await CatchAsync(() => wait.WaitAsync(TimeSpan.FromSeconds(5))) is OperationCanceledException && !bridge.CloseStarted,
            "Cancelling WaitForCloseAsync did not stop only the wait.");

        var closed = window.WaitForCloseAsync().AsTask();
        using var closeToken = new CancellationTokenSource();
        var result = await window.CloseAsync(TimeSpan.FromSeconds(1), closeToken.Token);
        Require(result.Drained && bridge.CloseStarted && bridge.CloseToken == closeToken.Token,
            "CloseAsync did not pass its timeout and cancellation token to the host.");
        await closed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test, NotInParallel(Order = 8)]
    public async Task WindowServiceOpensOnTheHostAndReleasesClosedWindows()
    {
        await using var provider = Services(bridge: true).AddRunicWindowService().BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var windows = provider.GetRequiredService<IRunicWindowService>();
        Require(await CatchAsync(() => windows.OpenWindowAsync<HostProbeWindow>().AsTask()) is InvalidOperationException error
            && error.Message.Contains("host.OpenWindowAsync", StringComparison.Ordinal) && HostProbeViewModel.Created == 0,
            "The window service opened a Window, or failed unclearly, before any host had opened one.");

        var host = new RecordingHost(provider);
        await using var first = await host.OpenWindowAsync<HostProbeWindow>();
        var options = new RunicWindowOptions { Width = 320, Height = 200 };
        var second = await windows.OpenWindowAsync<HostProbeWindow>(options);
        Require(host.Opened.Count == 2 && ReferenceEquals(second.Host, host.Opened[1].Window)
            && ReferenceEquals(host.Opened[1].Options, options),
            "The window service did not open the Window on the host that opened the first one.");
        Require(!ReferenceEquals(first.ViewModel.Scope, second.ViewModel.Scope), "A Window opened by the service shared a scope.");

        // The user closes the second window; nothing awaits it, so the service releases it.
        host.Opened[1].Window.CloseByUser();
        await second.ViewModel.Scope.Released.WaitAsync(TimeSpan.FromSeconds(5));
        Require(host.Opened[1].Attached!.Disposed && !first.ViewModel.Scope.Disposed,
            "Closing a Window opened by the service did not release only that Window.");
        await second.DisposeAsync();
    }

    [Test, NotInParallel(Order = 9)]
    public async Task WindowServiceIsBoundBeforeTheFirstViewModelAndKeepsAFake()
    {
        Task<HostProbeWindow>? fromConstructor = null;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var services = Services(bridge: true).AddRunicWindowService();
        // Replaces the plain registration: the ViewModel uses the service in its constructor, with a cancelled token so
        // it opens nothing. A bound service reaches the host's checks and is cancelled; an unbound one throws.
        services.AddScoped(provider =>
        {
            fromConstructor ??= provider.GetRequiredService<IRunicWindowService>()
                .OpenWindowAsync<HostProbeWindow>(cancellationToken: cancelled.Token).AsTask();
            return new HostProbeViewModel(provider.GetRequiredService<ScopeProbe>());
        });
        await using (var provider = services.BuildServiceProvider())
        {
            await using var window = await new RecordingHost(provider).OpenWindowAsync<HostProbeWindow>();
            Require(fromConstructor is { IsCanceled: true }, $"The first ViewModel's constructor got an unbound window service: {fromConstructor?.Exception}");
        }

        var fake = new RecordingWindowService();
        await using var faked = Services(bridge: true).AddSingleton<IRunicWindowService>(fake).AddRunicWindowService().BuildServiceProvider();
        var opened = await faked.GetRequiredService<IRunicWindowService>().OpenWindowAsync<HostProbeWindow>();
        Require(fake.Opened.SequenceEqual([typeof(HostProbeWindow)]) && HostProbeViewModel.Created == 0,
            "AddRunicWindowService replaced an application's own window service.");
        await opened.DisposeAsync();
    }

    // The test double an application registers: it records the requested Window and opens nothing.
    private sealed class RecordingWindowService : IRunicWindowService
    {
        public List<Type> Opened { get; } = [];

        public ValueTask<TWindow> OpenWindowAsync<TWindow>(RunicWindowOptions? options = null, CancellationToken cancellationToken = default)
            where TWindow : class, IRunicWindow, new()
        {
            Opened.Add(typeof(TWindow));
            return ValueTask.FromResult(new TWindow());
        }
    }

    private static ServiceCollection Services(bool bridge)
    {
        HostProbeViewModel.Created = 0;
        ScopeProbe.Live = 0;
        var services = new ServiceCollection();
        services.AddScoped<ScopeProbe>();
        services.AddScoped<HostProbeViewModel>();
        if (bridge)
            services.AddScoped<Func<IBridgeTransport, HostProbeViewModel, IDisposable>>(_ => static (_, _) => new Attachment());
        return services;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Exception? Catch(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static async Task<Exception?> CatchAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class Attachment : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    // A host that opens nothing native: it attaches the Bridge to an in-memory transport and owns the scope.
    private sealed class RecordingHost(IServiceProvider services) : IRunicWindowHost
    {
        public IServiceProvider Services { get; } = services;
        public IReadOnlyList<RunicWindowDiagnostic> Diagnostics { get; init; } = [];
        public bool BlockOpen { get; init; }
        public TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(RecordingWindow Window, RunicWindowOptions Options, Attachment? Attached)> Opened { get; } = [];

        public IReadOnlyList<RunicWindowDiagnostic> Validate(RunicWindowOptions options) => Diagnostics;

        public async ValueTask<IBridgeWindow> OpenAsync<TViewModel>(RunicWindowOpening<TViewModel> opening,
            CancellationToken cancellationToken) where TViewModel : class
        {
            try
            {
                if (!ReferenceEquals(opening.Window.DataContext, opening.ViewModel))
                    throw new InvalidOperationException("The Window's DataContext was not assigned before the host opened it.");
                OpenStarted.TrySetResult();
                if (BlockOpen) await Task.Delay(Timeout.Infinite, cancellationToken);
                var transport = new InMemoryViewTransport();
                var content = new WindowContentSession(transport, rootModel: opening.ViewModel);
                var attached = opening.AttachBridge(transport, content);
                var window = new RecordingWindow(opening.Scope, attached, content, transport);
                Opened.Add((window, opening.Options, attached as Attachment));
                return window;
            }
            catch
            {
                await opening.Scope.DisposeAsync();
                throw;
            }
        }
    }

    private sealed class RecordingWindow(AsyncServiceScope scope, IDisposable attached, WindowContentSession content,
        InMemoryViewTransport transport) : IBridgeWindow
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CloseStarted { get; private set; }
        public CancellationToken CloseToken { get; private set; }

        public async ValueTask<BridgeWindowCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            CloseStarted = true;
            CloseToken = cancellationToken;
            _closed.TrySetResult();
            var result = await content.BeginCloseAsync(timeout);
            attached.Dispose();
            content.Dispose();
            transport.Dispose();
            await scope.DisposeAsync();
            return result;
        }

        public ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default) =>
            new(_closed.Task.WaitAsync(cancellationToken));

        // The native window closed, as when the user closes it: only the wait completes.
        public void CloseByUser() => _closed.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            if (!CloseStarted) await CloseAsync(TimeSpan.Zero);
        }
    }

    // A container that does not implement IServiceProviderIsService.
    private sealed class OpaqueServices(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceProviderIsService) ? null : inner.GetService(serviceType);
    }

    private sealed record LogEntry(string Category, LogLevel Level, EventId EventId);

    private sealed class LogCapture : ILoggerFactory
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose()
        {
        }

        private sealed class Logger(LogCapture owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => owner.Entries.Enqueue(new(category, logLevel, eventId));
        }
    }
}
