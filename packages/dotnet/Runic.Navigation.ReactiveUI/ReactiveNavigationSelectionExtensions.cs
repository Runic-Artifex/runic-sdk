// Shared by Runic.Navigation.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Navigation.ReactiveUI.Reactive.
using System.ComponentModel;
#if SYSTEM_REACTIVE
using ReactiveUI.Reactive;
using FlavorScheduler = System.Reactive.Concurrency.IScheduler;
#else
using ReactiveUI;
using FlavorScheduler = ReactiveUI.Primitives.Concurrency.ISequencer;
#endif

#if SYSTEM_REACTIVE
namespace Runic.Navigation.ReactiveUI.Reactive;
#else
namespace Runic.Navigation.ReactiveUI;
#endif

/// <summary>
/// Observes a <see cref="NavigationSelection"/>, such as a page's tabs that each keep their own stack, and changes it with
/// a ReactiveUI command.
/// </summary>
/// <remarks>
/// Each subscription receives its initial value on the subscribing thread, inside <c>Subscribe</c>. Later values arrive
/// on the model turn in which the selection raises the change; a selection change arrives on its commit turn. Use
/// <c>ObserveOn</c> to move to another scheduler.
/// </remarks>
public static class ReactiveNavigationSelectionExtensions
{
    /// <summary>
    /// Observes the selection's <see cref="NavigationSelection.Selected"/> region. Each subscription receives the selected
    /// region, or <see langword="null"/> when the selection has no regions, and then each different region.
    /// </summary>
    /// <remarks>The sequence never completes; dispose the subscription to stop observing.</remarks>
    public static IObservable<NavigationRegion?> WhenSelectedChanged(this NavigationSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return new SelectionObservable<NavigationRegion?>(selection, static selection => selection.Selected,
            nameof(NavigationSelection.Selected), ReactiveNavigationRegionExtensions.Same<NavigationRegion?>.Instance);
    }

    /// <summary>
    /// Observes the selection's <see cref="NavigationSelection.SelectedIndex"/>. Each subscription receives the index of the
    /// selected region, or -1 when the selection has no regions, and then each different index, also when removing an
    /// earlier region moves the selected one.
    /// </summary>
    /// <remarks>The sequence never completes; dispose the subscription to stop observing.</remarks>
    public static IObservable<int> WhenSelectedIndexChanged(this NavigationSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return new SelectionObservable<int>(selection, static selection => selection.SelectedIndex,
            nameof(NavigationSelection.SelectedIndex), EqualityComparer<int>.Default);
    }

    /// <summary>
    /// Observes whether a selection change is in flight (<see cref="NavigationSelection.IsTransitioning"/>), including one
    /// started by another caller. Each subscription receives the current value and then each different value observed
    /// when the selection raises a change. The transitions of its regions are not included.
    /// </summary>
    /// <remarks>The sequence never completes; dispose the subscription to stop observing.</remarks>
    public static IObservable<bool> WhenIsTransitioningChanged(this NavigationSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return new SelectionObservable<bool>(selection, static selection => selection.IsTransitioning,
            nameof(NavigationSelection.IsTransitioning), EqualityComparer<bool>.Default);
    }

    /// <summary>
    /// Observes whether a request of the selection has not completed (<see cref="NavigationSelection.IsBusy"/>), including
    /// one started by another caller. Each subscription receives the current value and then each different value observed
    /// when the selection raises a change. Unlike <see cref="WhenIsTransitioningChanged"/>, it stays
    /// <see langword="true"/> after the commit until the request's task completes.
    /// </summary>
    /// <remarks>The sequence never completes; dispose the subscription to stop observing.</remarks>
    public static IObservable<bool> WhenIsBusyChanged(this NavigationSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return new SelectionObservable<bool>(selection, static selection => selection.IsBusy,
            nameof(NavigationSelection.IsBusy), EqualityComparer<bool>.Default);
    }

