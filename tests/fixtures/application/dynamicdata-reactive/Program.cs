using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DynamicData.Reactive;
using DynamicDataReactiveProof;
using Runic.Application.Views;
using Runic.Application.Views.Generated.Bridges;
using Runic.Application.Views.ReactiveUI.Reactive;
using Runic.Navigation;

// The System.Reactive flavor: DynamicData.Reactive, ReactiveUI.Reactive and Runic.Application.Views.ReactiveUI.Reactive.
await using var context = new RunicModelContext();
using var source = new SourceCache<ReactiveRow, int>(row => row.Id);
source.AddOrUpdate(Enumerable.Range(0, 1000).Select(id => new ReactiveRow(id, $"Row {id}", 0)));
using var model = new ReactiveRowsViewModel(source, ReactiveSchedulers.For(context), 30);
using var lease = RunicModelContextRegistry.Shared.Bind(context, model);
await context.InvokeAsync(() => { });
var transport = new ProofTransport();
using var bridge = new ReactiveRowsBridge(transport, model, "rows");
using var initial = JsonDocument.Parse(await transport.RequestAsync(Snapshot("rows")));
var rows = initial.RootElement.GetProperty("state").GetProperty("rows");
if (rows.GetArrayLength() != 30 || rows[0].GetProperty("id").GetInt32() != 0)
    throw new InvalidOperationException($"The System.Reactive viewport did not populate: {rows.GetArrayLength()} rows.");
var baseline = initial.RootElement.GetProperty("revision").GetInt64();
await transport.WaitForRevisionAsync(baseline);
transport.Frames.Clear();

// One changeset of ten updates, batched by BatchBridgeSnapshots, is one frame of ten replacements.
source.Edit(updater =>
{
    for (var id = 0; id < 10; id++) updater.AddOrUpdate(new ReactiveRow(id, $"Row {id}", 1));
});
await context.InvokeAsync(() => { });
await context.InvokeAsync(() => { });
using var updated = JsonDocument.Parse(await transport.RequestAsync(Snapshot("rows")));
await transport.WaitForRevisionAsync(updated.RootElement.GetProperty("revision").GetInt64());
var frames = transport.Frames.ToArray();
if (frames.Length != 1) throw new InvalidOperationException($"One batched changeset emitted {frames.Length} frames.");
using (var frame = JsonDocument.Parse(frames[0]))
{
    var changes = frame.RootElement.GetProperty("changes");
    if (frame.RootElement.GetProperty("kind").GetString() != "frame" || frame.RootElement.GetProperty("baseRevision").GetInt64() != baseline
        || changes.GetArrayLength() != 10 || changes.EnumerateArray().Any(change => change.GetProperty("kind").GetString() != "replace"))
        throw new InvalidOperationException($"The batched changeset was not one frame of ten replacements: {frames[0]}");
}

// A viewport move is published as indexed changes, which give the next snapshot.
transport.Frames.Clear();
var before = updated.RootElement.GetProperty("revision").GetInt64();
await context.InvokeAsync(() => model.Show(500, 20));
await context.InvokeAsync(() => { });
await context.InvokeAsync(() => { });
using var moved = JsonDocument.Parse(await transport.RequestAsync(Snapshot("rows")));
await transport.WaitForRevisionAsync(moved.RootElement.GetProperty("revision").GetInt64());
var movedRows = moved.RootElement.GetProperty("state").GetProperty("rows");
if (movedRows.GetArrayLength() != 20 || movedRows[0].GetProperty("id").GetInt32() != 500
    || moved.RootElement.GetProperty("revision").GetInt64() == before
    || transport.Frames.Any(push => push.Contains("\"kind\":\"state\"", StringComparison.Ordinal)))
    throw new InvalidOperationException("The System.Reactive viewport move was not published as frames.");
Console.WriteLine("DYNAMICDATA_REACTIVE_RUNIC_OK: System.Reactive DynamicData, ReactiveUI.Reactive scheduling, BatchBridgeSnapshots and generated collection frames.");

static string Snapshot(string route) => $"{{\"kind\":\"snapshot\",\"route\":\"{route}\"}}";

// A window transport without a page that keeps the state, frame and failure pushes.
internal sealed class ProofTransport : IBridgeTransport, IDisposable
{
    private IBridgeRequestHandler? _handler;
    private bool _greeted;
    private long _latestRevision;
    internal ConcurrentQueue<string> Frames { get; } = new();

    public IDisposable Serve(IBridgeRequestHandler handler)
    {
        _handler = handler;
        return this;
    }

    public ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        var json = Encoding.UTF8.GetString(message.Span);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("kind").GetString() is "state" or "frame" or "failure")
        {
            Frames.Enqueue(json);
            Interlocked.Exchange(ref _latestRevision, document.RootElement.GetProperty("revision").GetInt64());
        }
        return ValueTask.CompletedTask;
    }

    internal async Task<string> RequestAsync(string json)
    {
        if (!_greeted)
        {
            _greeted = true;
            await SendAsync("""{"kind":"hello","protocol":4,"session":"reactive","client":{"name":"reactive-proof","version":"1.0.0"}}""");
        }
        return await SendAsync(json);
    }

    internal async Task WaitForRevisionAsync(long revision)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (Interlocked.Read(ref _latestRevision) < revision) await Task.Delay(1, timeout.Token);
    }

    private async Task<string> SendAsync(string json)
    {
        var reply = await _handler!.HandleAsync(new BridgeRequest(Encoding.UTF8.GetBytes(json), "reactive", "reactive"), CancellationToken.None);
        var text = Encoding.UTF8.GetString(reply.Span);
        return text.StartsWith("{\"ok\":true", StringComparison.Ordinal) ? text : throw new InvalidOperationException($"{json} failed: {text}");
    }

    public void Dispose() => _handler = null;
}
