using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReactiveUI;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// #887: ViewModels whose getters count their calls, so a test can tell how often a notification reads each member.

// The getter calls of a ViewModel's members, by .NET name.
public sealed class GetterCounts
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    public void Read(string member)
    {
        lock (_counts) _counts[member] = _counts.GetValueOrDefault(member) + 1;
    }

    public Dictionary<string, int> Snapshot()
    {
        lock (_counts) return new(_counts, StringComparer.Ordinal);
    }
}

// What a notification-cost ViewModel offers a test, whichever MVVM flavour raises its notifications.
public interface INotificationCostViewModel : INotifyPropertyChanged
{
    GetterCounts Counts { get; }
    // The scalar members, by .NET name, in declaration order.
    IReadOnlyList<string> Scalars { get; }
    void SetScalar(string member, string value);
    // Notifies a member without changing it; null or "" means every member.
    void Notify(string? member);
    ObservableCollection<KeyedRow> Rows { get; }
    ObservableCollection<string> Tags { get; }
    ObservableCollection<KeyedRow> SourceRows { get; }
    // Replaces Rows, Tags or Source (whose rows SourceRows reads) and notifies name, which may be another member, null
    // or "".
    void ReplaceRows(IEnumerable<KeyedRow> rows, string? notify);
    void ReplaceTags(IEnumerable<string> tags, string? notify);
    void ReplaceSource(IEnumerable<KeyedRow> rows, string? notify);
}

// The rows SourceRows reads: replacing them notifies Source, which is not bridged, never SourceRows.
public sealed class NotificationCostSource(IEnumerable<KeyedRow> rows)
{
    public ObservableCollection<KeyedRow> Rows { get; } = [.. rows];
}

// The CommunityToolkit.Mvvm flavour.
public sealed class ToolkitCostViewModel : ObservableObject, INotificationCostViewModel
{
    private string _alpha = "", _beta = "", _gamma = "", _delta = "", _epsilon = "", _zeta = "";
    private ObservableCollection<KeyedRow> _rows = [new("a", "one")];
    private ObservableCollection<string> _tags = ["first"];
    private NotificationCostSource _source = new([new("s", "source")]);

    [RunicIgnore] public GetterCounts Counts { get; } = new();
    [RunicIgnore] public IReadOnlyList<string> Scalars { get; } = [nameof(Alpha), nameof(Beta), nameof(Gamma), nameof(Delta), nameof(Epsilon), nameof(Zeta)];

    public string Alpha { get { Counts.Read(nameof(Alpha)); return _alpha; } set => SetProperty(ref _alpha, value); }
    public string Beta { get { Counts.Read(nameof(Beta)); return _beta; } set => SetProperty(ref _beta, value); }
    public string Gamma { get { Counts.Read(nameof(Gamma)); return _gamma; } set => SetProperty(ref _gamma, value); }
    public string Delta { get { Counts.Read(nameof(Delta)); return _delta; } set => SetProperty(ref _delta, value); }
    public string Epsilon { get { Counts.Read(nameof(Epsilon)); return _epsilon; } set => SetProperty(ref _epsilon, value); }
    public string Zeta { get { Counts.Read(nameof(Zeta)); return _zeta; } set => SetProperty(ref _zeta, value); }

    [RunicCollection(nameof(KeyedRow.Key))]
    public ObservableCollection<KeyedRow> Rows { get { Counts.Read(nameof(Rows)); return _rows; } }

    public ObservableCollection<string> Tags { get { Counts.Read(nameof(Tags)); return _tags; } }

    [RunicCollection(nameof(KeyedRow.Key))]
    public ObservableCollection<KeyedRow> SourceRows { get { Counts.Read(nameof(SourceRows)); return _source.Rows; } }