    /// <summary>
    /// Creates a command that selects the region at the command's parameter, an index in
    /// <see cref="NavigationSelection.Regions"/>, through <see cref="NavigationSelection.SelectAsync"/>, and outputs the
    /// <see cref="NavigationResult{TContent}.Outcome"/>, a <see cref="NavigationOutcome"/> that a Runic View can receive. A
    /// veto keeps the selected region and is the output <see cref="NavigationOutcomeKind.Rejected"/>. For a selection that
    /// admits requests with <see cref="NavigationAdmission.RejectWhileBusy"/>, it can't execute while
    /// <see cref="NavigationSelection.IsBusy"/> is <see langword="true"/>; otherwise a later execution supersedes an earlier
    /// one that hasn't started committing, so the last tab chosen wins.
    /// </summary>
    /// <remarks>
    /// A rejected, superseded or failed selection change is an outcome, not an exception, so <c>ThrownExceptions</c>
    /// reports only defects and cancellation. An index outside <see cref="NavigationSelection.Regions"/> is such a defect:
    /// the execution fails with <see cref="ArgumentOutOfRangeException"/> and selects nothing. The index is read against
    /// <see cref="NavigationSelection.Regions"/> when the command executes.
    /// The command observes the selection through a <c>PropertyChanged</c> handler until the command is disposed. A
    /// command whose owner lives as long as the selection, such as a page ViewModel's command on its own tabs, needs no
    /// disposal.
    /// </remarks>
    /// <param name="selection">The selection.</param>
    /// <param name="canExecute">
    /// An additional condition for the command, combined with the selection's condition: the command can execute while
    /// both are <see langword="true"/>. <see langword="null"/> uses the selection's condition alone. Until it emits its first
    /// value, the command can't execute.
    /// </param>
    /// <param name="outputScheduler">
    /// The scheduler of the command's output and <c>IsExecuting</c>, or <see langword="null"/> for ReactiveUI's main-thread
    /// scheduler, as in <see cref="ReactiveNavigationRegionExtensions.CreateBackCommand{TContent}"/>.
    /// </param>
    public static ReactiveCommand<int, NavigationOutcome> CreateSelectCommand(this NavigationSelection selection,
        IObservable<bool>? canExecute = null, FlavorScheduler? outputScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        IObservable<bool> available = new SelectionObservable<bool>(selection,
            static selection => !(selection.Admission == NavigationAdmission.RejectWhileBusy && selection.IsBusy),
            null, EqualityComparer<bool>.Default);
        return ReactiveCommand.CreateFromTask<int, NavigationOutcome>(
            // Resume where the selection change started, so the default scheduler is called from the same model context.
            async (index, cancellationToken) =>
            {
                var regions = selection.Regions;
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, regions.Count);
                return (await selection.SelectAsync(regions[index], cancellationToken: cancellationToken)).Outcome;
            },
            canExecute is null ? available : new ReactiveNavigationRegionExtensions.BothTrue(available, canExecute),
            outputScheduler);
    }

    // Emits the value on subscription and then each change, distinct until changed, as the region extensions'
    // RegionObservable does: each subscription reads and emits under its own reentrant lock, so an observer must not block
    // on another thread that raises a change of the same selection.
    private sealed class SelectionObservable<T>(
        NavigationSelection selection, Func<NavigationSelection, T> read, string? property, IEqualityComparer<T> comparer)
        : IObservable<T>
    {
        private readonly NavigationSelection _selection = selection;
        private readonly Func<NavigationSelection, T> _read = read;
        private readonly string? _property = property;
        private readonly IEqualityComparer<T> _comparer = comparer;

        public IDisposable Subscribe(IObserver<T> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            var subscription = new Subscription(this, observer);
            _selection.PropertyChanged += subscription.Changed;
            try { subscription.Emit(); }
            catch
            {
                // The initial value's observer threw: remove the handler before the exception propagates.
                subscription.Dispose();
                throw;
            }
            return subscription;
        }

        private sealed class Subscription(SelectionObservable<T> source, IObserver<T> observer) : IDisposable
        {
            private readonly Lock _lock = new();
            private bool _emitted;
            private bool _disposed;
            private T _last = default!;

            public void Changed(object? sender, PropertyChangedEventArgs args)
            {
                if (source._property is null || string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == source._property)
                    Emit();
            }

            public void Emit()
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    var value = source._read(source._selection);
                    if (_emitted && source._comparer.Equals(_last, value)) return;
                    _emitted = true;
                    _last = value;
                    observer.OnNext(value);
                }
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    _disposed = true;
                }
                source._selection.PropertyChanged -= Changed;
            }
        }
    }
}
