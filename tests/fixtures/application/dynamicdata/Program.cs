using System.Text.Json;
using DynamicData;
using DynamicDataExample;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Advanced;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Application.Views.Generated.Bridges;
using Runic.Navigation;

if (args.Contains("--benchmark", StringComparer.Ordinal)) { await Benchmarks.RunAsync(); return; }
await using var firstContext = new RunicModelContext();
await using var secondContext = new RunicModelContext();
using var store = new RowStore(10000);
using var first = new RowsViewModel(store, firstContext);
using var second = new RowsViewModel(store, secondContext);
using var firstLease = RunicModelContextRegistry.Shared.Bind(firstContext, first);
using var secondLease = RunicModelContextRegistry.Shared.Bind(secondContext, second);
await firstContext.InvokeAsync(() => { });
await secondContext.InvokeAsync(() => { });
var transport = new Benchmarks.MeteredTransport();
using var bridge = new RowsBridge(transport, first, "rows");
using var initial = JsonDocument.Parse(await transport.RequestAsync(Snapshot("rows")));
if (first.Rows.Count != 30 || second.Rows.Count != 30) throw new InvalidOperationException("Deferred Bind did not populate both viewports.");
var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using var execute = first.SetViewportCommand.Execute(new(500, 20)).Subscribe(Witness.Create<ReactiveUI.Primitives.RxVoid>(
    _ => completed.TrySetResult(), error => completed.TrySetException(error)));
await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
await firstContext.InvokeAsync(() => { });
if (first.Rows.Count != 20 || first.Rows[0].Id != 500 || second.Rows[0].Id != 0)
    throw new InvalidOperationException("A viewport request leaked into another presentation.");
using var moved = JsonDocument.Parse(await transport.RequestAsync(Snapshot("rows")));
var before = moved.RootElement.GetProperty("revision").GetInt64();
store.Update(10);
await firstContext.InvokeAsync(() => { });
await secondContext.InvokeAsync(() => { });
using var outside = JsonDocument.Parse(await transport.RequestAsync(Snapshot("rows")));
if (outside.RootElement.GetProperty("revision").GetInt64() != before)
    throw new InvalidOperationException("Off-screen updates crossed the presentation bridge.");
var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using var back = first.SetViewportCommand.Execute(new(0, 20)).Subscribe(Witness.Create<ReactiveUI.Primitives.RxVoid>(
    _ => returned.TrySetResult(), error => returned.TrySetException(error)));
await returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
await firstContext.InvokeAsync(() => { });
await firstContext.InvokeAsync(() => { });
using var final = JsonDocument.Parse(await transport.RequestAsync(Snapshot("rows")));
var baseline = final.RootElement.GetProperty("revision").GetInt64();
// Delivery is asynchronous: drain the viewport move's frames before counting.
await transport.WaitForRevisionAsync(baseline);
transport.Frames.Clear();
store.Update(10);
await firstContext.InvokeAsync(() => { });
await firstContext.InvokeAsync(() => { });
using var updated = JsonDocument.Parse(await transport.RequestAsync(Snapshot("rows")));
await transport.WaitForRevisionAsync(updated.RootElement.GetProperty("revision").GetInt64());
var frames = transport.Frames.ToArray();
if (frames.Length != 1) throw new InvalidOperationException($"One deferred changeset emitted {frames.Length} bridge frames.");
using var delta = JsonDocument.Parse(frames[0]);
if (delta.RootElement.GetProperty("kind").GetString() != "frame" ||
    delta.RootElement.GetProperty("baseRevision").GetInt64() != baseline ||
    delta.RootElement.GetProperty("changes").GetArrayLength() != 10)
    throw new InvalidOperationException("Deferred application did not produce one incremental changeset frame.");
