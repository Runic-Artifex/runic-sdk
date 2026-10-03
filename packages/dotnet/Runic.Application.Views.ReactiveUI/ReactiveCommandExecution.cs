// Shared by Runic.Application.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.ReactiveUI.Reactive. Both flavors expose IReactiveCommand
// from the ReactiveUI namespace.
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows.Input;
using ReactiveUI;
using Runic.Application.Views;

#if SYSTEM_REACTIVE
namespace Runic.Application.Views.ReactiveUI.Reactive;
#else
namespace Runic.Application.Views.ReactiveUI;
#endif

/// <summary>
/// Executes the ReactiveUI command contracts that are intentionally broader
/// than <see cref="ICommand"/>.  Generated bridges use this helper instead of
/// assuming that a ViewModel exposed a concrete <c>ReactiveCommand&lt;TParam, TResult&gt;</c>.
/// </summary>
public static class ReactiveCommandExecution
{
    /// <summary>
    /// Executes a typed ReactiveUI command and requires exactly one result.
    /// ReactiveCommand normally has that cardinality; retaining the check
    /// protects a bridge contract from silently treating a custom observable
    /// implementation as last-value-wins.
    /// </summary>
    public static async Task<TResult> Execute<TInput, TResult>(
        IReactiveCommand<TInput, TResult> command, TInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        TResult? result = default;
        using var cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        using var subscription = command.Execute(input).Subscribe(new Observer<TResult>(
            value =>
            {
                count++;
                if (count == 1) result = value;
            },
            error => completion.TrySetException(error),
            () =>
            {
                if (count == 1) completion.TrySetResult(result!);
                else completion.TrySetException(new InvalidOperationException(
                    $"Reactive command produced {count} results; this bridge command requires exactly one."));
            }));
        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Explicit last-result cardinality. It is separate from <see cref="Execute{TInput, TResult}"/>
    /// so a generator cannot silently change a multi-value observable into a
    /// last-value contract.
    /// </summary>
    public static async Task<TResult> ExecuteLast<TInput, TResult>(
        IReactiveCommand<TInput, TResult> command, TInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        TResult? result = default;
        using var cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        using var subscription = command.Execute(input).Subscribe(new Observer<TResult>(
            value => { count++; result = value; },
            error => completion.TrySetException(error),
            () =>
            {
                if (count == 0) completion.TrySetException(new InvalidOperationException("Reactive command produced no result."));
                else completion.TrySetResult(result!);
            }));
        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for command completion when the generated contract has no output.
    /// No-result commands are allowed to publish zero or more internal values;
    /// requiring one would incorrectly turn a completed no-result effect into
    /// a cardinality failure.
    /// </summary>
    public static async Task ExecuteCompletion<TInput, TResult>(
        IReactiveCommand<TInput, TResult> command, TInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        using var subscription = command.Execute(input).Subscribe(new Observer<TResult>(
            _ => { },
            error => completion.TrySetException(error),
            () => completion.TrySetResult()));
        await completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes every observable value to an admitted bounded stream. Result
    /// serialization and retention failures remain visible delivery outcomes;
    /// they do not rewrite a successful side effect as a command failure. Once
    /// the stream overflows or fails, the execution is stopped: disposing its
    /// subscription cancels the command instead of producing into a stream
    /// that can no longer accept values.
    /// </summary>
    public static async Task<BridgeOperationResult> ExecuteStream<TInput, TResult>(
        IReactiveCommand<TInput, TResult> command,
        TInput input,
        BridgeOperationStream stream,
        Func<TResult, string> encode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(encode);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        using var subscription = command.Execute(input).Subscribe(new Observer<TResult>(
            value =>
            {
                bool published;
                try { published = stream.TryPublish(encode(value)); }
                catch (Exception)
                {
                    stream.Fail(new BridgeOperationDeliveryFailure(
                        BridgeOperationDeliveryFailureKind.ResultEncodingFailed,
                        "The operation completed, but a stream result could not be encoded."));
                    published = false;
                }
                if (!published) completion.TrySetResult();
            },
            error => completion.TrySetException(error),
            () => completion.TrySetResult()));
        await completion.Task.ConfigureAwait(false);
        return BridgeOperationResult.Stream(stream);
    }

    /// <summary>
    /// Returns the current availability when the command offers the normal
    /// ICommand adapter. ReactiveUI commands do, including commands exposed
    /// through their IReactiveCommand interface.
    /// </summary>
    public static bool CanExecute<TInput, TResult>(IReactiveCommand<TInput, TResult> command, TInput input)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is ICommand commandAdapter) return commandAdapter.CanExecute(input);
        if (States.TryGetValue(command, out var state) && state.IsObserved) return state.CanExecute;
        return LatestAvailability(command);
    }

    /// <summary>Returns the latest observed ReactiveUI execution state.</summary>
    public static bool IsExecuting<TInput, TResult>(IReactiveCommand<TInput, TResult> command) =>
        States.GetValue(command, static _ => new State()).IsExecuting;

    /// <summary>
    /// Observes state changes for the lifetime of one generated bridge.  The
    /// returned lease owns the observation, while state is shared per command
    /// instance so mirrored presentations see the same value.
    /// </summary>
    public static IDisposable Observe<TInput, TResult>(IReactiveCommand<TInput, TResult> command, Action changed)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(changed);
        return States.GetValue(command, static _ => new State()).Observe(command, changed);
    }

