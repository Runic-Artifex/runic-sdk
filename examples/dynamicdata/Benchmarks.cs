using System.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReactiveUI.Primitives.Concurrency;
using Runic.Application.Views;
using Runic.Application.Views.Generated;
using Runic.Application.Views.Generated.Bridges;

namespace DynamicDataExample;

internal static class Benchmarks
{
    internal static async Task RunAsync()
    {
        var results = new List<BenchmarkResult>();
        foreach (var edits in new[] { 1, 10, 100 })
        foreach (var mode in new[] { "platform-binding", "full-snapshots", "batched-snapshot", "incremental", "platform-viewport", "viewport" })
        {
            using var store = new RowStore(10000);
            using var model = new RowsViewModel(store, ImmediateSequencer.Instance, mode is "viewport" or "platform-viewport" ? 100 : 10000, mode != "full-snapshots");
            var transport = new MeteredTransport();
            using var bridge = mode is "platform-binding" or "platform-viewport" ? null
                : mode is "full-snapshots" or "batched-snapshot" ? new FullBridge(transport, model)
                : (IDisposable)new RowsBridge(transport, model, "rows");
            for (var warmup = 0; warmup < 50; warmup++) store.Update(edits);
            if (bridge is not null)
            {
                using var warm = JsonDocument.Parse(await transport.RequestAsync("{\"kind\":\"snapshot\",\"route\":\"rows\"}"));
                await transport.WaitForRevisionAsync(warm.RootElement.GetProperty("revision").GetInt64());
            }
            transport.Frames.Clear();
            Row.MeasureSerialization = true;
            Row.ResetSerializedRows();
            var times = new List<double>();
            long allocated = 0;
            for (var run = 0; run < 20; run++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                store.Update(edits);
                times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            }
            var encodedRows = Row.ResetSerializedRows();
            Row.MeasureSerialization = false;
            if (bridge is not null)
            {
                using var snapshot = JsonDocument.Parse(await transport.RequestAsync("{\"kind\":\"snapshot\",\"route\":\"rows\"}"));
                await transport.WaitForRevisionAsync(snapshot.RootElement.GetProperty("revision").GetInt64());
            }
            var frames = transport.Frames.ToArray();
            var rowsSerialized = frames.Sum(frame =>
            {
                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement;
                // A state carries every row; a frame the rows of its changes, or all of them in a set of the field.
                return root.GetProperty("kind").GetString() switch
                {
                    "state" => root.GetProperty("state").GetProperty("rows").GetArrayLength(),
                    "failure" => 0,
                    _ => root.GetProperty("changes").EnumerateArray().Sum(change => change.GetProperty("kind").GetString() == "set"
                        ? change.GetProperty("value") is { ValueKind: JsonValueKind.Array } value ? value.GetArrayLength() : 0
                        : change.GetProperty("items").GetArrayLength()),
                };
            });
            times.Sort();
            results.Add(new(mode, 10000, model.Rows.Count, edits,
                times[times.Count / 2], times[(int)(times.Count * .95) - 1],
                allocated / times.Count, frames.Length,
                frames.Sum(frame => System.Text.Encoding.UTF8.GetByteCount(frame)), rowsSerialized,
                encodedRows / times.Count));
        }
        Console.WriteLine(JsonSerializer.Serialize(new BenchmarkReport(
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            "DynamicData application and synchronous bridge encoding; excludes native rendering and browser hydration. Platform binding is the shared collection boundary used by Avalonia and MAUI, not an end-to-end UI measurement.",
            20, results), BenchmarkJsonContext.Default.BenchmarkReport));
    }

    private sealed class FullBridge(MeteredTransport transport, RowsViewModel model)
        : ViewModelBridge<RowsViewModel>(transport, model, "rows", [new BridgeStateMember<RowsViewModel>("rows", WriteRows)],
            [new("Rows", "rows", current => current.Rows, null)], [], [])
    {
        private static void WriteRows(Utf8JsonWriter writer, RowsViewModel current)
        {
            writer.WriteStartArray();
            foreach (var row in current.Rows)
            {
                writer.WriteStartObject(); writer.WriteNumber("id", row.Id); writer.WriteString("label", row.Label); writer.WriteNumber("value", row.Value); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
    }

    // The window's transport without a page: it records the state, frame and failure pushes and sends requests to the
    // window's request handler on one connection, after its hello.
    internal sealed class MeteredTransport : IBridgeTransport, IDisposable
    {
        private IBridgeRequestHandler? _handler;
        private bool _greeted;
        internal ConcurrentQueue<string> Frames { get; } = new();
        private long _latestRevision;
        internal long LatestRevision => Interlocked.Read(ref _latestRevision);
        public IDisposable Serve(IBridgeRequestHandler handler)
        {
            _handler = handler;
            return this;
        }
        public ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        {
            var json = Encoding.UTF8.GetString(message.Span);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("kind").GetString() is "state" or "frame" or "failure")
            {
                Frames.Enqueue(json);
                Interlocked.Exchange(ref _latestRevision, root.GetProperty("revision").GetInt64());
            }
            return ValueTask.CompletedTask;
        }
        internal async Task<string> RequestAsync(string json)
        {
            if (!_greeted)
            {
                _greeted = true;
                await SendAsync("{\"kind\":\"hello\",\"protocol\":4,\"session\":\"benchmarks\",\"client\":{\"name\":\"benchmarks\",\"version\":\"1.0.0\"}}");
            }
            return await SendAsync(json);
        }
        private async Task<string> SendAsync(string json)
        {
            var reply = await _handler!.HandleAsync(new BridgeRequest(Encoding.UTF8.GetBytes(json), "benchmarks", "benchmarks"), CancellationToken.None);
            var text = Encoding.UTF8.GetString(reply.Span);
            return text.StartsWith("{\"ok\":true", StringComparison.Ordinal) ? text : throw new InvalidOperationException($"{json} failed: {text}");
        }
        internal async Task WaitForRevisionAsync(long revision)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (LatestRevision < revision) await Task.Delay(1, timeout.Token);
        }
        public void Dispose() => _handler = null;
    }
}

internal sealed record BenchmarkResult(string Mode, int SourceRows, int PresentedRows, int Edits,
    double MedianApplyMs, double P95ApplyMs, long AllocatedBytesPerBatch, int DeliveredFrames,
    int DeliveredBytes, int RowsSerialized, long EncodedRowsPerBatch);
internal sealed record BenchmarkReport(string Runtime, string Os, string Scope, int Samples, List<BenchmarkResult> Results);
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BenchmarkReport))]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;
