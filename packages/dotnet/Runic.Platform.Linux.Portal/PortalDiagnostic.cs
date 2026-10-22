namespace Runic.Platform.Linux.Portal;

/// <summary>A setup message from the portal services: a problem the desktop reported, or what a service did.</summary>
/// <remarks>
/// <para>For logs only. The <see cref="Code"/> values are listed in the package README; they and the text may change in
/// any release, so do not branch on them. Operation outcomes are the services' results.</para>
/// <para>A sink also receives informational entries, such as <c>portal-identity-registered</c>.</para>
/// </remarks>
/// <param name="Code">A kebab-case identifier of the situation, such as <c>status-icon-no-watcher</c>.</param>
/// <param name="Message">What happened; it can be a D-Bus error name.</param>
/// <param name="Remediation">What to change, or what the service does next, when there is something to say.</param>
public sealed record PortalDiagnostic(string Code, string Message, string? Remediation = null)
{
    private readonly PortalDiagnosticSeverity _severity;

    /// <summary>
    /// Whether the entry reports a problem (<see cref="PortalDiagnosticSeverity.Warning"/>) or what a service did
    /// (<see cref="PortalDiagnosticSeverity.Information"/>), for choosing a log level. The README lists each code's
    /// severity.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public PortalDiagnosticSeverity Severity
    {
        get => _severity;
        init => _severity = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(Severity));
    }

    // The informational codes; every other code reports a problem. The README's table says the same (DiagnosticTests).
    private static readonly HashSet<string> InformationCodes = new(StringComparer.Ordinal)
    {
        "portal-identity-registered", "portal-notification-request-accepted", "portal-notification-action-received",
        "portal-notification-owner-changed", "portal-notification-activation-unmatched", "status-icon-bus-lost",
        "secret-service-plain-session", "desktop-applications-sandboxed", "thumbnail-cache-unavailable",
    };

    internal static PortalDiagnostic Create(string code, string message, string? remediation) =>
        new(code, message, remediation) { Severity = SeverityOf(code) };

    internal static PortalDiagnosticSeverity SeverityOf(string code) =>
        InformationCodes.Contains(code) ? PortalDiagnosticSeverity.Information : PortalDiagnosticSeverity.Warning;
}

/// <summary>How much a <see cref="PortalDiagnostic"/> matters, for choosing a log level.</summary>
/// <remarks>This set is open: a minor release may add severities, so handle unknown values.</remarks>
public enum PortalDiagnosticSeverity
{
    /// <summary>What a service did, such as registering the application ID; logged as detail.</summary>
    Information,
    /// <summary>A problem the desktop reported or a missing desktop service; the operation's result says what failed.</summary>
    Warning,
}
