using Microsoft.Extensions.Logging;

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
    // The accepted work whose factory or task is running in the current
    // execution context. It flows into the task's continuations, so a drain
    // started by that work (for example "Save and close" disposing its own
    // owner) can avoid waiting for itself.
    private static readonly AsyncLocal<AcceptedWork?> Current = new();

    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<AcceptedWork> _pending = [];
    private readonly List<(HashSet<AcceptedWork> Callers, TaskCompletionSource Drained)> _nestedDrains = [];
    private bool _draining;

    /// <summary>Creates a scope that accepts work until draining begins.</summary>
    /// <remarks>Failures of accepted tasks are written to <see cref="System.Diagnostics.Trace"/>.</remarks>
    public AcceptedWorkScope() : this(null) { }

    /// <summary>Creates a scope that accepts work until draining begins and logs failures of accepted tasks.</summary>
    /// <param name="logger">
    /// Receives event 1013 (<c>AcceptedWorkFailed</c>) when an accepted task faults. Null writes the
    /// entry to <see cref="System.Diagnostics.Trace"/>.
    /// </param>
    public AcceptedWorkScope(ILogger? logger) => _logger = logger ?? TraceFallbackLogger.Instance;

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
    /// <para>
    /// Cancellation only stops this caller's wait; admission remains closed and
    /// accepted work remains owned. Each returned task retains its original
    /// outcome. A faulted task is logged once (event 1013) without failing the
    /// drain; cancellations are not logged.
    /// Call again or dispose asynchronously to await completion after a cancelled wait.
    /// </para>
    /// <para>
    /// When called from accepted work of this scope, including code that inherits
    /// its execution context, the drain waits for all other accepted work but not
    /// for the calling work, which cannot finish while it awaits its own drain.
    /// Work such as "Save and close" can therefore dispose its owner. Code that
    /// runs in that work after the drain must not rely on resources the owner
    /// releases.
    /// </para>
    /// </remarks>
    public Task DrainAsync(CancellationToken cancellationToken = default)
    {
        var drained = BeginDrain();
        return cancellationToken.CanBeCanceled ? drained.WaitAsync(cancellationToken) : drained;
    }

    /// <summary>Stops accepting work and drains it without requesting cancellation.</summary>
    /// <remarks>
    /// Concurrent and repeated disposal wait for the same completion. Disposal from
    /// accepted work of this scope does not wait for the calling work; see
    /// <see cref="DrainAsync(CancellationToken)"/>.
    /// </remarks>
    public ValueTask DisposeAsync() => new(DrainAsync());

    private Task BeginDrain()
    {
        HashSet<AcceptedWork>? callers = null;
        for (var work = Current.Value; work is not null; work = work.Parent)
        {
            if (ReferenceEquals(work.Scope, this)) (callers ??= []).Add(work);
        }

        lock (_gate)
        {
            _draining = true;
            if (_pending.Count == 0) _drained.TrySetResult();
            if (callers is null || _drained.Task.IsCompleted) return _drained.Task;
            if (_pending.IsSubsetOf(callers)) return Task.CompletedTask;
            var nested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _nestedDrains.Add((callers, nested));
            return nested.Task;
        }
    }

    private TTask OwnTask<TTask>(Func<TTask> work) where TTask : Task
    {
        ArgumentNullException.ThrowIfNull(work);
        var parent = Current.Value;
        var accepted = new AcceptedWork(this, parent);
        lock (_gate)
        {
            if (_draining) throw new InvalidOperationException("The accepted-work scope is draining and no longer accepts work.");
            _pending.Add(accepted);
        }

        TTask task;
        Current.Value = accepted;
        try
        {
            task = work() ?? throw new InvalidOperationException("The accepted-work factory returned no task.");
        }
        catch
        {
            Complete(accepted);
            throw;
        }
        finally
        {
            Current.Value = parent;
        }

        _ = task.ContinueWith(static (completed, state) =>
        {
            var accepted = (AcceptedWork)state!;
            // An invocation may have stopped observing before this task faults,
            // so the scope reports each fault once. The original task keeps it.
            if (completed.Exception is { } error) accepted.Scope.ReportFailure(error);
            accepted.Scope.Complete(accepted);
        }, accepted, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private void ReportFailure(AggregateException error)
    {
        var failure = error.InnerExceptions.Count == 1 ? error.InnerExceptions[0] : error;
        if (BridgeDomainFailures.Find(failure) is { } domain)
        {
            // A declared failure is an expected outcome, logged at Debug like 1006 and 1007.
            var failureType = BridgeDomainFailures.TypeName(domain);
            ViewsLog.AcceptedWorkDomainFailed(_logger, domain, failureType);
        }
        else
        {
            var errorType = BridgeTelemetry.ErrorType(failure);
            ViewsLog.AcceptedWorkFailed(_logger, failure, errorType);
        }
    }

    private void Complete(AcceptedWork accepted)
    {
        lock (_gate)
        {
            _pending.Remove(accepted);
            if (!_draining) return;
            if (_pending.Count == 0) _drained.TrySetResult();
            for (var i = _nestedDrains.Count - 1; i >= 0; i--)
            {
                var (callers, drained) = _nestedDrains[i];
                if (!_pending.IsSubsetOf(callers)) continue;
                drained.TrySetResult();
                _nestedDrains.RemoveAt(i);
            }
        }
    }

    private sealed class AcceptedWork(AcceptedWorkScope scope, AcceptedWork? parent)
    {
        public AcceptedWorkScope Scope { get; } = scope;

        public AcceptedWork? Parent { get; } = parent;
    }
}