    public void SetScalar(string member, string value)
    {
        switch (member)
        {
            case nameof(Alpha): Alpha = value; break;
            case nameof(Beta): Beta = value; break;
            case nameof(Gamma): Gamma = value; break;
            case nameof(Delta): Delta = value; break;
            case nameof(Epsilon): Epsilon = value; break;
            case nameof(Zeta): Zeta = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(member), member, null);
        }
    }

    public void Notify(string? member) => OnPropertyChanged(new PropertyChangedEventArgs(member));

    public void ReplaceRows(IEnumerable<KeyedRow> rows, string? notify)
    {
        _rows = [.. rows];
        Notify(notify);
    }

    public void ReplaceTags(IEnumerable<string> tags, string? notify)
    {
        _tags = [.. tags];
        Notify(notify);
    }

    public void ReplaceSource(IEnumerable<KeyedRow> rows, string? notify)
    {
        _source = new(rows);
        Notify(notify);
    }
}

public sealed partial class ToolkitCostWindow : RunicWindow<ToolkitCostViewModel>;

// The ReactiveUI flavour.
public sealed class ReactiveCostViewModel : ReactiveObject, INotificationCostViewModel
{
    private string _alpha = "", _beta = "", _gamma = "", _delta = "", _epsilon = "", _zeta = "";
    private ObservableCollection<KeyedRow> _rows = [new("a", "one")];
    private ObservableCollection<string> _tags = ["first"];
    private NotificationCostSource _source = new([new("s", "source")]);

    [RunicIgnore] public GetterCounts Counts { get; } = new();
    [RunicIgnore] public IReadOnlyList<string> Scalars { get; } = [nameof(Alpha), nameof(Beta), nameof(Gamma), nameof(Delta), nameof(Epsilon), nameof(Zeta)];

    public string Alpha { get { Counts.Read(nameof(Alpha)); return _alpha; } set => this.RaiseAndSetIfChanged(ref _alpha, value); }
    public string Beta { get { Counts.Read(nameof(Beta)); return _beta; } set => this.RaiseAndSetIfChanged(ref _beta, value); }
    public string Gamma { get { Counts.Read(nameof(Gamma)); return _gamma; } set => this.RaiseAndSetIfChanged(ref _gamma, value); }
    public string Delta { get { Counts.Read(nameof(Delta)); return _delta; } set => this.RaiseAndSetIfChanged(ref _delta, value); }
    public string Epsilon { get { Counts.Read(nameof(Epsilon)); return _epsilon; } set => this.RaiseAndSetIfChanged(ref _epsilon, value); }
    public string Zeta { get { Counts.Read(nameof(Zeta)); return _zeta; } set => this.RaiseAndSetIfChanged(ref _zeta, value); }

    [RunicCollection(nameof(KeyedRow.Key))]
    public ObservableCollection<KeyedRow> Rows { get { Counts.Read(nameof(Rows)); return _rows; } }

    public ObservableCollection<string> Tags { get { Counts.Read(nameof(Tags)); return _tags; } }

    [RunicCollection(nameof(KeyedRow.Key))]
    public ObservableCollection<KeyedRow> SourceRows { get { Counts.Read(nameof(SourceRows)); return _source.Rows; } }

    public void SetScalar(string member, string value)
    {
        switch (member)
        {
            case nameof(Alpha): Alpha = value; break;
            case nameof(Beta): Beta = value; break;
            case nameof(Gamma): Gamma = value; break;
            case nameof(Delta): Delta = value; break;
            case nameof(Epsilon): Epsilon = value; break;
            case nameof(Zeta): Zeta = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(member), member, null);
        }
    }

    public void Notify(string? member) => this.RaisePropertyChanged(member ?? "");

    public void ReplaceRows(IEnumerable<KeyedRow> rows, string? notify)
    {
        _rows = [.. rows];
        Notify(notify);
    }

    public void ReplaceTags(IEnumerable<string> tags, string? notify)
    {
        _tags = [.. tags];
        Notify(notify);
    }

    public void ReplaceSource(IEnumerable<KeyedRow> rows, string? notify)
    {
        _source = new(rows);
        Notify(notify);
    }
}

public sealed partial class ReactiveCostWindow : RunicWindow<ReactiveCostViewModel>;
