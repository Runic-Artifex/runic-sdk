using System;

namespace RunicCommandLine;

/// <summary>Reports a malformed or unsupported command protocol response.</summary>
public sealed class CommandProtocolException : Exception
{
    /// <summary>Initializes a protocol exception with a stable error kind.</summary>
    public CommandProtocolException(string kind, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        Kind = kind;
    }

    /// <summary>Initializes a protocol exception with a stable error kind and inner exception.</summary>
    public CommandProtocolException(string kind, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(innerException);
        Kind = kind;
    }

    /// <summary>Gets the stable error kind.</summary>
    public string Kind { get; }
}
