using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace WebUIToolkit.CommandLine;

/// <summary>Identifies the parser-owned shape of a command-line parse result.</summary>
public enum ParseOutcomeKind
{
    /// <summary>A command and all of its values were parsed successfully.</summary>
    Invocation = 0,

    /// <summary>Help was requested without invoking a handler.</summary>
    Help = 1,

    /// <summary>The root version was requested without invoking a handler.</summary>
    Version = 2,

    /// <summary>One or more usage diagnostics prevented invocation.</summary>
    Error = 3,
}

/// <summary>Provides captured, replayable inputs that affect command parsing.</summary>
public sealed class ParseSettings
{
    /// <summary>Gets shared settings that select human output by default.</summary>
    public static ParseSettings Default { get; } = new();

    /// <summary>Initializes parser settings without reading process-global state.</summary>
    /// <param name="outputEnvironmentValue">
    /// The captured value of <see cref="CommandOutputClassifier.EnvironmentVariableName"/>.
    /// </param>
    /// <param name="defaultOutputMode">The output mode used when no higher-precedence source is present.</param>
    public ParseSettings(
        string? outputEnvironmentValue = null,
        CommandOutputMode defaultOutputMode = CommandOutputMode.Human)
    {
        if (!Enum.IsDefined(defaultOutputMode))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultOutputMode));
        }

        OutputEnvironmentValue = outputEnvironmentValue;
        DefaultOutputMode = defaultOutputMode;
    }

    /// <summary>Gets the captured output environment value, including an invalid value.</summary>
    public string? OutputEnvironmentValue { get; }

    /// <summary>Gets the output mode used when no explicit or environment value is present.</summary>
    public CommandOutputMode DefaultOutputMode { get; }
}

/// <summary>Represents one immutable option or argument binding.</summary>
public sealed class CommandValueBinding
{
    /// <summary>Initializes a binding with values in encounter order.</summary>
    /// <param name="id">The stable catalog parameter identifier.</param>
    /// <param name="values">The exact, unconverted values.</param>
    public CommandValueBinding(string id, IReadOnlyList<string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(values);

        var copy = new string[values.Count];
        for (int index = 0; index < values.Count; index++)
        {
            copy[index] = values[index] ??
                throw new ArgumentException("Binding values cannot contain null.", nameof(values));
        }

        Id = id;
        Values = Array.AsReadOnly(copy);
    }

    /// <summary>Gets the stable catalog parameter identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the exact values in encounter order.</summary>
    public IReadOnlyList<string> Values { get; }
}

/// <summary>Represents one immutable parser-neutral command invocation.</summary>
public sealed class ParsedInvocation
{
    /// <summary>Initializes a successfully parsed invocation.</summary>
    public ParsedInvocation(
        CommandDescriptor command,
        CommandPath path,
        IReadOnlyList<CommandValueBinding> options,
        IReadOnlyList<CommandValueBinding> arguments,
        CommandOutputClassification outputClassification)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!outputClassification.IsValid)
        {
            throw new ArgumentException(
                "An invocation requires a valid output classification.",
                nameof(outputClassification));
        }

        Command = command;
        Path = path;
        Options = Freeze(options, nameof(options));
        Arguments = Freeze(arguments, nameof(arguments));
        OutputClassification = outputClassification;
    }

    /// <summary>Gets the resolved immutable command descriptor.</summary>
    public CommandDescriptor Command { get; }

    /// <summary>Gets the canonical command path.</summary>
    public CommandPath Path { get; }

    /// <summary>Gets option bindings in first-occurrence order.</summary>
    public IReadOnlyList<CommandValueBinding> Options { get; }

    /// <summary>Gets argument bindings in descriptor order.</summary>
    public IReadOnlyList<CommandValueBinding> Arguments { get; }

    /// <summary>Gets the frozen output-mode decision for dispatch.</summary>
    public CommandOutputClassification OutputClassification { get; }

    private static ReadOnlyCollection<CommandValueBinding> Freeze(
        IReadOnlyList<CommandValueBinding> bindings,
        string parameterName)
    {
        var copy = new CommandValueBinding[bindings.Count];
        for (int index = 0; index < bindings.Count; index++)
        {
            copy[index] = bindings[index] ??
                throw new ArgumentException("Bindings cannot contain null.", parameterName);
        }

        return new ReadOnlyCollection<CommandValueBinding>(copy);
    }
}

