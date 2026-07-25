using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.CommandLine;

namespace WebUIToolkit.CommandLine.Hosting;

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

        ParseOutcome outcome = _syntaxAdapter.Parse(
            _catalog,
            input.Arguments.ToArray(),
            new ParseSettings(input.OutputEnvironmentValue, input.DefaultOutputMode));

        return outcome.Kind switch
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
                    outputClassification: outcome.OutputClassification),
            ParseOutcomeKind.Version =>
                new HostedCommandLineDecision(
                    HostedCommandLineDecisionKind.Version,
                    input,
                    invocation: null,
                    owner: _decisionOwner,
                    diagnostics: outcome.Diagnostics,
                    outputClassification: outcome.OutputClassification),
            ParseOutcomeKind.Error =>
                new HostedCommandLineDecision(
                    HostedCommandLineDecisionKind.Invalid,
                    input,
                    invocation: null,
                    owner: _decisionOwner,
                    diagnostics: outcome.Diagnostics,
                    outputClassification: outcome.OutputClassification),
            _ => throw new InvalidOperationException("The command syntax adapter returned an incomplete outcome."),
        };
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
            input.CorrelationId);
        CommandExecutionResult result = await _executor.ExecuteAsync(
            request,
            input.OutcomeSink,
            cancellationToken).ConfigureAwait(false);
        return new HostedCommandLineExecutionResult(result);
    }
}
