namespace Runic.Application.Views;

/// <summary>Owns accepted application tasks until their actual completion.</summary>
/// <remarks>
/// Use one instance for the application lifetime that owns the work, such as a
/// scoped model. Register the complete task, including recovery and cleanup,
/// before starting it. This scope does not serialize work, dispatch it to a
/// model context, or request cancellation.
/// </remarks>
public sealed class AcceptedWorkScope : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pending;
    private bool _draining;

    /// <summary>Creates a scope that accepts work until draining begins.</summary>
    public AcceptedWorkScope() { }

    /// <summary>Accepts ownership before invoking <paramref name="work"/> and returns its actual task.</summary>
    /// <remarks>
    /// The factory runs synchronously on the calling thread without holding a
    /// scope lock. A synchronous factory exception is rethrown to the caller.
    /// Cancellation of a separate invocation or observation task does not
    /// release ownership of the returned task.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The factory is null.</exception>
    /// <exception cref="InvalidOperationException">Draining has begun, or the factory returns null.</exception>
    public Task RunAsync(Func<Task> work) => OwnTask(work);

    /// <summary>Accepts ownership before invoking <paramref name="work"/> and returns its actual result task.</summary>
    /// <remarks>
    /// The factory runs synchronously on the calling thread without holding a
    /// scope lock. A synchronous factory exception is rethrown to the caller.
    /// Cancellation of a separate invocation or observation task does not
    /// release ownership of the returned task.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The factory is null.</exception>
    /// <exception cref="InvalidOperationException">Draining has begun, or the factory returns null.</exception>
    public Task<TResult> RunAsync<TResult>(Func<Task<TResult>> work) => OwnTask(work);

    /// <summary>Stops accepting work and waits for all accepted factories and tasks to finish.</summary>
    /// <remarks>
    /// Cancellation only stops this caller's wait; admission remains closed and
    /// accepted work remains owned. Drain observes task faults and cancellations
    /// without propagating them; each returned task retains its original outcome.
    /// Call again or dispose asynchronously to await completion after a cancelled wait.
    /// </remarks>
    public Task DrainAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _draining = true;
            if (_pending == 0) _drained.TrySetResult();
        }
        return cancellationToken.CanBeCanceled ? _drained.Task.WaitAsync(cancellationToken) : _drained.Task;
    }

    /// <summary>Stops accepting work and drains it without requesting cancellation.</summary>
    /// <remarks>Concurrent and repeated disposal wait for the same completion.</remarks>
    public ValueTask DisposeAsync() => new(DrainAsync());

    private TTask OwnTask<TTask>(Func<TTask> work) where TTask : Task
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_draining) throw new InvalidOperationException("The accepted-work scope is draining and no longer accepts work.");
            _pending++;
        }

        TTask task;
        try
        {
            task = work() ?? throw new InvalidOperationException("The accepted-work factory returned no task.");
        }
        catch
        {
            Complete();
            throw;
        }

        _ = task.ContinueWith(static (completed, state) =>
        {
            // An invocation may have stopped observing before this task faults.
            // Observe it here while leaving the original task outcome intact.
            _ = completed.Exception;
            ((AcceptedWorkScope)state!).Complete();
        }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private void Complete()
    {
        lock (_gate)
        {
            _pending--;
            if (_draining && _pending == 0) _drained.TrySetResult();
        }
    }
}
