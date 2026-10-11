using CommunityToolkit.Mvvm.ComponentModel;
using CsWebUi;
using CsWebUiWindowProbes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

// Each probe prints one marker that CI matches. No probe launches a browser: the window probe serves its
// content without one, through the same switch test harnesses use.
Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", "1");
switch (args.SingleOrDefault())
{
    case "--probe-factory-failure":
        await ProbeFactoryFailureAsync();
        break;
    case "--probe-window-close":
        return ProbeWindowClose();
    case "--probe-missing-bridge":
        await ProbeMissingBridgeAsync();
        break;
    default:
        Console.Error.WriteLine("Usage: CsWebUiWindowProbes --probe-factory-failure | --probe-window-close | --probe-missing-bridge");
        return 2;
}
WebUiApplication.Clean();
return 0;

// A ViewModel construction failure must release the Window scope exactly once, before any native window exists.
static async Task ProbeFactoryFailureAsync()
{
    var services = new ServiceCollection();
    var scopeDisposals = 0;
    services.AddScoped(_ => new ScopeProbe(() => scopeDisposals++));
    services.AddScoped<ProbeViewModel>(provider =>
    {
        _ = provider.GetRequiredService<ScopeProbe>();
        throw new InvalidOperationException("Expected Window construction failure.");
    });
    services.AddRunicViews();
    using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    var host = new RunicCsWebUiHost(provider);
    try
    {
        await using var window = await host.OpenWindowAsync<ProbeWindow>();
        throw new InvalidOperationException("The Window unexpectedly opened.");
    }
    catch (InvalidOperationException error) when (error.Message == "Expected Window construction failure.")
    {
        if (scopeDisposals != 1)
            throw new InvalidOperationException($"Failed Window construction released the scope {scopeDisposals} times.");
        Console.WriteLine("FIRST_WINDOW_FACTORY_FAILURE_OK");
    }
}

// An idle Window opened through the application entry point closes without remaining operations, its
// WaitForCloseAsync returns, and Run returns the application's result once the Window is released.
static int ProbeWindowClose()
{
    var services = new ServiceCollection();
    services.AddScoped<ProbeViewModel>();
    services.AddRunicViews();
    using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    var result = RunicCsWebUiHost.Run(provider, async host =>
    {
        await using var window = await host.OpenWindowAsync<ProbeWindow>();
        if (window.ViewModel is null || !ReferenceEquals(window.DataContext, window.ViewModel))
            throw new InvalidOperationException("The Window was not given its ViewModel.");
        var close = await window.CloseAsync(TimeSpan.Zero);
        await close.Completion;
        if (!close.Drained || close.RemainingOperations != 0)
            throw new InvalidOperationException("An idle Window did not close cleanly.");
        await window.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return 7;
    });
    if (result != 7) throw new InvalidOperationException($"Run returned {result} instead of the application's result.");
    Console.WriteLine("FIRST_WINDOW_CLOSE_OK");
    return 0;
}

// Without AddRunicViews(), validation and OpenWindowAsync name the missing Bridge before a native window
// or ViewModel exists, also for a container that cannot report its registrations.
static async Task ProbeMissingBridgeAsync()
{
    var services = new ServiceCollection();
    var viewModels = 0;
    services.AddScoped(_ =>
    {
        viewModels++;
        return new ProbeViewModel();
    });
    var log = new RecordingLoggerFactory();
    services.AddSingleton<ILoggerFactory>(log);
    using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    var validation = new RunicCsWebUiHost(provider).ValidateWindow<ProbeWindow>();
    if (validation.IsValid || validation.Errors.Count != 1)
        throw new InvalidOperationException("ValidateWindow did not report the missing Bridge.");
    RequireBridgeNotRegistered(new RunicWindowConfigurationException(validation.Errors), "ValidateWindow");
    foreach (var (name, candidate) in new (string, IServiceProvider)[]
        { ("OpenWindowAsync", provider), ("OpenWindowAsync without IServiceProviderIsService", new OpaqueProvider(provider)) })
    {
        try
        {
            await using var window = await new RunicCsWebUiHost(candidate).OpenWindowAsync<ProbeWindow>();
            throw new InvalidOperationException($"{name} opened a Window without its Bridge.");
        }
        catch (RunicWindowConfigurationException error)
        {
            RequireBridgeNotRegistered(error, name);
        }
    }
    if (viewModels != 0)
        throw new InvalidOperationException($"A missing Bridge still constructed {viewModels} ViewModels.");
    if (log.Entries.Count != 3 || log.Entries.Any(static entry => entry.Category != "Runic.Application.Views" ||
            entry.EventId.Id != 1050 || entry.EventId.Name != "WindowRegistrationMissing" ||
            entry.Level != LogLevel.Error || !entry.Message.Contains("bridge-not-registered", StringComparison.Ordinal)))
        throw new InvalidOperationException("A missing Bridge was not logged once per check as event 1050 in Runic.Application.Views.");

    var registered = new ServiceCollection();
    registered.AddScoped<ProbeViewModel>();
    registered.AddRunicViews();
    using var complete = registered.BuildServiceProvider();
    new RunicCsWebUiHost(complete).ValidateWindow<ProbeWindow>().ThrowIfInvalid();
    Console.WriteLine("FIRST_WINDOW_MISSING_BRIDGE_OK");
}

static void RequireBridgeNotRegistered(RunicWindowConfigurationException error, string name)
{
    if (error.Diagnostics is [{ Code: RunicWindowConfigurationException.BridgeNotRegisteredCode }] &&
        error.Message.Contains("- bridge-not-registered: ", StringComparison.Ordinal) &&
        error.Message.Contains("AddRunicViews()", StringComparison.Ordinal) &&
        error.Message.Contains(typeof(ProbeViewModel).FullName!, StringComparison.Ordinal))
        return;
    throw new InvalidOperationException($"{name} did not report the missing Bridge with its code and remediation:\n{error}");
}

namespace CsWebUiWindowProbes
{
    public sealed partial class ProbeViewModel : ObservableObject
    {
        [ObservableProperty] private int count;
    }

    public sealed partial class ProbeWindow : RunicWindow<ProbeViewModel>;

    // Hides IServiceProviderIsService, as some third-party containers do.
    internal sealed class OpaqueProvider(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceProviderIsService) ? null : inner.GetService(serviceType);
    }

    internal sealed record LogEntry(string Category, EventId EventId, LogLevel Level, string Message);

    internal sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public List<LogEntry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class RecordingLogger(RecordingLoggerFactory owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Entries.Add(new LogEntry(category, eventId, logLevel, formatter(state, exception)));
        }
    }

    internal sealed class ScopeProbe(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
