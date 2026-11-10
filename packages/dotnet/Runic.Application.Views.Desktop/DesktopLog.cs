using Microsoft.Extensions.Logging;
#if RUNIC_PORTABLE_PLATFORMS
using Runic.Platform.Linux.Portal;
#endif

namespace Runic.Application.Views.Desktop;

// Event IDs are documented in the Views README (Telemetry).
internal static partial class DesktopLog
{
    [LoggerMessage(EventId = 2000, EventName = "DesktopSnapshotDeliveryFailed", Level = LogLevel.Error,
        Message = "Desktop Bridge push delivery failed with {ErrorType}.")]
    internal static partial void PushDeliveryFailed(ILogger logger, Exception? exception, string errorType);

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

    [LoggerMessage(EventId = 2008, EventName = "DesktopLayoutDirectionFailed", Level = LogLevel.Warning,
        Message = "The window did not change its layout direction for its new locale ({ErrorType}).")]
    internal static partial void LayoutDirectionFailed(ILogger logger, Exception? exception, string errorType);

#if RUNIC_PORTABLE_PLATFORMS
    // A problem, such as a rejected identity or an unreachable portal, is a warning; accepted requests and
    // registrations are details. An unknown severity from a later package version is a warning too.
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal static void PortalDiagnostic(ILogger logger, PortalDiagnostic diagnostic) =>
        PortalDiagnostic(logger, diagnostic.Severity == PortalDiagnosticSeverity.Information ? LogLevel.Debug : LogLevel.Warning,
            diagnostic.Code, diagnostic.Message, diagnostic.Remediation ?? string.Empty);
#endif

    [LoggerMessage(EventId = 2009, EventName = "DesktopAppearanceWatchFailed", Level = LogLevel.Warning,
        Message = "The desktop's appearance settings could not be watched ({ErrorType}); pages keep the last appearance read, and the next window that opens watches again.")]
    internal static partial void AppearanceWatchFailed(ILogger logger, Exception? exception, string errorType);

    [LoggerMessage(EventId = 2010, EventName = "DesktopHostedStartFailed", Level = LogLevel.Error,
        Message = "Hosted mode did not start ({Code}): {Reason}")]
    internal static partial void HostedStartFailed(ILogger logger, string code, string reason);

    [LoggerMessage(EventId = 2011, EventName = "DesktopHostedShutdown", Level = LogLevel.Information,
        Message = "Hosted mode ends: {Reason}; the Windows close and the application shuts down.")]
    internal static partial void HostedShutdown(ILogger logger, string reason);

    [LoggerMessage(EventId = 2012, EventName = "DesktopHostedExit", Level = LogLevel.Warning,
        Message = "The application did not end within {Seconds} seconds after its host went away; the process exits.")]
    internal static partial void HostedExit(ILogger logger, int seconds);

    [LoggerMessage(EventId = 2013, EventName = "DesktopHostedChannelFailed", Level = LogLevel.Warning,
        Message = "A message of the host channel failed with {ErrorType}.")]
    internal static partial void HostedChannelFailed(ILogger logger, Exception? exception, string errorType);

    [LoggerMessage(EventId = 2014, EventName = "DesktopHostedBodyEnded", Level = LogLevel.Information,
        Message = "The application's body ended with {ErrorType} after its host went away; Run returns 0.")]
    internal static partial void HostedBodyEnded(ILogger logger, Exception? exception, string errorType);

    // The host of hosted applications (IRunicHostedApplications).
    [LoggerMessage(EventId = 2015, EventName = "DesktopHostedApplicationStarted", Level = LogLevel.Information,
        Message = "The hosted application {Name} {Version} started (process {ProcessId}).")]
    internal static partial void HostedApplicationStarted(ILogger logger, string name, string version, int processId);

    [LoggerMessage(EventId = 2016, EventName = "DesktopHostedApplicationStartFailed", Level = LogLevel.Warning,
        Message = "The hosted application {FileName} did not start ({Code}): {Reason}")]
    internal static partial void HostedApplicationStartFailed(ILogger logger, string fileName, string code, string reason);

    [LoggerMessage(EventId = 2017, EventName = "DesktopHostedApplicationExited", Level = LogLevel.Information,
        Message = "The hosted application {Name} exited with {ExitCode}; its windows closed.")]
    internal static partial void HostedApplicationExited(ILogger logger, string name, int exitCode);

    [LoggerMessage(EventId = 2018, EventName = "DesktopHostedApplicationChannelFailed", Level = LogLevel.Warning,
        Message = "A message of a hosted application's channel failed with {ErrorType}.")]
    internal static partial void HostedApplicationChannelFailed(ILogger logger, Exception? exception, string errorType);

    [LoggerMessage(EventId = 2019, EventName = "DesktopHostedWindowFailed", Level = LogLevel.Warning,
        Message = "A window of the hosted application {Name} did not open or close cleanly ({ErrorType}).")]
    internal static partial void HostedWindowFailed(ILogger logger, Exception? exception, string name, string errorType);

    [LoggerMessage(EventId = 2020, EventName = "DesktopHostedWindowPolicyFailed", Level = LogLevel.Error,
        Message = "The hosted window policy or a hosted window event handler failed with {ErrorType}.")]
    internal static partial void HostedWindowPolicyFailed(ILogger logger, Exception? exception, string errorType);

    [LoggerMessage(EventId = 2021, EventName = "DesktopHostedGuestOutput", Level = LogLevel.Debug,
        Message = "{FileName}: {Line}")]
    internal static partial void HostedGuestOutput(ILogger logger, string fileName, string line);

    [LoggerMessage(EventId = 2022, EventName = "DesktopHostedGuestError", Level = LogLevel.Debug,
        Message = "{FileName} (standard error): {Line}")]
    internal static partial void HostedGuestError(ILogger logger, string fileName, string line);

    [LoggerMessage(EventId = 2023, EventName = "DesktopHostedLocaleFailed", Level = LogLevel.Warning,
        Message = "The application's locale could not follow the host's locale: {ErrorType}.")]
    internal static partial void HostedLocaleFailed(ILogger logger, Exception? exception, string errorType);

    [LoggerMessage(EventId = 2024, EventName = "DesktopHostedServiceFailed", Level = LogLevel.Warning,
        Message = "A window service of a hosted application failed with {ErrorType}.")]
    internal static partial void HostedServiceFailed(ILogger logger, Exception? exception, string errorType);
}
