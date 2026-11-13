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
    // A sub-object the data graph observes (#891).
    NotificationCostDetail Detail { get; }
    void ReplaceDetail(NotificationCostDetail detail, string? notify);
}

// A sub-object whose changes the data graph observes.
public sealed class NotificationCostDetail : INotifyPropertyChanged
{
    private string _label = "";

    public string Label
    {
        get => _label;
        set { _label = value; PropertyChanged?.Invoke(this, new(nameof(Label))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
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
    private NotificationCostDetail _detail = new() { Label = "first" };

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

    public NotificationCostDetail Detail { get { Counts.Read(nameof(Detail)); return _detail; } }

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

    public void ReplaceDetail(NotificationCostDetail detail, string? notify)
    {
        _detail = detail;
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
    private NotificationCostDetail _detail = new() { Label = "first" };

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

    public NotificationCostDetail Detail { get { Counts.Read(nameof(Detail)); return _detail; } }

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

    public void ReplaceDetail(NotificationCostDetail detail, string? notify)
    {
        _detail = detail;
        Notify(notify);
    }
}

public sealed partial class ReactiveCostWindow : RunicWindow<ReactiveCostViewModel>;

// #909: a keyed collection whose rows change their own label, beside a scalar, for row edits after other notifications.
public sealed class EditableRow(string key, string label) : INotifyPropertyChanged
{
    private string _label = label;

    public string Key { get; } = key;

    public string Label
    {
        get => _label;
        set { _label = value; PropertyChanged?.Invoke(this, new(nameof(Label))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class RowIndexViewModel : ObservableObject
{
    private ObservableCollection<EditableRow> _rows = [new("a", "one"), new("b", "two"), new("c", "three")];
    private int _selected;

    [RunicCollection(nameof(EditableRow.Key))]
    public ObservableCollection<EditableRow> Rows => _rows;

    public int Selected { get => _selected; set => SetProperty(ref _selected, value); }

    public void Notify(string? member) => OnPropertyChanged(new PropertyChangedEventArgs(member));

    // Replaces Rows and notifies name, which may be another member.
    public void ReplaceRows(IEnumerable<EditableRow> rows, string? notify)
    {
        _rows = [.. rows];
        Notify(notify);
    }
}

public sealed partial class RowIndexWindow : RunicWindow<RowIndexViewModel>;

// #911: scalar members that change without their own notification, including values that are equal but encode
// differently (0.0 and -0.0, 1.0m and 1.00m, a date's kind), and a member computed from another.
public sealed class ScalarMembersViewModel : ObservableObject
{
    private int _count;

    public double Ratio { get; private set; }
    public decimal Amount { get; private set; } = 1.0m;
    public DateTime At { get; private set; } = new(2026, 11, 13, 0, 0, 0, DateTimeKind.Unspecified);
    public int Count { get => _count; set => SetProperty(ref _count, value); }
    // Computed from Count; never announced.
    public int Doubled => _count * 2;

    public void Notify(string? member) => OnPropertyChanged(new PropertyChangedEventArgs(member));

    // Changes the members without a notification of theirs.
    public void SetSilently(double? ratio = null, decimal? amount = null, DateTime? at = null)
    {
        if (ratio is { } r) Ratio = r;
        if (amount is { } a) Amount = a;
        if (at is { } t) At = t;
    }
}

public sealed partial class ScalarMembersWindow : RunicWindow<ScalarMembersViewModel>;
