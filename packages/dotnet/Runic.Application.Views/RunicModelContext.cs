using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

/// <summary>
/// Owns short, serialized mutations and reads for one mutable application-model graph.
/// </summary>
/// <remarks>
/// <para>
/// A turn is deliberately synchronous. Do asynchronous I/O and interactions outside a
/// turn, then use a new turn to commit their result. A presentation lease never owns
/// this context; the composition root or an explicit context lease does.
/// </para>
/// <para>
/// Generated bridges and content sessions serve synchronous transport routes. They run
/// work inline when <see cref="IsExecuting"/> is true and otherwise block the calling
/// thread until <see cref="InvokeAsync(Action, CancellationToken)"/> completes. An
/// implementation must therefore report <see langword="true"/> for code running inside
/// one of its turns, and a turn must not need a blocked caller's thread to make progress.
/// </para>
/// </remarks>
public interface IRunicModelContext : IAsyncDisposable
{
    /// <summary>Gets whether the calling code is executing one of this context's turns.</summary>
    bool IsExecuting { get; }

    /// <summary>
    /// Receives exceptions thrown by turns queued with <see cref="TryPost"/>, and an
    /// <see cref="ObjectDisposedException"/> for each posted turn dropped by disposal.
    /// </summary>
    event Action<Exception>? UnhandledTurnException;

    /// <summary>Queues a short synchronous turn when the context is still accepting work.</summary>
    bool TryPost(Action turn);

    /// <summary>Runs a short synchronous turn in this context.</summary>
    ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default);

    /// <summary>Runs a short synchronous turn in this context and returns its result.</summary>
    ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default);
}

// Synchronous entry points for transport routes. They follow the blocking
// contract documented on IRunicModelContext.
internal static class RunicModelTurns
{
    public static void Run(IRunicModelContext context, Action work)
    {
        if (context.IsExecuting) work();
        else context.InvokeAsync(work).AsTask().GetAwaiter().GetResult();
    }

    public static T Run<T>(IRunicModelContext context, Func<T> work) =>
        context.IsExecuting ? work() : context.InvokeAsync(work).AsTask().GetAwaiter().GetResult();

    // Teardown must still release subscriptions and lifetimes when an
    // application-owned context was disposed first. If the context rejected
    // the turn before it started, run it inline under the supplied gate.
    public static void RunForTeardown(IRunicModelContext? context, Action work, object? gate = null)
    {
        if (context is not null)
        {
            var started = 0;
            try
            {
                Run(context, () =>
                {
                    Volatile.Write(ref started, 1);
                    work();
                });
                return;
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref started) == 0) { }
        }
        if (gate is null) work();
        else lock (gate) work();
    }
}

/// <summary>
/// A host-neutral serial execution context for a ViewModel graph.
/// </summary>
public sealed class RunicModelContext : IRunicModelContext
{
    [ThreadStatic]
    private static RunicModelContext? Current;
    private readonly object _gate = new();
    private readonly Queue<IWorkItem> _queued = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _draining;
    private bool _disposed;
    private readonly ILogger _logger;

    /// <summary>Creates a model context that writes unhandled turn failures to <see cref="System.Diagnostics.Trace"/>.</summary>
    public RunicModelContext() : this(null)
    {
    }

    /// <summary>Creates a model context that logs unhandled turn failures to <paramref name="logger"/>.</summary>
    /// <param name="logger">The logger, or <see langword="null"/> for <see cref="System.Diagnostics.Trace"/> output.</param>
    public RunicModelContext(ILogger<RunicModelContext>? logger) => _logger = logger ?? (ILogger)TraceFallbackLogger.Instance;

    /// <inheritdoc />
    public event Action<Exception>? UnhandledTurnException;

    /// <inheritdoc />
    public bool IsExecuting => ReferenceEquals(Current, this);

    /// <inheritdoc />
    public bool TryPost(Action turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return Enqueue(new PostedWorkItem(turn, this));
    }

