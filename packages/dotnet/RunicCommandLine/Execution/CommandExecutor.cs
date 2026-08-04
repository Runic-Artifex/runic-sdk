using System;
using System.Threading;
using System.Threading.Tasks;

namespace RunicCommandLine;

/// <summary>Executes closed typed command registrations with deterministic scope ownership.</summary>
public sealed class CommandExecutor
{
    private readonly ICommandExecutionScopeFactory _scopeFactory;
    private readonly IExitCodePolicy _exitCodePolicy;
    private readonly ICommandExecutionObserver? _observer;

    /// <summary>Initializes a command executor.</summary>
    public CommandExecutor(
        ICommandExecutionScopeFactory scopeFactory,
        IExitCodePolicy? exitCodePolicy = null,
        ICommandExecutionObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
        _exitCodePolicy = exitCodePolicy ?? DefaultExitCodePolicy.Instance;
        _observer = observer;
        ValidateExitPolicy(_exitCodePolicy);
    }

    /// <summary>Executes a successfully parsed invocation and dispatches exactly one typed outcome.</summary>
    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandExecutionRequest request,
        ICommandOutcomeSink outcomeSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(outcomeSink);
        return request.Invocation.Command.Registration.ExecuteAsync(
            request.Invocation.Command,
            request,
            _scopeFactory,
            _exitCodePolicy,
            outcomeSink,
            _observer,
            cancellationToken);
    }

    private static void ValidateExitPolicy(IExitCodePolicy policy)
    {
        foreach (CommandExitCategory category in Enum.GetValues<CommandExitCategory>())
        {
            int exitCode = policy.GetExitCode(category);
            if ((category == CommandExitCategory.Success) != (exitCode == 0))
            {
                throw new ArgumentException(
                    "An exit-code policy must map success to zero and every failure to a non-zero value.",
                    nameof(policy));
            }
        }
    }
}