// An invalid key is withheld and reported without ending the DynamicData binding.
var keyTransport = new Benchmarks.MeteredTransport();
using var keyModel = new KeyProofViewModel();
using var keyBridge = new KeyProofBridge(keyTransport, keyModel, "keys");
using (JsonDocument.Parse(await keyTransport.RequestAsync(Snapshot("keys")))) { }
keyModel.Source.Add(new Row(1, "one", 0));
await NextFrameAsync(frame => Kind(frame) == "frame", "the first row");
keyModel.Source.Add(new Row(1, "again", 0));
await NextFrameAsync(frame => Kind(frame) == "failure", "the duplicate key");
keyModel.Source.RemoveAt(1);
// The withheld collection becomes a set of the field once its keys are valid again.
await NextFrameAsync(frame => Kind(frame) == "frame" && frame.GetProperty("changes").EnumerateArray().Any(change =>
    change.GetProperty("kind").GetString() == "set" && change.GetProperty("value").GetArrayLength() == 1), "the corrected keys");
keyModel.Source.Add(new Row(2, "two", 0));
await NextFrameAsync(frame => Kind(frame) == "frame" && frame.GetProperty("changes").EnumerateArray().Any(change =>
    change.GetProperty("kind").GetString() == "add"), "a row after the correction");
if (keyModel.Rows.Count != 2) throw new InvalidOperationException("The DynamicData binding stopped after an invalid key.");

// Frame replay: the frames since a snapshot, applied as a client applies them, give the next snapshot. A viewport move
// (SortAndVirtualize) and a SortAndBind comparer change move, add and remove rows.
await using var replayContext = new RunicModelContext();
using var replayModel = new RowsViewModel(store, replayContext);
using var replayLease = RunicModelContextRegistry.Shared.Bind(replayContext, replayModel);
await replayContext.InvokeAsync(() => { });
var replayTransport = new Benchmarks.MeteredTransport();
using var replayBridge = new RowsBridge(replayTransport, replayModel, "viewport");
await ReplayAsync(replayTransport, "viewport", replayContext, async () =>
{
    await ExecuteAsync(replayModel.SetViewportCommand.Execute(new(5, 30)));
    store.Update(40);
    await ExecuteAsync(replayModel.SetViewportCommand.Execute(new(20, 10)));
    await ExecuteAsync(replayModel.SetViewportCommand.Execute(new(0, 30)));
}, minimumFrames: 3, requiredKinds: ["add", "remove"]);

await using var sortContext = new RunicModelContext();
using var sortSource = new SourceCache<Row, int>(row => row.Id);
sortSource.AddOrUpdate(Enumerable.Range(0, 40).Select(id => new Row(id, $"Row {(id * 7) % 40:00}", id % 5)));
using var sortModel = new SortProofViewModel(sortSource, ReactiveSchedulers.For(sortContext));
using var sortLease = RunicModelContextRegistry.Shared.Bind(sortContext, sortModel);
await sortContext.InvokeAsync(() => { });
var sortTransport = new Benchmarks.MeteredTransport();
using var sortBridge = new SortProofBridge(sortTransport, sortModel, "sorted");
// The comparer changes in the model turn, in order with the changesets that ObserveOn delivers there.
// The comparer changes in the model turn, in order with the changesets that ObserveOn delivers there.
await ReplayAsync(sortTransport, "sorted", sortContext, async () =>
{
    await sortContext.InvokeAsync(() => sortModel.SortBy(SortProofViewModel.ByValueDescending));
    sortSource.Edit(updater =>
    {
        updater.AddOrUpdate(new Row(3, "Row moved", 9));
        updater.RemoveKey(10);
        updater.AddOrUpdate(new Row(40, "Row added", 2));
    });
    await sortContext.InvokeAsync(() => { });
    await sortContext.InvokeAsync(() => sortModel.SortBy(SortProofViewModel.ByLabel));
    await sortContext.InvokeAsync(() => sortModel.SortBy(SortProofViewModel.ByRank));
    // A refresh after a rank change moves the row, forward and back.
    foreach (var (id, rank) in new[] { (5, 100), (30, -1), (12, 50) })
    {
        SortProofViewModel.Ranks[id] = rank;
        sortSource.Refresh(sortSource.Lookup(id).Value);
        await sortContext.InvokeAsync(() => { });
    }
    await sortContext.InvokeAsync(() => sortModel.SortBy(SortProofViewModel.ById));
}, minimumFrames: 3, requiredKinds: ["move", "set", "add", "remove"]);
Console.WriteLine("DYNAMICDATA_RUNIC_OK: independent viewports, deferred batching, off-screen suppression, generated collection deltas, withheld invalid keys and frames that replay to the snapshot.");

