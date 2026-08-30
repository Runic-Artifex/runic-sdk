using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Runic.CommandLine;

/// <summary>Identifies the command pipeline phase that produced a diagnostic.</summary>
public enum CommandDiagnosticPhase
{
    /// <summary>The token sequence could not be parsed.</summary>
    Parse = 0,

    /// <summary>Parsed values could not be bound or validated.</summary>
    Binding = 1,

    /// <summary>Command execution produced a diagnostic.</summary>
    Execution = 2,
}

/// <summary>Identifies the presentation severity of a command diagnostic.</summary>
public enum CommandDiagnosticSeverity
{
    /// <summary>The invocation cannot continue.</summary>
    Error = 0,

    /// <summary>The invocation may continue with a caution.</summary>
    Warning = 1,

    /// <summary>The diagnostic is informational.</summary>
    Information = 2,
}

/// <summary>Describes a stable, consumer-safe command diagnostic.</summary>
/// <remarks>
/// Messages must not contain raw secret-bearing token values, stack traces,
/// exception type names, environment variables, or internal absolute paths.
/// </remarks>
public sealed record CommandDiagnostic
{
    private const int MaximumArgumentCount = 16;
    private static readonly IReadOnlyList<string> NoArguments =
        new ReadOnlyCollection<string>(Array.Empty<string>());

    /// <summary>Initializes a stable command diagnostic.</summary>
    /// <param name="code">A reserved <c>RCLI####</c> diagnostic identifier.</param>
    /// <param name="kind">A stable symbolic diagnostic kind.</param>
    /// <param name="message">A safe, non-empty presentation message.</param>
    /// <param name="phase">The pipeline phase that produced the diagnostic.</param>
    /// <param name="severity">The diagnostic severity.</param>
    /// <param name="tokenIndex">The zero-based token index, when applicable.</param>
    /// <param name="arguments">
    /// Up to 16 ordered, safe presentation arguments. Raw secret-bearing token
    /// values must not be supplied.
    /// </param>
    /// <param name="path">
    /// The canonical command path associated with the diagnostic, or null for
    /// the catalog root.
    /// </param>
    /// <param name="messageKey">
    /// A bounded localization key, or null to use <c>diagnostics.{kind}</c>.
    /// </param>
    /// <exception cref="ArgumentException">A required string is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An enum value is not defined or <paramref name="tokenIndex"/> is negative.
    /// </exception>
    public CommandDiagnostic(
        string code,
        string kind,
        string message,
        CommandDiagnosticPhase phase,
        CommandDiagnosticSeverity severity,
        int? tokenIndex = null,
        IEnumerable<string>? arguments = null,
        CommandPath? path = null,
        string? messageKey = null)
    {
        ValidateCode(code);
        ValidateKind(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        if (tokenIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenIndex));
        }

        Code = code;
        Kind = kind;
        Message = message;
        Phase = phase;
        Severity = severity;
        TokenIndex = tokenIndex;
        Arguments = CopyArguments(arguments);
        Path = path ?? CommandPath.Root;
        MessageKey = ValidateMessageKey(messageKey ?? $"diagnostics.{kind}");
    }

    /// <summary>Gets the reserved <c>RCLI####</c> identifier.</summary>
    public string Code { get; }

    /// <summary>Gets the stable symbolic diagnostic kind.</summary>
    public string Kind { get; }

    /// <summary>Gets the safe presentation message.</summary>
    public string Message { get; }

    /// <summary>Gets the pipeline phase that produced the diagnostic.</summary>
    public CommandDiagnosticPhase Phase { get; }

    /// <summary>Gets the diagnostic severity.</summary>
    public CommandDiagnosticSeverity Severity { get; }

    /// <summary>Gets the zero-based token index, when applicable.</summary>
    public int? TokenIndex { get; }

    /// <summary>Gets ordered safe presentation arguments.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets the canonical command path associated with the diagnostic.</summary>
    public CommandPath Path { get; }

    /// <summary>Gets the stable localization message key.</summary>
    public string MessageKey { get; }

    private static IReadOnlyList<string> CopyArguments(IEnumerable<string>? arguments)
    {
        if (arguments is null)
        {
            return NoArguments;
        }

        var copy = new List<string>();
        foreach (string argument in arguments)
        {
            if (argument is null)
            {
                throw new ArgumentException(
                    "Diagnostic arguments cannot contain null.",
                    nameof(arguments));
            }

            if (copy.Count == MaximumArgumentCount)
            {
                throw new ArgumentException(
                    $"Diagnostics cannot contain more than {MaximumArgumentCount} arguments.",
                    nameof(arguments));
            }

            copy.Add(argument);
        }

        return copy.Count == 0
            ? NoArguments
            : new ReadOnlyCollection<string>([.. copy]);
    }

    private static void ValidateCode(string code)
    {
        const int prefixLength = 4;
        const int codeLength = 8;

        if (code is null ||
            code.Length != codeLength ||
            !code.StartsWith("RCLI", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Diagnostic codes must be reserved RCLI0001 through RCLI9999 identifiers.",
                nameof(code));
        }

        int number = 0;
        for (int index = prefixLength; index < codeLength; index++)
        {
            char character = code[index];
            if (character is < '0' or > '9')
            {
                throw new ArgumentException(
                    "Diagnostic codes must be reserved RCLI0001 through RCLI9999 identifiers.",
                    nameof(code));
            }

            number = (number * 10) + (character - '0');
        }

        if (number == 0)
        {
            throw new ArgumentException(
                "Diagnostic codes must be reserved RCLI0001 through RCLI9999 identifiers.",
                nameof(code));
        }
    }

    private static string ValidateMessageKey(string messageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageKey);
        if (messageKey.Length > 128 || !IsAsciiLetter(messageKey[0]))
        {
            throw new ArgumentException(
                "Diagnostic message keys must be safe identifiers of at most 128 characters.",
                nameof(messageKey));
        }

        for (int index = 1; index < messageKey.Length; index++)
        {
            char character = messageKey[index];
            if (!IsAsciiLetter(character) &&
                character is not (>= '0' and <= '9') &&
                character is not '.' and not '_' and not '-')
            {
                throw new ArgumentException(
                    "Diagnostic message keys must contain only ASCII letters, digits, dots, underscores, or hyphens.",
                    nameof(messageKey));
            }
        }

        return messageKey;
    }

    private static void ValidateKind(string kind)
    {
        if (string.IsNullOrEmpty(kind) || kind.Length > 128 || kind[0] is < 'a' or > 'z')
        {
            throw new ArgumentException(
                "Diagnostic kinds must match [a-z][a-z0-9-]* and cannot exceed 128 ASCII bytes.",
                nameof(kind));
        }

        for (int index = 1; index < kind.Length; index++)
        {
            char character = kind[index];
            if (character != '-' &&
                character is not (>= 'a' and <= 'z') &&
                character is not (>= '0' and <= '9'))
            {
                throw new ArgumentException(
                    "Diagnostic kinds must match [a-z][a-z0-9-]*.",
                    nameof(kind));
            }

            if (character == '-' && (index == kind.Length - 1 || kind[index - 1] == '-'))
            {
                throw new ArgumentException(
                    "Diagnostic kinds cannot contain consecutive or trailing hyphens.",
                    nameof(kind));
            }
        }
    }

    private static bool IsAsciiLetter(char character) =>
        character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');
}
