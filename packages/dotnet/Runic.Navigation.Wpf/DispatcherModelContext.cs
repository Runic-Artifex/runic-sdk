using System.Diagnostics.CodeAnalysis;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace Runic.Navigation.Wpf;

/// <summary>
/// A model context on a WPF <see cref="System.Windows.Threading.Dispatcher"/>: turns and navigation hooks run on
/// the UI thread, and code on the UI thread counts as executing.
/// </summary>
/// <remarks>
/// <para>
/// Turns requested on the UI thread run inline. Turns requested on another thread are dispatched, and their
/// exceptions complete the caller's task; they never reach <see cref="Dispatcher.UnhandledException"/>. Hooks run
/// through <see cref="IRunicModelHookScheduler"/> as their own dispatcher operations, never inline in the caller,
/// and their awaits resume on the UI thread.
/// </para>
/// <para>
/// A turn must not pump: no <c>ShowDialog</c>, <c>MessageBox</c> or <c>Wait()</c> inside a turn, a commit's
/// <c>PropertyChanged</c> handler or an <c>OnCommitted</c> action. A turn dispatched inside another turn's nested
/// pump is logged once as event 1081. A hook may pump.
/// </para>
/// <para>
/// The context closes when it is disposed or when its dispatcher starts shutting down. Pending and later
/// requests then fail with <see cref="ObjectDisposedException"/>, and <see cref="TryPost"/> returns
/// <see langword="false"/>. Disposing the context never shuts the dispatcher down.
/// </para>
/// </remarks>
[Experimental(RunicNavigator.DiagnosticId)]
public sealed class DispatcherModelContext : IRunicModelContext, IRunicModelHookScheduler, IDisposable
{
    private static readonly DispatcherOperationCallback RunItem = static state =>
    {
        ((WorkItem)state!).Run();
        return null;
    };

    private readonly object _gate = new();
    private readonly HashSet<WorkItem> _pending = [];
    private readonly DispatcherPriority _priority;
    private readonly ILogger _logger;
    private bool _closed;
    private bool _nestedLogged;
    // Turns this context runs on the UI thread; only read and written there. Hooks don't count.
    private int _turnDepth;

