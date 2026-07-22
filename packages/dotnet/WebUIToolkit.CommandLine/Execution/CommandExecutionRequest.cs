using System;
using System.Globalization;
using System.Text;

namespace WebUIToolkit.CommandLine;

/// <summary>Supplies invocation-local presentation state to the command executor.</summary>
public sealed class CommandExecutionRequest
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>Initializes an execution request for a successfully parsed invocation.</summary>
    public CommandExecutionRequest(
        ParsedInvocation invocation,
        ICommandConsole console,
        CultureInfo culture,
        string correlationId)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(culture);
        ValidateCorrelationId(correlationId);

        Invocation = invocation;
        Console = console;
        Culture = CultureInfo.ReadOnly((CultureInfo)culture.Clone());
        CorrelationId = correlationId;
    }

    private static void ValidateCorrelationId(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(correlationId);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "The correlation ID must contain valid Unicode text.",
                nameof(correlationId),
                exception);
        }

        if (byteCount > 128)
        {
            throw new ArgumentException("The correlation ID cannot exceed 128 UTF-8 bytes.", nameof(correlationId));
        }

        foreach (char character in correlationId)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                throw new ArgumentException(
                    "The correlation ID cannot contain whitespace or control characters.",
                    nameof(correlationId));
            }
        }

        foreach (Rune rune in correlationId.EnumerateRunes())
        {
            int scalar = rune.Value;
            if (scalar is >= 0xFDD0 and <= 0xFDEF || (scalar & 0xFFFE) == 0xFFFE)
            {
                throw new ArgumentException(
                    "The correlation ID cannot contain a Unicode noncharacter.",
                    nameof(correlationId));
            }
        }
    }

    /// <summary>Gets the neutral parsed invocation.</summary>
    public ParsedInvocation Invocation { get; }

    /// <summary>Gets the invocation-local console.</summary>
    public ICommandConsole Console { get; }

    /// <summary>Gets the culture used for presentation.</summary>
    public CultureInfo Culture { get; }

    /// <summary>Gets the safe opaque correlation identifier.</summary>
    public string CorrelationId { get; }
}
