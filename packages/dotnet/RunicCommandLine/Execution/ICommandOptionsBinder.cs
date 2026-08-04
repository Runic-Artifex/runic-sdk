using System.Threading;
using System.Threading.Tasks;

namespace RunicCommandLine;

/// <summary>Binds a neutral parsed invocation to immutable typed command options.</summary>
/// <typeparam name="TOptions">The command options type.</typeparam>
public interface ICommandOptionsBinder<TOptions>
{
    /// <summary>Binds and validates one parsed invocation without reflection discovery.</summary>
    ValueTask<CommandOutcome<TOptions>> BindAsync(
        ParsedInvocation invocation,
        CancellationToken cancellationToken);
}
