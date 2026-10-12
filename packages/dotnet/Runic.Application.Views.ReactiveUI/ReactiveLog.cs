// Shared by Runic.Application.Views.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.Views.ReactiveUI.Reactive.
using System.Diagnostics;
using Microsoft.Extensions.Logging;

#if SYSTEM_REACTIVE
namespace Runic.Application.Views.ReactiveUI.Reactive;
#else
namespace Runic.Application.Views.ReactiveUI;
#endif

// Event IDs 1040-1043 and 1090-1099 belong to this package (1044-1049 are reserved for Runic.Navigation.ReactiveUI);
// they continue the Views table and are documented in the Views README
// (Logging and telemetry). The category is RunicViewsTelemetry.LogCategory.
internal static partial class ReactiveLog
{
    [LoggerMessage(EventId = 1040, EventName = "RoutedRegionRouteIncompatible", Level = LogLevel.Error,
        Message = "A routed region for {Region} received {Model}, which it cannot present; the region presents no content.")]
    internal static partial void RouteIncompatible(ILogger logger, string region, string model);

    [LoggerMessage(EventId = 1041, EventName = "RoutedRegionRouterFailed", Level = LogLevel.Error,
        Message = "The router of a routed region for {Region} failed with {ErrorType}; the region keeps its last content.")]
    internal static partial void RouterFailed(ILogger logger, Exception? exception, string region, string errorType);

    [LoggerMessage(EventId = 1042, EventName = "ReactiveCommandFailed", Level = LogLevel.Error,
        Message = "{Source} reported {ErrorType} on ThrownExceptions.")]
    internal static partial void CommandFailed(ILogger logger, Exception? exception, string source, string errorType);

    [LoggerMessage(EventId = 1043, EventName = "ReactiveSchedulerOutsideModelTurn", Level = LogLevel.Warning,
        Message = "Work was scheduled on ReactiveUI's main-thread scheduler outside a Runic model turn, from {Source}, so it runs on {Fallback}, not on a model context. Observe a background source on the context's scheduler instead, for example ObserveOn with an injected scheduler. Later occurrences from this source are not logged.")]
    internal static partial void OutsideModelTurn(ILogger logger, string source, string fallback);

    [LoggerMessage(EventId = 1090, EventName = "ReactiveSchedulerKept", Level = LogLevel.Information,
        Message = "A window opens while ReactiveUI's main-thread scheduler is {Scheduler}, which was set before AddRunicReactiveModelContext() and kept, so commands created without an output scheduler deliver on it, not on the window's model context. Leave ReactiveUI's default scheduler in place, or call WithRunicModelContext() on ReactiveUI's builder, to use the model context.")]
    internal static partial void SchedulerKept(ILogger logger, string scheduler);

    [LoggerMessage(EventId = 1091, EventName = "ReactiveSchedulerReplaced", Level = LogLevel.Warning,
        Message = "A window opens while ReactiveUI's main-thread scheduler is {Scheduler}, which replaced the model-context scheduler after AddRunicReactiveModelContext() installed it, for example in a later RxAppBuilder BuildApp(), so commands created without an output scheduler deliver on it, not on the window's model context. Call WithRunicModelContext() on that builder, or ignore this if the replacement is deliberate.")]
    internal static partial void SchedulerReplaced(ILogger logger, string scheduler);
}

// The fallback when a component has no ILoggerFactory, matching the Views
// TraceFallbackLogger: Warning and above go to Trace as TraceWarning or
// TraceError. Trace receives the same source-generated message text as an
// ILogger, but not the event ID or name.
internal sealed class TraceLogger : ILogger
{
    internal static readonly TraceLogger Instance = new();

    private TraceLogger()
    {
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        // D-12: the entry carries the exception in every environment.
        if (exception is not null) message = $"{message} {exception}";
        if (logLevel >= LogLevel.Error) Trace.TraceError(message);
        else Trace.TraceWarning(message);
    }
}
