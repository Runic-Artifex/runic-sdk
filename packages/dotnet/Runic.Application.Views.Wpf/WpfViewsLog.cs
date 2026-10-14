using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Runic.Application.Views.Desktop;

namespace Runic.Application.Views.Wpf;

// Event IDs are documented in the Views README (Logging and telemetry).
internal static partial class WpfViewsLog
{
    internal const string Category = "Runic.Application.Views.Wpf";

    [LoggerMessage(EventId = 1110, EventName = "ViewHostFallbackShown", Level = LogLevel.Warning,
        Message = "RunicViewHost could not open the web presentation ({ErrorType}); showing the fallback.")]
    internal static partial void FallbackShown(ILogger logger, Exception exception, string errorType);

    [LoggerMessage(EventId = 1111, EventName = "ViewHostFallbackFailed", Level = LogLevel.Error,
        Message = "RunicViewHost fallback failed with {ErrorType}.")]
    internal static partial void FallbackFailed(ILogger logger, Exception exception, string errorType);

    [LoggerMessage(EventId = 1112, EventName = "ViewHostPresentationChangeFailed", Level = LogLevel.Error,
        Message = "RunicViewHost presentation change failed with {ErrorType}.")]
    internal static partial void PresentationChangeFailed(ILogger logger, Exception exception, string errorType);

    [LoggerMessage(EventId = 1113, EventName = "ViewHostCleanupFailed", Level = LogLevel.Error,
        Message = "RunicViewHost presentation cleanup failed with {ErrorType}.")]
    internal static partial void CleanupFailed(ILogger logger, Exception exception, string errorType);

    [LoggerMessage(EventId = 1114, EventName = "ChildViewCleanupFailed", Level = LogLevel.Error,
        Message = "WPF child view cleanup failed with {ErrorType}.")]
    internal static partial void ChildViewCleanupFailed(ILogger logger, Exception exception, string errorType);

    internal static string ErrorType(Exception error) => error.GetType().FullName ?? error.GetType().Name;

    // The logger of a host: the application's ILoggerFactory, lent so that a disposed one falls back to Trace.
    internal static ILogger For(LentLoggerFactory? factory) => factory?.CreateLogger(Category) ?? TraceFallbackLogger.Instance;
}

// Without an ILoggerFactory, Warning and above go to System.Diagnostics.Trace, so they stay visible to a
// debugger or trace listener. Matches the Views fallback.
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
