using Microsoft.Extensions.Logging;

namespace Runic.Desktop.Internal;

// The exception is not attached: a close callback is application code and its
// message can contain application data. The type identifies the failure.
internal static partial class DesktopLog
{
    internal const string Category = "Runic.Desktop";

    internal static string ErrorType(Exception error) => error.GetType().FullName ?? error.GetType().Name;

    [LoggerMessage(EventId = 3000, EventName = "WindowCloseCancellationCallbackFailed", Level = LogLevel.Error,
        Message = "A window close cancellation callback failed with {ErrorType}.")]
    internal static partial void CloseCancellationCallbackFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 3001, EventName = "WindowCloseConfirmationFailed", Level = LogLevel.Error,
        Message = "Window close confirmation failed with {ErrorType}; the window was kept open.")]
    internal static partial void CloseConfirmationFailed(ILogger logger, string errorType);
}
