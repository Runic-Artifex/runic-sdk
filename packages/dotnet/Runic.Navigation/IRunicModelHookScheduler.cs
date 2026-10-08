using System.Diagnostics.CodeAnalysis;

namespace Runic.Navigation;

/// <summary>
/// Optionally implemented by an <see cref="IRunicModelContext"/> whose model has thread affinity, such as a UI
/// dispatcher context. A navigator whose <see cref="RunicNavigator.ModelContext"/> implements this interface runs
/// its hooks through it, each as a separate operation on the model's thread.
/// </summary>
/// <remarks>
/// <para>
/// The navigator schedules target factories, departure guards, initialize and resume hooks, and the disposal of
/// owned content, one operation per invocation. Commits still run in turns (<see cref="IRunicModelContext.InvokeAsync(Action, CancellationToken)"/>),
/// as operations separate from any hook. Disposal that can't be scheduled because the context is closed runs on the thread pool.
/// </para>
/// <para>
/// Hook operations are not turns: they don't count toward a context's turn nesting, they hold no turn across their
/// awaits, and a hook may pump (for example, show a modal dialog), during which commits of other transitions can run.
/// </para>
/// <para>
/// The navigator detects the scheduler with a type test on the context object it was given. A decorator that wraps
/// a scheduling context must implement and forward this interface too; otherwise it hides the scheduler and hooks
/// run on the thread pool.
/// </para>
/// </remarks>
[Experimental(RunicNavigator.DiagnosticId)]
public interface IRunicModelHookScheduler
{
    /// <summary>
    /// Starts <paramref name="hook"/> as a separate operation on the model's thread and returns a task that the scheduler
    /// completes itself, from its own <see cref="TaskCompletionSource{TResult}"/> created with
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An implementation never runs <paramref name="hook"/> inline in the calling frame, even when the caller is
    /// already on the model's thread. When the hook's own awaits capture a context, they resume on the model's thread.
    /// It runs the hook inside a wrapper that catches a synchronous throw, so an exception never escapes into the host's
    /// message loop. The outcomes are distinguishable:
    /// </para>
    /// <list type="bullet">
    /// <item>the context is closed, or closes before the operation starts: <see cref="ObjectDisposedException"/>;</item>
    /// <item><paramref name="cancellationToken"/> is cancelled before the operation starts: <see cref="OperationCanceledException"/>;</item>
    /// <item><paramref name="hook"/> throws synchronously, or its task faults or is cancelled: that exception, unchanged.</item>
    /// </list>
    /// </remarks>
    /// <typeparam name="T">The hook's result type.</typeparam>
    /// <param name="hook">The hook to run on the model's thread.</param>
    /// <param name="cancellationToken">Cancels the operation before it starts. A started hook observes its own token.</param>
    /// <returns>A task that completes with the hook's result.</returns>
    Task<T> RunHookAsync<T>(Func<Task<T>> hook, CancellationToken cancellationToken);
}
