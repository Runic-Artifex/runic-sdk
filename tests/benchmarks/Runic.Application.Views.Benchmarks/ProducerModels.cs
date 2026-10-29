using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Views.Benchmarks;

// The models of the producer benchmarks (ProducerBenchmarks.cs), with hand-written bridges that follow the generated
// ones: a keyed [RunicCollection] of observable items, a large list that is not keyed, a text field and a counter.
public sealed class TaskItem(int id) : INotifyPropertyChanged
{
    private string _title = $"Task {id}";

    public int Id { get; } = id;

    public string Title
    {
        get => _title;
        set { _title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ProducerModel : INotifyPropertyChanged
{
    private string _title = "";
    private int _counter;

    public ProducerModel(int items, int archive)
    {
        Items = [.. Enumerable.Range(0, items).Select(id => new TaskItem(id))];
        Archive = [.. Enumerable.Range(0, archive).Select(Wire.SampleRow)];
    }

    // Keyed by Id, as [RunicCollection(Key = nameof(TaskItem.Id))] declares it.
    public ObservableCollection<TaskItem> Items { get; }

    // A large list that is not a [RunicCollection]: published by comparison as one member.
    public Row[] Archive { get; }

    public string Title
    {
        get => _title;
        set { _title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); }
    }

    public int Counter
    {
        get => _counter;
        set { _counter = value; PropertyChanged?.Invoke(this, new(nameof(Counter))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static void WriteItem(Utf8JsonWriter writer, object? value)
    {
        var item = (TaskItem)value!;
        writer.WriteStartObject();
        writer.WriteNumber("id", item.Id);
        writer.WriteString("title", item.Title);
        writer.WriteEndObject();
    }

    public static readonly BridgeStateMember<ProducerModel>[] State =
    [
        new("archive", static (writer, model) =>
        {
            writer.WriteStartArray();
            foreach (var row in model.Archive) Wire.WriteRow(writer, row);
            writer.WriteEndArray();
        }),
        new("counter", static (writer, model) => writer.WriteNumberValue(model.Counter)),
        new("items", static (writer, model) =>
        {
            writer.WriteStartArray();
            foreach (var item in model.Items) WriteItem(writer, item);
            writer.WriteEndArray();
        }),
        new("title", static (writer, model) => writer.WriteStringValue(model.Title)),
    ];

    public static readonly BridgeDataSubscriptionMember[] DataMetadata =
    [
        new("items", static owner => ((ProducerModel)owner).Items,
            enumerateChildren: static value => (System.Collections.IEnumerable)value, propertyName: nameof(Items)),
        new("title", static owner => ((ProducerModel)owner).Title, propertyName: nameof(Title)),
    ];
}

public sealed class ProducerBridge(IBridgeTransport transport, ProducerModel model, string route = "producer",
    WindowContentSession? content = null) : ViewModelBridge<ProducerModel>(
    transport, model, route, ProducerModel.State,
    [
        new("Items", "items", vm => vm.Items, null),
        new("Title", "title", vm => vm.Title, (vm, value) => vm.Title = BridgeWire.ReadString(value)),
    ],
    [], [], contractFingerprint: "benchmarks", content: content,
    dataSubscriptions: ProducerModel.DataMetadata,
    collections:
    [
        new("items", static vm => vm.Items, ProducerModel.WriteItem,
            static item => ((TaskItem)item!).Id.ToString(CultureInfo.InvariantCulture)),
    ]);

// A keyed collection of record rows (Wire.SampleRow, about 120 bytes of JSON each) and nothing else. Rows counts how
// often the collection was encoded.
public sealed class RecordModel(int rows) : INotifyPropertyChanged
{
    private int _writes;

    public ObservableCollection<Row> Rows { get; } = [.. Enumerable.Range(0, rows).Select(Wire.SampleRow)];

    public int Writes => Volatile.Read(ref _writes);

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public static readonly BridgeStateMember<RecordModel>[] State =
    [
        new("rows", static (writer, model) =>
        {
            Interlocked.Increment(ref model._writes);
            writer.WriteStartArray();
            foreach (var row in model.Rows) Wire.WriteRow(writer, row);
            writer.WriteEndArray();
        }),
    ];
}

public sealed class RecordBridge(IBridgeTransport transport, RecordModel model, string route) : ViewModelBridge<RecordModel>(
    transport, model, route, RecordModel.State, [new("Rows", "rows", vm => vm.Rows, null)], [], [],
    contractFingerprint: "benchmarks",
    collections:
    [
        new("rows", static vm => vm.Rows, static (writer, item) => Wire.WriteRow(writer, (Row)item!),
            static item => ((Row)item!).Id.ToString(CultureInfo.InvariantCulture)),
    ]);

// A page that takes every push at once. It counts the pushes and completes a wait when a push contains a marker.
public sealed class CountingTransport : IBridgeTransport, IDisposable
{
    private readonly object _gate = new();
    private IBridgeRequestHandler? _handler;
    private bool _greeted;
    private byte[]? _marker;
    private TaskCompletionSource? _seen;
    private long _awaitedPushes = long.MaxValue;
    private TaskCompletionSource? _counted;
    private long _pushes;
    private long _bytes;

    public long Pushes => Interlocked.Read(ref _pushes);

    public long Bytes => Interlocked.Read(ref _bytes);

    public IDisposable Serve(IBridgeRequestHandler handler)
    {
        _handler = handler;
        return this;
    }

    public ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        var pushes = Interlocked.Increment(ref _pushes);
        Interlocked.Add(ref _bytes, message.Length);
        TaskCompletionSource? seen = null, counted = null;
        lock (_gate)
        {
            if (_marker is { } marker && message.Span.IndexOf(marker) >= 0)
            {
                seen = _seen;
                _marker = null;
                _seen = null;
            }
            if (pushes >= _awaitedPushes)
            {
                counted = _counted;
                _counted = null;
                _awaitedPushes = long.MaxValue;
            }
        }
        seen?.TrySetResult();
        counted?.TrySetResult();
        return ValueTask.CompletedTask;
    }

    // Completes when a push contains marker.
    public Task Expect(string marker)
    {
        lock (_gate)
        {
            _marker = Encoding.UTF8.GetBytes(marker);
            _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _seen.Task;
        }
    }

    // Completes when count more pushes arrived.
    public Task ExpectPushes(int count)
    {
        lock (_gate)
        {
            _counted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _awaitedPushes = Pushes + count;
            return _counted.Task;
        }
    }

    public string Request(string json) => RequestAsync(json).AsTask().GetAwaiter().GetResult();

    public async ValueTask<string> RequestAsync(string json)
    {
        var handler = _handler ?? throw new InvalidOperationException("No window serves the transport.");
        if (!_greeted)
        {
            _greeted = true;
            await handler.HandleAsync(new BridgeRequest(
                "{\"kind\":\"hello\",\"protocol\":4,\"session\":\"benchmarks\",\"client\":{\"name\":\"benchmarks\",\"version\":\"1.0.0\"}}"u8.ToArray(),
                "benchmarks", "benchmarks"), CancellationToken.None).ConfigureAwait(false);
        }
        var reply = await handler.HandleAsync(new BridgeRequest(Encoding.UTF8.GetBytes(json), "benchmarks", "benchmarks"),
            CancellationToken.None).ConfigureAwait(false);
        return Encoding.UTF8.GetString(reply.Span);
    }

    public void Dispose() => _handler = null;
}
