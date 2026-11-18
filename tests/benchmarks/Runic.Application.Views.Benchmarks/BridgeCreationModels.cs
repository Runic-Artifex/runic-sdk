using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace Runic.Application.Views.Benchmarks;

// BridgeCreationBenchmarks: ten bridged CommunityToolkit ViewModels of one realistic shape (the bridge-size research
// fixture's, runic-sdk#1109, without its interactions, which need a window session): settable text, flag, number, enum
// and record members, read-only and computed members, a keyed collection, and synchronous and asynchronous commands
// with inputs and results. Distinct types, so that each one's first Bridge pays the cold cost.

public sealed record CreationRow(int Id, string Label);
public sealed record CreationAddress(string Street, string City, int Zip);
public enum CreationPriority { Low, Normal, High }

public sealed partial class CreationVm0 : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;
    [ObservableProperty] public partial int Quantity { get; set; } = 1;
    [ObservableProperty] public partial CreationPriority Level { get; set; }
    [ObservableProperty] public partial CreationAddress Shipping { get; set; } = new("", "", 0);

    private int _count;
    public int Count { get => _count; private set => SetProperty(ref _count, value); }
    public string Summary => $"{Title}: {Count}";
    public bool HasRows => Rows.Count > 0;

    [RunicCollection(nameof(CreationRow.Id))] public ObservableCollection<CreationRow> Rows { get; } = new();

    [RelayCommand] private void Refresh() => Count++;
    [RelayCommand] private Task SaveAsync() => Task.CompletedTask;
    [RelayCommand] private void Select(int id) => Quantity = id;
    [RelayCommand] private Task<int> LoadAsync(string filter) => Task.FromResult(filter.Length);
    [RelayCommand] private void Rename(CreationRow row) => Title = row.Label;
    [RelayCommand] private Task<bool> DeleteAsync(CreationRow row, CancellationToken token) => Task.FromResult(Rows.Remove(row));
}

public sealed partial class CreationVm0Window : RunicWindow<CreationVm0>;

public sealed partial class CreationVm1 : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;
    [ObservableProperty] public partial int Quantity { get; set; } = 1;
    [ObservableProperty] public partial CreationPriority Level { get; set; }
    [ObservableProperty] public partial CreationAddress Shipping { get; set; } = new("", "", 0);

    private int _count;
    public int Count { get => _count; private set => SetProperty(ref _count, value); }
    public string Summary => $"{Title}: {Count}";
    public bool HasRows => Rows.Count > 0;

    [RunicCollection(nameof(CreationRow.Id))] public ObservableCollection<CreationRow> Rows { get; } = new();

    [RelayCommand] private void Refresh() => Count++;
    [RelayCommand] private Task SaveAsync() => Task.CompletedTask;
    [RelayCommand] private void Select(int id) => Quantity = id;
    [RelayCommand] private Task<int> LoadAsync(string filter) => Task.FromResult(filter.Length);
    [RelayCommand] private void Rename(CreationRow row) => Title = row.Label;
    [RelayCommand] private Task<bool> DeleteAsync(CreationRow row, CancellationToken token) => Task.FromResult(Rows.Remove(row));
}

public sealed partial class CreationVm1Window : RunicWindow<CreationVm1>;

public sealed partial class CreationVm2 : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;
    [ObservableProperty] public partial int Quantity { get; set; } = 1;
    [ObservableProperty] public partial CreationPriority Level { get; set; }
    [ObservableProperty] public partial CreationAddress Shipping { get; set; } = new("", "", 0);

    private int _count;
    public int Count { get => _count; private set => SetProperty(ref _count, value); }
    public string Summary => $"{Title}: {Count}";
    public bool HasRows => Rows.Count > 0;

    [RunicCollection(nameof(CreationRow.Id))] public ObservableCollection<CreationRow> Rows { get; } = new();

    [RelayCommand] private void Refresh() => Count++;
    [RelayCommand] private Task SaveAsync() => Task.CompletedTask;
    [RelayCommand] private void Select(int id) => Quantity = id;
    [RelayCommand] private Task<int> LoadAsync(string filter) => Task.FromResult(filter.Length);
    [RelayCommand] private void Rename(CreationRow row) => Title = row.Label;
    [RelayCommand] private Task<bool> DeleteAsync(CreationRow row, CancellationToken token) => Task.FromResult(Rows.Remove(row));
}

