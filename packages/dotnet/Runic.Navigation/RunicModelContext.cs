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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "Shipped in Runic.Application 0.7.0-preview.3 and moved unchanged; the overloads differ by generic arity. Remove once PublicAPI.Shipped.txt lists them (eng/release/README.md step 6).")]
    ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default);

    /// <summary>Runs a short synchronous turn in this context and returns its result.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "Shipped in Runic.Application 0.7.0-preview.3 and moved unchanged; the overloads differ by generic arity. Remove once PublicAPI.Shipped.txt lists them (eng/release/README.md step 6).")]
    ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default);
}

/// <summary>
/// A host-neutral serial execution context for a ViewModel graph.
/// </summary>
#pragma warning disable RUNICNAV001 // The context stays stable; only the optional navigator lifetime interface is experimental.
public sealed class RunicModelContext : IRunicModelContext, IRunicModelContextLifetime
#pragma warning restore RUNICNAV001
{
    [ThreadStatic]
    private static RunicModelContext? Current;
    private readonly object _gate = new();
    private readonly Queue<IWorkItem> _queued = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Keep the token usable by observers registering after disposal, like DispatcherModelContext.
    private readonly CancellationTokenSource _closedSource = new();
    private bool _draining;
    private bool _disposed;
    private bool _shutdownNotified;
    private readonly ILogger _logger;

    // The logger of a RunicModelContext, or the Trace fallback for another context.
    internal static ILogger LoggerOf(IRunicModelContext context) =>
        (context as RunicModelContext)?._logger ?? TraceFallbackLogger.Instance;

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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "Shipped in Runic.Application 0.7.0-preview.3 and moved unchanged; the overloads differ by generic arity. Remove once PublicAPI.Shipped.txt lists them (eng/release/README.md step 6).")]
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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "Shipped in Runic.Application 0.7.0-preview.3 and moved unchanged; the overloads differ by generic arity. Remove once PublicAPI.Shipped.txt lists them (eng/release/README.md step 6).")]
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
                        if (_disposed && _shutdownNotified) _stopped.TrySetResult();
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
