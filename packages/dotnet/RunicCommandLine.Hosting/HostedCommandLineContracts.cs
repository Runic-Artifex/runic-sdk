using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using RunicCommandLine;

namespace RunicCommandLine.Hosting;

/// <summary>Controls how an explicitly empty launch is classified.</summary>
public enum EmptyInputFallback
{
    /// <summary>Preserve command-line grammar behavior and report an invalid launch.</summary>
    Invalid = 0,

    /// <summary>Select the host's user-interface mode without parsing a command.</summary>
    UserInterface = 1,
}

/// <summary>Identifies the neutral result of hosted command-line classification.</summary>
public enum HostedCommandLineDecisionKind
{
    /// <summary>A valid command invocation is ready for command-line execution.</summary>
    Invocation = 0,

    /// <summary>Help was requested without invoking a command handler.</summary>
    Help = 1,

    /// <summary>Version information was requested without invoking a command handler.</summary>
    Version = 2,

    /// <summary>The captured arguments are not a valid command-line launch.</summary>
    Invalid = 3,

    /// <summary>An explicit empty-input policy selected the host user interface.</summary>
    UserInterface = 4,
}

/// <summary>Contains replayable launch inputs without reading process-global state.</summary>
public sealed class HostedCommandLineLaunchInput
{
    /// <summary>Initializes captured arguments and output-environment input.</summary>
    public HostedCommandLineLaunchInput(
        IReadOnlyList<string> arguments,
        string? outputEnvironmentValue = null,
        EmptyInputFallback emptyInputFallback = EmptyInputFallback.Invalid,
        CommandOutputMode defaultOutputMode = CommandOutputMode.Human)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!Enum.IsDefined(emptyInputFallback))
        {
            throw new ArgumentOutOfRangeException(nameof(emptyInputFallback));
        }

        if (!Enum.IsDefined(defaultOutputMode))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultOutputMode));
        }

        var snapshot = new string[arguments.Count];
        for (int index = 0; index < arguments.Count; index++)
        {
            snapshot[index] = arguments[index] ?? throw new ArgumentException(
                "Launch arguments cannot contain null entries.",
                nameof(arguments));
        }

        Arguments = new ReadOnlyCollection<string>(snapshot);
        OutputEnvironmentValue = outputEnvironmentValue;
        EmptyInputFallback = emptyInputFallback;
        DefaultOutputMode = defaultOutputMode;
    }

    /// <summary>Gets the immutable captured argument sequence.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets the captured <c>RUNIC_COMMANDLINE_OUTPUT</c> value, if any.</summary>
    public string? OutputEnvironmentValue { get; }

    /// <summary>Gets the explicit policy for an empty argument sequence.</summary>
    public EmptyInputFallback EmptyInputFallback { get; }

    /// <summary>Gets the output mode used when no higher-precedence source is supplied.</summary>
    public CommandOutputMode DefaultOutputMode { get; }
}

/// <summary>Contains a frozen parser-neutral decision for a hosted launch.</summary>
public sealed class HostedCommandLineDecision
{
    private readonly ParsedInvocation? _invocation;
    private readonly object? _owner;

    /// <summary>Initializes an immutable decision for a custom hosted adapter.</summary>
    /// <remarks>
    /// An invocation created through this constructor is executable only by the
    /// adapter that created it. The first-party adapter attaches its parsed
    /// invocation internally so it can delegate to <see cref="CommandExecutor"/>.
    /// </remarks>
    public HostedCommandLineDecision(
        HostedCommandLineDecisionKind kind,
        HostedCommandLineLaunchInput input,
        CommandPath? path = null,
        IReadOnlyList<CommandDiagnostic>? diagnostics = null,
        CommandOutputClassification? outputClassification = null)
        : this(kind, input, null, null, path, diagnostics, outputClassification)
    {
    }

    internal HostedCommandLineDecision(
        HostedCommandLineDecisionKind kind,
        HostedCommandLineLaunchInput input,
        ParsedInvocation? invocation = null,
        object? owner = null,
        CommandPath? path = null,
        IReadOnlyList<CommandDiagnostic>? diagnostics = null,
        CommandOutputClassification? outputClassification = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        ArgumentNullException.ThrowIfNull(input);
        Arguments = input.Arguments;
        _invocation = invocation;
        _owner = owner;
        Path = path;
        Diagnostics = FreezeDiagnostics(diagnostics);
        OutputClassification = outputClassification;
    }

    /// <summary>Gets the selected neutral launch category.</summary>
    public HostedCommandLineDecisionKind Kind { get; }

