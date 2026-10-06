using System.Globalization;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace Runic.Application.Views.Benchmarks;

// Snapshot publication and route replies. PropertyBurst holds the host's
// first delivery while the model changes, then measures until the final
// revision has been delivered, like a busy native host that coalesces.
public class SnapshotBenchmarks
{
    private HostTransport _host = null!;
    private BoardModel _model = null!;
    private BoardBridge _bridge = null!;
    private long _revision;

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
        _revision += Changes;
        var delivered = _host.Hold($"{{\"revision\":{_revision.ToString(CultureInfo.InvariantCulture)},");
        for (var change = 0; change < Changes; change++) _model.Counter++;
        _host.Release();
        await delivered.ConfigureAwait(false);
    }

}

// The snapshot route reply embeds the state in its envelope.
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
    public string SnapshotReply() => _host.Inner.Call("boardSnapshot");
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
        _content = new WindowContentSession(_host, rootModel: model);
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
        var payload = $"{{\"requestId\":\"write-{_version.ToString(CultureInfo.InvariantCulture)}\",\"expectedVersion\":{_version.ToString(CultureInfo.InvariantCulture)},\"expectedValue\":{_encoded[current]},\"value\":{_encoded[1 - current]}}}";
        _version++;
        return _host.Inner.Call("boardWritePreferences", new(StringValue: payload));
    }

    [Benchmark]
    public string CanonicalEncode() => _codec.Encode(_values[0]);

    [Benchmark]
    public bool StructuralEquals() => _codec.StructuralEquals(_values[0], _copy);
}

// Bindings go to the in-memory transport; publications are dropped after an
// optional hold so benchmarks do not retain states.
public sealed class HostTransport : IBridgeTransport, IDisposable
{
    private readonly ManualResetEventSlim _released = new(true);
    private TaskCompletionSource? _delivered;
    private string? _finalPrefix;

    public InMemoryViewTransport Inner { get; } = new();

    public IDisposable Bind(string name, Func<IBridgeArguments, string> handler) => Inner.Bind(name, handler);

    public IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler) =>
        Inner.BindAsync(name, handler);

    public Task Hold(string finalPrefix)
    {
        _released.Reset();
        _finalPrefix = finalPrefix;
        _delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _delivered.Task;
    }

    public void Release() => _released.Set();

    public void Publish(string name, string stateJson)
    {
        _released.Wait();
        if (_finalPrefix is { } prefix && stateJson.StartsWith(prefix, StringComparison.Ordinal))
            _delivered?.TrySetResult();
    }

    public void Dispose()
    {
        _released.Set();
        Inner.Dispose();
    }
}
