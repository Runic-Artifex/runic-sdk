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
    string? Remediation = null);
