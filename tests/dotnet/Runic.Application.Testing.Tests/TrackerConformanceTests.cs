using System.Text.Json.Nodes;
using Runic.Application.Testing;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Runs the shared collection delta fixtures through RunicStateTracker: it must reach
// the fixture state from the producer's frames, reject a frame after a lost one, and
// reject frames the generated client rejects. The fixtures (specs/application/fixtures/collection-deltas) are
// protocol 3; their frames are translated to the protocol-4 pushes state, frame and failure. The producer side of
// the same cases is in the conformance corpus (specs/application/conformance/scenarios/state-changes).
internal sealed class TrackerConformanceTests
{
    private const string Route = "collectionDelta";

    // One case per fixture case, in file order, named "<file>: <case>" as the former runner named them.
    public static IEnumerable<Func<(string Name, JsonObject TestCase)>> Cases()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "collection-deltas");
        foreach (var file in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
            foreach (var testCase in JsonNode.Parse(File.ReadAllText(file))!["cases"]!.AsArray())
            {
                var name = $"{Path.GetFileName(file)}: {testCase!["name"]}";
                var json = testCase.ToJsonString();
                yield return () => (name, JsonNode.Parse(json)!.AsObject());
            }
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    [DisplayName("$name")]
    public void TrackerReachesTheFixtureState(string name, JsonObject testCase)
    {
        if (testCase["error"] is not null && testCase["steps"]!.AsArray().Count == 0) return;
        var initial = Expand(testCase["initial"]!).AsObject();
        using var host = Host(initial);
        var tracker = host.Root.Track();
        var offset = tracker.Revision - initial["revision"]!.GetValue<long>();
        var dropped = testCase["dropped"]?.AsArray().Select(index => index!.GetValue<int>()).ToHashSet() ?? [];
        var frames = Expand(testCase["frames"]!).AsArray();
        for (var index = 0; index < frames.Count; index++)
        {
            if (dropped.Contains(index)) continue;
            var frame = frames[index]!.AsObject();
            foreach (var key in new[] { "revision", "baseRevision" })
                if (frame[key] is { } value) frame[key] = value.GetValue<long>() + offset;
            Push(host, Translate(frame));
            if (dropped.Count > 0 && index > dropped.Min() && frame["__runicDelta"] is not null)
            {
                Require(Throws(() => tracker.Apply(), "lost or reordered"), $"{name}: a delta after a lost frame was applied.");
                return;
            }
            tracker.Apply();
        }
        Require(dropped.Count == 0, $"{name}: the lost frame was not detected.");
        var expected = Expand(testCase["expected"] ?? testCase["initial"]!).AsObject();
        expected.Remove("revision");
        var actual = JsonNode.Parse(tracker.State.Json.GetRawText())!.AsObject();
        Require(JsonNode.DeepEquals(expected, actual), $"{name}: the tracker reached {actual.ToJsonString()[..Math.Min(400, actual.ToJsonString().Length)]}");
        var failures = frames.Count(frame => frame!["__runicFailure"] is not null);
        Require(tracker.Failures.Count == failures, $"{name}: the tracker recorded {tracker.Failures.Count} failure notices, not {failures}.");
    }

    [Test]
    public void RejectsWhatTheClientRejects()
    {
        var initial = new JsonObject { ["revision"] = 0, ["rows"] = new JsonArray(Row(1), Row(2)), ["title"] = "rows" };
        foreach (var (change, problem) in new (JsonObject Change, string Problem)[]
        {
            (Change("add", 5, -1, ["3"], Row(3)), "index 5, outside 0 to 2"),
            (Change("add", 2, -1, ["3"]), "0 items for 1 keys"),
            (Change("add", 2, -1, ["4"], Row(3)), "has key '3', but the change names '4'"),
            (Change("add", 2, -1, ["1"], Row(1)), "duplicate key '1'"),
            (Change("remove", 1, 1, ["1"]), "expects key '1' at row 1"),
            (Change("remove", 2, 2, ["2"]), "index 2, outside 0 to 1"),
            (Change("remove", 0, 0, []), "names no keys"),
            (Change("replace", 0, 1, ["1"], Row(1)), "oldIndex 1"),
            (Change("move", 3, 0, ["1"]), "destination 3"),
            (Change("insert", 0, 0, ["1"]), "'insert' is not a collection change"),
        })
        {
            using var host = Host(initial);
            var tracker = host.Root.Track();
            var frame = new JsonObject
            {
                ["kind"] = "frame", ["route"] = Route, ["baseRevision"] = tracker.Revision, ["revision"] = tracker.Revision + 1,
                ["changes"] = new JsonArray(change),
            };
            Push(host, frame);
            Require(Throws(() => tracker.Apply(), problem), $"The tracker accepted a frame with {problem}.");
        }
    }

    // Broadcasts a push on the host's transport, as the window would.
    private static void Push(RunicWindowTestHost<CollectionDeltaViewModel> host, JsonObject push) =>
        host.Transport.BroadcastAsync(System.Text.Encoding.UTF8.GetBytes(push.ToJsonString())).AsTask().GetAwaiter().GetResult();

    // A protocol-3 fixture frame as the protocol-4 push: a delta is a frame, a failure notice a failure, and anything
    // else a full state.
    private static JsonObject Translate(JsonObject frame)
    {
        var fixture = frame.DeepClone().AsObject();
        var push = new JsonObject();
        if (fixture.Remove("__runicDelta"))
        {
            push["kind"] = "frame";
            push["route"] = Route;
            foreach (var key in new[] { "baseRevision", "revision", "changes" }) push[key] = fixture[key]!.DeepClone();
        }
        else if (fixture.Remove("__runicFailure"))
        {
            push["kind"] = "failure";
            push["route"] = Route;
            foreach (var key in new[] { "revision", "error" }) push[key] = fixture[key]!.DeepClone();
        }
        else
        {
            push["kind"] = "state";
            push["route"] = Route;
            push["revision"] = fixture["revision"]!.DeepClone();
            fixture.Remove("revision");
            push["state"] = fixture.DeepClone();
        }
        return push;
    }

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

    private static RunicWindowTestHost<CollectionDeltaViewModel> Host(JsonObject initial)
    {
        var model = new CollectionDeltaViewModel();
        foreach (var row in initial["rows"]!.AsArray()) model.Items.Add(new(row!["id"]!.GetValue<int>(), row["label"]!.GetValue<string>()));
        return new(model, (transport, content, vm) => new CollectionDeltaBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = Route });
    }

    private static JsonObject Row(int id) => new() { ["id"] = id, ["label"] = $"row {id}" };

    private static JsonObject Change(string kind, int index, int oldIndex, string[] keys, params JsonObject[] items) => new()
    {
        ["field"] = "rows", ["kind"] = kind, ["index"] = index, ["oldIndex"] = oldIndex,
        ["keys"] = new JsonArray(keys.Select(key => (JsonNode)key).ToArray()),
        ["items"] = new JsonArray(items.Select(item => (JsonNode)item).ToArray()),
    };

    private static bool Throws(Action action, string message)
    {
        try { action(); return false; }
        catch (InvalidOperationException error) when (error.Message.Contains(message, StringComparison.Ordinal)) { return true; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
