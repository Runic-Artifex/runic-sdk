using System.Collections.ObjectModel;
using System.ComponentModel;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

public sealed record CollectionRow(int Id, string Label);

public sealed class CollectionDeltaViewModel : INotifyPropertyChanged
{
    private string _title = "rows";
    public CollectionDeltaViewModel() => Rows = new(Items);
    [RunicIgnore]
    public ObservableCollection<CollectionRow> Items { get; } = [];
    [RunicCollection(nameof(CollectionRow.Id))]
    public ReadOnlyObservableCollection<CollectionRow> Rows { get; }
    public string Title
    {
        get => _title;
        set { _title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class CollectionDeltaWindow(CollectionDeltaViewModel model) : RunicWindow<CollectionDeltaViewModel>(model);

public sealed class MutableCollectionRow(int id, string label) : INotifyPropertyChanged
{
    private string _label = label;
    public int Id { get; } = id;
    public string Label
    {
        get => _label;
        set { _label = value; PropertyChanged?.Invoke(this, new(nameof(Label))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MutableCollectionViewModel : INotifyPropertyChanged
{
    private bool _shared;
    public MutableCollectionViewModel() => Rows = new ReadOnlyObservableCollection<MutableCollectionRow>(new ObservableCollection<MutableCollectionRow> { new(42, "initial") });
    [RunicCollection(nameof(MutableCollectionRow.Id))]
    public IReadOnlyList<MutableCollectionRow> Rows { get; }
    public MutableCollectionRow? Selected => _shared ? Rows[0] : null;
    public void Share() { _shared = true; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class MutableCollectionWindow(MutableCollectionViewModel model) : RunicWindow<MutableCollectionViewModel>(model);

public sealed record KeyedRow(string Key, string Label);

public sealed class KeyedCollectionViewModel : INotifyPropertyChanged
{
    public KeyedCollectionViewModel() => Rows = new(Items);
    [RunicIgnore]
    public ObservableCollection<KeyedRow> Items { get; } = [];
    [RunicCollection(nameof(KeyedRow.Key))]
    public ReadOnlyObservableCollection<KeyedRow> Rows { get; }
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class KeyedCollectionWindow(KeyedCollectionViewModel model) : RunicWindow<KeyedCollectionViewModel>(model);

// Validation keeps collections on full states; their keys are still checked.
public sealed class ValidatedCollectionViewModel : INotifyPropertyChanged, INotifyDataErrorInfo
{
    public ValidatedCollectionViewModel() => Rows = new(Items);
    [RunicIgnore]
    public ObservableCollection<KeyedRow> Items { get; } = [];
    [RunicCollection(nameof(KeyedRow.Key))]
    public ReadOnlyObservableCollection<KeyedRow> Rows { get; }
    public bool HasErrors => false;
    public System.Collections.IEnumerable GetErrors(string? propertyName) => Array.Empty<string>();
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged { add { } remove { } }
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class ValidatedCollectionWindow(ValidatedCollectionViewModel model) : RunicWindow<ValidatedCollectionViewModel>(model);

public sealed class MutableKeyedRow(string key, string label) : INotifyPropertyChanged
{
    private string _key = key;
    public string Key
    {
        get => _key;
        set { _key = value; PropertyChanged?.Invoke(this, new(nameof(Key))); }
    }
    public string Label { get; } = label;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MutableKeyedViewModel : INotifyPropertyChanged
{
    public MutableKeyedViewModel() => Rows = new(Items);
    [RunicIgnore]
    public ObservableCollection<MutableKeyedRow> Items { get; } = [new("a", "one"), new("b", "two")];
    [RunicCollection(nameof(MutableKeyedRow.Key))]
    public ReadOnlyObservableCollection<MutableKeyedRow> Rows { get; }
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class MutableKeyedWindow(MutableKeyedViewModel model) : RunicWindow<MutableKeyedViewModel>(model);
