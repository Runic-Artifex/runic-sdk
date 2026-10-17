// Shared by Runic.Navigation.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Navigation.ReactiveUI.Reactive.
using System.ComponentModel;
#if SYSTEM_REACTIVE
using ReactiveUI.Reactive;
using FlavorUnit = System.Reactive.Unit;
using FlavorScheduler = System.Reactive.Concurrency.IScheduler;
#else
using ReactiveUI;
using FlavorUnit = ReactiveUI.Primitives.RxVoid;
using FlavorScheduler = ReactiveUI.Primitives.Concurrency.ISequencer;
#endif

#if SYSTEM_REACTIVE
namespace Runic.Navigation.ReactiveUI.Reactive;
#else
namespace Runic.Navigation.ReactiveUI;
#endif

/// <summary>
/// Observes a <see cref="NavigationRegion{TContent}"/> and goes back with a ReactiveUI command.
/// The navigator replaces ReactiveUI's <c>RoutingState</c>; these helpers do not wrap one.
/// </summary>
/// <remarks>
/// Each subscription receives its initial value on the subscribing thread, inside <c>Subscribe</c>.
/// Later values arrive on the model turn in which the region raises the change; content changes
/// arrive on the navigation's commit turn. Use <c>ObserveOn</c> to move to another scheduler.
/// An entry's lifetime is not ReactiveUI activation: a retained entry stays alive while it is not presented, and its View may
/// deactivate and activate again when it returns.
/// </remarks>
public static class ReactiveNavigation
{
    /// <summary>
    /// Observes the region's <see cref="NavigationRegion{TContent}.Current"/> content. Each
    /// subscription receives the current value, or <see langword="null"/> when the region is empty,
    /// and then each different instance. Pushing the same borrowed instance again does not emit;
    /// observe <see cref="WhenEntryChanged{TContent}"/> to see every entry.
    /// </summary>
    /// <remarks>The sequence never completes; dispose the subscription to stop observing.</remarks>
    public static IObservable<TContent?> WhenCurrentChanged<TContent>(this NavigationRegion<TContent> region)
        where TContent : class
    {
        ArgumentNullException.ThrowIfNull(region);
        return new RegionObservable<TContent, TContent?>(region, static region => region.Current,
            nameof(NavigationRegion<TContent>.Current), Same<TContent?>.Instance);
    }

    /// <summary>
    /// Observes the region's <see cref="NavigationRegion{TContent}.CurrentEntry"/>. Each subscription
    /// receives the current entry, or <see langword="null"/> when the region is empty, and then each
    /// different entry, also when two entries present the same content.
    /// </summary>
    /// <remarks>The sequence never completes; dispose the subscription to stop observing.</remarks>
    public static IObservable<NavigationEntry<TContent>?> WhenEntryChanged<TContent>(this NavigationRegion<TContent> region)
        where TContent : class
    {
        ArgumentNullException.ThrowIfNull(region);
        return new RegionObservable<TContent, NavigationEntry<TContent>?>(region, static region => region.CurrentEntry,
            nameof(NavigationRegion<TContent>.CurrentEntry), Same<NavigationEntry<TContent>?>.Instance);
    }

    /// <summary>
    /// Observes whether the region has Back history. Each subscription receives the current value
    /// and then each different value. This does not include transition availability; compose with
    /// <see cref="WhenIsTransitioningChanged{TContent}"/> when creating a command.
    /// </summary>
    /// <remarks>The sequence never completes; dispose the subscription to stop observing.</remarks>
    public static IObservable<bool> WhenCanGoBackChanged<TContent>(this NavigationRegion<TContent> region)
        where TContent : class
    {
        ArgumentNullException.ThrowIfNull(region);
        return new RegionObservable<TContent, bool>(region, static region => region.CanGoBack,
            nameof(NavigationRegion<TContent>.CanGoBack), EqualityComparer<bool>.Default);
    }

    /// <summary>
    /// Observes whether the region has an unsettled transition, including operations started by
    /// other callers. Each subscription receives the current value and then each different value
    /// observed when the region raises a change. An ancestor's transitions are not included.
    /// </summary>
    /// <remarks>The sequence never completes; dispose the subscription to stop observing.</remarks>
    public static IObservable<bool> WhenIsTransitioningChanged<TContent>(this NavigationRegion<TContent> region)
        where TContent : class
    {
        ArgumentNullException.ThrowIfNull(region);
        return new RegionObservable<TContent, bool>(region, static region => region.IsTransitioning,
            nameof(NavigationRegion<TContent>.IsTransitioning), EqualityComparer<bool>.Default);
    }

    /// <summary>
    /// Observes whether the region is busy (<see cref="NavigationRegion{TContent}.IsBusy"/>): a request of the region,
    /// including one started by another caller, has not completed. Each subscription receives the current value and then
    /// each different value observed when the region raises a change. Unlike <see cref="WhenIsTransitioningChanged{TContent}"/>,
    /// it stays <see langword="true"/> after the commit until the request's task completes.
    /// </summary>
    /// <remarks>The sequence never completes; dispose the subscription to stop observing.</remarks>
    public static IObservable<bool> WhenIsBusyChanged<TContent>(this NavigationRegion<TContent> region)
        where TContent : class
    {
        ArgumentNullException.ThrowIfNull(region);
        return new RegionObservable<TContent, bool>(region, static region => region.IsBusy,
            nameof(NavigationRegion<TContent>.IsBusy), EqualityComparer<bool>.Default);
    }

