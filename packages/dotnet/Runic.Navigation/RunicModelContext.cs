using Microsoft.Extensions.Logging;

namespace Runic.Navigation;

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
/// <para>
/// Generated bridges start command bodies in a turn. An implementation should run its turns under a
/// <see cref="SynchronizationContext"/> that resumes their awaits in its turns, as <see cref="RunicModelContext"/>
/// and a UI dispatcher do, so state a command writes after <c>await</c> is applied in a turn.
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

/// <summary>
/// A host-neutral serial execution context for a ViewModel graph.
/// </summary>
/// <remarks>
/// <para>
/// Each turn runs under a <see cref="SynchronizationContext"/> that queues posted callbacks as new turns. An
/// <c>await</c> in asynchronous code started in a turn, such as the body of an async command that the bridge
/// invokes, therefore resumes in a later turn of this context, as code started on a UI thread resumes on that
/// thread. State written after the <c>await</c> is applied in a turn, in the order in which the continuations
/// became ready, and no other turn runs between two awaits of the same body. Code after
/// <c>ConfigureAwait(false)</c>, or in work started with <c>Task.Run</c>, leaves the context; commit its result
/// with <see cref="InvokeAsync(Action, CancellationToken)"/>.
/// </para>
/// <para>
/// A continuation posted after the context stopped accepting work, or still queued when it closed, runs on the
/// thread pool outside any turn, so the asynchronous code still finishes and work that waits for it is not
/// stranded. <see cref="SynchronizationContext.Send"/> runs its callback in a turn of this context, or inline on
/// the calling thread once the context is closed. An exception that escapes a posted callback, such as one thrown
/// by an <c>async void</c> method, is reported through <see cref="UnhandledTurnException"/>.
/// </para>
/// </remarks>
public sealed class RunicModelContext : IRunicModelContext, IRunicModelContextLifetime
{
    [ThreadStatic]
    private static RunicModelContext? Executing;
    private readonly object _gate = new();
    private readonly Queue<IWorkItem> _queued = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Keep the token usable by observers registering after disposal, like DispatcherModelContext.
    private readonly CancellationTokenSource _closedSource = new();
    private bool _draining;
    private bool _disposed;
    private bool _shutdownNotified;
    private readonly ILogger _logger;
    private readonly TurnSynchronizationContext _synchronizationContext;

    // The logger of a RunicModelContext, or the Trace fallback for another context.
    internal static ILogger LoggerOf(IRunicModelContext context) =>
        (context as RunicModelContext)?._logger ?? TraceFallbackLogger.Instance;

    /// <summary>Creates a model context that writes unhandled turn failures to <see cref="System.Diagnostics.Trace"/>.</summary>
    public RunicModelContext() : this(null)
    {
    }

    /// <summary>Creates a model context that logs unhandled turn failures to <paramref name="logger"/>.</summary>
    /// <param name="logger">The logger, or <see langword="null"/> for <see cref="System.Diagnostics.Trace"/> output.</param>
    public RunicModelContext(ILogger<RunicModelContext>? logger)
    {
        _logger = logger ?? (ILogger)TraceFallbackLogger.Instance;
        _synchronizationContext = new(this);
    }

    /// <inheritdoc />
    public event Action<Exception>? UnhandledTurnException;

    /// <inheritdoc />
    public bool IsExecuting => ReferenceEquals(Executing, this);

    /// <summary>
    /// Gets the context whose turn is executing on the calling thread, or <see langword="null"/> outside a turn of a
    /// <see cref="RunicModelContext"/>.
    /// </summary>
    /// <remarks>
    /// An <c>await</c> started in a turn resumes in a later turn, where this returns the context again. MVVM adapters
    /// use it to deliver work scheduled in a turn back to the same context, as a UI framework's main-thread scheduler
    /// does for its dispatcher. Another <see cref="IRunicModelContext"/> implementation, such as a dispatcher context,
    /// is not reported.
    /// </remarks>
    public static RunicModelContext? Current => Executing;