    private static readonly ConditionalWeakTable<object, State> States = [];

    // Interface-only implementations do not promise an ICommand adapter. A
    // generated bridge uses its owned observation when available; direct
    // callers otherwise sample CanExecute without retaining a subscription.
    // ReactiveUI's stream replays its current value synchronously; a custom
    // asynchronous source that has not supplied a value is conservatively
    // unavailable.
    private static bool LatestAvailability(IReactiveCommand command)
    {
        bool? value = null;
        try
        {
            using var subscription = command.CanExecute.Subscribe(new Observer<bool>(next => value = next));
        }
        catch (Exception)
        {
            return false;
        }
        return value == true;
    }

    // Subscribing through IObserver<T> keeps this source independent of each
    // flavor's Subscribe extension methods. Without an error callback a
    // failure is rethrown unchanged, as the Rx Subscribe overloads do.
    private sealed class Observer<T>(Action<T> next, Action<Exception>? error = null, Action? completed = null) : IObserver<T>
    {
        public void OnCompleted() => completed?.Invoke();

        public void OnError(Exception exception)
        {
            if (error is null) ExceptionDispatchInfo.Capture(exception).Throw();
            error(exception);
        }

        public void OnNext(T value) => next(value);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
        Justification = "Weakly cached per command; the last observation lease disposes the subscription.")]
    private sealed class State
    {
        private readonly object _gate = new();
        private readonly List<Action> _listeners = [];
        private IDisposable? _subscription;
        private bool _isExecuting;
        private bool _canExecute;

        public bool CanExecute
        {
            get { lock (_gate) return _canExecute && !_isExecuting; }
        }

        public bool IsExecuting
        {
            get { lock (_gate) return _isExecuting; }
        }

        public bool IsObserved
        {
            get { lock (_gate) return _subscription is not null; }
        }

        public IDisposable Observe(IReactiveCommand command, Action changed)
        {
            lock (_gate)
            {
                Start(command);
                _listeners.Add(changed);
            }
            return new Lease(this, changed);
        }

        private void Start(IReactiveCommand command)
        {
            if (_subscription is not null) return;
            var executing = command.IsExecuting.Subscribe(new Observer<bool>(value => Update(value, isExecuting: true)));
            var available = command.CanExecute.Subscribe(new Observer<bool>(value => Update(value, isExecuting: false)));
            _subscription = new CompositeLease(executing, available);
        }

        private void Update(bool value, bool isExecuting)
        {
            Action[] listeners;
            lock (_gate)
            {
                if (isExecuting)
                {
                    if (_isExecuting == value) return;
                    _isExecuting = value;
                }
                else
                {
                    if (_canExecute == value) return;
                    _canExecute = value;
                }
                listeners = [.. _listeners];
            }
            foreach (var listener in listeners) listener();
        }

        private sealed class Lease(State owner, Action listener) : IDisposable
        {
            private State? _owner = owner;
            private readonly Action _listener = listener;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is null) return;
                lock (owner._gate)
                {
                    owner._listeners.Remove(_listener);
                    if (owner._listeners.Count != 0) return;
                    owner._subscription?.Dispose();
                    owner._subscription = null;
                    owner._isExecuting = false;
                    owner._canExecute = false;
                }
            }
        }

        private sealed class CompositeLease(IDisposable first, IDisposable second) : IDisposable
        {
            public void Dispose()
            {
                first.Dispose();
                second.Dispose();
            }
        }
    }
}
