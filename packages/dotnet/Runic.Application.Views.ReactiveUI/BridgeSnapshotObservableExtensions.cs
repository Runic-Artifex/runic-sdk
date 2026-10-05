#if SYSTEM_REACTIVE
using System.Reactive.Linq;
#else
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Signals;
#endif
using Runic.Application.Views;

#if SYSTEM_REACTIVE
namespace Runic.Application.Views.ReactiveUI.Reactive;
#else
namespace Runic.Application.Views.ReactiveUI;
#endif

/// <summary>Applies observable batches on Runic's bridge publication boundary.</summary>
public static class BridgeSnapshotObservableExtensions
{
    /// <summary>Defers bridge captures until the synchronous delivery of each changeset completes.</summary>
    /// <remarks>
    /// Place this after <c>ObserveOn(modelSequencer)</c> and immediately before
    /// <c>Bind</c> or <c>SortAndBind</c>. A batch around <c>SourceCache.Edit</c>
    /// ends too early when a scheduler defers collection updates. Disposal and
    /// terminal notifications are forwarded to the source.
    /// </remarks>
    public static IObservable<T> BatchBridgeSnapshots<T>(this IObservable<T> source, object model)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(model);
#if SYSTEM_REACTIVE
        return Observable.Create<T>(observer => source.Subscribe(value =>
#else
        return Signal.Create<T>(observer => source.Subscribe(value =>
#endif
        {
            using var batch = BridgeSnapshotBatch.Begin(model);
            observer.OnNext(value);
        }, observer.OnError, observer.OnCompleted));
    }
}
