using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReactiveUI;
using Runic.Application.Views;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests;

public sealed record CatalogRow(int Id, string Label, int Size);

public static class CatalogRows
{
    public static RunicQueryFields<CatalogRow> Fields() => new RunicQueryFields<CatalogRow>()
        .Add("id", row => row.Id)
        .AddText("label", row => row.Label)
        .Add("size", row => row.Size)
        .AddCustom("even", (rows, _) => rows.Where(row => row.Id % 2 == 0));

    public static CatalogRow[] Create(int count) => [.. Enumerable.Range(0, count).Select(id => new CatalogRow(id, $"row {id}", id % 7))];
}

// The ReactiveUI flavor: a query collection beside ordinary state and a command that changes the query from .NET.
public sealed class CatalogViewModel : ReactiveObject
{
    private string _title = "catalog";

    public CatalogViewModel(IRunicRowSource<CatalogRow, int> source, IRunicModelContext? context = null)
    {
        Rows = new(source, row => row.Id, new RunicQueryCollectionOptions { PageSize = 50, ModelContext = context });
        SortByIdCommand = ReactiveCommand.Create(() => Rows.SetQuery(Rows.Query.WithSort(new RunicSortKey("id", Descending: true))));
    }

    /// <summary>The catalog's rows.</summary>
    public RunicQueryCollection<CatalogRow, int> Rows { get; }

    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> SortByIdCommand { get; }
}

public sealed partial class CatalogWindow : RunicWindow<CatalogViewModel>;

// The CommunityToolkit flavor.
public sealed class ToolkitCatalogViewModel : ObservableObject
{
    public ToolkitCatalogViewModel(IRunicRowSource<CatalogRow, int> source, IRunicModelContext? context = null)
    {
        Rows = new(source, row => row.Id, new RunicQueryCollectionOptions { ModelContext = context });
        RefreshCommand = new RelayCommand(Rows.Refresh);
    }

    public RunicQueryCollection<CatalogRow, int> Rows { get; }

    public IRelayCommand RefreshCommand { get; }
}

public sealed partial class ToolkitCatalogWindow : RunicWindow<ToolkitCatalogViewModel>;

// A ViewModel that only holds a collection a test configured.
public sealed class CatalogHolder(RunicQueryCollection<CatalogRow, int> rows) : System.ComponentModel.INotifyPropertyChanged
{
    public RunicQueryCollection<CatalogRow, int> Rows { get; } = rows;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class CatalogHolderWindow : RunicWindow<CatalogHolder>;
