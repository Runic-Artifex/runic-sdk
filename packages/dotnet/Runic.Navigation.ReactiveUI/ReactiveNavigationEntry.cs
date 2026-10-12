// Shared by Runic.Navigation.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Navigation.ReactiveUI.Reactive.
using System.Diagnostics.CodeAnalysis;
#if SYSTEM_REACTIVE
using ReactiveUI.Reactive;
using FlavorUnit = System.Reactive.Unit;
using FlavorScheduler = System.Reactive.Concurrency.IScheduler;
using FlavorDisposables = ReactiveUI.Primitives.Reactive.Disposables.ContainerDisposable;
#else
using ReactiveUI;
using FlavorUnit = ReactiveUI.Primitives.RxVoid;
using FlavorScheduler = ReactiveUI.Primitives.Concurrency.ISequencer;
using FlavorDisposables = ReactiveUI.Primitives.Disposables.MultipleDisposable;
#endif

#if SYSTEM_REACTIVE
namespace Runic.Navigation.ReactiveUI.Reactive;
#else
namespace Runic.Navigation.ReactiveUI;
#endif

/// <summary>
/// Reacts to a <see cref="NavigationEntryContext"/> becoming current and being left, and completes, dismisses or
/// closes it with ReactiveUI commands. Content takes its entry context as a constructor parameter.
/// </summary>
/// <remarks>
/// <see cref="WhenNavigatedTo"/> and <see cref="WhenNavigatedFrom"/> are built on
/// <see cref="NavigationEntryContext.StateChanged"/>, so they run in the model turn that commits the change, at the same
/// moment as <see cref="INavigationAware"/>. Navigation lifetime is not ReactiveUI activation: a retained entry stays
/// alive while nothing presents it, and its View may deactivate and activate again when it returns.
/// <para>
/// The entry owns what these methods create: a <see cref="WhenNavigatedTo"/> subscription ends, and a command is disposed,
/// when the entry retires. Dispose either yourself only to stop it earlier.
/// </para>
/// </remarks>
[Experimental(RunicNavigator.DiagnosticId, UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public static class ReactiveNavigationEntry
{
    /// <summary>
    /// Calls <paramref name="onNavigatedTo"/> each time the entry becomes its region's current entry: when the push,
    /// replace or reset that creates it commits, and each time a Back or BackTo resumes it. Each call receives a new
    /// disposable scope, which is disposed when the entry stops being current, whether it is retained or retires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The callback runs in the commit turn, with <see cref="NavigationEntryContext.StateChanged"/>, before the regions
    /// raise <c>PropertyChanged</c>; the scope is disposed in the turn that commits the departure. It must not block or
    /// await navigation. A callback that throws is logged by the navigator (event 1075); the scope it received is
    /// disposed when the entry is left, like any other.
    /// </para>
    /// <para>
    /// Subscribe while the entry is pending, in the constructor or <see cref="INavigationInitialize.InitializeAsync"/>;
    /// a subscription made while the entry is current starts with its next arrival. When the entry's model context
    /// closed before the departure could be raised, the scope is disposed when the entry retires, outside a turn.
    /// Disposing the returned subscription disposes the current scope and stops further calls.
    /// </para>
    /// </remarks>
    /// <param name="entry">The entry context.</param>
    /// <param name="onNavigatedTo">Receives the arrival and the scope for subscriptions that end when the entry is left.</param>
    /// <returns>The subscription, which ends when the entry retires. Dispose it only to stop the calls earlier.</returns>
    public static IDisposable WhenNavigatedTo(this NavigationEntryContext entry, Action<NavigationResume, FlavorDisposables> onNavigatedTo)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(onNavigatedTo);
        return new ArrivalSubscription(entry, onNavigatedTo);
    }

    /// <summary>
    /// Observes the entry stopping being its region's current entry: a push retains it
    /// (<see cref="NavigationDepartureKind.Retain"/>), or a Back, BackTo, Replace, Reset, Clear or the close of its region
    /// retires it (<see cref="NavigationDepartureKind.Retire"/>). The sequence completes when the entry retires.
    /// </summary>
    /// <remarks>
    /// Values arrive in the commit turn, with <see cref="NavigationEntryContext.StateChanged"/> and
    /// <see cref="INavigationAware.OnNavigatedFrom"/>. A retained entry that retires emits nothing more and completes.
    /// There is no initial value; a subscription to a retired entry completes at once.
    /// </remarks>
    /// <param name="entry">The entry context.</param>
    public static IObservable<NavigationExit> WhenNavigatedFrom(this NavigationEntryContext entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new ExitObservable(entry);
    }

    /// <summary>
    /// Creates a command that produces a result with <paramref name="produce"/>, then calls
    /// <see cref="NavigationEntryContext.CompleteAsync{TResult}"/> with it, and outputs the
    /// <see cref="NavigationOutcome"/> of the Back it issues, which a Runic View can receive. This is a Save or OK command.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Back runs the entry's departure guards. A veto outputs <see cref="NavigationOutcomeKind.Rejected"/>, the entry stays
    /// current and the request stays open, so the command can run again. The caller of
    /// <see cref="NavigationRegion{TContent}.PushForResult{TResult}"/> receives the typed result only when the Back commits.
    /// </para>
    /// <para>
    /// A failure of <paramref name="produce"/>, or an entry that was not pushed with <c>PushForResult</c>, is reported on
    /// <c>ThrownExceptions</c>, like a cancellation of the command. A rejected, superseded or failed Back is its output.
    /// </para>
    /// </remarks>
    /// <typeparam name="TResult">The result type of the request.</typeparam>
    /// <param name="entry">The entry context.</param>
    /// <param name="produce">Produces the result, for example by saving. It receives the command's cancellation token.</param>
    /// <param name="canExecute">The command's availability, or <see langword="null"/> to always allow it.</param>
    /// <param name="outputScheduler">
    /// The scheduler of the command's output and <c>IsExecuting</c>, or <see langword="null"/> for ReactiveUI's main-thread
    /// scheduler, as in <see cref="ReactiveNavigation.CreateBackCommand{TContent}"/>.
    /// </param>
    /// <returns>The command, which the entry disposes when it retires.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "The overloads differ in the required produce delegate, which decides the command's input type.")]
    public static ReactiveCommand<FlavorUnit, NavigationOutcome> CreateCompleteCommand<TResult>(this NavigationEntryContext entry,
        Func<CancellationToken, Task<TResult>> produce, IObservable<bool>? canExecute = null, FlavorScheduler? outputScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(produce);
        return DisposeOnRetirement(entry, ReactiveCommand.CreateFromTask(async cancellationToken =>
        {
            // Resume where the command started, so the result is produced and completed on the same model context.
            var result = await produce(cancellationToken);
            return (await entry.CompleteAsync(result, cancellationToken)).Outcome;
        }, canExecute, outputScheduler));
    }

    /// <summary>
    /// Creates a command that completes the entry's request with the command's input, through
    /// <see cref="NavigationEntryContext.CompleteAsync{TResult}"/>, and outputs the <see cref="NavigationOutcome"/> of the
    /// Back it issues. This is a command such as a list's Pick, whose parameter is the result.
    /// </summary>
    /// <remarks>
    /// The semantics are those of the overload that produces the result: a guard veto leaves the entry current and the
    /// request open, and an entry that was not pushed with <c>PushForResult</c> is reported on <c>ThrownExceptions</c>.
    /// </remarks>
    /// <typeparam name="TResult">The result type of the request, and the command's input type.</typeparam>
    /// <param name="entry">The entry context.</param>
    /// <param name="canExecute">The command's availability, or <see langword="null"/> to always allow it.</param>
    /// <param name="outputScheduler">
    /// The scheduler of the command's output and <c>IsExecuting</c>, or <see langword="null"/> for ReactiveUI's main-thread
    /// scheduler.
    /// </param>
    /// <returns>The command, which the entry disposes when it retires.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "The overloads differ in the required produce delegate, which decides the command's input type.")]
    public static ReactiveCommand<TResult, NavigationOutcome> CreateCompleteCommand<TResult>(this NavigationEntryContext entry,
        IObservable<bool>? canExecute = null, FlavorScheduler? outputScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return DisposeOnRetirement(entry, ReactiveCommand.CreateFromTask<TResult, NavigationOutcome>(
            async (result, cancellationToken) => (await entry.CompleteAsync(result, cancellationToken)).Outcome,
            canExecute, outputScheduler));
    }

    /// <summary>
    /// Creates a command that leaves the entry without a result, through <see cref="NavigationEntryContext.DismissAsync"/>,
    /// and outputs the <see cref="NavigationOutcome"/> of the Back. This is a dialog's Cancel command.
    /// </summary>
    /// <remarks>
    /// A request pushed with <see cref="NavigationRegion{TContent}.PushForResult{TResult}"/> ends
    /// <see cref="NavigationCompletion{TResult}.Dismissed"/> at once, before the entry's guards run; if a guard then vetoes
    /// the Back, the request stays dismissed and the entry stays current. Without a request, the command goes back from
    /// the entry.
    /// </remarks>
    /// <param name="entry">The entry context.</param>
    /// <param name="canExecute">The command's availability, or <see langword="null"/> to always allow it.</param>
    /// <param name="outputScheduler">
    /// The scheduler of the command's output and <c>IsExecuting</c>, or <see langword="null"/> for ReactiveUI's main-thread
    /// scheduler.
    /// </param>
    /// <returns>The command, which the entry disposes when it retires.</returns>
    public static ReactiveCommand<FlavorUnit, NavigationOutcome> CreateDismissCommand(this NavigationEntryContext entry,
        IObservable<bool>? canExecute = null, FlavorScheduler? outputScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return DisposeOnRetirement(entry, ReactiveCommand.CreateFromTask(
            async cancellationToken => (await entry.DismissAsync(cancellationToken)).Outcome, canExecute, outputScheduler));
    }

    /// <summary>
    /// Creates a command that goes back from the entry through its departure guards, with
    /// <see cref="NavigationEntryContext.BackAsync"/>, and outputs the <see cref="NavigationOutcome"/>. This is an editor's
    /// Close command: a guard, such as a leave confirmation, may veto it.
    /// </summary>
    /// <remarks>
    /// A request pushed with <see cref="NavigationRegion{TContent}.PushForResult{TResult}"/> stays open on a veto, and ends
    /// <see cref="NavigationCompletion{TResult}.Dismissed"/> only when the Back commits.
    /// </remarks>
    /// <param name="entry">The entry context.</param>
    /// <param name="canExecute">The command's availability, or <see langword="null"/> to always allow it.</param>
    /// <param name="outputScheduler">
    /// The scheduler of the command's output and <c>IsExecuting</c>, or <see langword="null"/> for ReactiveUI's main-thread
    /// scheduler.
    /// </param>
    /// <returns>The command, which the entry disposes when it retires.</returns>
    public static ReactiveCommand<FlavorUnit, NavigationOutcome> CreateCloseCommand(this NavigationEntryContext entry,
        IObservable<bool>? canExecute = null, FlavorScheduler? outputScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return DisposeOnRetirement(entry, ReactiveCommand.CreateFromTask(
            async cancellationToken => (await entry.BackAsync(cancellationToken)).Outcome, canExecute, outputScheduler));
    }

    // The entry owns the commands it creates: once it retires, none of them can act, so their canExecute subscription
    // ends with it and the ViewModel needs no dispose line. Disposing a ReactiveCommand during its own execution, as a
    // Close command's committed Back does, only stops its availability updates. A retired entry disposes it at once.
    private static TCommand DisposeOnRetirement<TCommand>(NavigationEntryContext entry, TCommand command) where TCommand : IDisposable
    {
        entry.Retirement.UnsafeRegister(static state => ((IDisposable)state!).Dispose(), command);
        return command;
    }

    // One WhenNavigatedTo subscription: a scope per arrival, disposed when the entry stops being current. State changes
    // arrive in the context's turns; disposal may come from any thread, so the scope is swapped under a lock and disposed
    // outside it.
    private sealed class ArrivalSubscription : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly NavigationEntryContext _entry;
        private readonly Action<NavigationResume, FlavorDisposables> _onNavigatedTo;
        private readonly CancellationTokenRegistration _retirement;
        private FlavorDisposables? _scope;
        private bool _disposed;

        public ArrivalSubscription(NavigationEntryContext entry, Action<NavigationResume, FlavorDisposables> onNavigatedTo)
        {
            _entry = entry;
            _onNavigatedTo = onNavigatedTo;
            entry.StateChanged += Changed;
            // The fallback when the departure is never raised because the model context closed.
            _retirement = entry.Retirement.UnsafeRegister(static state => ((ArrivalSubscription)state!).Dispose(), this);
        }

        private void Changed(object? sender, NavigationEntryStateChangedEventArgs change)
        {
            if (change.PreviousState == NavigationEntryState.Active) TakeScope()?.Dispose();
            if (change.State == NavigationEntryState.Retired) Dispose();
            if (change.State != NavigationEntryState.Active) return;
            var scope = new FlavorDisposables();
            lock (_lock)
            {
                if (_disposed) return;
                _scope = scope;
            }
            _onNavigatedTo(new NavigationResume(_entry.Id, change.Operation), scope);
        }

        private FlavorDisposables? TakeScope()
        {
            lock (_lock)
            {
                var scope = _scope;
                _scope = null;
                return scope;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            _entry.StateChanged -= Changed;
            _retirement.Dispose();
            TakeScope()?.Dispose();
        }
    }

    private sealed class ExitObservable(NavigationEntryContext entry) : IObservable<NavigationExit>
    {
        public IDisposable Subscribe(IObserver<NavigationExit> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            var subscription = new Subscription(entry, observer);
            entry.StateChanged += subscription.Changed;
            // A retirement raised before the handler was added would never complete the sequence.
            if (entry.State == NavigationEntryState.Retired) subscription.Complete();
            return subscription;
        }

        private sealed class Subscription(NavigationEntryContext entry, IObserver<NavigationExit> observer) : IDisposable
        {
            private int _stopped;

            public void Changed(object? sender, NavigationEntryStateChangedEventArgs change)
            {
                if (Volatile.Read(ref _stopped) != 0) return;
                if (change.PreviousState == NavigationEntryState.Active)
                {
                    var kind = change.State == NavigationEntryState.Retained ? NavigationDepartureKind.Retain : NavigationDepartureKind.Retire;
                    observer.OnNext(new NavigationExit(entry.Id, change.Operation, kind));
                }
                if (change.State == NavigationEntryState.Retired) Complete();
            }

            public void Complete()
            {
                if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
                entry.StateChanged -= Changed;
                observer.OnCompleted();
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
                entry.StateChanged -= Changed;
            }
        }
    }
}
