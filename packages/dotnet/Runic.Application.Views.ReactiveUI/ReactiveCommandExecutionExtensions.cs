// Shared by Runic.Application.Views.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.Views.ReactiveUI.Reactive. Both flavors expose IReactiveCommand
// from the ReactiveUI namespace.
using ReactiveUI;

#if SYSTEM_REACTIVE
using Runic.Application.Views.ReactiveUI.Reactive.Generated;

namespace Runic.Application.Views.ReactiveUI.Reactive;
#else
using Runic.Application.Views.ReactiveUI.Generated;

namespace Runic.Application.Views.ReactiveUI;
#endif

/// <summary>
/// Runs a ReactiveUI command from hand-written code the way a generated Bridge call runs it.
/// </summary>
public static class ReactiveCommandExecutionExtensions
{
    /// <summary>
    /// Executes <paramref name="command"/> with <paramref name="input"/> and completes when the execution completes,
    /// after zero or more values, as a generated Bridge call of a command without a result does. A command that
    /// produces no value completes normally; the values are not returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use it in hand-written code that runs a command on behalf of a Bridge call or inside a model turn, such as a
    /// wrapper command that a Bridge presents and that runs a native command whose input has no wire form, when the
    /// code handles the command's failure itself, runs outside any Bridge call, or needs completion without a value.
    /// A failure of the execution is rethrown unchanged, and Runic counts it as received by this call: it is never
    /// logged as <c>ReactiveCommandFailed</c> (Views event 1042), whether the caller lets it propagate, wraps it or
    /// handles it, inside or outside a Bridge call. Failures of other executions of the command that arrive while this
    /// one runs are held until it ends and are logged then, as during a Bridge call.
    /// </para>
    /// <para>
    /// The execution's notifications follow the model turn that started it, as for a bridged execution. Cancelling
    /// <paramref name="cancellationToken"/> disposes the execution, which cancels a token-taking
    /// <c>CreateFromTask</c> body, and the returned task is cancelled. A token that is already cancelled does not
    /// start it.
    /// </para>
    /// <para>
    /// A plain <c>await command.Execute(input)</c> is enough inside a Bridge call when the exception propagates or is
    /// wrapped: the Bridge call receives it. The Views.ReactiveUI guide's "Which call to use" section compares them.
    /// </para>
    /// </remarks>
    /// <typeparam name="TInput">The command's input type.</typeparam>
    /// <typeparam name="TResult">The command's output type.</typeparam>
    /// <param name="command">The command.</param>
    /// <param name="input">The command's input.</param>
    /// <param name="cancellationToken">Cancels and disposes the execution.</param>
    /// <returns>A task that completes when the execution completes, fails with its exception, or is cancelled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    public static Task RunAsync<TInput, TResult>(this IReactiveCommand<TInput, TResult> command, TInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ReactiveCommandExecution.CompletionAsync(command, input, cancellationToken);
    }
}
