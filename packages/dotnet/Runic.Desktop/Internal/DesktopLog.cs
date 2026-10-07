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
