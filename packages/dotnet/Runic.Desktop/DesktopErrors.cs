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
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Category = category;
        Code = code;
        Retryable = retryable;
    }

    public DesktopErrorCategory Category { get; }
    public string Code { get; }
    public bool Retryable { get; }
}