    /// <summary>Creates a model context that runs turns and hooks on <paramref name="dispatcher"/>.</summary>
    /// <param name="dispatcher">The UI thread's dispatcher.</param>
    /// <param name="priority">The priority of dispatched turns and hooks.</param>
    /// <param name="logger">The logger, or <see langword="null"/> for <see cref="System.Diagnostics.Trace"/> output.</param>
    /// <exception cref="InvalidOperationException">The dispatcher has started shutting down.</exception>
    public DispatcherModelContext(Dispatcher dispatcher, DispatcherPriority priority = DispatcherPriority.Normal,
        ILogger<DispatcherModelContext>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (priority is DispatcherPriority.Invalid or DispatcherPriority.Inactive)
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "Dispatched turns need a priority that runs.");
        Dispatcher = dispatcher;
        _priority = priority;
        _logger = logger ?? (ILogger)TraceFallbackLogger.Instance;
        if (dispatcher.HasShutdownStarted)
            throw new InvalidOperationException("The dispatcher has started shutting down.");
        dispatcher.ShutdownStarted += OnShutdownStarted;
    }

    /// <summary>Gets the dispatcher that runs this context's turns and hooks.</summary>
    public Dispatcher Dispatcher { get; }

    /// <inheritdoc />
    public event Action<Exception>? UnhandledTurnException;

    /// <summary>
    /// Gets whether the context is open and the caller is on its UI thread. All UI-thread code is serialized
    /// with the turns, so it counts as executing.
    /// </summary>
    public bool IsExecuting => !Volatile.Read(ref _closed) && Dispatcher.CheckAccess();

    // The current turn nesting on the UI thread, for tests.
    internal int TurnDepth => _turnDepth;

    /// <inheritdoc />
    public bool TryPost(Action turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return Dispatch(new PostedItem(this, turn));
    }

    /// <inheritdoc />
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "Implements IRunicModelContext, whose overloads differ by generic arity.")]
    public ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return new(InvokeAsync(() =>
        {
            turn();
            return true;
        }, cancellationToken).AsTask());
    }

    /// <inheritdoc />
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "Implements IRunicModelContext, whose overloads differ by generic arity.")]
    public ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (IsExecuting)
        {
            // Inline and synchronous, like RunicModelContext inside a turn.
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(RunTurn(turn));
        }
        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<T>(cancellationToken);
        var item = new InvokeItem<T>(this, turn, cancellationToken);
        if (!Dispatch(item)) item.Reject();
        return new(item.Completion);
    }

    /// <inheritdoc />
    public Task<T> RunHookAsync<T>(Func<Task<T>> hook, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hook);
        if (Volatile.Read(ref _closed)) return Task.FromException<T>(new ObjectDisposedException(nameof(DispatcherModelContext)));
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);
        // Always dispatched, even on the UI thread: a hook never runs inline in the caller.
        var item = new HookItem<T>(this, hook, cancellationToken);
        if (!Dispatch(item)) item.Reject();
        return item.Completion;
    }

    /// <summary>
    /// Closes the context: later requests fail with <see cref="ObjectDisposedException"/>, pending invocations and
    /// hooks fail with it, and pending posted turns are reported as dropped. A running turn is not interrupted.
    /// </summary>
    public void Dispose() => Close();

    /// <summary>Closes the context like <see cref="Dispose"/>; completes at once.</summary>
    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    private void OnShutdownStarted(object? sender, EventArgs e) => Close();

    private void Close()
    {
        WorkItem[] pending;
        lock (_gate)
        {
            if (_closed) return;
            Volatile.Write(ref _closed, true);
            pending = [.. _pending];
            _pending.Clear();
        }
        Dispatcher.ShutdownStarted -= OnShutdownStarted;
        foreach (var item in pending) item.Reject();
    }

    // Queues the item on the dispatcher, or returns false once closed.
    private bool Dispatch(WorkItem item)
    {
        lock (_gate)
        {
            if (_closed) return false;
            _pending.Add(item);
        }
        DispatcherOperation operation;
        try
        {
            operation = Dispatcher.BeginInvoke(_priority, RunItem, item);
        }
        catch (Exception)
        {
            Forget(item);
            item.Reject();
            return true;
        }
        // A dispatcher that finished shutting down aborts operations at once; shutdown aborts queued ones.
        operation.Aborted += (_, _) =>
        {
            Forget(item);
            item.Reject();
        };
        if (operation.Status == DispatcherOperationStatus.Aborted)
        {
            Forget(item);
            item.Reject();
        }
        return true;
    }

    private bool Forget(WorkItem item)
    {
        lock (_gate) return _pending.Remove(item);
    }

    // Runs a turn on the UI thread with the depth counted. A dispatched turn that starts inside another turn
    // started in a nested pump.
    private T RunTurn<T>(Func<T> turn, bool dispatched = false)
    {
        if (dispatched && _turnDepth > 0 && !_nestedLogged)
        {
            _nestedLogged = true;
            WpfNavigationLog.ModelTurnNested(_logger);
        }
        _turnDepth++;
        try { return turn(); }
        finally { _turnDepth--; }
    }

    private void Report(Exception error, bool dropped)
    {
        var handler = UnhandledTurnException;
        if (handler is not null)
        {
            try
            {
                handler(error);
                return;
            }
            catch (Exception handlerError)
            {
                WpfNavigationLog.UnhandledTurnHandlerFailed(_logger, handlerError, WpfNavigationLog.ErrorType(handlerError));
            }
        }
        if (dropped) WpfNavigationLog.PostedTurnDropped(_logger, error, WpfNavigationLog.ErrorType(error));
        else WpfNavigationLog.PostedTurnFailed(_logger, error, WpfNavigationLog.ErrorType(error));
    }

    // One dispatched request. Exactly one of Run (once started) and Reject decides its outcome.
    private abstract class WorkItem(DispatcherModelContext owner)
    {
        private int _claimed;

        protected DispatcherModelContext Owner { get; } = owner;

        protected bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;

        public void Run()
        {
            Owner.Forget(this);
            if (!TryClaim()) return;
            if (Volatile.Read(ref Owner._closed)) Rejected();
            else Execute();
        }

        public void Reject()
        {
            if (TryClaim()) Rejected();
        }

        protected abstract void Execute();

        protected abstract void Rejected();
    }

    private sealed class PostedItem(DispatcherModelContext owner, Action turn) : WorkItem(owner)
    {
        protected override void Execute()
        {
            try { Owner.RunTurn(() => { turn(); return true; }, dispatched: true); }
            catch (Exception error) { Owner.Report(error, dropped: false); }
        }

        protected override void Rejected() => Owner.Report(new ObjectDisposedException(nameof(DispatcherModelContext),
            "A posted model turn was dropped because its context closed."), dropped: true);
    }

    private sealed class InvokeItem<T> : WorkItem
    {
        private readonly Func<T> _turn;
        private readonly CancellationToken _cancellationToken;
        private readonly CancellationTokenRegistration _registration;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InvokeItem(DispatcherModelContext owner, Func<T> turn, CancellationToken cancellationToken) : base(owner)
        {
            _turn = turn;
            _cancellationToken = cancellationToken;
            _registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(static state => ((InvokeItem<T>)state!).Cancel(), this)
                : default;
        }

        public Task<T> Completion => _completion.Task;

        protected override void Execute()
        {
            _registration.Dispose();
            try { _completion.TrySetResult(Owner.RunTurn(_turn, dispatched: true)); }
            catch (Exception error) { _completion.TrySetException(error); }
        }

        protected override void Rejected()
        {
            _registration.Dispose();
            _completion.TrySetException(new ObjectDisposedException(nameof(DispatcherModelContext)));
        }

        private void Cancel()
        {
            if (TryClaim()) _completion.TrySetCanceled(_cancellationToken);
        }
    }

    // A hook operation: not a turn, so it may pump and doesn't count toward the depth.
    private sealed class HookItem<T> : WorkItem
    {
        private readonly Func<Task<T>> _hook;
        private readonly CancellationToken _cancellationToken;
        private readonly CancellationTokenRegistration _registration;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HookItem(DispatcherModelContext owner, Func<Task<T>> hook, CancellationToken cancellationToken) : base(owner)
        {
            _hook = hook;
            _cancellationToken = cancellationToken;
            _registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(static state => ((HookItem<T>)state!).Cancel(), this)
                : default;
        }

        public Task<T> Completion => _completion.Task;

        protected override void Execute()
        {
            _registration.Dispose();
            Task<T> task;
            try { task = _hook(); }
            catch (Exception error)
            {
                _completion.TrySetException(error);
                return;
            }
            if (task.IsCompleted) Complete(task, _completion);
            else
                task.ContinueWith(static (completed, state) => Complete(completed, (TaskCompletionSource<T>)state!), _completion,
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        protected override void Rejected()
        {
            _registration.Dispose();
            _completion.TrySetException(new ObjectDisposedException(nameof(DispatcherModelContext)));
        }

        private void Cancel()
        {
            if (TryClaim()) _completion.TrySetCanceled(_cancellationToken);
        }

        private static void Complete(Task<T> task, TaskCompletionSource<T> completion)
        {
            if (task.IsCompletedSuccessfully) completion.TrySetResult(task.Result);
            else if (task.IsFaulted) completion.TrySetException(task.Exception!.InnerExceptions);
            else
            {
                try { task.GetAwaiter().GetResult(); }
                catch (OperationCanceledException canceled) { completion.TrySetCanceled(canceled.CancellationToken); }
                catch (Exception error) { completion.TrySetException(error); }
            }
        }
    }
}
