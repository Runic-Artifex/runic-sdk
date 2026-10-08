using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using System.Windows.Input;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests;

public sealed record TypedSaveRequest(string DocumentId, string Content, int ExpectedVersion);
public sealed record TypedSaveResult(string DocumentId, int SavedVersion, int ContentLength);

// Keep the property typed as the ReactiveUI interface. This verifies discovery
// through the contract instead of depending on a particular factory's concrete
// ReactiveCommand type.
public sealed class TypedReactiveViewModel : ReactiveObject
{
    private readonly TaskCompletionSource _saveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _saveEnabled = true;
    private int _executions;
    private int _plainApplyCount;

    public TypedReactiveViewModel(IRunicModelContext modelContext)
    {
        ArgumentNullException.ThrowIfNull(modelContext);
        var scheduler = new RunicReactiveSchedulerProvider().For(modelContext);
        var canSave = this.WhenAnyValue(model => model.SaveEnabled);
        SaveCommand = ReactiveCommand.CreateFromTask<TypedSaveRequest, TypedSaveResult>(async request =>
        {
            await _saveRelease.Task.ConfigureAwait(false);
            Executions++;
            return new TypedSaveResult(request.DocumentId, request.ExpectedVersion + 1, request.Content.Length);
        }, canSave, scheduler);

        MultiResultCommand = ReactiveCommand.CreateFromObservable<TypedSaveRequest, int>(
            _ => new MultipleResultObservable(), scheduler);
        LastResultCommand = ReactiveCommand.CreateFromObservable<TypedSaveRequest, int>(
            _ => new MultipleResultObservable(), scheduler);
        StreamResultCommand = ReactiveCommand.CreateFromObservable<TypedSaveRequest, int>(
            _ => new SequenceObservable(129), scheduler);
        EmptyCompletionCommand = ReactiveCommand.CreateFromObservable<RxVoid, RxVoid>(
            _ => new EmptyObservable<RxVoid>(), scheduler);
        ImmediateCompletionCommand = ReactiveCommand.CreateFromTask<string>(async _ =>
        {
            await Task.CompletedTask;
        }, scheduler);
        FailCommand = ReactiveCommand.CreateFromTask<TypedSaveRequest, int>(
            _ => Task.FromException<int>(new InvalidOperationException("application failure")), scheduler);
        CancelCommand = ReactiveCommand.CreateFromTask<TypedSaveRequest, int>(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }, scheduler);
        InterfaceOnlyCommand = new InterfaceOnlyReactiveCommand(replayAvailabilityOnlyToFirstSubscriber: true);
        PlainApplyCommand = new PlainApply(this);
    }

    public bool SaveEnabled
    {
        get => _saveEnabled;
        set => this.RaiseAndSetIfChanged(ref _saveEnabled, value);
    }

    public int Executions
    {
        get => _executions;
        private set => this.RaiseAndSetIfChanged(ref _executions, value);
    }

    public int PlainApplyCount
    {
        get => _plainApplyCount;
        private set => this.RaiseAndSetIfChanged(ref _plainApplyCount, value);
    }

    public IReactiveCommand<TypedSaveRequest, TypedSaveResult> SaveCommand { get; }
    public IReactiveCommand<TypedSaveRequest, int> MultiResultCommand { get; }

    [RunicCommandResult(BridgeCommandResultCardinality.Last)]
    public IReactiveCommand<TypedSaveRequest, int> LastResultCommand { get; }

    [RunicCommandResult(BridgeCommandResultCardinality.Stream)]
    public IReactiveCommand<TypedSaveRequest, int> StreamResultCommand { get; }
    public IReactiveCommand<RxVoid, RxVoid> EmptyCompletionCommand { get; }
    public IReactiveCommand<string, RxVoid> ImmediateCompletionCommand { get; }
    public IReactiveCommand<TypedSaveRequest, int> FailCommand { get; }
    public IReactiveCommand<TypedSaveRequest, int> CancelCommand { get; }
    public IReactiveCommand<string, int> InterfaceOnlyCommand { get; }

    [RunicCommandInput(typeof(TypedSaveRequest))]
    public ICommand PlainApplyCommand { get; }

    public void ReleaseSave() => _saveRelease.TrySetResult();

    public void SetInterfaceOnlyAvailability(bool value) =>
        ((InterfaceOnlyReactiveCommand)InterfaceOnlyCommand).SetAvailability(value);

    private sealed class PlainApply(TypedReactiveViewModel owner) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => parameter is TypedSaveRequest request && request.DocumentId == "document-1";
        public void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) throw new InvalidOperationException("Plain command received an invalid input.");
            owner.PlainApplyCount++;
        }
    }

    private sealed class MultipleResultObservable : IObservable<int>
    {
        public IDisposable Subscribe(IObserver<int> observer)
        {
            observer.OnNext(1);
            observer.OnNext(2);
            observer.OnCompleted();
            return EmptyDisposable.Instance;
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();
        public void Dispose() { }
    }

    private sealed class SequenceObservable(int count) : IObservable<int>
    {
        public IDisposable Subscribe(IObserver<int> observer)
        {
            for (var value = 1; value <= count; value++) observer.OnNext(value);
            observer.OnCompleted();
            return EmptyDisposable.Instance;
        }
    }

    private sealed class EmptyObservable<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            observer.OnCompleted();
            return EmptyDisposable.Instance;
        }
    }

    // Deliberately implements the ReactiveUI interface without ICommand. Its
    // availability only replays to the bridge-owned Observe lease, proving
    // that subsequent availability checks use that lease rather than leaking
    // a new subscription or assuming an ICommand adapter.
    internal sealed class InterfaceOnlyReactiveCommand : IReactiveCommand<string, int>
    {
        private readonly AvailabilityObservable _availability;
        private readonly ValueObservable<bool> _isExecuting = new(false);
        private readonly EmptyObservable<Exception> _exceptions = new();
        private int _disposedNeverExecutions;

        public InterfaceOnlyReactiveCommand(bool replayAvailabilityOnlyToFirstSubscriber = false) =>
            _availability = new AvailabilityObservable(true, replayAvailabilityOnlyToFirstSubscriber);

        public IObservable<bool> CanExecute => _availability;
        public IObservable<bool> IsExecuting => _isExecuting;
        public IObservable<Exception> ThrownExceptions => _exceptions;
        public int DisposedNeverExecutions => Volatile.Read(ref _disposedNeverExecutions);

        public IDisposable Subscribe(IObserver<int> observer) => EmptyDisposable.Instance;
        public IObservable<int> Execute(string parameter) => parameter == "never"
            ? new NeverObservable(() => Interlocked.Increment(ref _disposedNeverExecutions))
            : new ResultObservable(parameter.Length);

        public IObservable<int> Execute() => Execute(string.Empty);
        public void Dispose() { }

        public void SetAvailability(bool value) => _availability.Set(value);

        private sealed class ResultObservable(int result) : IObservable<int>
        {
            public IDisposable Subscribe(IObserver<int> observer)
            {
                observer.OnNext(result);
                observer.OnCompleted();
                return EmptyDisposable.Instance;
            }
        }

        private sealed class NeverObservable(Action dispose) : IObservable<int>
        {
            public IDisposable Subscribe(IObserver<int> observer) => new DelegateDisposable(dispose);
        }

        private sealed class AvailabilityObservable(bool initial, bool replayOnlyToFirstSubscriber) : IObservable<bool>
        {
            private readonly object _gate = new();
            private readonly List<IObserver<bool>> _observers = [];
            private bool _value = initial;
            private int _subscriptions;

            public IDisposable Subscribe(IObserver<bool> observer)
            {
                var replay = false;
                lock (_gate)
                {
                    _observers.Add(observer);
                    replay = !replayOnlyToFirstSubscriber || _subscriptions++ == 0;
                }
                if (replay) observer.OnNext(_value);
                return new DelegateDisposable(() =>
                {
                    lock (_gate) _observers.Remove(observer);
                });
            }

            public void Set(bool value)
            {
                IObserver<bool>[] observers;
                lock (_gate)
                {
                    _value = value;
                    observers = [.. _observers];
                }
                foreach (var observer in observers) observer.OnNext(value);
            }
        }

        private sealed class ValueObservable<T>(T initial) : IObservable<T>
        {
            private readonly T _value = initial;
            public IDisposable Subscribe(IObserver<T> observer)
            {
                observer.OnNext(_value);
                return EmptyDisposable.Instance;
            }
        }

        private sealed class DelegateDisposable(Action dispose) : IDisposable
        {
            private Action? _dispose = dispose;
            public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}

public sealed partial class TypedReactiveWindow(TypedReactiveViewModel model) : RunicWindow<TypedReactiveViewModel>(model);
