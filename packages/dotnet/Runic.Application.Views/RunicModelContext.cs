namespace Runic.Application.Views;

/// <summary>
/// Owns short, serialized mutations and reads for one mutable application-model graph.
/// </summary>
/// <remarks>
/// A turn is deliberately synchronous. Do asynchronous I/O and interactions outside a
/// turn, then use a new turn to commit their result. A presentation lease never owns
/// this context; the composition root or an explicit context lease does.
/// </remarks>
public interface IRunicModelContext : IAsyncDisposable
{
    /// <summary>Queues a short synchronous turn when the context is still accepting work.</summary>
    bool TryPost(Action turn);

    /// <summary>Runs a short synchronous turn in this context.</summary>
    ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default);

    /// <summary>Runs a short synchronous turn in this context and returns its result.</summary>
    ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default);
}

// This is intentionally internal. BridgeModelTurn needs a synchronous entry point for
// existing synchronous transport routes; public callers use InvokeAsync instead.
internal interface IRunicSynchronousModelContext : IRunicModelContext
{
    bool IsExecuting { get; }
    void Run(Action turn);
    T Run<T>(Func<T> turn);
}

/// <summary>
/// A host-neutral serial execution context for a ViewModel graph.
/// </summary>
public sealed class RunicModelContext : IRunicSynchronousModelContext
{
    [ThreadStatic]
    private static RunicModelContext? Current;
    private readonly object _gate = new();
    private readonly Queue<IWorkItem> _queued = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _draining;
    private bool _disposed;

    /// <summary>Receives exceptions from fire-and-forget turns after they have been isolated from the queue drain.</summary>
    public event Action<Exception>? UnhandledTurnException;

    /// <summary>Gets whether the calling code is executing one of this context's turns.</summary>
    public bool IsExecuting => ReferenceEquals(Current, this);

    /// <inheritdoc />
    public bool TryPost(Action turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return Enqueue(new PostedWorkItem(turn, ReportUnhandled));
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

    void IRunicSynchronousModelContext.Run(Action turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (IsExecuting) turn();
        else InvokeAsync(turn).AsTask().GetAwaiter().GetResult();
    }

    T IRunicSynchronousModelContext.Run<T>(Func<T> turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return IsExecuting ? turn() : InvokeAsync(turn).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Rejects queued work and completes after a currently executing synchronous turn returns.
    /// A running turn cannot be interrupted.
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
        var queueDrain = false;
        lock (_gate)
        {
            if (_disposed) return false;
            _queued.Enqueue(item);
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

                item.Execute();
            }
        }
        finally
        {
            Current = previous;
        }
    }

    private interface IWorkItem
    {
        void Execute();
        void RejectDisposed();
    }

    private void ReportUnhandled(Exception error)
    {
        try
        {
            UnhandledTurnException?.Invoke(error);
        }
        catch (Exception reportError)
        {
            System.Diagnostics.Trace.TraceError(reportError.ToString());
        }
        System.Diagnostics.Trace.TraceError(error.ToString());
    }

    private sealed class PostedWorkItem(Action turn, Action<Exception> report) : IWorkItem
    {
        public void Execute()
        {
            try { turn(); }
            catch (Exception error) { report(error); }
        }

        public void RejectDisposed() { }
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

        public void Execute()
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

        public void Execute()
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
        if (context is IRunicSynchronousModelContext synchronous && synchronous.IsExecuting)
        {
            _ = ObserveAsync(shutdown);
            return;
        }
        shutdown.AsTask().GetAwaiter().GetResult();
    }

    private static async Task ObserveAsync(ValueTask shutdown)
    {
        try { await shutdown.ConfigureAwait(false); }
        catch (Exception error) { System.Diagnostics.Trace.TraceError(error.ToString()); }
    }
}