    /// <inheritdoc />
    public ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (IsExecuting)
        {
            cancellationToken.ThrowIfCancellationRequested();
            turn();
            return ValueTask.CompletedTask;
        }

        var item = new InvokeWorkItem(turn, cancellationToken);
        if (!Enqueue(item))
            return ValueTask.FromException(new ObjectDisposedException(nameof(RunicModelContext)));
        return new(item.Completion);
    }

    /// <inheritdoc />
    public ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (IsExecuting)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(turn());
        }

        var item = new InvokeWorkItem<T>(turn, cancellationToken);
        if (!Enqueue(item))
            return ValueTask.FromException<T>(new ObjectDisposedException(nameof(RunicModelContext)));
        return new(item.Completion);
    }

    /// <summary>
    /// Rejects queued work and completes after a currently executing synchronous turn returns.
    /// A running turn cannot be interrupted. Rejected invocations fault with
    /// <see cref="ObjectDisposedException"/>; rejected posted turns are reported through
    /// <see cref="UnhandledTurnException"/>.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        IWorkItem[] rejected;
        lock (_gate)
        {
            if (_disposed) return new(_stopped.Task);
            _disposed = true;
            rejected = _queued.ToArray();
            _queued.Clear();
            if (!_draining) _stopped.TrySetResult();
        }

        foreach (var item in rejected) item.RejectDisposed();
        return new(_stopped.Task);
    }

    private bool Enqueue(IWorkItem item)
    {
        // A model turn may be queued while a trusted bridge invocation is
        // active. Capture its ambient execution context with this individual
        // item, rather than with the drain, because one drain deliberately
        // batches unrelated callers. This also honors SuppressFlow.
        var captured = new ExecutionContextWorkItem(item, ExecutionContext.Capture());
        var queueDrain = false;
        lock (_gate)
        {
            if (_disposed) return false;
            _queued.Enqueue(captured);
            if (!_draining)
            {
                _draining = true;
                queueDrain = true;
            }
        }

        if (queueDrain) ThreadPool.UnsafeQueueUserWorkItem(static state => state.Drain(), this, preferLocal: false);
        return true;
    }

    private void Drain()
    {
        var previous = Current;
        // Unsafe queueing prevents the caller's ambient values from becoming
        // this worker's baseline. A suppressed-flow item must nevertheless
        // run in this clean baseline even when it follows an item that did
        // capture an AsyncLocal scope.
        var baselineExecutionContext = ExecutionContext.Capture();
        Current = this;
        try
        {
            while (true)
            {
                IWorkItem? item;
                lock (_gate)
                {
                    if (_queued.Count == 0)
                    {
                        _draining = false;
                        if (_disposed) _stopped.TrySetResult();
                        return;
                    }
                    item = _queued.Dequeue();
                }

                item.Execute(baselineExecutionContext);
            }
        }
        finally
        {
            Current = previous;
        }
    }

    private interface IWorkItem
    {
        void Execute(ExecutionContext? baselineExecutionContext);
        void RejectDisposed();
    }

    private sealed class ExecutionContextWorkItem(IWorkItem inner, ExecutionContext? context) : IWorkItem
    {
        public void Execute(ExecutionContext? baselineExecutionContext)
        {
            var executionContext = context ?? baselineExecutionContext;
            if (executionContext is null)
            {
                inner.Execute(null);
                return;
            }
            ExecutionContext.Run(executionContext, static state => ((IWorkItem)state!).Execute(null), inner);
        }

        public void RejectDisposed() => inner.RejectDisposed();
    }

    private void ReportUnhandled(Exception error) => Report(error, dropped: false);

    private void Report(Exception error, bool dropped)
    {
        try
        {
            UnhandledTurnException?.Invoke(error);
        }
        catch (Exception reportError)
        {
            ViewsLog.UnhandledTurnHandlerFailed(_logger, BridgeTelemetry.LoggedException(reportError), BridgeTelemetry.ErrorType(reportError));
        }
        if (dropped) ViewsLog.ModelTurnDropped(_logger, null, BridgeTelemetry.ErrorType(error));
        else ViewsLog.ModelTurnFailed(_logger, BridgeTelemetry.LoggedException(error), BridgeTelemetry.ErrorType(error));
    }

    // A posted turn has no completion to fault. Dropping it at shutdown is
    // still observable: the owner may have expected the turn to release work.
    private sealed class PostedWorkItem(Action turn, RunicModelContext owner) : IWorkItem
    {
        public void Execute(ExecutionContext? baselineExecutionContext)
        {
            try { turn(); }
            catch (Exception error) { owner.ReportUnhandled(error); }
        }

        public void RejectDisposed() => owner.Report(new ObjectDisposedException(nameof(RunicModelContext),
            "A posted model turn was dropped because its context was disposed."), dropped: true);
    }

    private sealed class InvokeWorkItem : IWorkItem
    {
        private readonly Action _turn;
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state;

        public InvokeWorkItem(Action turn, CancellationToken cancellationToken)
        {
            _turn = turn;
            _cancellationRegistration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(static state => ((InvokeWorkItem)state!).Cancel(), this)
                : default;
        }

        public Task Completion => _completion.Task;

        public void Execute(ExecutionContext? baselineExecutionContext)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                _cancellationRegistration.Dispose();
                return;
            }
            try
            {
                _turn();
                _completion.TrySetResult();
            }
            catch (Exception error)
            {
                _completion.TrySetException(error);
            }
            finally
            {
                _cancellationRegistration.Dispose();
            }
        }

        public void RejectDisposed()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
                _completion.TrySetException(new ObjectDisposedException(nameof(RunicModelContext)));
            _cancellationRegistration.Dispose();
        }

        private void Cancel()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
            _completion.TrySetCanceled();
        }
    }

    private sealed class InvokeWorkItem<T> : IWorkItem
    {
        private readonly Func<T> _turn;
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state;

        public InvokeWorkItem(Func<T> turn, CancellationToken cancellationToken)
        {
            _turn = turn;
            _cancellationRegistration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(static state => ((InvokeWorkItem<T>)state!).Cancel(), this)
                : default;
        }

        public Task<T> Completion => _completion.Task;

        public void Execute(ExecutionContext? baselineExecutionContext)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                _cancellationRegistration.Dispose();
                return;
            }
            try
            {
                _completion.TrySetResult(_turn());
            }
            catch (Exception error)
            {
                _completion.TrySetException(error);
            }
            finally
            {
                _cancellationRegistration.Dispose();
            }
        }

        public void RejectDisposed()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
                _completion.TrySetException(new ObjectDisposedException(nameof(RunicModelContext)));
            _cancellationRegistration.Dispose();
        }

        private void Cancel()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
            _completion.TrySetCanceled();
        }
    }
}

// An IDisposable host can be closed by a view callback, which itself runs in a
// model turn. Waiting for that same turn to leave the queue would deadlock.
// Keep the normal synchronous disposal contract for external callers, but let
// the owner turn initiate shutdown and finish naturally when it returns.
internal static class RunicModelContextDisposal
{
    public static void DisposeSynchronously(IRunicModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var shutdown = context.DisposeAsync();
        if (shutdown.IsCompletedSuccessfully) return;
        if (context.IsExecuting)
        {
            _ = ObserveAsync(shutdown);
            return;
        }
        shutdown.AsTask().GetAwaiter().GetResult();
    }

    private static async Task ObserveAsync(ValueTask shutdown)
    {
        try { await shutdown.ConfigureAwait(false); }
        catch (Exception error)
        { ViewsLog.ModelContextReleaseFailed(TraceFallbackLogger.Instance, BridgeTelemetry.LoggedException(error), BridgeTelemetry.ErrorType(error)); }
    }
}