public sealed partial class CreationVm2Window : RunicWindow<CreationVm2>;

public sealed partial class CreationVm3 : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;
    [ObservableProperty] public partial int Quantity { get; set; } = 1;
    [ObservableProperty] public partial CreationPriority Level { get; set; }
    [ObservableProperty] public partial CreationAddress Shipping { get; set; } = new("", "", 0);

    private int _count;
    public int Count { get => _count; private set => SetProperty(ref _count, value); }
    public string Summary => $"{Title}: {Count}";
    public bool HasRows => Rows.Count > 0;

    [RunicCollection(nameof(CreationRow.Id))] public ObservableCollection<CreationRow> Rows { get; } = new();

    [RelayCommand] private void Refresh() => Count++;
    [RelayCommand] private Task SaveAsync() => Task.CompletedTask;
    [RelayCommand] private void Select(int id) => Quantity = id;
    [RelayCommand] private Task<int> LoadAsync(string filter) => Task.FromResult(filter.Length);
    [RelayCommand] private void Rename(CreationRow row) => Title = row.Label;
    [RelayCommand] private Task<bool> DeleteAsync(CreationRow row, CancellationToken token) => Task.FromResult(Rows.Remove(row));
}

public sealed partial class CreationVm3Window : RunicWindow<CreationVm3>;

public sealed partial class CreationVm4 : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;
    [ObservableProperty] public partial int Quantity { get; set; } = 1;
    [ObservableProperty] public partial CreationPriority Level { get; set; }
    [ObservableProperty] public partial CreationAddress Shipping { get; set; } = new("", "", 0);

    private int _count;
    public int Count { get => _count; private set => SetProperty(ref _count, value); }
    public string Summary => $"{Title}: {Count}";
    public bool HasRows => Rows.Count > 0;

    [RunicCollection(nameof(CreationRow.Id))] public ObservableCollection<CreationRow> Rows { get; } = new();

    [RelayCommand] private void Refresh() => Count++;
    [RelayCommand] private Task SaveAsync() => Task.CompletedTask;
    [RelayCommand] private void Select(int id) => Quantity = id;
    [RelayCommand] private Task<int> LoadAsync(string filter) => Task.FromResult(filter.Length);
    [RelayCommand] private void Rename(CreationRow row) => Title = row.Label;
    [RelayCommand] private Task<bool> DeleteAsync(CreationRow row, CancellationToken token) => Task.FromResult(Rows.Remove(row));
}

public sealed partial class CreationVm4Window : RunicWindow<CreationVm4>;

public sealed partial class CreationVm5 : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;
    [ObservableProperty] public partial int Quantity { get; set; } = 1;
    [ObservableProperty] public partial CreationPriority Level { get; set; }
    [ObservableProperty] public partial CreationAddress Shipping { get; set; } = new("", "", 0);

    private int _count;
    public int Count { get => _count; private set => SetProperty(ref _count, value); }
    public string Summary => $"{Title}: {Count}";
    public bool HasRows => Rows.Count > 0;

    [RunicCollection(nameof(CreationRow.Id))] public ObservableCollection<CreationRow> Rows { get; } = new();

    [RelayCommand] private void Refresh() => Count++;
    [RelayCommand] private Task SaveAsync() => Task.CompletedTask;
    [RelayCommand] private void Select(int id) => Quantity = id;
    [RelayCommand] private Task<int> LoadAsync(string filter) => Task.FromResult(filter.Length);
    [RelayCommand] private void Rename(CreationRow row) => Title = row.Label;
    [RelayCommand] private Task<bool> DeleteAsync(CreationRow row, CancellationToken token) => Task.FromResult(Rows.Remove(row));
}

public sealed partial class CreationVm5Window : RunicWindow<CreationVm5>;

