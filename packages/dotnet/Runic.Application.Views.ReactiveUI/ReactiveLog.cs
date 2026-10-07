// Shared by Runic.Application.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.ReactiveUI.Reactive.
using System.Diagnostics;
using Microsoft.Extensions.Logging;

#if SYSTEM_REACTIVE
namespace Runic.Application.Views.ReactiveUI.Reactive;
#else
namespace Runic.Application.Views.ReactiveUI;
#endif

// Event IDs continue the Views table and are documented in the Views README
// (Logging and telemetry). The category is RunicViewsTelemetry.LogCategory.
internal static partial class ReactiveLog
{
    [LoggerMessage(EventId = 1040, EventName = "RoutedRegionRouteIncompatible", Level = LogLevel.Error,
        Message = "A routed region for {Region} received {Model}, which it cannot present; the region presents no content.")]
    internal static partial void RouteIncompatible(ILogger logger, string region, string model);

    [LoggerMessage(EventId = 1041, EventName = "RoutedRegionRouterFailed", Level = LogLevel.Error,
        Message = "The router of a routed region for {Region} failed with {ErrorType}; the region keeps its last content.")]
    internal static partial void RouterFailed(ILogger logger, Exception? exception, string region, string errorType);
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
