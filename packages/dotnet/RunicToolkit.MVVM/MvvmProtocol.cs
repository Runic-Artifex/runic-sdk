using System.Text;

namespace RunicToolkit.MVVM;

/// <summary>Identifies the protocol implemented by this runtime.</summary>
public static class MvvmProtocol
{
    /// <summary>The registered protocol identity.</summary>
    public const string Identity = "runic.toolkit.mvvm/1";

    /// <summary>The wire protocol major version.</summary>
    public const int MajorVersion = 1;
}

/// <summary>Stable, machine-readable protocol fault codes.</summary>
public static class MvvmFaultCodes
{
    /// <summary>The peer does not support the requested protocol.</summary>
    public const string ProtocolUnsupported = "protocol.unsupported";

    /// <summary>The request is malformed or is not valid in the current state.</summary>
    public const string RequestInvalid = "request.invalid";

    /// <summary>The requested generated member does not exist.</summary>
    public const string MemberUnknown = "member.unknown";

    /// <summary>The mutation was based on a stale revision.</summary>
    public const string RevisionStale = "revision.stale";

    /// <summary>A configured resource or payload limit was exceeded.</summary>
    public const string LimitExceeded = "limit.exceeded";

    /// <summary>The request was cancelled before it completed.</summary>
    public const string RequestCancelled = "request.cancelled";

    /// <summary>The request exceeded the configured execution timeout.</summary>
    public const string RequestTimeout = "request.timeout";

    /// <summary>The target session has already closed.</summary>
    public const string SessionClosed = "session.closed";

    /// <summary>Returns whether a code belongs to the closed protocol version 1 catalog.</summary>
    public static bool IsDefined(string code) => code is
        ProtocolUnsupported or
        RequestInvalid or
        MemberUnknown or
        RevisionStale or
        LimitExceeded or
        RequestCancelled or
        RequestTimeout or
        SessionClosed;
}

/// <summary>An ordinal, case-sensitive logical ViewModel contract identifier.</summary>
public readonly record struct MvvmContract
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>Creates a validated contract identifier.</summary>
    /// <param name="value">A non-empty identifier whose UTF-8 form is at most 128 bytes.</param>
    /// <exception cref="ArgumentException">The identifier is empty, invalid Unicode, or too long.</exception>
    public MvvmContract(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("The contract cannot contain control characters.", nameof(value));
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("The contract must contain valid Unicode.", nameof(value), exception);
        }

        if (byteCount > 128)
        {
            throw new ArgumentException("The contract must be at most 128 UTF-8 bytes.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the logical contract value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Identifies one runtime session.</summary>
public readonly record struct MvvmSessionId
{
    /// <summary>Creates a non-empty session identifier.</summary>
    public MvvmSessionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A session identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the UUID value.</summary>
    public Guid Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Identifies one request within a session.</summary>
public readonly record struct MvvmRequestId
{
    /// <summary>Creates a non-empty request identifier.</summary>
    public MvvmRequestId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A request identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the UUID value.</summary>
    public Guid Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}
