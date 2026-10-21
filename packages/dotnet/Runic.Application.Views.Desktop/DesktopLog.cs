using Microsoft.Extensions.Logging;
#if RUNIC_PORTABLE_PLATFORMS
using Runic.Platform.Linux.Portal;
#endif

namespace Runic.Application.Views.Desktop;

// Event IDs are documented in the Views README (Telemetry).
internal static partial class DesktopLog
{
    [LoggerMessage(EventId = 2000, EventName = "DesktopSnapshotDeliveryFailed", Level = LogLevel.Error,
        Message = "Desktop Bridge state delivery on route {Route} failed with {ErrorType}.")]
    internal static partial void SnapshotDeliveryFailed(ILogger logger, Exception? exception, string route, string errorType);

    [LoggerMessage(EventId = 2002, EventName = "DesktopWindowCloseAfterOpen", Level = LogLevel.Warning,
        Message = "{Variable}=1 is set; the Desktop window that just opened is being closed.")]
    internal static partial void CloseAfterOpen(ILogger logger, string variable);

    [LoggerMessage(EventId = 2003, EventName = "DesktopPortalDiagnostic",
        Message = "xdg-desktop-portal reported {Code}: {DiagnosticMessage} {Remediation}")]
    internal static partial void PortalDiagnostic(ILogger logger, LogLevel level, string code, string diagnosticMessage, string remediation);

    [LoggerMessage(EventId = 2004, EventName = "DesktopMenuCommandFailed", Level = LogLevel.Error,
        Message = "The menu command {CommandId} failed with {ErrorType}.")]
    internal static partial void MenuCommandFailed(ILogger logger, Exception? exception, string commandId, string errorType);

    [LoggerMessage(EventId = 2005, EventName = "DesktopDevelopmentAssetsServed", Level = LogLevel.Information,
        Message = "Serving the development assets in {Directory}; windows reload when they change.")]
    internal static partial void DevelopmentAssetsServed(ILogger logger, string directory);

    [LoggerMessage(EventId = 2006, EventName = "DesktopDevelopmentAssetsUnavailable", Level = LogLevel.Warning,
        Message = "The development assets in {Directory} cannot be served ({ErrorType}); the window serves {Instead}.")]
    internal static partial void DevelopmentAssetsUnavailable(ILogger logger, Exception? exception, string directory,
        string errorType, string instead);

    [LoggerMessage(EventId = 2007, EventName = "DesktopDevelopmentReloadFailed", Level = LogLevel.Debug,
        Message = "A window did not reload after its development assets changed.")]
    internal static partial void DevelopmentReloadFailed(ILogger logger, Exception? exception);

#if RUNIC_PORTABLE_PLATFORMS
    // A rejected identity or an unreachable portal is a warning; accepted requests and registrations are details.
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal static void PortalDiagnostic(ILogger logger, PortalDiagnostic diagnostic) =>
        PortalDiagnostic(logger, IsProblem(diagnostic.Code) ? LogLevel.Warning : LogLevel.Debug, diagnostic.Code,
            diagnostic.Message, diagnostic.Remediation ?? string.Empty);

    internal static bool IsProblem(string code) =>
        code.EndsWith("-failed", StringComparison.Ordinal) || code.EndsWith("-unavailable", StringComparison.Ordinal)
        || code.EndsWith("-denied", StringComparison.Ordinal) || code.EndsWith("-unspecified", StringComparison.Ordinal);
#endif
}
