namespace Runic.Desktop;

/// <summary>Identifies a stable presentation-boundary error category.</summary>
/// <remarks>The categories match the language-neutral Desktop presentation-host contract.</remarks>
public enum DesktopErrorCategory
{
    /// <summary>An argument or configuration value is invalid.</summary>
    InvalidArgument,

    /// <summary>The operation is not valid in the object's current state.</summary>
    InvalidState,

    /// <summary>A frame or structured payload is malformed.</summary>
    InvalidFrame,

    /// <summary>A size, depth, count, or other configured limit was exceeded.</summary>
    LimitExceeded,

    /// <summary>The session credential is missing or not valid for the surface.</summary>
    AuthenticationDenied,

    /// <summary>The requesting origin is not admitted by the surface security policy.</summary>
    OriginDenied,

    /// <summary>The capability or window option is not admitted or not supported.</summary>
    CapabilityDenied,

    /// <summary>The requested resource does not exist.</summary>
    NotFound,

    /// <summary>The operation conflicts with existing state.</summary>
    Conflict,

    /// <summary>The operation was cancelled before it completed.</summary>
    Cancelled,

    /// <summary>The operation did not complete within its deadline.</summary>
    TimedOut,

    /// <summary>The presentation session transport closed before the operation completed.</summary>
    TransportClosed,

    /// <summary>The Desktop host is stopping and no longer accepts the operation.</summary>
    HostStopping,

    /// <summary>The presentation, window host, or capability is not available.</summary>
    Unavailable,

    /// <summary>The operation failed for a reason the caller cannot correct; details are redacted.</summary>
    OperationFailed,
}

/// <summary>Represents a redacted, stable Desktop operation failure.</summary>
public class DesktopException : Exception
{
    /// <summary>Creates a Desktop failure with a stable category and code.</summary>
    /// <param name="category">The stable error category.</param>
    /// <param name="code">The stable, machine-readable error code, such as <c>origin-not-allowed</c>.</param>
    /// <param name="message">The safe, redacted message.</param>
    /// <param name="retryable">Whether repeating the operation may succeed.</param>
    /// <param name="innerException">The underlying failure, kept for diagnostics only.</param>
    /// <param name="correlationId">The diagnostic correlation identity, or <see langword="null"/> to generate one.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is <see langword="null"/>.</exception>
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

    /// <summary>Gets the stable error category.</summary>
    public DesktopErrorCategory Category { get; }

    /// <summary>Gets the stable, machine-readable error code.</summary>
    public string Code { get; }

    /// <summary>Gets whether repeating the operation may succeed.</summary>
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
