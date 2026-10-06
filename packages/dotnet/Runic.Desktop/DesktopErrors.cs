namespace Runic.Desktop;

/// <summary>Identifies a stable presentation-boundary error category.</summary>
public enum DesktopErrorCategory
{
    InvalidArgument,
    InvalidState,
    InvalidFrame,
    LimitExceeded,
    AuthenticationDenied,
    OriginDenied,
    CapabilityDenied,
    NotFound,
    Conflict,
    Cancelled,
    TimedOut,
    TransportClosed,
    HostStopping,
    Unavailable,
    OperationFailed,
}

/// <summary>Represents a redacted, stable Desktop operation failure.</summary>
public class DesktopException : Exception
{
    public DesktopException(
        DesktopErrorCategory category,
        string code,
        string message,
        bool retryable = false,
        Exception? innerException = null,
        string? correlationId = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Category = category;
        Code = code;
        Retryable = retryable;
        CorrelationId = correlationId ?? Guid.NewGuid().ToString("N");
    }

    public DesktopErrorCategory Category { get; }
    public string Code { get; }
    public bool Retryable { get; }
    /// <summary>Gets the opaque identity used to correlate redacted frontend and host diagnostics.</summary>
    public string CorrelationId { get; }
}

/// <summary>Describes one redacted presentation diagnostic.</summary>
public sealed record DesktopDiagnostic(
    DesktopErrorCategory Category,
    string Code,
    string Message,
    bool Retryable,
    string CorrelationId = "",
    string? Remediation = null)
{
    /// <summary>Gets whether the diagnostic prevents the configuration from working or only reports a limitation.</summary>
    public DesktopDiagnosticSeverity Severity { get; init; } = DesktopDiagnosticSeverity.Error;

    /// <summary>Gets the configuration property the diagnostic concerns, such as <c>DesktopWindowOptions.Transparent</c>.</summary>
    public string? Option { get; init; }
}

/// <summary>Identifies whether a configuration diagnostic is a failure or a limitation.</summary>
public enum DesktopDiagnosticSeverity
{
    /// <summary>The configuration cannot work as requested; opening the presentation fails.</summary>
    Error,

    /// <summary>The presentation opens, but ignores or narrows part of the request.</summary>
    Warning,
}

/// <summary>Reports a Desktop configuration that failed validation before a window opened.</summary>
public sealed class DesktopConfigurationException : DesktopException
{
    /// <summary>The stable code of every configuration validation failure.</summary>
    public const string ConfigurationInvalidCode = "desktop-configuration-invalid";

    /// <summary>Creates an exception that lists every failed configuration check in its message.</summary>
    public DesktopConfigurationException(IReadOnlyList<DesktopDiagnostic> diagnostics)
        : base(DesktopErrorCategory.InvalidArgument, ConfigurationInvalidCode, FormatMessage(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    /// <summary>Gets the failed checks.</summary>
    public IReadOnlyList<DesktopDiagnostic> Diagnostics { get; }

    private static string FormatMessage(IReadOnlyList<DesktopDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var message = new System.Text.StringBuilder("The Desktop configuration is invalid:");
        foreach (var diagnostic in diagnostics)
        {
            message.Append(Environment.NewLine).Append("- ").Append(diagnostic.Code).Append(": ").Append(diagnostic.Message);
            if (!string.IsNullOrWhiteSpace(diagnostic.Remediation))
            {
                message.Append(' ').Append(diagnostic.Remediation);
            }
        }
        return message.ToString();
    }
}
