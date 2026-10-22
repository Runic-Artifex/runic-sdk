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

    [LoggerMessage(EventId = 3002, EventName = "DesktopConfigurationInvalid", Level = LogLevel.Error,
        Message = "Desktop configuration check {Code} failed for {Option}: {DiagnosticMessage} {Remediation}")]
    internal static partial void ConfigurationInvalid(
        ILogger logger, string code, string option, string diagnosticMessage, string remediation);

    [LoggerMessage(EventId = 3003, EventName = "DesktopConfigurationLimited", Level = LogLevel.Warning,
        Message = "Desktop configuration check {Code} reported {Option}: {DiagnosticMessage} {Remediation}")]
    internal static partial void ConfigurationLimited(
        ILogger logger, string code, string option, string diagnosticMessage, string remediation);

    [LoggerMessage(EventId = 3004, EventName = "BrowserLaunchStalled", Level = LogLevel.Warning,
        Message = "The browser was still running but had requested nothing after {TimeoutSeconds} seconds on launch attempt {Attempt} of {Attempts}; relaunching it.")]
    internal static partial void BrowserLaunchStalled(ILogger logger, int attempt, int attempts, double timeoutSeconds);

    [LoggerMessage(EventId = 3005, EventName = "BridgeHandshakeExpired", Level = LogLevel.Warning,
        Message = "Bridge WebSocket {ConnectionId} sent no token check in {SilentSeconds} seconds and was closed ({Reason}); the page reconnects.")]
    internal static partial void BridgeHandshakeExpired(ILogger logger, ulong connectionId, double silentSeconds, string reason);

    [LoggerMessage(EventId = 3006, EventName = "DesktopShutdownRequested", Level = LogLevel.Information,
        Message = "{Reason} received; closing Desktop windows. Send it again to terminate immediately.")]
    internal static partial void ShutdownRequested(ILogger logger, string reason);

    [LoggerMessage(EventId = 3007, EventName = "DesktopShutdownFailed", Level = LogLevel.Error,
        Message = "Closing Desktop windows for shutdown failed with {ErrorType}.")]
    internal static partial void ShutdownFailed(ILogger logger, Exception exception, string errorType);

    [LoggerMessage(EventId = 3008, EventName = "BrowserProfileNotDeleted", Level = LogLevel.Warning,
        Message = "A generated browser profile could not be deleted; it remains in the temporary folder.")]
    internal static partial void BrowserProfileNotDeleted(ILogger logger);

    [LoggerMessage(EventId = 3009, EventName = "DesktopDiagnosticSinkFailed", Level = LogLevel.Warning,
        Message = "The diagnostic sink failed for {Code} with {ErrorType}.")]
    internal static partial void DiagnosticSinkFailed(ILogger logger, Exception exception, string code, string errorType);

    [LoggerMessage(EventId = 3010, EventName = "EmbeddedWindowDiagnostic",
        Message = "The embedded window reported {Code}: {DiagnosticMessage} {Remediation}")]
    internal static partial void EmbeddedWindowDiagnostic(
        ILogger logger, LogLevel level, string code, string diagnosticMessage, string remediation);

    [LoggerMessage(EventId = 3011, EventName = "WindowStateHandlerFailed", Level = LogLevel.Error,
        Message = "A DesktopWindow.StateChanged handler failed with {ErrorType}.")]
    internal static partial void WindowStateHandlerFailed(ILogger logger, Exception exception, string errorType);

    [LoggerMessage(EventId = 3012, EventName = "FileDropHandlerFailed", Level = LogLevel.Error,
        Message = "A DesktopWindow.FilesDropped handler failed with {ErrorType}.")]
    internal static partial void FileDropHandlerFailed(ILogger logger, Exception exception, string errorType);

    [LoggerMessage(EventId = 3013, EventName = "WindowClosedHandlerFailed", Level = LogLevel.Error,
        Message = "A DesktopWindow.Closed handler failed with {ErrorType}.")]
    internal static partial void WindowClosedHandlerFailed(ILogger logger, Exception exception, string errorType);

    [LoggerMessage(EventId = 3014, EventName = "MenuCommandFailed", Level = LogLevel.Error,
        Message = "The menu command target's {Member} failed for {CommandId} with {ErrorType}.")]
    internal static partial void MenuCommandFailed(ILogger logger, Exception exception, string member, string commandId, string errorType);

    // 3015 MenuBarPlacementChanged and 3016 GlobalMenuFailed belong to the Linux global menu.

    [LoggerMessage(EventId = 3017, EventName = "MenuBarPlacementHandlerFailed", Level = LogLevel.Error,
        Message = "A DesktopWindow.MenuBarPlacementChanged handler failed with {ErrorType}.")]
    internal static partial void MenuBarPlacementHandlerFailed(ILogger logger, Exception exception, string errorType);

    internal static void Diagnostic(ILogger logger, DesktopDiagnostic diagnostic)
    {
        var option = diagnostic.Option ?? "the presentation";
        var remediation = diagnostic.Remediation ?? string.Empty;
        if (diagnostic.Severity == DesktopDiagnosticSeverity.Error)
            ConfigurationInvalid(logger, diagnostic.Code, option, diagnostic.Message, remediation);
        else
            ConfigurationLimited(logger, diagnostic.Code, option, diagnostic.Message, remediation);
    }
}
