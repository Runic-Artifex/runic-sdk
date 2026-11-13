using System.Collections.ObjectModel;
using System.ComponentModel;
using Runic.Application.Views;

namespace Runic.Application.Scaling.Tests;

// The getter calls of a ViewModel's members, by member index. The cases read them on the test thread after the
// burst and the unit, which run inline: a ViewModel without a model context runs its turns on the notifying thread.
public sealed class GetterCounts(int members)
{
    private readonly long[] _reads = new long[members];

    public void Read(int member) => Interlocked.Increment(ref _reads[member]);

    public long this[int member] => Volatile.Read(ref _reads[member]);

    public long[] Snapshot() => [.. _reads.Select((_, index) => this[index])];

    public long Total => Snapshot().Sum();
}

// A ViewModel with Size scalar members M00, M01, ...: strings at even indexes, numbers at odd ones (SizedFixtures.cs).
public interface IWideViewModel : INotifyPropertyChanged
{
    GetterCounts Counts { get; }
    int Size { get; }
    // Sets a member to value, or for a string member to its text.
    void SetScalar(int index, int value);
    // Notifies a member without changing it; null or "" means every member.
    void Notify(string? member);
}

// A ViewModel with Size keyed collections Rows0, Rows1, ... and a scalar Selected (SizedFixtures.cs).
public interface ICollectionsViewModel : INotifyPropertyChanged
{
    // Rows0..Rows(Size - 1), then Selected.
    GetterCounts Counts { get; }
    int Size { get; }
    int Selected { get; set; }
    // The collection, without counting a getter call.
    ObservableCollection<ScalingRow> Rows(int index);
    void Notify(string? member);
}

// A row the data graph observes: a change of Label is a replace of the row.
public sealed class ScalingRow(string key, string label) : INotifyPropertyChanged
{
    private string _label = label;

    public string Key { get; } = key;

    public string Label
    {
        get => _label;
        set
        {
            _label = value;
            PropertyChanged?.Invoke(this, new(nameof(Label)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

// A sub-object the Bridge observes through PropertyDescriptor.Observe.
public sealed class ObservedDetail : INotifyPropertyChanged
{
    private string _value = "";

    public string Value
    {
        get => _value;
        set
        {
            _value = value;
            PropertyChanged?.Invoke(this, new(nameof(Value)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

// Size integer members and one observed sub-object, bridged by a Bridge built at run time (ObservedWideBridge): the
// generator emits Observe accessors only for navigation types, so this is how a case counts them.
public sealed class ObservedWideModel(int size) : INotifyPropertyChanged
{
    private readonly int[] _values = new int[size];
    private int _observeCalls;

    public int Size => size;

    public ObservedDetail Detail { get; } = new() { Value = "initial" };

    public int ObserveCalls => Volatile.Read(ref _observeCalls);

    public int Get(int index) => _values[index];

    public void Set(int index, int value)
    {
        _values[index] = value;
        PropertyChanged?.Invoke(this, new(Name(index)));
    }

    public ObservedDetail Observe()
    {
        Interlocked.Increment(ref _observeCalls);
        return Detail;
    }

    public static string Name(int index) => $"M{index:00}";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ObservedWideBridge(IBridgeTransport transport, ObservedWideModel model, string route) : ViewModelBridge<ObservedWideModel>(
    transport, model, route,
    [
        .. Enumerable.Range(0, model.Size).Select(index => new BridgeStateMember<ObservedWideModel>(Wire(index),
            (writer, vm) => writer.WriteNumberValue(vm.Get(index)))),
        new BridgeStateMember<ObservedWideModel>("detail", static (writer, vm) => writer.WriteStringValue(vm.Detail.Value)),
    ],
    [
        .. Enumerable.Range(0, model.Size).Select(index => new PropertyDescriptor<ObservedWideModel>(ObservedWideModel.Name(index), Wire(index),
            vm => vm.Get(index), null)),
        new PropertyDescriptor<ObservedWideModel>(nameof(ObservedWideModel.Detail), "detail", static vm => vm.Detail.Value, null,
            static vm => vm.Observe()),
    ], [], [])
{
    private static string Wire(int index) => $"m{index:00}";
}
