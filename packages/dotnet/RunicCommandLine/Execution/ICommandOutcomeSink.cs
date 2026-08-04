using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RunicCommandLine;

/// <summary>Receives a closed typed outcome for human or machine presentation.</summary>
public interface ICommandOutcomeSink
{
    /// <summary>Writes one complete invocation outcome.</summary>
    ValueTask WriteAsync<TResult>(
        CommandDescriptor command,
        CommandExecutionContext context,
        CommandOutcome<TResult> outcome,
        ICommandResultCodec<TResult> codec,
        int exitCode,
        IReadOnlyList<CommandDiagnostic> diagnostics,
        CancellationToken cancellationToken);
}
