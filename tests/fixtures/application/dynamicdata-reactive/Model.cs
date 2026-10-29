using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData.Reactive;
using DynamicData.Reactive.Binding;
using ReactiveUI;
using ReactiveUI.Reactive;
using Runic.Application.Views;

namespace DynamicDataReactiveProof;

public sealed record ReactiveRow(int Id, string Label, int Value);

// The System.Reactive flavor of examples/dynamicdata: a sorted, virtualized viewport of a SourceCache, observed on the
// window's System.Reactive scheduler and batched per changeset with BatchBridgeSnapshots.
public sealed class ReactiveRowsViewModel : ReactiveObject, IDisposable
{
    private readonly ObservableCollection<ReactiveRow> _rows = [];
    private readonly BehaviorSubject<IVirtualRequest> _requests;
    private readonly IDisposable _binding;
    private int _totalCount;

    public ReactiveRowsViewModel(SourceCache<ReactiveRow, int> source, IScheduler scheduler, int size)
    {
        Rows = new(_rows);
        _requests = new(new VirtualRequest(0, size));
        _binding = source.Connect()
            .SortAndVirtualize(Comparer<ReactiveRow>.Create((left, right) => left.Id.CompareTo(right.Id)), _requests)
            .ObserveOn(scheduler)
            .BatchBridgeSnapshots(this)
            .Do(change => TotalCount = change.Context.Response.TotalSize)
            .Bind(_rows, new SortAndBindOptions { ResetThreshold = int.MaxValue })
            .Subscribe();
    }

    [RunicCollection(nameof(ReactiveRow.Id))]
    public ReadOnlyObservableCollection<ReactiveRow> Rows { get; }

    public int TotalCount { get => _totalCount; private set => this.RaiseAndSetIfChanged(ref _totalCount, value); }

    public void Show(int start, int size) => _requests.OnNext(new VirtualRequest(start, size));

    public void Dispose()
    {
        _binding.Dispose();
        _requests.Dispose();
    }
}

public sealed partial class ReactiveRowsWindow : RunicWindow<ReactiveRowsViewModel>;
