using System.Globalization;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Runic.Application.Views;

namespace Runic.Application.Views.Benchmarks;

// State publication and replies. PropertyBurst holds the host's first
// delivery while the model changes, then measures until the frame with the
// final value has been delivered, like a busy native host.
public class SnapshotBenchmarks
{
    private HostTransport _host = null!;
    private BoardModel _model = null!;
    private BoardBridge _bridge = null!;
    private long _counter;

    [Params(200)]
    public int Rows { get; set; }

    [Params(10, 1000)]
    public int Changes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _host = new HostTransport();
        _model = new BoardModel(Rows);
        _bridge = new BoardBridge(_host, _model, content: null);
        // A page reads the state before the route publishes frames.
        _ = _host.Request("{\"kind\":\"snapshot\",\"route\":\"board\"}");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bridge.Dispose();
        _host.Dispose();
    }

    [Benchmark]
    public async Task PropertyBurst()
    {
        _counter += Changes;
        var delivered = _host.Hold($"{{\"field\":\"counter\",\"kind\":\"set\",\"value\":{_counter.ToString(CultureInfo.InvariantCulture)}}}");
        for (var change = 0; change < Changes; change++) _model.Counter++;
        _host.Release();
        await delivered.ConfigureAwait(false);
    }

}

// The snapshot reply carries the full state.
public class ReplyBenchmarks
{
    private HostTransport _host = null!;
    private BoardBridge _bridge = null!;

    [GlobalSetup]
    public void Setup()
    {
        _host = new HostTransport();
        _bridge = new BoardBridge(_host, new BoardModel(rows: 200), content: null);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bridge.Dispose();
        _host.Dispose();
    }

    [Benchmark]
    public string SnapshotReply() => _host.Request("{\"kind\":\"snapshot\",\"route\":\"board\"}");
}

// Checked structured field writes and the canonical comparer they use.
public class CheckedFieldBenchmarks
{
    private readonly Preferences[] _values = [Wire.SamplePreferences(0), Wire.SamplePreferences(1)];
    private readonly string[] _encoded = new string[2];
    private HostTransport _host = null!;
    private WindowContentSession _content = null!;
    private BoardBridge _bridge = null!;
    private BridgeValueCodec<Preferences> _codec = null!;
    private Preferences _copy = null!;
    private long _version;

    [GlobalSetup]
    public void Setup()
    {
        var model = new BoardModel(rows: 20);
        _host = new HostTransport();
        _content = new WindowContentSession(_host, new WindowContentSessionOptions { RootModel = model });
        _bridge = new BoardBridge(_host, model, _content);
        _codec = new BridgeValueCodec<Preferences>(Wire.ReadPreferences, Wire.WritePreferences);
        for (var index = 0; index < 2; index++) _encoded[index] = _codec.Encode(_values[index]);
        _copy = _values[0] with { Shortcuts = _values[0].Shortcuts.Reverse().ToDictionary() };
        for (var warmup = 0; warmup < 2; warmup++)
        {
            using var reply = JsonDocument.Parse(CheckedWrite());
            if (reply.RootElement.GetProperty("receipt").GetProperty("kind").GetString() != "applied")
                throw new InvalidOperationException($"The benchmark write was not applied: {reply.RootElement.GetRawText()}");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bridge.Dispose();
        _content.Dispose();
        _host.Dispose();
    }

    [Benchmark]
    public string CheckedWrite()
    {
        var current = (int)(_version % 2);
        var request = $"{{\"kind\":\"write\",\"route\":\"board\",\"member\":\"preferences\",\"requestId\":\"write-{_version.ToString(CultureInfo.InvariantCulture)}\",\"expectedVersion\":{_version.ToString(CultureInfo.InvariantCulture)},\"expectedValue\":{_encoded[current]},\"value\":{_encoded[1 - current]}}}";
        _version++;
        return _host.Request(request);
    }

    [Benchmark]
    public string CanonicalEncode() => _codec.Encode(_values[0]);

    [Benchmark]
    public bool StructuralEquals() => _codec.StructuralEquals(_values[0], _copy);
}

// The window's transport without a page: requests go to the window's request handler on one connection, after its
// hello, and pushes are dropped after an optional hold so benchmarks do not retain states.
public sealed class HostTransport : IBridgeTransport, IDisposable
{
    private readonly ManualResetEventSlim _released = new(true);
    private TaskCompletionSource? _delivered;
    private byte[]? _final;
    private IBridgeRequestHandler? _handler;
    private bool _greeted;

    public IDisposable Serve(IBridgeRequestHandler handler)
    {
        _handler = handler;
        return this;
    }

    public ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        _released.Wait(cancellationToken);
        if (_final is { } final && message.Span.IndexOf(final) >= 0) _delivered?.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public string Request(string json)
    {
        if (!_greeted)
        {
            _greeted = true;
            _ = Send("{\"kind\":\"hello\",\"protocol\":4,\"session\":\"benchmarks\",\"client\":{\"name\":\"benchmarks\",\"version\":\"1.0.0\"}}");
        }
        return Send(json);
    }

    // Completes when a push containing finalChange was delivered.
    public Task Hold(string finalChange)
    {
        _released.Reset();
        _final = System.Text.Encoding.UTF8.GetBytes(finalChange);
        _delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _delivered.Task;
    }

    public void Release() => _released.Set();

    private string Send(string json)
    {
        var handler = _handler ?? throw new InvalidOperationException("No window serves the transport.");
        var reply = handler.HandleAsync(new BridgeRequest(System.Text.Encoding.UTF8.GetBytes(json), "benchmarks", "benchmarks"),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return System.Text.Encoding.UTF8.GetString(reply.Span);
    }

    public void Dispose()
    {
        _released.Set();
        _released.Dispose();
        _handler = null;
    }
}

// Canonicalizing an array of rows whose members are in declaration order,
// as generated writers emit them.
public class CanonicalArrayBenchmarks
{
    private byte[] _rows = [];

    [Params(4000, 16000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (var id = 0; id < Rows; id++) Wire.WriteRow(writer, Wire.SampleRow(id));
            writer.WriteEndArray();
        }
        _rows = buffer.WrittenSpan.ToArray();
    }

    [Benchmark]
    public byte[] CanonicalizeUnsortedRows() => BridgeWire.Canonicalize(_rows);
}
