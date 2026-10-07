using System.Text.Json.Nodes;
using Runic.Application.Testing;

namespace Runic.Application.Testing.Tests;

// Runs the shared collection delta fixtures through RunicStateTracker: it must reach
// the fixture state from the producer's frames, reject a frame after a lost one, and
// reject frames the generated client rejects.
internal static class TrackerConformanceTests
{
    private const string Route = "collectionDelta";

    internal static void Run()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "collection-deltas");
        foreach (var file in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
            foreach (var testCase in JsonNode.Parse(File.ReadAllText(file))!["cases"]!.AsArray())
                RunCase($"{Path.GetFileName(file)}: {testCase!["name"]}", testCase.AsObject());
        RejectsWhatTheClientRejects();
    }

    private static void RunCase(string name, JsonObject testCase)
    {
        if (testCase["error"] is not null && testCase["steps"]!.AsArray().Count == 0) return;
        var initial = CollectionDeltaConformanceTests.Expand(testCase["initial"]!).AsObject();
        using var host = Host(initial);
        var tracker = host.Root.Track();
        var offset = tracker.Revision - initial["revision"]!.GetValue<long>();
        var dropped = testCase["dropped"]?.AsArray().Select(index => index!.GetValue<int>()).ToHashSet() ?? [];
        var frames = CollectionDeltaConformanceTests.Expand(testCase["frames"]!).AsArray();
        for (var index = 0; index < frames.Count; index++)
        {
            if (dropped.Contains(index)) continue;
            var frame = frames[index]!.AsObject();
            foreach (var key in new[] { "revision", "baseRevision" })
                if (frame[key] is { } value) frame[key] = value.GetValue<long>() + offset;
            host.Transport.Publish(Route, frame.ToJsonString());
            if (dropped.Count > 0 && index > dropped.Min() && frame["__runicDelta"] is not null)
            {
                Require(Throws(() => tracker.Apply(), "lost or reordered"), $"{name}: a delta after a lost frame was applied.");
                return;
            }
            tracker.Apply();
        }
        Require(dropped.Count == 0, $"{name}: the lost frame was not detected.");
        var expected = CollectionDeltaConformanceTests.Expand(testCase["expected"] ?? testCase["initial"]!).AsObject();
        expected.Remove("revision");
        var actual = JsonNode.Parse(tracker.State.Json.GetRawText())!.AsObject();
        actual.Remove("revision");
        actual.Remove("__runicFields");
        Require(JsonNode.DeepEquals(expected, actual), $"{name}: the tracker reached {actual.ToJsonString()[..Math.Min(400, actual.ToJsonString().Length)]}");
        var failures = frames.Count(frame => frame!["__runicFailure"] is not null);
        Require(tracker.Failures.Count == failures, $"{name}: the tracker recorded {tracker.Failures.Count} failure notices, not {failures}.");
    }

    private static void RejectsWhatTheClientRejects()
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
                ["__runicDelta"] = 1, ["baseRevision"] = tracker.Revision, ["revision"] = tracker.Revision + 1,
                ["changes"] = new JsonArray(change),
            };
            host.Transport.Publish(Route, frame.ToJsonString());
            Require(Throws(() => tracker.Apply(), problem), $"The tracker accepted a frame with {problem}.");
        }
    }

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
