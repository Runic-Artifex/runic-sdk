using System.Text.Json;
using DynamicData;
using DynamicDataExample;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Advanced;
using Runic.Application.Views;
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
using var initial = JsonDocument.Parse(transport.Call("rowsSnapshot"));
if (first.Rows.Count != 30 || second.Rows.Count != 30) throw new InvalidOperationException("Deferred Bind did not populate both viewports.");
var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using var execute = first.SetViewportCommand.Execute(new(500, 20)).Subscribe(Witness.Create<ReactiveUI.Primitives.RxVoid>(
    _ => completed.TrySetResult(), error => completed.TrySetException(error)));
await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
await firstContext.InvokeAsync(() => { });
if (first.Rows.Count != 20 || first.Rows[0].Id != 500 || second.Rows[0].Id != 0)
    throw new InvalidOperationException("A viewport request leaked into another presentation.");
using var moved = JsonDocument.Parse(transport.Call("rowsSnapshot"));
var before = moved.RootElement.GetProperty("state").GetProperty("revision").GetInt64();
store.Update(10);
await firstContext.InvokeAsync(() => { });
await secondContext.InvokeAsync(() => { });
using var outside = JsonDocument.Parse(transport.Call("rowsSnapshot"));
if (outside.RootElement.GetProperty("state").GetProperty("revision").GetInt64() != before)
    throw new InvalidOperationException("Off-screen updates crossed the presentation bridge.");
var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using var back = first.SetViewportCommand.Execute(new(0, 20)).Subscribe(Witness.Create<ReactiveUI.Primitives.RxVoid>(
    _ => returned.TrySetResult(), error => returned.TrySetException(error)));
await returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
await firstContext.InvokeAsync(() => { });
await firstContext.InvokeAsync(() => { });
using var final = JsonDocument.Parse(transport.Call("rowsSnapshot"));
var baseline = final.RootElement.GetProperty("state").GetProperty("revision").GetInt64();
// Delivery is asynchronous: drain the viewport move's frames before counting.
await transport.WaitForRevisionAsync(baseline);
transport.Frames.Clear();
store.Update(10);
await firstContext.InvokeAsync(() => { });
await firstContext.InvokeAsync(() => { });
using var updated = JsonDocument.Parse(transport.Call("rowsSnapshot"));
await transport.WaitForRevisionAsync(updated.RootElement.GetProperty("state").GetProperty("revision").GetInt64());
var frames = transport.Frames.ToArray();
if (frames.Length != 1) throw new InvalidOperationException($"One deferred changeset emitted {frames.Length} bridge frames.");
using var delta = JsonDocument.Parse(frames[0]);
if (delta.RootElement.GetProperty("__runicDelta").GetInt32() != 1 ||
    delta.RootElement.GetProperty("baseRevision").GetInt64() != baseline ||
    delta.RootElement.GetProperty("changes").GetArrayLength() != 10)
    throw new InvalidOperationException("Deferred application did not produce one incremental changeset frame.");
// An invalid key is withheld and reported without ending the DynamicData binding.
var keyTransport = new Benchmarks.MeteredTransport();
using var keyModel = new KeyProofViewModel();
using var keyBridge = new KeyProofBridge(keyTransport, keyModel, "keys");
using (JsonDocument.Parse(keyTransport.Call("keysSnapshot"))) { }
keyModel.Source.Add(new Row(1, "one", 0));
await NextFrameAsync(frame => !frame.TryGetProperty("__runicFailure", out _), "the first row");
keyModel.Source.Add(new Row(1, "again", 0));
await NextFrameAsync(frame => frame.TryGetProperty("__runicFailure", out _), "the duplicate key");
keyModel.Source.RemoveAt(1);
await NextFrameAsync(frame => frame.TryGetProperty("rows", out var rows) && rows.GetArrayLength() == 1, "the corrected keys");
keyModel.Source.Add(new Row(2, "two", 0));
await NextFrameAsync(frame => frame.TryGetProperty("__runicDelta", out _), "a row after the correction");
if (keyModel.Rows.Count != 2) throw new InvalidOperationException("The DynamicData binding stopped after an invalid key.");
Console.WriteLine("DYNAMICDATA_RUNIC_OK: independent viewports, deferred batching, off-screen suppression, generated collection deltas and withheld invalid keys.");

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
