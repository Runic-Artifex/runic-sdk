using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Runic.Navigation.Wpf;

// Event IDs 1080-1089, category Runic.Navigation.Wpf (W240-001 §14, A7). Logs carry exception and
// type names, never messages or values.
internal static partial class WpfNavigationLog
{
    internal const string Category = "Runic.Navigation.Wpf";

    [LoggerMessage(EventId = 1080, EventName = "PostedTurnFailed", Level = LogLevel.Error,
        Message = "A posted model turn failed with {ErrorType}, and no UnhandledTurnException handler took it.")]
    internal static partial void PostedTurnFailed(ILogger logger, Exception? exception, string errorType);

    [LoggerMessage(EventId = 1081, EventName = "ModelTurnNested", Level = LogLevel.Warning,
        Message = "A model turn started inside another turn, in a nested message pump (ShowDialog, MessageBox or PushFrame inside a turn). Turns must not pump; logged once per context.")]
    internal static partial void ModelTurnNested(ILogger logger);

    [LoggerMessage(EventId = 1082, EventName = "ViewNotFound", Level = LogLevel.Warning,
        Message = "No view locator returned a view and no DataTemplate is keyed by {ContentType} or a base type; logged once per type.")]
    internal static partial void ViewNotFound(ILogger logger, string contentType);

    [LoggerMessage(EventId = 1083, EventName = "DialogShowFailed", Level = LogLevel.Error,
        Message = "Showing the dialog window for {ContentType} failed with {ErrorType}; the host clears the dialog region once.")]
    internal static partial void DialogShowFailed(ILogger logger, Exception? exception, string contentType, string errorType);

    [LoggerMessage(EventId = 1084, EventName = "PostedTurnDropped", Level = LogLevel.Warning,
        Message = "A dispatcher model context dropped a posted turn at shutdown: {ErrorType}.")]
    internal static partial void PostedTurnDropped(ILogger logger, Exception? exception, string errorType);

    [LoggerMessage(EventId = 1086, EventName = "DialogHostWithoutWindow", Level = LogLevel.Warning,
        Message = "A NavigationDialogHost was loaded outside a Window, so it can't own dialog windows; its region's entries get no windows. Logged once per host.")]
    internal static partial void DialogHostWithoutWindow(ILogger logger);

    [LoggerMessage(EventId = 1085, EventName = "UnhandledTurnHandlerFailed", Level = LogLevel.Error,
        Message = "An UnhandledTurnException handler failed with {ErrorType}.")]
    internal static partial void UnhandledTurnHandlerFailed(ILogger logger, Exception? exception, string errorType);

    internal static string ErrorType(Exception error) => error.GetType().FullName ?? error.GetType().Name;

    internal static string TypeName(Type type) => type.FullName ?? type.Name;

    // The logger of a host: the navigator's ILoggerFactory when its services have one.
    internal static ILogger For(IServiceProvider? services) =>
        services?.GetService(typeof(ILoggerFactory)) is ILoggerFactory factory
            ? factory.CreateLogger(Category)
            : TraceFallbackLogger.Instance;
}

// Without an ILoggerFactory, Warning and above go to System.Diagnostics.Trace, so they stay visible to a
// debugger or trace listener. Matches the navigator's fallback.
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
