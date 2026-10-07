using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

public sealed record DtoListEntry(string Name, int Count);

// A list of DTOs as a setter, a checked write and a command argument: their
// generated encoders map each item to an object literal.
public sealed class DtoListViewModel : INotifyPropertyChanged
{
    private IReadOnlyList<DtoListEntry> _entries = [new("first", 1)];

    public DtoListViewModel() => ReplaceCommand = new RelayCommand<IReadOnlyList<DtoListEntry>>(entries => Entries = entries!);

    public IReadOnlyList<DtoListEntry> Entries
    {
        get => _entries;
        set { _entries = value; PropertyChanged?.Invoke(this, new(nameof(Entries))); }
    }

    public IRelayCommand<IReadOnlyList<DtoListEntry>> ReplaceCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class DtoListWindow(DtoListViewModel model) : RunicWindow<DtoListViewModel>(model);
