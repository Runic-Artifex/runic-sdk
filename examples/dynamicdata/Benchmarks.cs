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
                using var warm = JsonDocument.Parse(transport.Call("rowsSnapshot"));
                await transport.WaitForRevisionAsync(warm.RootElement.GetProperty("state").GetProperty("revision").GetInt64());
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
                using var snapshot = JsonDocument.Parse(transport.Call("rowsSnapshot"));
                var revision = snapshot.RootElement.GetProperty("state").GetProperty("revision").GetInt64();
                await transport.WaitForRevisionAsync(revision);
            }
            var frames = transport.Frames.ToArray();
            var rowsSerialized = frames.Sum(frame =>
            {
                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement;
                return root.TryGetProperty("rows", out var rows) ? rows.GetArrayLength()
                    : root.GetProperty("changes").EnumerateArray().Sum(change => change.GetProperty("items").GetArrayLength());
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
        : ViewModelBridge<RowsViewModel>(transport, model, "rows", Write,
            [new("Rows", current => current.Rows, null)], [], [])
    {
        private static void Write(Utf8JsonWriter writer, RowsViewModel current, long revision, Action<Utf8JsonWriter> writeFieldMetadata)
        {
            writer.WriteStartObject(); writer.WriteNumber("revision", revision); writer.WriteStartArray("rows");
            foreach (var row in current.Rows)
            {
                writer.WriteStartObject(); writer.WriteNumber("id", row.Id); writer.WriteString("label", row.Label); writer.WriteNumber("value", row.Value); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
    }

    // Until the producer speaks protocol 4 (P4), the benchmark reads the bridge's protocol-3 states and calls its
    // routes through the window dispatcher's legacy routes.
    internal sealed class MeteredTransport : IBridgeTransport, IDisposable
    {
        private BridgeDispatcher? _dispatcher;
        internal ConcurrentQueue<string> Frames { get; } = new();
        private long _latestRevision;
        internal long LatestRevision => Interlocked.Read(ref _latestRevision);
        public IDisposable Serve(IBridgeRequestHandler handler)
        {
            _dispatcher = (BridgeDispatcher)handler;
            _dispatcher.Legacy.SetPublisher((name, state, _) =>
            {
                Publish(Encoding.UTF8.GetString(state.Span));
                return ValueTask.CompletedTask;
            });
            return this;
        }
        public ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        private void Publish(string stateJson)
        {
            Frames.Enqueue(stateJson);
            using var document = JsonDocument.Parse(stateJson);
            Interlocked.Exchange(ref _latestRevision, document.RootElement.GetProperty("revision").GetInt64());
        }
        internal string Call(string name) =>
            _dispatcher!.Legacy.TryFind(name, out var sync, out _) && sync is not null ? sync(new Arguments()) : throw new KeyNotFoundException(name);
        internal async Task WaitForRevisionAsync(long revision)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (LatestRevision < revision) await Task.Delay(1, timeout.Token);
        }
        public void Dispose() => _dispatcher?.Legacy.SetPublisher(null);
        private sealed class Arguments : IBridgeArguments
        {
            public long GetInt64() => throw new InvalidOperationException();
            public bool GetBoolean() => throw new InvalidOperationException();
            public string GetString() => throw new InvalidOperationException();
        }
    }
}

internal sealed record BenchmarkResult(string Mode, int SourceRows, int PresentedRows, int Edits,
    double MedianApplyMs, double P95ApplyMs, long AllocatedBytesPerBatch, int DeliveredFrames,
    int DeliveredBytes, int RowsSerialized, long EncodedRowsPerBatch);
internal sealed record BenchmarkReport(string Runtime, string Os, string Scope, int Samples, List<BenchmarkResult> Results);
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BenchmarkReport))]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;
