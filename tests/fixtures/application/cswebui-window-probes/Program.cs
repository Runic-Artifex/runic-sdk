using CommunityToolkit.Mvvm.ComponentModel;
using CsWebUi;
using CsWebUiWindowProbes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views.CsWebUi;

// Each probe prints one marker that CI matches.
switch (args.SingleOrDefault())
{
    case "--probe-factory-failure":
        ProbeFactoryFailure();
        break;
    case "--probe-window-close":
        await ProbeWindowCloseAsync();
        break;
    case "--probe-missing-bridge":
        ProbeMissingBridge();
        break;
    default:
        Console.Error.WriteLine("Usage: CsWebUiWindowProbes --probe-factory-failure | --probe-window-close | --probe-missing-bridge");
        return 2;
}
WebUiApplication.Clean();
return 0;

// A Window factory failure must release the Window scope exactly once.
static void ProbeFactoryFailure()
{
    var services = new ServiceCollection();
    var scopeDisposals = 0;
    services.AddScoped(_ => new ScopeProbe(() => scopeDisposals++));
    services.AddScoped(provider =>
    {
        _ = provider.GetRequiredService<ScopeProbe>();
        return new ProbeViewModel();
    });
    services.AddRunicViews();
    using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    try
    {
        _ = provider.OpenWindow<ProbeWindow, ProbeViewModel>(
            _ => throw new InvalidOperationException("Expected Window construction failure."));
        throw new InvalidOperationException("The Window factory unexpectedly succeeded.");
    }
    catch (InvalidOperationException error) when (error.Message == "Expected Window construction failure.")
    {
        if (scopeDisposals != 1)
            throw new InvalidOperationException($"Failed Window construction released the scope {scopeDisposals} times.");
        Console.WriteLine("FIRST_WINDOW_FACTORY_FAILURE_OK");
    }
}

// An idle Window closes without remaining operations and disposes cleanly.
static async Task ProbeWindowCloseAsync()
{
    var services = new ServiceCollection();
    services.AddScoped<ProbeViewModel>();
    services.AddRunicViews();
    using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    var window = provider.OpenWindow<ProbeWindow, ProbeViewModel>(host => new ProbeWindow(host));
    var close = await window.CloseAsync(TimeSpan.Zero);
    await close.Completion;
    if (!close.Drained || close.RemainingOperations != 0)
        throw new InvalidOperationException("An idle Window did not close cleanly.");
    await window.DisposeAsync();
    Console.WriteLine("FIRST_WINDOW_CLOSE_OK");
}

// Without AddRunicViews(), validation and OpenWindow name the missing Bridge
// before a native window exists, also for a container that cannot report
// its registrations.
static void ProbeMissingBridge()
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
    RequireBridgeNotRegistered(() => provider.ValidateWindow<ProbeViewModel>(), "ValidateWindow");
    foreach (var (name, candidate) in new (string, IServiceProvider)[]
        { ("OpenWindow", provider), ("OpenWindow without IServiceProviderIsService", new OpaqueProvider(provider)) })
    {
        var created = false;
        RequireBridgeNotRegistered(() => candidate.OpenWindow<ProbeWindow, ProbeViewModel>(host =>
        {
            created = true;
            return new ProbeWindow(host);
        }), name);
        if (created) throw new InvalidOperationException($"{name} constructed a Window without its Bridge.");
    }
    if (viewModels != 0)
        throw new InvalidOperationException($"A missing Bridge still constructed {viewModels} ViewModels.");
    if (log.Entries.Count != 3 || log.Entries.Any(static entry => entry.Category != "Runic.Application.Views" ||
            entry.EventId.Id != 1050 || entry.EventId.Name != "CsWebUiWindowRegistrationMissing" ||
            entry.Level != LogLevel.Error || !entry.Message.Contains("bridge-not-registered", StringComparison.Ordinal)))
        throw new InvalidOperationException("A missing Bridge was not logged once per check as event 1050 in Runic.Application.Views.");

    var registered = new ServiceCollection();
    registered.AddScoped<ProbeViewModel>();
    registered.AddRunicViews();
    using var complete = registered.BuildServiceProvider();
    complete.ValidateWindow<ProbeViewModel>();
    Console.WriteLine("FIRST_WINDOW_MISSING_BRIDGE_OK");
}

static void RequireBridgeNotRegistered(Action open, string name)
{
    try
    {
        open();
    }
    catch (CsWebUiConfigurationException error)
        when (error.Code == CsWebUiConfigurationException.BridgeNotRegisteredCode &&
            error.Message.StartsWith("bridge-not-registered: ", StringComparison.Ordinal) &&
            error.Message.Contains("AddRunicViews()", StringComparison.Ordinal) &&
            error.Message.Contains(typeof(ProbeViewModel).FullName!, StringComparison.Ordinal))
    {
        return;
    }
    throw new InvalidOperationException($"{name} did not report the missing Bridge with its code and remediation.");
}

namespace CsWebUiWindowProbes
{
    public sealed partial class ProbeViewModel : ObservableObject
    {
        [ObservableProperty] private int count;
    }

    public sealed partial class ProbeWindow(CsWebUiBridgeWindow<ProbeViewModel> host)
        : CsWebUiWindow<ProbeViewModel>(host);

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
