using Microsoft.Extensions.Logging;

namespace Runic.Application.Views.CsWebUi;

// Event IDs are documented in the Views README (Telemetry).
internal static partial class CsWebUiLog
{
    [LoggerMessage(EventId = 1050, EventName = "CsWebUiWindowRegistrationMissing", Level = LogLevel.Error,
        Message = "CS-WebUI Window check {Code} failed: {DiagnosticMessage} {Remediation}")]
    internal static partial void RegistrationMissing(ILogger logger, string code, string diagnosticMessage, string remediation);
}
