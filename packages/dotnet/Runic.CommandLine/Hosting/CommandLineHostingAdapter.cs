using System;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;

namespace Runic.CommandLine.Hosting;

/// <summary>
/// First-party bridge that delegates syntax analysis and command scope ownership
/// to the existing CommandLine parser and executor.
/// </summary>
public sealed class CommandLineHostingAdapter : IHostedCommandLineAdapter
{
    private readonly object _decisionOwner = new();
    private readonly CommandCatalog _catalog;
    private readonly ICommandSyntaxAdapter _syntaxAdapter;
    private readonly CommandExecutor _executor;

    /// <summary>Initializes the bridge with the application command catalog and executor.</summary>
    public CommandLineHostingAdapter(
        CommandCatalog catalog,
        CommandExecutor executor,
        ICommandSyntaxAdapter? syntaxAdapter = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(executor);
        _catalog = catalog;
        _executor = executor;
        _syntaxAdapter = syntaxAdapter ?? PortableCommandSyntaxAdapter.Instance;
    }

    /// <summary>Gets framework presentation shared with the standalone runner.</summary>
    public CommandPresentation Presentation { get; init; } = new();

    /// <inheritdoc />
    public HostedCommandLineDecision Classify(HostedCommandLineLaunchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Arguments.Count == 0 &&
            input.EmptyInputFallback == EmptyInputFallback.UserInterface)
        {
            return new HostedCommandLineDecision(
                HostedCommandLineDecisionKind.UserInterface,
                input,
                invocation: null,
                owner: _decisionOwner);
        }

        if (input.Arguments.Count == 2 && input.Arguments[0] == "completion" && !_catalog.TryGetCommand("completion", out _))
            return new HostedCommandLineDecision(HostedCommandLineDecisionKind.Completion, input, owner: _decisionOwner);

        ParseOutcome outcome = _syntaxAdapter.Parse(
            _catalog,
            input.Arguments.ToArray(),
            new ParseSettings(
                input.OutputEnvironmentValue,
                input.DefaultOutputMode,
                input.TransportOutputOptionName)
            {
                GetEnvironmentVariable = name => input.EnvironmentVariables.TryGetValue(name, out string? value) ? value : null,
            });

        HostedCommandLineDecision decision = outcome.Kind switch
        {
            ParseOutcomeKind.Invocation when outcome.Invocation is not null =>
                new HostedCommandLineDecision(
                    HostedCommandLineDecisionKind.Invocation,
                    input,
                    outcome.Invocation,
                    _decisionOwner,
                    outcome.Invocation.Path,
                    outcome.Diagnostics,
                    outcome.OutputClassification),
            ParseOutcomeKind.Help when outcome.HelpRequest is not null =>
                new HostedCommandLineDecision(
                    HostedCommandLineDecisionKind.Help,
                    input,
                    invocation: null,
                    owner: _decisionOwner,
                    path: outcome.HelpRequest.Path,
                    diagnostics: outcome.Diagnostics,
                    outputClassification: outcome.OutputClassification) { ParseOutcome = outcome },
            ParseOutcomeKind.Version =>
                new HostedCommandLineDecision(
                    HostedCommandLineDecisionKind.Version,
                    input,
                    invocation: null,
                    owner: _decisionOwner,
                    diagnostics: outcome.Diagnostics,
                    outputClassification: outcome.OutputClassification) { ParseOutcome = outcome },
            ParseOutcomeKind.Error =>
                new HostedCommandLineDecision(
                    HostedCommandLineDecisionKind.Invalid,
                    input,
                    invocation: null,
                    owner: _decisionOwner,
                    diagnostics: outcome.Diagnostics,
                    outputClassification: outcome.OutputClassification) { ParseOutcome = outcome },
            _ => throw new InvalidOperationException("The command syntax adapter returned an incomplete outcome."),
        };
        return decision;
    }

    /// <summary>Presents help, version, usage errors or completion without creating a scope or owning host cancellation.</summary>
    public ValueTask<int> PresentAsync(HostedCommandLineDecision decision, ICommandConsole console,
        CultureInfo culture, string correlationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        decision.ValidateOwner(_decisionOwner);
        if (decision.Kind == HostedCommandLineDecisionKind.Completion)
            return Presentation.GuardAsync(() => Presentation.WriteCompletionAsync(_catalog, decision.Arguments[1], decision.LaunchInput.TransportOutputOptionName, console, cancellationToken), cancellationToken);
        if (decision.Kind is not (HostedCommandLineDecisionKind.Help or HostedCommandLineDecisionKind.Version or HostedCommandLineDecisionKind.Invalid) || decision.ParseOutcome is null)
            throw new InvalidOperationException("Presentation requires a framework decision created by this adapter.");
        return Presentation.GuardAsync(() => Presentation.WriteAsync(_catalog, decision.ParseOutcome, decision.LaunchInput.TransportOutputOptionName,
            console, culture, correlationId, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<HostedCommandLineExecutionResult> ExecuteAsync(
        HostedCommandLineExecutionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ParsedInvocation invocation = input.Decision.GetInvocation(_decisionOwner);
        var request = new CommandExecutionRequest(
            invocation,
            input.Console,
            input.Culture,
            input.CorrelationId) { ExceptionObserver = input.ExceptionObserver };
        CommandExecutionResult result = await _executor.ExecuteAsync(
            request,
            input.OutcomeSink,
            cancellationToken).ConfigureAwait(false);
        return new HostedCommandLineExecutionResult(result);
    }
}
