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
public sealed record PortalDiagnostic(string Code, string Message, string? Remediation = null);
