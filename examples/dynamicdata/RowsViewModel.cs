using System.Collections.ObjectModel;
using System.ComponentModel;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using ReactiveUI.Primitives.Signals;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;

namespace DynamicDataExample;

public sealed record Row(int Id, string Label, int Value)
{
    private string _label = Label;
    public string Label
    {
        get { if (MeasureSerialization) Interlocked.Increment(ref _serializedRows); return _label; }
        init => _label = value;
    }
    private static long _serializedRows;
    internal static bool MeasureSerialization { get; set; }
    internal static long ResetSerializedRows() => Interlocked.Exchange(ref _serializedRows, 0);
}
public sealed record ViewportRequest(int Start, int Size);

public sealed class RowStore : IDisposable
{
    public RowStore(int count = 100000)
    {
        Source.AddOrUpdate(Enumerable.Range(0, count).Select(id => new Row(id, $"Row {id}", 0)));
    }
    public SourceCache<Row, int> Source { get; } = new(row => row.Id);
    public void Update(int count) => Source.Edit(updater =>
    {
        for (var id = 0; id < Math.Min(count, Source.Count); id++)
        {
            var row = updater.Lookup(id).Value;
            updater.AddOrUpdate(row with { Value = row.Value + 1 });
        }
    });
    public void Dispose() => Source.Dispose();
}

// A window owns this view-model and its request signal. Another presentation of
// the same store gets an independent viewport, subscriptions and sequencer.
public sealed class RowsViewModel : ReactiveObject, IDisposable
{
    private readonly ObservableCollection<Row> _rows = [];
    private readonly StateSignal<IVirtualRequest> _requests;
    private readonly IDisposable _binding;
    private int _totalCount;
    private int _start;

    public RowsViewModel(RowStore store, IRunicModelContext context)
        : this(store, new RunicReactiveSchedulerProvider().For(context), 30)
    {
    }

    internal RowsViewModel(RowStore store, ISequencer sequencer, int size, bool batch = true)
    {
        Rows = new(_rows);
        _requests = new(new VirtualRequest(0, size));
        var changes = store.Source.Connect()
            .SortAndVirtualize(Comparer<Row>.Create((left, right) => left.Id.CompareTo(right.Id)), _requests)
            .ObserveOn(sequencer);
        if (batch) changes = changes.BatchBridgeSnapshots(this);
        _binding = changes.Do(change =>
        {
            TotalCount = change.Context.Response.TotalSize;
            Start = change.Context.Response.StartIndex;
        }).Bind(_rows, new SortAndBindOptions { ResetThreshold = int.MaxValue }).Subscribe();
        SetViewportCommand = ReactiveCommand.Create<ViewportRequest>(request =>
        {
            if (request.Start < 0 || request.Size is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(request));
            _requests.OnNext(new VirtualRequest(Math.Min(request.Start, Math.Max(0, store.Source.Count - 1)), request.Size));
        }, sequencer);
        UpdateCommand = ReactiveCommand.Create(() => store.Update(100), sequencer);
    }

    [RunicCollection(nameof(Row.Id))]
    public ReadOnlyObservableCollection<Row> Rows { get; }
    public int TotalCount { get => _totalCount; private set => this.RaiseAndSetIfChanged(ref _totalCount, value); }
    public int Start { get => _start; private set => this.RaiseAndSetIfChanged(ref _start, value); }
    public ReactiveCommand<ViewportRequest, ReactiveUI.Primitives.RxVoid> SetViewportCommand { get; }
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> UpdateCommand { get; }

    public void Dispose()
    {
        _binding.Dispose();
        _requests.Dispose();
        SetViewportCommand.Dispose();
        UpdateCommand.Dispose();
    }
}