static async Task ExecuteAsync(IObservable<ReactiveUI.Primitives.RxVoid> execution)
{
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var subscription = execution.Subscribe(Witness.Create<ReactiveUI.Primitives.RxVoid>(
        _ => done.TrySetResult(), error => done.TrySetException(error), () => done.TrySetResult()));
    await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
}

// Reads a snapshot, runs the changes, reads the next snapshot and requires that the frames between them, applied to the
// first, give the second: frames only, at least minimumFrames, with each of requiredKinds among their changes.
static async Task ReplayAsync(Benchmarks.MeteredTransport transport, string route, RunicModelContext context, Func<Task> change,
    int minimumFrames, string[] requiredKinds)
{
    using var start = JsonDocument.Parse(await transport.RequestAsync(Snapshot(route)));
    var state = System.Text.Json.Nodes.JsonNode.Parse(start.RootElement.GetProperty("state").GetRawText())!.AsObject();
    var revision = start.RootElement.GetProperty("revision").GetInt64();
    await change();
    for (var turn = 0; turn < 3; turn++) await context.InvokeAsync(() => { });
    using var end = JsonDocument.Parse(await transport.RequestAsync(Snapshot(route)));
    var until = end.RootElement.GetProperty("revision").GetInt64();
    await transport.WaitForRevisionAsync(until);
    var replayed = FrameReplay.Apply(state, revision, transport.Frames.ToArray(), route, until);
    var expected = System.Text.Json.Nodes.JsonNode.Parse(end.RootElement.GetProperty("state").GetRawText());
    if (replayed.Revision != until || !System.Text.Json.Nodes.JsonNode.DeepEquals(replayed.State, expected))
        throw new InvalidOperationException($"The frames of {route} did not replay to its snapshot at {until} (reached {replayed.Revision}).\n" +
            $"replayed: {replayed.State.ToJsonString()}\nsnapshot: {expected!.ToJsonString()}");
    var missing = requiredKinds.Where(kind => !replayed.Kinds.ContainsKey(kind)).ToArray();
    if (replayed.Frames < minimumFrames || missing.Length > 0)
        throw new InvalidOperationException($"The {route} replay applied {replayed.Frames} frames with the changes " +
            $"{string.Join(", ", replayed.Kinds.Select(pair => $"{pair.Key}×{pair.Value}"))}; it needs {minimumFrames} frames and {string.Join(", ", missing)}.");
    Console.WriteLine($"{route}: {replayed.Frames} frames ({string.Join(", ", replayed.Kinds.Select(pair => $"{pair.Key}×{pair.Value}"))}) replay to the snapshot.");
}

static string Snapshot(string route) => $"{{\"kind\":\"snapshot\",\"route\":\"{route}\"}}";

static string? Kind(JsonElement push) => push.GetProperty("kind").GetString();

async Task NextFrameAsync(Func<JsonElement, bool> expected, string what)
{
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < deadline)
    {
        while (keyTransport.Frames.TryDequeue(out var json))
        {
            using var frame = JsonDocument.Parse(json);
            if (expected(frame.RootElement)) return;
            throw new InvalidOperationException($"Unexpected frame after {what}: {json}");
        }
        await Task.Delay(5);
    }
    throw new InvalidOperationException($"No frame after {what}.");
}
