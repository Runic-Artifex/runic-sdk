// Shared by Runic.Application.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.ReactiveUI.Reactive.
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
#if SYSTEM_REACTIVE
using ReactiveUI.Reactive;
using FlavorUnit = System.Reactive.Unit;
using FlavorScheduler = System.Reactive.Concurrency.IScheduler;
#else
using ReactiveUI;
using FlavorUnit = ReactiveUI.Primitives.RxVoid;
using FlavorScheduler = ReactiveUI.Primitives.Concurrency.ISequencer;
#endif
using Runic.Application.Views;

#if SYSTEM_REACTIVE
namespace Runic.Application.Views.ReactiveUI.Reactive;
#else
namespace Runic.Application.Views.ReactiveUI;
#endif

/// <summary>
/// Observes a <see cref="NavigationRegion{TContent}"/> and goes back with a ReactiveUI command.
/// The navigator replaces ReactiveUI's <c>RoutingState</c>; these helpers do not wrap one.
/// </summary>
/// <remarks>
/// A region raises its changes inside the model turn that commits them, so the observables deliver
/// on that turn. Use <c>ObserveOn</c> to move to another scheduler. An entry's lifetime is not
/// ReactiveUI activation: a retained entry stays alive while it is not presented, and its View may
/// deactivate and activate again when it returns.
/// </remarks>
[Experimental(RunicNavigator.DiagnosticId)]
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
    /// Creates a command that goes back in <paramref name="region"/>. It can execute while
    /// <see cref="NavigationRegion{TContent}.CanGoBack"/> is <see langword="true"/> and
    /// <see cref="NavigationRegion{TContent}.IsTransitioning"/> is <see langword="false"/>, and its
    /// output is the <see cref="NavigationResult{TContent}"/>.
    /// </summary>
    /// <remarks>
    /// A rejected, superseded or failed Back is a result, not an exception, so <c>ThrownExceptions</c>
    /// reports only defects and cancellation. Observe it, for example with
    /// <c>RunicReactiveExceptions.ObserveBridgeExceptions</c>.
    /// Cancelling an execution cancels the Back until it commits.
    /// </remarks>
    /// <param name="region">The region.</param>
    /// <param name="scheduler">
    /// The scheduler of the command's output and <c>CanExecute</c>, such as the model context's scheduler from
    /// <see cref="IRunicReactiveSchedulerProvider"/>.
    /// </param>
    public static ReactiveCommand<FlavorUnit, NavigationResult<TContent>> CreateBackCommand<TContent>(
        this NavigationRegion<TContent> region, FlavorScheduler scheduler) where TContent : class
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(scheduler);
        var canExecute = new RegionObservable<TContent, bool>(region,
            static region => region.CanGoBack && !region.IsTransitioning, null, EqualityComparer<bool>.Default);
        return ReactiveCommand.CreateFromTask(
            cancellationToken => region.BackAsync(cancellationToken: cancellationToken).AsTask(), canExecute, scheduler);
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
            subscription.Emit();
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