public sealed partial class CreationVm6 : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;
    [ObservableProperty] public partial int Quantity { get; set; } = 1;
    [ObservableProperty] public partial CreationPriority Level { get; set; }
    [ObservableProperty] public partial CreationAddress Shipping { get; set; } = new("", "", 0);

    private int _count;
    public int Count { get => _count; private set => SetProperty(ref _count, value); }
    public string Summary => $"{Title}: {Count}";
    public bool HasRows => Rows.Count > 0;

    [RunicCollection(nameof(CreationRow.Id))] public ObservableCollection<CreationRow> Rows { get; } = new();

    [RelayCommand] private void Refresh() => Count++;
    [RelayCommand] private Task SaveAsync() => Task.CompletedTask;
    [RelayCommand] private void Select(int id) => Quantity = id;
    [RelayCommand] private Task<int> LoadAsync(string filter) => Task.FromResult(filter.Length);
    [RelayCommand] private void Rename(CreationRow row) => Title = row.Label;
    [RelayCommand] private Task<bool> DeleteAsync(CreationRow row, CancellationToken token) => Task.FromResult(Rows.Remove(row));
}

public sealed partial class CreationVm6Window : RunicWindow<CreationVm6>;

public sealed partial class CreationVm7 : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;
    [ObservableProperty] public partial int Quantity { get; set; } = 1;
    [ObservableProperty] public partial CreationPriority Level { get; set; }
    [ObservableProperty] public partial CreationAddress Shipping { get; set; } = new("", "", 0);

    private int _count;
    public int Count { get => _count; private set => SetProperty(ref _count, value); }
    public string Summary => $"{Title}: {Count}";
    public bool HasRows => Rows.Count > 0;

    [RunicCollection(nameof(CreationRow.Id))] public ObservableCollection<CreationRow> Rows { get; } = new();

    [RelayCommand] private void Refresh() => Count++;
    [RelayCommand] private Task SaveAsync() => Task.CompletedTask;
    [RelayCommand] private void Select(int id) => Quantity = id;
    [RelayCommand] private Task<int> LoadAsync(string filter) => Task.FromResult(filter.Length);
    [RelayCommand] private void Rename(CreationRow row) => Title = row.Label;
    [RelayCommand] private Task<bool> DeleteAsync(CreationRow row, CancellationToken token) => Task.FromResult(Rows.Remove(row));
}

public sealed partial class CreationVm7Window : RunicWindow<CreationVm7>;

public sealed partial class CreationVm8 : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;
    [ObservableProperty] public partial int Quantity { get; set; } = 1;
    [ObservableProperty] public partial CreationPriority Level { get; set; }
    [ObservableProperty] public partial CreationAddress Shipping { get; set; } = new("", "", 0);

    private int _count;
    public int Count { get => _count; private set => SetProperty(ref _count, value); }
    public string Summary => $"{Title}: {Count}";
    public bool HasRows => Rows.Count > 0;

    [RunicCollection(nameof(CreationRow.Id))] public ObservableCollection<CreationRow> Rows { get; } = new();

    [RelayCommand] private void Refresh() => Count++;
    [RelayCommand] private Task SaveAsync() => Task.CompletedTask;
    [RelayCommand] private void Select(int id) => Quantity = id;
    [RelayCommand] private Task<int> LoadAsync(string filter) => Task.FromResult(filter.Length);
    [RelayCommand] private void Rename(CreationRow row) => Title = row.Label;
    [RelayCommand] private Task<bool> DeleteAsync(CreationRow row, CancellationToken token) => Task.FromResult(Rows.Remove(row));
}

public sealed partial class CreationVm8Window : RunicWindow<CreationVm8>;

public sealed partial class CreationVm9 : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;
    [ObservableProperty] public partial int Quantity { get; set; } = 1;
    [ObservableProperty] public partial CreationPriority Level { get; set; }
    [ObservableProperty] public partial CreationAddress Shipping { get; set; } = new("", "", 0);

    private int _count;
    public int Count { get => _count; private set => SetProperty(ref _count, value); }
    public string Summary => $"{Title}: {Count}";
    public bool HasRows => Rows.Count > 0;

    [RunicCollection(nameof(CreationRow.Id))] public ObservableCollection<CreationRow> Rows { get; } = new();

    [RelayCommand] private void Refresh() => Count++;
    [RelayCommand] private Task SaveAsync() => Task.CompletedTask;
    [RelayCommand] private void Select(int id) => Quantity = id;
    [RelayCommand] private Task<int> LoadAsync(string filter) => Task.FromResult(filter.Length);
    [RelayCommand] private void Rename(CreationRow row) => Title = row.Label;
    [RelayCommand] private Task<bool> DeleteAsync(CreationRow row, CancellationToken token) => Task.FromResult(Rows.Remove(row));
}

public sealed partial class CreationVm9Window : RunicWindow<CreationVm9>;
