using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Runic.Application.Views;

namespace Runic.Application.Views.Benchmarks;

// #887: a burst of notifications on a ViewModel with many bridged members, as when a root ViewModel re-raises its state
// after the page navigates back. Each benchmark raises Notifications single-member notifications in one batch and
// measures until the page received the frame. Two of the members' getters serialize JSON on read, as in the report.
public class NotificationBurstBenchmarks
{
    private CountingTransport _page = null!;
    private WideModel _model = null!;
    private WideBridge _bridge = null!;
    private int _round;

    [Params(75)]
    public int Members { get; set; }

    [Params(60)]
    public int Notifications { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _page = new CountingTransport();
        Producer.UnitsImmediately(_page);
        _model = new WideModel(Members);
        _bridge = new WideBridge(_page, _model);
        _ = _page.Request("{\"kind\":\"snapshot\",\"route\":\"wide\"}");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bridge.Dispose();
        _page.Dispose();
    }

    // Notifications of distinct members, each changed.
    [Benchmark]
    public void Burst()
    {
        var round = ++_round;
        var delivered = _page.ExpectPushes(1);
        using (BridgeSnapshotBatch.Begin(_model))
            for (var notification = 0; notification < Notifications; notification++)
                _model.Set(notification % Members, round);
        if (!delivered.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The burst's frame was not published.");
    }

    // The getter calls of one burst, printed by the probe.
    public static (long Getters, long Serializations) CountOneBurst(int members, int notifications)
    {
        using var page = new CountingTransport();
        Producer.UnitsImmediately(page);
        var model = new WideModel(members);
        using var bridge = new WideBridge(page, model);
        _ = page.Request("{\"kind\":\"snapshot\",\"route\":\"wide\"}");
        var (getters, serializations) = (model.Getters, model.Serializations);
        var delivered = page.ExpectPushes(1);
        using (BridgeSnapshotBatch.Begin(model))
            for (var notification = 0; notification < notifications; notification++) model.Set(notification % members, 1);
        if (!delivered.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The burst's frame was not published.");
        return (model.Getters - getters, model.Serializations - serializations);
    }
}

// A ViewModel with members M0 to Mn-1. M0 and M1 serialize 200 rows to JSON on every read.
public sealed class WideModel(int members) : INotifyPropertyChanged
{
    private const int ExpensiveMembers = 2;
    private readonly int[] _values = new int[members];
    private long _getters;
    private long _serializations;

    public int Count => _values.Length;

    public long Getters => Interlocked.Read(ref _getters);

    public long Serializations => Interlocked.Read(ref _serializations);

    public object Get(int member)
    {
        Interlocked.Increment(ref _getters);
        if (member >= ExpensiveMembers) return _values[member];
        Interlocked.Increment(ref _serializations);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (var row = 0; row < 200; row++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", row);
                writer.WriteString("title", $"Row {row} of {_values[member]}");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public void Set(int member, int value)
    {
        _values[member] = value;
        PropertyChanged?.Invoke(this, new(Name(member)));
    }

    public static string Name(int member) => "M" + member.ToString(CultureInfo.InvariantCulture);

    public event PropertyChangedEventHandler? PropertyChanged;
}

// A hand-written bridge that follows the generated ones: one state member and one property descriptor per member.
public sealed class WideBridge(IBridgeTransport transport, WideModel model) : ViewModelBridge<WideModel>(
    transport, model, "wide",
    [.. Enumerable.Range(0, model.Count).Select(member => new BridgeStateMember<WideModel>(Wire(member), (writer, vm) =>
    {
        switch (vm.Get(member))
        {
            case string text: writer.WriteStringValue(text); break;
            case int number: writer.WriteNumberValue(number); break;
        }
    }))],
    [.. Enumerable.Range(0, model.Count).Select(member => new PropertyDescriptor<WideModel>(WideModel.Name(member), Wire(member), vm => vm.Get(member), null))],
    [], [])
{
    private static string Wire(int member) => "m" + member.ToString(CultureInfo.InvariantCulture);
}
