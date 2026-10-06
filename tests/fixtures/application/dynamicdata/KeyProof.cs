using System.Collections.ObjectModel;
using System.ComponentModel;
using DynamicData;
using ReactiveUI.Primitives;
using Runic.Application.Views;

namespace DynamicDataExample;

// A DynamicData Bind target whose source can hold duplicate keys: an invalid
// key must not throw into the binding, which would end it.
public sealed class KeyProofViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IDisposable _binding;
    public KeyProofViewModel()
    {
        _binding = Source.Connect().Bind(out var rows).Subscribe();
        Rows = rows;
    }
    [RunicIgnore]
    public SourceList<Row> Source { get; } = new();
    [RunicCollection(nameof(Row.Id))]
    public ReadOnlyObservableCollection<Row> Rows { get; }
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public void Dispose()
    {
        _binding.Dispose();
        Source.Dispose();
    }
}

public sealed partial class KeyProofWindow(KeyProofViewModel model) : RunicWindow<KeyProofViewModel>(model);
