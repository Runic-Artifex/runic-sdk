using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class CollectionDeltaTests
{
    [Test]
    public async Task CollectionChangesPublishOrderedFramesAsync()
    {
        var model = new CollectionDeltaViewModel();
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new CollectionDeltaBridge(transport, model, "rows");
        using var baseline = transport.SnapshotState("rows");
        Require(baseline.RootElement.GetProperty("state").GetProperty("rows").GetArrayLength() == 0, "Initial collection snapshot is missing.");
        var baseRevision = baseline.RootElement.GetProperty("revision").GetInt64();
        using (BridgeSnapshotBatch.Begin(model))
            for (var id = 0; id < 100; id++) model.Items.Add(new(id, $"row {id}"));
        using var added = await NextAsync(transport, "rows");
        Require(Kind(added) == "frame", "A collection changeset captured a full state.");
        Require(added.RootElement.GetProperty("baseRevision").GetInt64() == baseRevision
            && added.RootElement.GetProperty("revision").GetInt64() > baseRevision,
            $"A batch lost its revision baseline: {added.RootElement.GetRawText()}");
        Require(Changes(added).Length == 100, "A batch dropped collection edits.");
        await Task.Delay(50);
        Require(transport.TakePushes("rows").Count == 0, "A generated collection published a changeset twice.");

        using (BridgeSnapshotBatch.Begin(model))
        {
            model.Items[2] = new(2, "replaced");
            model.Items.Move(2, 1);
            model.Items.RemoveAt(0);
        }
        using var changed = await NextAsync(transport, "rows");
        var changes = Changes(changed);
        Require(changes.Select(change => change.GetProperty("kind").GetString()).SequenceEqual(["replace", "move", "remove"]),
            $"Indexed edits were reordered: {changed.RootElement.GetRawText()}");
        Require(changed.RootElement.GetProperty("baseRevision").GetInt64() == added.RootElement.GetProperty("revision").GetInt64(),
            "The next frame did not continue the previous frame.");
        Require(changes.All(change => change.GetProperty("field").GetString() == "rows"), "A collection-only edit published unrelated state.");

        // Protocol 4: state and collection changes of one unit are one frame, a set of the member beside the add.
        using (BridgeSnapshotBatch.Begin(model)) { model.Items.Add(new(100, "new")); model.Title = "mixed"; }
        using var mixed = await NextAsync(transport, "rows");
        var mixedChanges = Changes(mixed);
        Require(Kind(mixed) == "frame"
            && mixedChanges.Any(change => change.GetProperty("field").GetString() == "title" && change.GetProperty("kind").GetString() == "set"
                && change.GetProperty("value").GetString() == "mixed")
            && mixedChanges.Any(change => change.GetProperty("field").GetString() == "rows" && change.GetProperty("kind").GetString() == "add"),
            $"Mixed state and collection changes were not published as one frame: {mixed.RootElement.GetRawText()}");
        // A reset is a set of the whole collection.
        model.Items.Clear();
        using var reset = await NextAsync(transport, "rows");
        Require(SetOf(reset, "rows") is { } cleared && cleared.GetArrayLength() == 0, $"A reset did not set the collection: {reset.RootElement.GetRawText()}");

        var mutable = new MutableCollectionViewModel();
        using var mutableTransport = new InMemoryBridgeTransport();
        using var mutableBridge = new MutableCollectionBridge(mutableTransport, mutable, "mutable");
        // A route publishes frames once a client read its state, the frames' first baseline.
        using (mutableTransport.SnapshotState("mutable")) { }
        mutable.Rows[0].Label = "updated";
        using var rowUpdate = await NextAsync(mutableTransport, "mutable");
        var rowChange = Changes(rowUpdate).Single(change => change.GetProperty("field").GetString() == "rows");
        Require(rowChange.GetProperty("kind").GetString() == "replace"
            && rowChange.GetProperty("index").GetInt32() == rowChange.GetProperty("oldIndex").GetInt32()
            && rowChange.GetProperty("items")[0].GetProperty("label").GetString() == "updated",
            "A mutable row update did not serialize its replacement.");
        mutable.Share();
        using var shared = await NextAsync(mutableTransport, "mutable");
        mutable.Rows[0].Label = "shared update";
        using var sharedUpdate = await NextAsync(mutableTransport, "mutable");
        Require(SetOf(sharedUpdate, "selected") is { } selected && selected.GetProperty("label").GetString() == "shared update" &&
            // The row is a replace, or a set of the collection when the row is also reachable through another member.
            Changes(sharedUpdate).Any(change => change.GetProperty("field").GetString() == "rows"
                && (change.TryGetProperty("items", out var rows) || change.TryGetProperty("value", out rows))
                && rows[0].GetProperty("label").GetString() == "shared update"),
            $"A shared row update left another DTO path stale: {sharedUpdate.RootElement.GetRawText()}");

        // More than 4,096 changes: the collection's indexed changes become one set.
        using (BridgeSnapshotBatch.Begin(model))
            for (var id = 0; id < 4100; id++) model.Items.Add(new(id, $"row {id}"));
        using var oversized = await NextAsync(transport, "rows");
        Require(SetOf(oversized, "rows") is { } all && all.GetArrayLength() == 4100,
            $"An oversized changeset did not fall back to a set of the collection: {oversized.RootElement.GetRawText()[..Math.Min(300, oversized.RootElement.GetRawText().Length)]}");

        await InvalidKeysAreWithheldAsync();
    }

    // specs/application/collection-deltas.md: the producer never publishes keys
    // the client rejects, and never throws into the code that changed them.
    private static async Task InvalidKeysAreWithheldAsync()
    {
        const string Rule = ". [RunicCollection] keys must be nonempty and unique within the collection.";
        // Development detail is on for every window of this case, so failed reads carry the key.
        var development = new WindowContentSessionOptions { IncludeFailureDetail = true };
        {
            // An explicit read fails with the bounded reason; development detail has the key.
            foreach (var (row, problem) in new[] { (new KeyedRow("", "empty"), "row 1 has an empty key"), (new KeyedRow(null!, "null"), "row 1 has a null key") })
            {
                var initial = new KeyedCollectionViewModel();
                initial.Items.Add(new("a", "one"));
                initial.Items.Add(row);
                using var initialTransport = new InMemoryBridgeTransport();
                using var initialSession = new WindowContentSession(initialTransport, development);
                using var initialBridge = new KeyedCollectionBridge(initialTransport, initial, "keyed", content: initialSession);
                RequireFailedRead(initialTransport, "keyed", "KeyedCollectionViewModel.rows: " + problem + Rule);
            }

            var model = new KeyedCollectionViewModel();
            model.Items.Add(new("a", "one"));
            using var transport = new InMemoryBridgeTransport();
            using var session = new WindowContentSession(transport, development);
            using var bridge = new KeyedCollectionBridge(transport, model, "keyed", content: session);
            using (transport.SnapshotState("keyed")) { }
            // Observers after the bridge, such as a ReactiveUI or DynamicData binding, still run.
            var later = 0;
            ((System.Collections.Specialized.INotifyCollectionChanged)model.Rows).CollectionChanged += (_, _) => later++;
            foreach (var (mutate, undo, problem, key) in new (Action, Action, string, string)[]
            {
                (() => model.Items.Add(new("", "empty")), () => model.Items.RemoveAt(1), "row 1 has an empty key", "row 1 has an empty key"),
                (() => model.Items.Add(null!), () => model.Items.RemoveAt(1), "row 1 is null", "row 1 is null"),
                (() => model.Items.Insert(0, new("a", "again")), () => model.Items.RemoveAt(0), "rows 0 and 1 have the same key", "rows 0 and 1 have the duplicate key 'a'"),
            })
            {
                var before = later;
                mutate();
                Require(later == before + 1, $"A later collection observer was skipped after {problem}.");
                using (var failure = await NextAsync(transport, "keyed"))
                {
                    var error = failure.RootElement.GetProperty("error");
                    Require(Kind(failure) == "failure" && error.GetProperty("kind").GetString() == "failed" &&
                        error.GetProperty("message").GetString() == "KeyedCollectionViewModel.rows: " + problem + Rule &&
                        error.GetProperty("detail").GetProperty("message").GetString() == "KeyedCollectionViewModel.rows: " + key + Rule,
                        $"The route did not report {problem}: {failure.RootElement.GetRawText()}");
                }
                RequireFailedRead(transport, "keyed", "KeyedCollectionViewModel.rows: " + key + Rule);
                // Once the keys are valid again, the next change publishes a frame that sets the collection.
                undo();
                using var recovered = await NextAsync(transport, "keyed");
                Require(SetOf(recovered, "rows") is { } rows && rows.GetArrayLength() == 1,
                    $"The route did not recover with a set of the collection after {problem}: {recovered.RootElement.GetRawText()}");
            }
            model.Items.Add(new("b", "two"));
            using (var frame = await NextAsync(transport, "keyed"))
                Require(Changes(frame).Single().GetProperty("kind").GetString() == "add", "Indexed changes did not resume after the keys were fixed.");

            // Rows shifting under the same duplicate is not a new failure.
            model.Items.Insert(0, new("a", "again"));
            using (var failure = await NextAsync(transport, "keyed"))
                Require(Kind(failure) == "failure", "The duplicate key was not reported.");
            model.Items.Insert(0, new("x", "shift"));
            model.Items.Insert(0, new("y", "shift"));
            await Task.Delay(200);
            Require(transport.TakePushes("keyed").Count == 0, "Shifted rows reported the same duplicate key again.");
            model.Items.RemoveAt(2);
            using (var recovered = await NextAsync(transport, "keyed"))
                Require(SetOf(recovered, "rows") is { } rows && rows.GetArrayLength() == 4, "The route did not recover after shifted rows.");

            // A command that ran says so when its effects cannot be published: no revision, the reason state-withheld.
            using (var reply = JsonDocument.Parse(transport.Command("keyed", "addDuplicate")))
            {
                var error = reply.RootElement.GetProperty("error");
                Require(!reply.RootElement.GetProperty("ok").GetBoolean() && !reply.RootElement.TryGetProperty("revision", out _)
                    && error.GetProperty("kind").GetString() == "failed" && error.GetProperty("reason").GetString() == "state-withheld"
                    && error.GetProperty("message").GetString()!.StartsWith("The call ran, but the updated state could not be published", StringComparison.Ordinal),
                    $"A command reply did not say that the command ran: {reply.RootElement.GetRawText()}");
            }
            Require(model.Items.Count == 5, "The command did not run.");
            transport.TakeMessages();

            // A key changed in place: the baseline follows it, so a later valid key is
            // not a false duplicate and a real duplicate is not missed.
            var mutable = new MutableKeyedViewModel();
            using var mutableTransport = new InMemoryBridgeTransport();
            using var mutableSession = new WindowContentSession(mutableTransport, development);
            using var mutableBridge = new MutableKeyedBridge(mutableTransport, mutable, "mutableKeyed", content: mutableSession);
            using (mutableTransport.SnapshotState("mutableKeyed")) { }
            mutable.Items[1].Key = "c";
            using (var changed = await NextAsync(mutableTransport, "mutableKeyed"))
                Require(SetOf(changed, "rows") is { } rows && rows[1].GetProperty("key").GetString() == "c",
                    $"A key changed in place did not set the collection: {changed.RootElement.GetRawText()}");
            mutable.Items.Add(new("b", "three"));
            using (var added = await NextAsync(mutableTransport, "mutableKeyed"))
                Require(Changes(added).Single().GetProperty("kind").GetString() == "add", "A key freed in place was reported as a duplicate.");
            mutable.Items[2].Key = "a";
            using (var duplicate = await NextAsync(mutableTransport, "mutableKeyed"))
                Require(Kind(duplicate) == "failure", "A duplicate key made in place was published.");
            mutable.Items[2].Key = "d";
            using (var fixedKey = await NextAsync(mutableTransport, "mutableKeyed"))
                Require(SetOf(fixedKey, "rows") is { } rows && rows.GetArrayLength() == 3, "A key fixed in place did not set the collection.");

            var validated = new ValidatedCollectionViewModel();
            using var validatedTransport = new InMemoryBridgeTransport();
            // Without a window, a Bridge's replies follow the environment, which is not Development here.
            using var validatedBridge = new ValidatedCollectionBridge(validatedTransport, validated, "validated");
            // The route publishes once a client read its state.
            using (validatedTransport.SnapshotState("validated")) { }
            validated.Items.Add(new("a", "one"));
            validated.Items.Add(new("a", "again"));
            var notices = 0;
            for (var waited = 0; waited < 2000 && notices == 0; waited += 10)
            {
                notices += validatedTransport.TakePushes("validated").Count(push => Kind(push) == "failure");
                await Task.Delay(10);
            }
            Require(notices == 1, "A validation model did not report its duplicate key.");
            RequireFailedRead(validatedTransport, "validated", detail: null);
        }
    }

    // An explicit read of a route with invalid keys fails with the reason state-withheld.
    private static void RequireFailedRead(InMemoryBridgeTransport transport, string route, string? detail)
    {
        using var reply = JsonDocument.Parse(transport.Snapshot(route));
        var root = reply.RootElement;
        var error = root.GetProperty("error");
        Require(!root.GetProperty("ok").GetBoolean() && !root.TryGetProperty("state", out _)
            && error.GetProperty("kind").GetString() == "failed" && error.GetProperty("reason").GetString() == "state-withheld" &&
            (detail is null
                ? !error.TryGetProperty("detail", out _)
                : error.GetProperty("detail").GetProperty("message").GetString() == detail),
            $"{route} did not fail with \"{detail}\": {root.GetRawText()}");
    }

    private static async Task<JsonDocument> NextAsync(InMemoryBridgeTransport transport, string route)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeout)
        {
            var pushes = transport.TakePushes(route);
            if (pushes.Count > 0)
            {
                Require(pushes.Count == 1, $"A changeset emitted more than one push: {string.Join(", ", pushes.Select(push => push.RootElement.GetRawText()))}");
                return pushes[0];
            }
            await Task.Delay(5);
        }
        throw new InvalidOperationException("The bridge did not publish a collection frame.");
    }

    private static string? Kind(JsonDocument push) => push.RootElement.GetProperty("kind").GetString();

    private static JsonElement[] Changes(JsonDocument frame)
    {
        Require(Kind(frame) == "frame", $"Expected a frame: {frame.RootElement.GetRawText()}");
        return [.. frame.RootElement.GetProperty("changes").EnumerateArray()];
    }

    // The value of a frame's set of a field, or null when the frame has none.
    private static JsonElement? SetOf(JsonDocument frame, string field) =>
        Changes(frame).Where(change => change.GetProperty("field").GetString() == field && change.GetProperty("kind").GetString() == "set")
            .Select(change => (JsonElement?)change.GetProperty("value")).FirstOrDefault();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
