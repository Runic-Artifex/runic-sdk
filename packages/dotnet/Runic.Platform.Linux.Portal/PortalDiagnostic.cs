namespace Runic.Platform.Linux.Portal;

/// <summary>A redacted portal failure with actionable setup guidance.</summary>
public sealed record PortalDiagnostic(string Code, string Message, string Remediation);