    /// <summary>
    /// Creates a command that goes back in <paramref name="region"/> and outputs the
    /// <see cref="NavigationResult{TContent}.Outcome"/>, a <see cref="NavigationOutcome"/> that a Runic View can
    /// receive. It can execute while <see cref="NavigationRegion{TContent}.CanGoBack"/> is <see langword="true"/> and
    /// <see cref="NavigationRegion{TContent}.IsTransitioning"/> is <see langword="false"/>, and, for a region that admits
    /// requests with <see cref="NavigationAdmission.RejectWhileBusy"/>, while <see cref="NavigationRegion{TContent}.IsBusy"/>
    /// is <see langword="false"/>. <c>CanExecute</c> reflects this region only: a transition of an ancestor region does not
    /// disable it.
    /// </summary>
    /// <remarks>
    /// A rejected, superseded or failed Back is an outcome, not an exception, so <c>ThrownExceptions</c>
    /// reports only defects and cancellation. While a generated Runic Views Bridge presents the command, it logs those
    /// defects; otherwise, without a <c>ThrownExceptions</c> subscriber, ReactiveUI routes them to its default exception
    /// handler.
    /// Code that needs the full <see cref="NavigationResult{TContent}"/>, such as a failure's exception, awaits
    /// <see cref="NavigationRegion{TContent}.BackAsync"/> in its own command.
    /// Overlapping executions to the same destination share one Back and confirmation. Cancelling one execution
    /// cancels that caller; the shared Back is cancelled only when every caller cancels before commit.
    /// The command observes the region through a <c>PropertyChanged</c> handler until the command is disposed. A
    /// command whose owner lives as long as the region, such as a shell ViewModel's command on its own region, needs no
    /// disposal: they become unreachable together. Dispose it with its owner only when the region outlives the owner,
    /// such as a page's command on its parent's region.
    /// </remarks>
    /// <param name="region">The region.</param>
    /// <param name="outputScheduler">
    /// The scheduler of the command's output and <c>IsExecuting</c>, or <see langword="null"/> for ReactiveUI's
    /// main-thread scheduler, as any <c>ReactiveCommand</c> uses: in a Runic Views app,
    /// <c>AddRunicReactiveModelContext</c> from <c>Runic.Application.Views.ReactiveUI</c> makes that the model context of
    /// the turn that executes the command, and in a WPF app it is the dispatcher.
    /// </param>
    public static ReactiveCommand<FlavorUnit, NavigationOutcome> CreateBackCommand<TContent>(
        this NavigationRegion<TContent> region, FlavorScheduler? outputScheduler = null) where TContent : class
    {
        ArgumentNullException.ThrowIfNull(region);
        var canExecute = new RegionObservable<TContent, bool>(region,
            static region => region.CanGoBack && !region.IsTransitioning
                && !(region.Admission == NavigationAdmission.RejectWhileBusy && region.IsBusy),
            null, EqualityComparer<bool>.Default);
        return ReactiveCommand.CreateFromTask(
            // Resume where Back started, so the default scheduler is called from the same model context.
            async cancellationToken => (await region.BackAsync(cancellationToken: cancellationToken)).Outcome,
            canExecute, outputScheduler);
    }

    private sealed class Same<T> : IEqualityComparer<T> where T : class?
    {
        public static readonly Same<T> Instance = new();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T value) => value is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }

    // Emits the value on subscription and then each change, distinct until changed. Each
    // subscription reads and emits under its own lock, so a change raised while the initial
    // value is emitted on another thread is neither lost nor delivered out of order.
    // OnNext therefore runs under that lock: an observer must not block on another thread
    // that raises a change of the same region. The lock is reentrant, so a change raised
    // synchronously from OnNext on the same thread is delivered inside that OnNext call.
    private sealed class RegionObservable<TContent, T>(
        NavigationRegion<TContent> region, Func<NavigationRegion<TContent>, T> read, string? property, IEqualityComparer<T> comparer)
        : IObservable<T> where TContent : class
    {
        private readonly NavigationRegion<TContent> _region = region;
        private readonly Func<NavigationRegion<TContent>, T> _read = read;
        private readonly string? _property = property;
        private readonly IEqualityComparer<T> _comparer = comparer;

        public IDisposable Subscribe(IObserver<T> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            var subscription = new Subscription(this, observer);
            _region.PropertyChanged += subscription.Changed;
            try { subscription.Emit(); }
            catch
            {
                // The initial value's observer threw: remove the handler before the exception propagates.
                subscription.Dispose();
                throw;
            }
            return subscription;
        }

        private sealed class Subscription(RegionObservable<TContent, T> source, IObserver<T> observer) : IDisposable
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
                    var value = source._read(source._region);
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
                source._region.PropertyChanged -= Changed;
            }
        }
    }
}