    /// <summary>Gets the immutable argument snapshot that was classified.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets the canonical command or help path when one was resolved.</summary>
    public CommandPath? Path { get; }

    /// <summary>Gets the canonical root command name when a command path was resolved.</summary>
    public string? CommandName => Path is { Count: > 0 } ? Path[0] : null;

    /// <summary>Gets parser-owned safe diagnostics for an invalid decision.</summary>
    public IReadOnlyList<CommandDiagnostic> Diagnostics { get; }

    /// <summary>Gets the frozen output classification when parsing reached that stage.</summary>
    public CommandOutputClassification? OutputClassification { get; }

    /// <summary>Gets whether this decision can be executed by the command-line engine.</summary>
    public bool CanExecute => Kind == HostedCommandLineDecisionKind.Invocation;

    internal ParsedInvocation GetInvocation(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!CanExecute || _invocation is null)
        {
            throw new InvalidOperationException("Only an invocation decision can be executed.");
        }

        if (!ReferenceEquals(_owner, owner))
        {
            throw new InvalidOperationException(
                "The invocation decision can be executed only by the adapter that created it.");
        }

        return _invocation;
    }

    private static IReadOnlyList<CommandDiagnostic> FreezeDiagnostics(
        IReadOnlyList<CommandDiagnostic>? diagnostics)
    {
        if (diagnostics is null || diagnostics.Count == 0)
        {
            return Array.Empty<CommandDiagnostic>();
        }

        var snapshot = new CommandDiagnostic[diagnostics.Count];
        for (int index = 0; index < diagnostics.Count; index++)
        {
            snapshot[index] = diagnostics[index] ?? throw new ArgumentException(
                "Diagnostics cannot contain null entries.",
                nameof(diagnostics));
        }

        return new ReadOnlyCollection<CommandDiagnostic>(snapshot);
    }
}

/// <summary>Contains presentation inputs for one valid hosted command invocation.</summary>
public sealed class HostedCommandLineExecutionInput
{
    /// <summary>Initializes execution inputs without introducing a host service scope.</summary>
    public HostedCommandLineExecutionInput(
        HostedCommandLineDecision decision,
        ICommandConsole console,
        CultureInfo culture,
        string correlationId,
        ICommandOutcomeSink outcomeSink)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentNullException.ThrowIfNull(outcomeSink);
        if (!decision.CanExecute)
        {
            throw new ArgumentException("Execution requires an invocation decision.", nameof(decision));
        }

        Decision = decision;
        Console = console;
        Culture = CultureInfo.ReadOnly((CultureInfo)culture.Clone());
        CorrelationId = correlationId;
        OutcomeSink = outcomeSink;
    }

    /// <summary>Gets the invocation decision produced by classification.</summary>
    public HostedCommandLineDecision Decision { get; }

    /// <summary>Gets the invocation-local console.</summary>
    public ICommandConsole Console { get; }

    /// <summary>Gets the presentation culture.</summary>
    public CultureInfo Culture { get; }

    /// <summary>Gets the opaque invocation correlation identifier.</summary>
    public string CorrelationId { get; }

    /// <summary>Gets the outcome presentation sink.</summary>
    public ICommandOutcomeSink OutcomeSink { get; }
}

/// <summary>Contains the completed result returned by the command-line engine.</summary>
public sealed class HostedCommandLineExecutionResult
{
    internal HostedCommandLineExecutionResult(CommandExecutionResult commandResult)
    {
        ArgumentNullException.ThrowIfNull(commandResult);
        ExitCategory = commandResult.ExitCategory;
        ExitCode = commandResult.ExitCode;
        Fault = commandResult.Fault;
    }

    /// <summary>Gets the semantic command outcome category.</summary>
    public CommandExitCategory ExitCategory { get; }

    /// <summary>Gets the command engine's selected process exit code.</summary>
    public int ExitCode { get; }

    /// <summary>Gets the safe command fault, when execution was unsuccessful.</summary>
    public CommandFault? Fault { get; }

    /// <summary>Gets whether the command engine completed successfully.</summary>
    public bool IsSuccess => ExitCategory == CommandExitCategory.Success;
}

/// <summary>Classifies and executes command-line launches for an application host.</summary>
public interface IHostedCommandLineAdapter
{
    /// <summary>Classifies captured inputs without executing a handler or creating a scope.</summary>
    HostedCommandLineDecision Classify(HostedCommandLineLaunchInput input);

    /// <summary>Delegates a valid invocation to the command-line execution engine.</summary>
    ValueTask<HostedCommandLineExecutionResult> ExecuteAsync(
        HostedCommandLineExecutionInput input,
        CancellationToken cancellationToken = default);
}
