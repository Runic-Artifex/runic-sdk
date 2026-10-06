using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// Drives the C# producer through the shared collection delta fixtures in
// specs/application/fixtures/collection-deltas and requires byte-identical
// frames. The views package applies the same fixtures as a consumer.
internal static class CollectionDeltaConformanceTests
{
    private const string Route = "rows";

    internal static async Task RunAsync()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "collection-deltas");
        var files = Directory.GetFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Require(files.Length >= 8, $"The collection delta fixtures were not copied to {directory}.");
        foreach (var file in files)
        {
            var fixture = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            foreach (var testCase in fixture["cases"]!.AsArray())
                await RunCaseAsync($"{Path.GetFileName(file)}: {testCase!["name"]}", testCase.AsObject());
        }
    }

    private static async Task RunCaseAsync(string name, JsonObject testCase)
    {
        var model = new CollectionDeltaViewModel();
        var initial = Expand(testCase["initial"]!);
        foreach (var row in initial["rows"]!.AsArray()) model.Items.Add(Row(row!));
        var held = testCase["delivery"]?.GetValue<string>() == "held";
        using var transport = new FixtureTransport(held);
        using var bridge = new CollectionDeltaBridge(transport, model, Route);
        if (testCase["error"]?.GetValue<string>() is { } error)
        {
            await RunInvalidKeyCaseAsync(name, testCase, model, transport, error);
            return;
        }
        RequireJson(name, "initial snapshot", initial, State(transport));

        var actions = new List<Action>();
        foreach (var step in testCase["steps"]!.AsArray())
        {
            if (step!["batch"] is JsonArray batch)
                actions.Add(() => { using (BridgeSnapshotBatch.Begin(model)) foreach (var mutation in batch) Apply(model, mutation!); });
            else
                foreach (var mutation in step["each"]!.AsArray())
                    foreach (var action in Split(model, mutation!)) actions.Add(action);
        }
        for (var index = 0; index < actions.Count; index++)
        {
            actions[index]();
            // A held host has the first frame in flight before later frames queue.
            if (held && index == 0) await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        transport.Release.Set();

        var expectedFrames = testCase["frames"]!.AsArray().Select(frame => Expand(frame!)).ToArray();
        var frames = await transport.WaitForAsync(expectedFrames.Length);
        Require(frames.Count == expectedFrames.Length,
            $"{name}: expected {expectedFrames.Length} frames, observed {frames.Count}:\n{string.Join("\n", frames.Select(Abbreviate))}");
        for (var index = 0; index < frames.Count; index++)
            RequireJson(name, $"frame {index}", expectedFrames[index], frames[index]);
        RequireJson(name, "final snapshot", Expand(testCase["expected"]!), State(transport));
    }

    // The producer refuses the initial state, or the last mutation, and publishes nothing.
    private static async Task RunInvalidKeyCaseAsync(string name, JsonObject testCase, CollectionDeltaViewModel model,
        FixtureTransport transport, string error)
    {
        var mutations = testCase["steps"]!.AsArray().SelectMany(step => step!["each"]!.AsArray()).ToArray();
        if (mutations.Length != 0)
        {
            RequireJson(name, "initial snapshot", Expand(testCase["initial"]!), State(transport));
            foreach (var mutation in mutations[..^1]) Apply(model, mutation!);
        }
        try
        {
            if (mutations.Length == 0) State(transport);
            else Apply(model, mutations[^1]!);
        }
        catch (InvalidOperationException exception)
        {
            Require(exception.Message == error, $"{name}: the producer failed with another message.\nexpected: {error}\nactual:   {exception.Message}");
            var frames = await transport.WaitForAsync(0);
            Require(frames.Count == 0, $"{name}: the producer published {frames.Count} frames for invalid keys.");
            return;
        }
        throw new InvalidOperationException($"{name}: the producer accepted invalid keys.");
    }

    private static string State(FixtureTransport transport)
    {
        using var reply = JsonDocument.Parse(transport.Inner.Call($"{Route}Snapshot"));
        return reply.RootElement.GetProperty("state").GetRawText();
    }

    private static void Apply(CollectionDeltaViewModel model, JsonNode mutation)
    {
        foreach (var action in Split(model, mutation)) action();
    }

    // Each returned action raises exactly one collection notification.
    private static IEnumerable<Action> Split(CollectionDeltaViewModel model, JsonNode mutation)
    {
        var items = model.Items;
        switch (mutation["op"]!.GetValue<string>())
        {
            case "insert":
                yield return () => items.Insert(mutation["index"]!.GetValue<int>(), Row(mutation["item"]!));
                break;
            case "removeAt":
                yield return () => items.RemoveAt(mutation["index"]!.GetValue<int>());
                break;
            case "set":
                yield return () => items[mutation["index"]!.GetValue<int>()] = Row(mutation["item"]!);
                break;
            case "move":
                yield return () => items.Move(mutation["oldIndex"]!.GetValue<int>(), mutation["newIndex"]!.GetValue<int>());
                break;
            case "appendRows":
                var start = mutation["start"]!.GetValue<int>();
                var width = mutation["width"]?.GetValue<int>() ?? 0;
                for (var id = start; id < start + mutation["count"]!.GetValue<int>(); id++)
                {
                    var row = GeneratedRow(id, width);
                    yield return () => items.Add(row);
                }
                break;
            default:
                throw new InvalidOperationException($"Unknown fixture mutation {mutation.ToJsonString()}.");
        }
    }

    private static CollectionRow Row(JsonNode row) => new(row["id"]!.GetValue<int>(), row["label"]!.GetValue<string>());

    private static CollectionRow GeneratedRow(int id, int width) => new(id, $"row {id}".PadRight(width, '.'));

    // Expands the fixture generators documented in specs/application/collection-deltas.md.
    private static JsonNode Expand(JsonNode node)
    {
        switch (node)
        {
            case JsonArray array:
                var result = new JsonArray();
                foreach (var item in array)
                {
                    if (item is JsonObject { Count: 1 } generator && generator["$rows"] is JsonArray rows)
                        foreach (var (id, width) in Range(rows)) result.Add(RowNode(id, width));
                    else if (item is JsonObject { Count: 1 } adds && adds["$adds"] is JsonArray range)
                        foreach (var (id, width) in Range(range))
                            result.Add(new JsonObject
                            {
                                ["field"] = "rows", ["kind"] = "add", ["index"] = id, ["oldIndex"] = -1,
                                ["keys"] = new JsonArray(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                                ["items"] = new JsonArray(RowNode(id, width)),
                            });
                    else result.Add(item is null ? null : Expand(item));
                }
                return result;
            case JsonObject value:
                var copy = new JsonObject();
                foreach (var (key, child) in value) copy[key] = child is null ? null : Expand(child);
                return copy;
            default:
                return node.DeepClone();
        }
    }

    private static IEnumerable<(int Id, int Width)> Range(JsonArray range)
    {
        var start = range[0]!.GetValue<int>();
        var width = range.Count > 2 ? range[2]!.GetValue<int>() : 0;
        return Enumerable.Range(start, range[1]!.GetValue<int>()).Select(id => (id, width));
    }

    private static JsonObject RowNode(int id, int width) => new() { ["id"] = id, ["label"] = $"row {id}".PadRight(width, '.') };

    private static void RequireJson(string name, string what, JsonNode expected, string actual)
    {
        var encoded = expected.ToJsonString();
        Require(string.Equals(encoded, actual, StringComparison.Ordinal),
            $"{name}: {what} differs from the fixture.\nexpected: {Abbreviate(encoded)}\nactual:   {Abbreviate(actual)}");
    }

    private static string Abbreviate(string json) => json.Length <= 2048 ? json : $"{json[..2048]}... ({json.Length} chars)";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixtureTransport(bool held) : IBridgeTransport, IDisposable
    {
        private readonly object _gate = new();
        private readonly List<string> _frames = [];
        public InMemoryViewTransport Inner { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(!held);
        public IDisposable Bind(string name, Func<IBridgeArguments, string> handler) => Inner.Bind(name, handler);
        public IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler) =>
            Inner.BindAsync(name, handler);
        public void Publish(string name, string stateJson)
        {
            Entered.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(10));
            lock (_gate) _frames.Add(stateJson);
        }

        public async Task<IReadOnlyList<string>> WaitForAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate) if (_frames.Count >= count) break;
                await Task.Delay(5);
            }
            // Give an unexpected trailing frame a chance to show up.
            await Task.Delay(50);
            lock (_gate) return _frames.ToArray();
        }

        public void Dispose()
        {
            Release.Set();
            Inner.Dispose();
        }
    }
}
