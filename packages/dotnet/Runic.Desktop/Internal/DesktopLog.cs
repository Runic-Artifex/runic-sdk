using Microsoft.Extensions.Logging;

namespace Runic.Desktop.Internal;

internal static partial class DesktopLog
{
    internal const string Category = "Runic.Desktop";

    internal static string ErrorType(Exception error) => error.GetType().FullName ?? error.GetType().Name;

    [LoggerMessage(EventId = 3000, EventName = "WindowCloseCancellationCallbackFailed", Level = LogLevel.Error,
        Message = "A window close cancellation callback failed with {ErrorType}.")]
    internal static partial void CloseCancellationCallbackFailed(ILogger logger, Exception exception, string errorType);

    [LoggerMessage(EventId = 3001, EventName = "WindowCloseConfirmationFailed", Level = LogLevel.Error,
        Message = "Window close confirmation failed with {ErrorType}; the window was kept open.")]
    internal static partial void CloseConfirmationFailed(ILogger logger, Exception exception, string errorType);
}
