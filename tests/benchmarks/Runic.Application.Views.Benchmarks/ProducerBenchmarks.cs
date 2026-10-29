using System.Globalization;
using BenchmarkDotNet.Attributes;
using Runic.Application.Views;
using Runic.Navigation;

namespace Runic.Application.Views.Benchmarks;

// Producer performance (0.14, batch T1). Each benchmark measures until the page received what the change produced.

// Changes of a keyed collection of 10,000 observable rows outside and inside a BridgeSnapshotBatch: the subscription
// graph must not be walked on every change.
public class SubscriptionBenchmarks
{
    private const int Changes = 500;
    private CountingTransport _page = null!;
    private ProducerModel _model = null!;
    private ProducerBridge _bridge = null!;
    private int _nextId;

    [Params(10_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _page = new CountingTransport();
        // The graph's cost, not the minimum interval between units.
        Producer.UnitsImmediately(_page);
        _model = new ProducerModel(Rows, archive: 0);
        _bridge = new ProducerBridge(_page, _model);
        _ = _page.Request("{\"kind\":\"snapshot\",\"route\":\"producer\"}");
        _nextId = Rows;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bridge.Dispose();
        _page.Dispose();
    }

    // Restores 10,000 rows in one batch before each measured iteration.
    [IterationSetup]
    public void Restore()
    {
        if (_model.Items.Count == Rows) return;
        var restored = _page.ExpectPushes(1);
        using (BridgeSnapshotBatch.Begin(_model))
        {
            while (_model.Items.Count > Rows) _model.Items.RemoveAt(_model.Items.Count - 1);
            while (_model.Items.Count < Rows) _model.Items.Add(new TaskItem(_nextId++));
        }
        if (!restored.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The restored rows were not published.");
    }

    [Benchmark]
    public Task AddOutsideBatch()
    {
        var delivered = _page.Expect(Key(_nextId + Changes - 1));
        for (var change = 0; change < Changes; change++) _model.Items.Add(new TaskItem(_nextId++));
        return delivered;
    }

    [Benchmark]
    public Task AddInsideBatch()
    {
        var delivered = _page.Expect(Key(_nextId + Changes - 1));
        using (BridgeSnapshotBatch.Begin(_model))
            for (var change = 0; change < Changes; change++) _model.Items.Add(new TaskItem(_nextId++));
        return delivered;
    }

    [Benchmark]
    public Task RemoveOutsideBatch()
    {
        var last = _model.Items[_model.Items.Count - Changes];
        var delivered = _page.Expect(Key(last.Id));
        for (var change = 0; change < Changes; change++) _model.Items.RemoveAt(_model.Items.Count - 1);
        return delivered;
    }

    private static string Key(int id) => $"\"keys\":[\"{id.ToString(CultureInfo.InvariantCulture)}\"]";
}

// One keystroke: a set of a text member, beside a list of 10,000 rows that is not a [RunicCollection].
public class KeystrokeBenchmarks
{
    private CountingTransport _page = null!;
    private ProducerBridge _bridge = null!;
    private long _edit;

    [Params(10_000)]
    public int Archive { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _page = new CountingTransport();
        _bridge = new ProducerBridge(_page, new ProducerModel(items: 0, Archive));
        _ = _page.Request("{\"kind\":\"snapshot\",\"route\":\"producer\"}");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bridge.Dispose();
        _page.Dispose();
    }

    [Benchmark]
    public string SetText()
    {
        var edit = (++_edit).ToString(CultureInfo.InvariantCulture);
        return _page.Request($"{{\"kind\":\"set\",\"route\":\"producer\",\"member\":\"title\",\"value\":\"draft {edit}\",\"edit\":{edit}}}");
    }
}

// Full states of a keyed collection of 100,000 record rows (about 12 MB of JSON): the snapshot reply, attaching a
// route, which captures its first state, and attaching and opening it.
public class StateBenchmarks
{
    private CountingTransport _page = null!;
    private RecordModel _model = null!;
    private RecordBridge _bridge = null!;
    private int _route;

    [Params(100_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _page = new CountingTransport();
        _model = new RecordModel(Rows);
        _bridge = new RecordBridge(_page, _model, "records");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bridge.Dispose();
        _page.Dispose();
    }

    [Benchmark]
    public string SnapshotReply() => _page.Request("{\"kind\":\"snapshot\",\"route\":\"records\"}");

    [Benchmark]
    public void Attach()
    {
        using var bridge = AttachRoute(out _);
    }

    [Benchmark]
    public string AttachAndOpen()
    {
        using var bridge = AttachRoute(out var route);
        return _page.Request($"{{\"kind\":\"open\",\"route\":\"{route}\",\"presentation\":\"p1\"}}");
    }

    // A new route of the same model, after its first state was captured.
    private RecordBridge AttachRoute(out string route)
    {
        route = $"attached{(++_route).ToString(CultureInfo.InvariantCulture)}";
        var writes = _model.Writes;
        var bridge = new RecordBridge(_page, _model, route);
        var spinner = new SpinWait();
        while (_model.Writes == writes) spinner.SpinOnce();
        return bridge;
    }
}

// 64 routes whose models share one RunicModelContext publish a change each: every drain needs a turn of the context.
public class ModelTurnBenchmarks
{
    private const int Routes = 64;
    private readonly ProducerModel[] _models = new ProducerModel[Routes];
    private readonly ProducerBridge[] _bridges = new ProducerBridge[Routes];
    private RunicModelContext _context = null!;
    private IRunicModelContextLease _ownership = null!;
    private CountingTransport _page = null!;
    private int _counter;

    [GlobalSetup]
    public void Setup()
    {
        _context = new RunicModelContext();
        _page = new CountingTransport();
        // The turns' cost, not the minimum interval between units.
        Producer.UnitsImmediately(_page);
        for (var index = 0; index < Routes; index++) _models[index] = new ProducerModel(items: 0, archive: 0);
        _ownership = RunicModelContextRegistry.Shared.Bind(_context, [.. _models]);
        for (var index = 0; index < Routes; index++)
        {
            _bridges[index] = new ProducerBridge(_page, _models[index], $"route{index.ToString(CultureInfo.InvariantCulture)}");
            _ = _page.Request($"{{\"kind\":\"snapshot\",\"route\":\"route{index.ToString(CultureInfo.InvariantCulture)}\"}}");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var bridge in _bridges) bridge.Dispose();
        _ownership.Dispose();
        _page.Dispose();
        _context.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task ChangeEveryRoute()
    {
        var counter = ++_counter;
        var delivered = _page.ExpectPushes(Routes);
        await _context.InvokeAsync(() =>
        {
            foreach (var model in _models) model.Counter = counter;
        }).ConfigureAwait(false);
        await delivered.ConfigureAwait(false);
    }
}

public static class Producer
{
    // Runs notification units without the minimum interval between them.
    public static void UnitsImmediately(IBridgeTransport transport) =>
        BridgeDispatcher.For(transport).MinimumUnitInterval = TimeSpan.Zero;
}
