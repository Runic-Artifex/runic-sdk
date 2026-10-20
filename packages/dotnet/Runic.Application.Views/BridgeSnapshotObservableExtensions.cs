namespace Runic.Application.Views;

/// <summary>Applies observable batches on Runic's bridge publication boundary.</summary>
public static class BridgeSnapshotObservableExtensions
{
    /// <summary>Defers bridge captures until the synchronous delivery of each changeset completes.</summary>
    /// <remarks>
    /// Place this after <c>ObserveOn(modelSequencer)</c> and immediately before
    /// <c>Bind</c> or <c>SortAndBind</c>. A batch around <c>SourceCache.Edit</c>
    /// ends too early when a scheduler defers collection updates. Disposal and
    /// terminal notifications are forwarded to the source. It works with any
    /// <see cref="IObservable{T}"/>, such as DynamicData's or either ReactiveUI flavor's.
    /// </remarks>
    /// <param name="source">The changesets to deliver.</param>
    /// <param name="model">The ViewModel whose Bridge snapshots are deferred while a changeset is delivered.</param>
    public static IObservable<T> BatchBridgeSnapshots<T>(this IObservable<T> source, object model)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(model);
        return new BatchedObservable<T>(source, model);
    }

    private sealed class BatchedObservable<T>(IObservable<T> source, object model) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            return source.Subscribe(new BatchedObserver<T>(observer, model));
        }
    }

    private sealed class BatchedObserver<T>(IObserver<T> observer, object model) : IObserver<T>
    {
        public void OnNext(T value)
        {
            using var batch = BridgeSnapshotBatch.Begin(model);
            observer.OnNext(value);
        }

        public void OnError(Exception error) => observer.OnError(error);

        public void OnCompleted() => observer.OnCompleted();
    }
}
