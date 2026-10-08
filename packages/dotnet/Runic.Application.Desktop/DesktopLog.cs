using Microsoft.Extensions.Logging;

namespace Runic.Application.Views.Desktop;

// Event IDs are documented in the Views README (Telemetry).
internal static partial class DesktopLog
{
    [LoggerMessage(EventId = 2000, EventName = "DesktopSnapshotDeliveryFailed", Level = LogLevel.Error,
        Message = "Desktop Bridge state delivery on route {Route} failed with {ErrorType}.")]
    internal static partial void SnapshotDeliveryFailed(ILogger logger, Exception? exception, string route, string errorType);

    [LoggerMessage(EventId = 2001, EventName = "DesktopWindowRegistrationMissing", Level = LogLevel.Error,
        Message = "Desktop Window check {Code} failed: {DiagnosticMessage} {Remediation}")]
    internal static partial void RegistrationMissing(ILogger logger, string code, string diagnosticMessage, string remediation);

    [LoggerMessage(EventId = 2002, EventName = "DesktopWindowCloseAfterOpen", Level = LogLevel.Warning,
        Message = "{Variable}=1 is set; the Desktop window that just opened is being closed.")]
    internal static partial void CloseAfterOpen(ILogger logger, string variable);
}
