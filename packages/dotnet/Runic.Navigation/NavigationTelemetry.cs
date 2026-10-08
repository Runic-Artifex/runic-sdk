using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Runic.Navigation;

internal static class NavigationTelemetry
{
    // Logs carry exception types, never messages or values.
    internal static string ErrorType(Exception error) => error.GetType().FullName ?? error.GetType().Name;
}

// Model-context event IDs 1030-1033. Runic.Application logs its own 1033 when a
// window session's context shutdown fails in the background.
internal static partial class ModelContextLog
{
    [LoggerMessage(EventId = 1030, EventName = "ModelTurnFailed", Level = LogLevel.Error,
        Message = "A model context turn failed with {ErrorType}.")]
    internal static partial void ModelTurnFailed(ILogger logger, Exception? exception, string errorType);

    [LoggerMessage(EventId = 1031, EventName = "ModelTurnDropped", Level = LogLevel.Warning,
        Message = "A model context dropped a posted turn at shutdown: {ErrorType}.")]
    internal static partial void ModelTurnDropped(ILogger logger, Exception? exception, string errorType);

    [LoggerMessage(EventId = 1032, EventName = "UnhandledTurnHandlerFailed", Level = LogLevel.Error,
        Message = "An UnhandledTurnException handler failed with {ErrorType}.")]
    internal static partial void UnhandledTurnHandlerFailed(ILogger logger, Exception? exception, string errorType);

    [LoggerMessage(EventId = 1033, EventName = "ModelContextReleaseFailed", Level = LogLevel.Error,
        Message = "Releasing a model context failed with {ErrorType}.")]
    internal static partial void ModelContextReleaseFailed(ILogger logger, Exception? exception, string errorType);
}

// Without an ILoggerFactory, failures go to System.Diagnostics.Trace, so they stay
// visible to a debugger or trace listener.
internal sealed class TraceFallbackLogger : ILogger
{
    internal static readonly TraceFallbackLogger Instance = new();

    private TraceFallbackLogger()
    {
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        if (exception is not null) message = $"{message} {exception}";
        if (logLevel >= LogLevel.Error) Trace.TraceError(message);
        else Trace.TraceWarning(message);
    }
}