/// <summary>Represents a help request for a canonical command path.</summary>
public sealed record HelpRequest
{
    /// <summary>Initializes a help request.</summary>
    public HelpRequest(CommandPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Path = path;
    }

    /// <summary>Gets the canonical path, or the root path for root help.</summary>
    public CommandPath Path { get; }
}

/// <summary>Represents the immutable result of parser-neutral syntax analysis.</summary>
public sealed class ParseOutcome
{
    private static readonly IReadOnlyList<CommandDiagnostic> NoDiagnostics =
        Array.Empty<CommandDiagnostic>();

    private ParseOutcome(
        ParseOutcomeKind kind,
        ParsedInvocation? invocation,
        HelpRequest? helpRequest,
        IReadOnlyList<CommandDiagnostic> diagnostics,
        CommandOutputClassification? outputClassification)
    {
        Kind = kind;
        Invocation = invocation;
        HelpRequest = helpRequest;
        Diagnostics = Freeze(diagnostics);
        OutputClassification = outputClassification;
    }

    /// <summary>Gets the discriminated result kind.</summary>
    public ParseOutcomeKind Kind { get; }

    /// <summary>Gets the parsed invocation when <see cref="Kind"/> is <see cref="ParseOutcomeKind.Invocation"/>.</summary>
    public ParsedInvocation? Invocation { get; }

    /// <summary>Gets the help request when <see cref="Kind"/> is <see cref="ParseOutcomeKind.Help"/>.</summary>
    public HelpRequest? HelpRequest { get; }

    /// <summary>Gets ordered safe diagnostics for an error outcome.</summary>
    public IReadOnlyList<CommandDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Gets the frozen output classification for invocation, help, version, or
    /// an invalid environment value. It is null for syntax errors that prevent classification.
    /// </summary>
    public CommandOutputClassification? OutputClassification { get; }

    /// <summary>Creates an invocation result.</summary>
    public static ParseOutcome FromInvocation(ParsedInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return new(
            ParseOutcomeKind.Invocation,
            invocation,
            null,
            NoDiagnostics,
            invocation.OutputClassification);
    }

    /// <summary>Creates a help result.</summary>
    public static ParseOutcome FromHelp(
        HelpRequest request,
        CommandOutputClassification outputClassification)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureValid(outputClassification);
        return new(ParseOutcomeKind.Help, null, request, NoDiagnostics, outputClassification);
    }

    /// <summary>Creates a root version result.</summary>
    public static ParseOutcome FromVersion(CommandOutputClassification outputClassification)
    {
        EnsureValid(outputClassification);
        return new(ParseOutcomeKind.Version, null, null, NoDiagnostics, outputClassification);
    }

    /// <summary>Creates a syntax error result.</summary>
    public static ParseOutcome FromError(CommandDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new(ParseOutcomeKind.Error, null, null, new[] { diagnostic }, null);
    }

    /// <summary>Creates an invalid-output-classification result.</summary>
    public static ParseOutcome FromOutputError(
        CommandDiagnostic diagnostic,
        CommandOutputClassification outputClassification)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (outputClassification.IsValid)
        {
            throw new ArgumentException(
                "An output error requires an invalid classification.",
                nameof(outputClassification));
        }

        return new(
            ParseOutcomeKind.Error,
            null,
            null,
            new[] { diagnostic },
            outputClassification);
    }

    private static ReadOnlyCollection<CommandDiagnostic> Freeze(IReadOnlyList<CommandDiagnostic> diagnostics)
    {
        var copy = new CommandDiagnostic[diagnostics.Count];
        for (int index = 0; index < diagnostics.Count; index++)
        {
            copy[index] = diagnostics[index] ??
                throw new ArgumentException("Diagnostics cannot contain null.", nameof(diagnostics));
        }

        return new ReadOnlyCollection<CommandDiagnostic>(copy);
    }

    private static void EnsureValid(CommandOutputClassification classification)
    {
        if (!classification.IsValid)
        {
            throw new ArgumentException("A successful parse requires a valid output classification.");
        }
    }
}

/// <summary>Converts pre-tokenized arguments into the library-owned invocation model.</summary>
public interface ICommandSyntaxAdapter
{
    /// <summary>Parses one captured argument sequence without consulting global state.</summary>
    ParseOutcome Parse(
        CommandCatalog catalog,
        ReadOnlySpan<string> args,
        ParseSettings settings);
}
