using System.Runtime.CompilerServices;
using System.Windows.Input;
using ReactiveUI;
using Runic.Application.Views;

namespace Runic.Application.Views.ReactiveUI.Reactive;

/// <summary>
/// Executes System.Reactive-flavor ReactiveUI commands from generated Runic bridges.
/// </summary>
public static class ReactiveCommandExecution
{
    /// <summary>Executes a typed command and requires its observable to produce exactly one result.</summary>
    public static async Task<TResult> Execute<TInput, TResult>(
        IReactiveCommand<TInput, TResult> command, TInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        TResult? result = default;
        IDisposable? subscription = null;
        using var cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        subscription = command.Execute(input).Subscribe(
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
            });
        try
        {
            var value = await completion.Task.ConfigureAwait(false);
            return value;
        }
        finally { subscription.Dispose(); }
    }

    /// <summary>Explicitly retains the last result from a multi-value command execution.</summary>
    public static async Task<TResult> ExecuteLast<TInput, TResult>(
        IReactiveCommand<TInput, TResult> command, TInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        TResult? result = default;
        using var cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        using var subscription = command.Execute(input).Subscribe(
            value => { count++; result = value; },
            error => completion.TrySetException(error),
            () =>
            {
                if (count == 0) completion.TrySetException(new InvalidOperationException("Reactive command produced no result."));
                else completion.TrySetResult(result!);
            });
        var value = await completion.Task.ConfigureAwait(false);
        return value;
    }

    /// <summary>
    /// Waits for completion of a no-result command. Internal observable values
    /// are deliberately ignored because a no-result bridge contract does not
    /// require a value to be published.
    /// </summary>
    public static async Task ExecuteCompletion<TInput, TResult>(
        IReactiveCommand<TInput, TResult> command, TInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        using var subscription = command.Execute(input).Subscribe(
            _ => { },
            error => completion.TrySetException(error),
            () => completion.TrySetResult());
        await completion.Task.ConfigureAwait(false);
    }

    /// <summary>Publishes each command result to a bounded Runic operation stream.</summary>
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
        using var subscription = command.Execute(input).Subscribe(
            value =>
            {
                try { _ = stream.TryPublish(encode(value)); }
                catch (Exception)
                {
                    stream.Fail(new BridgeOperationDeliveryFailure(
                        BridgeOperationDeliveryFailureKind.ResultEncodingFailed,
                        "The operation completed, but a stream result could not be encoded."));
                }
            },
            error => completion.TrySetException(error),
            () => completion.TrySetResult());
        await completion.Task.ConfigureAwait(false);
        return BridgeOperationResult.Stream(stream);
    }

    /// <summary>Returns the current availability for a typed command and input.</summary>
    public static bool CanExecute<TInput, TResult>(IReactiveCommand<TInput, TResult> command, TInput input)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is ICommand commandAdapter) return commandAdapter.CanExecute(input);
        if (States.TryGetValue(command, out var state) && state.IsObserved) return state.CanExecute;
        return LatestAvailability(command);
    }

    /// <summary>Returns the latest observed command execution state.</summary>
    public static bool IsExecuting<TInput, TResult>(IReactiveCommand<TInput, TResult> command) =>
        States.GetValue(command, static _ => new State()).IsExecuting;

    /// <summary>Observes availability and execution changes for one bridge lifetime.</summary>
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
            using var subscription = command.CanExecute.Subscribe(next => value = next);
        }
        catch (Exception)
        {
            return false;
        }
        return value == true;
    }

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
            var executing = command.IsExecuting.Subscribe(value => Update(value, isExecuting: true));
            var available = command.CanExecute.Subscribe(value => Update(value, isExecuting: false));
            _subscription = new CompositeLease(executing, available);
        }

        private void Update(bool value, bool isExecuting)
        {
            Action[] listeners = [];
            lock (_gate)
            {
                if (!isExecuting)
                {
                    if (_canExecute == value) listeners = [];
                    else
                    {
                        _canExecute = value;
                        listeners = [.. _listeners];
                    }
                }
                else
                {
                    if (_isExecuting == value) return;
                    _isExecuting = value;
                }
                if (isExecuting) listeners = [.. _listeners];
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