    /// <summary>Gets a token cancelled after the context stops accepting work and before queued work is rejected.</summary>
    /// <remarks>
    /// Callbacks run synchronously on the closing thread. Their exceptions are reported through
    /// <see cref="UnhandledTurnException"/> and do not interrupt shutdown. The token remains usable after disposal.
    /// </remarks>
    public CancellationToken Closed => _closedSource.Token;

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
        }

        try { _closedSource.Cancel(); }
        catch (AggregateException failures)
        {
            foreach (var failure in failures.InnerExceptions) ReportUnhandled(failure);
        }
        foreach (var item in rejected) item.RejectDisposed();
        lock (_gate)
        {
            _shutdownNotified = true;
            if (!_draining) _stopped.TrySetResult();
        }
        return new(_stopped.Task);
    }

    // An item that already carries its execution context, such as a posted
    // continuation, passes captureExecutionContext: false.
    private bool Enqueue(IWorkItem item, bool captureExecutionContext = true)
    {
        // A model turn may be queued while a trusted bridge invocation is
        // active. Capture its ambient execution context with this individual
        // item, rather than with the drain, because one drain deliberately
        // batches unrelated callers. This also honors SuppressFlow.
        var captured = captureExecutionContext ? new ExecutionContextWorkItem(item, ExecutionContext.Capture()) : item;
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
        var previous = Executing;
        // Unsafe queueing prevents the caller's ambient values from becoming
        // this worker's baseline. A suppressed-flow item must nevertheless
        // run in this clean baseline even when it follows an item that did
        // capture an AsyncLocal scope.
        var baselineExecutionContext = ExecutionContext.Capture();
        var previousSynchronizationContext = SynchronizationContext.Current;
        Executing = this;
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
                        if (_disposed && _shutdownNotified) _stopped.TrySetResult();
                        return;
                    }
                    item = _queued.Dequeue();
                }

                // Set per item: a turn that replaced the context must not pass that on to the next one.
                SynchronizationContext.SetSynchronizationContext(_synchronizationContext);
                item.Execute(baselineExecutionContext);
            }
        }
        finally
        {
            Executing = previous;
            SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
        }
    }

    // Queues a continuation as a turn. It runs on the thread pool instead when the context no longer
    // accepts work, and again when the context closes before the turn starts (see the class remarks).
    private void PostContinuation(SendOrPostCallback callback, object? state)
    {
        var continuation = new ContinuationWorkItem(callback, state, ExecutionContext.Capture(), this);
        if (!Enqueue(continuation, captureExecutionContext: false)) continuation.RejectDisposed();
    }

    // The SynchronizationContext every turn runs under. It holds no state of its own, so copies are not needed.
    private sealed class TurnSynchronizationContext(RunicModelContext owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);
            owner.PostContinuation(d, state);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);
            if (owner.IsExecuting)
            {
                d(state);
                return;
            }
            var started = 0;
            try
            {
                owner.InvokeAsync(() =>
                {
                    Volatile.Write(ref started, 1);
                    d(state);
                }).AsTask().GetAwaiter().GetResult();
            }
            // The context closed before the callback started: run it here, like a closed dispatcher context.
            catch (ObjectDisposedException) when (Volatile.Read(ref started) == 0) { d(state); }
        }

        public override SynchronizationContext CreateCopy() => this;
    }

    // A posted continuation, for example of an await. It runs in the execution context captured when it
    // was posted (the baseline when the poster suppressed the flow), in a turn or, once the context has
    // closed, on the thread pool under no SynchronizationContext.
    private sealed class ContinuationWorkItem(SendOrPostCallback callback, object? state, ExecutionContext? context,
        RunicModelContext owner) : IWorkItem
    {
        private static readonly ContextCallback InvokeInContext = static self => ((ContinuationWorkItem)self!).Invoke();

        public void Execute(ExecutionContext? baselineExecutionContext)
        {
            var executionContext = context ?? baselineExecutionContext;
            if (executionContext is null) Invoke();
            else ExecutionContext.Run(executionContext, InvokeInContext, this);
        }

        public void RejectDisposed() =>
            ThreadPool.UnsafeQueueUserWorkItem(static self => self.Execute(null), this, preferLocal: false);

        private void Invoke()
        {
            try { callback(state); }
            catch (Exception error) { owner.ReportUnhandled(error); }
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
            ModelContextLog.UnhandledTurnHandlerFailed(_logger, reportError, NavigationTelemetry.ErrorType(reportError));
        }
        if (dropped) ModelContextLog.ModelTurnDropped(_logger, error, NavigationTelemetry.ErrorType(error));
        else ModelContextLog.ModelTurnFailed(_logger, error, NavigationTelemetry.ErrorType(error));
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
