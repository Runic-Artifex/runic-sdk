using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

internal static class CollectionDeltaTests
{
    internal static async Task RunAsync()
    {
        var model = new CollectionDeltaViewModel();
        using var transport = new InMemoryViewTransport();
        using var bridge = new CollectionDeltaBridge(transport, model, "rows");
        using var baseline = JsonDocument.Parse(transport.Call("rowsSnapshot"));
        Require(baseline.RootElement.GetProperty("state").GetProperty("rows").GetArrayLength() == 0, "Initial collection snapshot is missing.");
        using (BridgeSnapshotBatch.Begin(model))
            for (var id = 0; id < 100; id++) model.Items.Add(new(id, $"row {id}"));
        using var added = await NextAsync(transport);
        Require(added.RootElement.GetProperty("__runicDelta").GetInt32() == 1, "A collection changeset captured a full snapshot.");
        Require(added.RootElement.GetProperty("baseRevision").GetInt64() == 0 && added.RootElement.GetProperty("revision").GetInt64() == 100,
            "A batch lost its revision baseline.");
        Require(added.RootElement.GetProperty("changes").GetArrayLength() == 100, "A batch dropped collection edits.");
        Require(transport.DrainPublications().Count == 0, "A generated collection had duplicate graph and legacy subscriptions.");

        using (BridgeSnapshotBatch.Begin(model))
        {
            model.Items[2] = new(2, "replaced");
            model.Items.Move(2, 1);
            model.Items.RemoveAt(0);
        }
        using var changed = await NextAsync(transport);
        var changes = changed.RootElement.GetProperty("changes").EnumerateArray().ToArray();
        Require(changes.Select(change => change.GetProperty("kind").GetString()).SequenceEqual(["replace", "move", "remove"]),
            "Indexed edits were reordered.");
        Require(changed.RootElement.GetProperty("baseRevision").GetInt64() == 100, "The next frame did not continue the previous frame.");
        Require(!changed.RootElement.TryGetProperty("title", out _), "A collection-only edit serialized unrelated state.");

        using (BridgeSnapshotBatch.Begin(model)) { model.Items.Add(new(100, "new")); model.Title = "mixed"; }
        using var mixed = await NextAsync(transport);
        Require(mixed.RootElement.GetProperty("title").GetString() == "mixed" && mixed.RootElement.GetProperty("rows").GetArrayLength() == 100,
            "Mixed state and collection changes did not publish an atomic full snapshot.");
        model.Items.Clear();
        using var reset = await NextAsync(transport);
        Require(reset.RootElement.GetProperty("rows").GetArrayLength() == 0, "A reset did not send its recovery snapshot.");

        var mutable = new MutableCollectionViewModel();
        using var mutableTransport = new InMemoryViewTransport();
        using var mutableBridge = new MutableCollectionBridge(mutableTransport, mutable, "mutable");
        mutable.Rows[0].Label = "updated";
        using var rowUpdate = await NextAsync(mutableTransport);
        Require(rowUpdate.RootElement.GetProperty("changes")[0].GetProperty("items")[0].GetProperty("label").GetString() == "updated",
            "A mutable row update did not serialize its replacement.");
        mutable.Share();
        using var shared = await NextAsync(mutableTransport);
        mutable.Rows[0].Label = "shared update";
        using var sharedUpdate = await NextAsync(mutableTransport);
        Require(sharedUpdate.RootElement.GetProperty("selected").GetProperty("label").GetString() == "shared update" &&
            sharedUpdate.RootElement.GetProperty("rows")[0].GetProperty("label").GetString() == "shared update",
            "A shared row update left another DTO path stale.");

        using (BridgeSnapshotBatch.Begin(model))
            for (var id = 0; id < 4100; id++) model.Items.Add(new(id, $"row {id}"));
        using var oversized = await NextAsync(transport);
        Require(oversized.RootElement.GetProperty("rows").GetArrayLength() == 4100,
            "An oversized changeset did not fall back to a full snapshot.");

        await InvalidKeysAreWithheldAsync();
    }

    // specs/application/collection-deltas.md: the producer never publishes keys
    // the client rejects, and never throws into the code that changed them.
    private static async Task InvalidKeysAreWithheldAsync()
    {
        const string Rule = ". [RunicCollection] keys must be nonempty and unique within the collection.";
        var detail = BridgeDiagnostics.IncludeFailureDetail;
        BridgeDiagnostics.IncludeFailureDetail = true;
        try
        {
            // An explicit read fails with the bounded reason; development detail has the key.
            foreach (var (row, problem) in new[] { (new KeyedRow("", "empty"), "row 1 has an empty key"), (new KeyedRow(null!, "null"), "row 1 has a null key") })
            {
                var initial = new KeyedCollectionViewModel();
                initial.Items.Add(new("a", "one"));
                initial.Items.Add(row);
                using var initialTransport = new InMemoryViewTransport();
                using var initialBridge = new KeyedCollectionBridge(initialTransport, initial, "keyed");
                RequireFailedRead(initialTransport, "keyedSnapshot", "KeyedCollectionViewModel.rows: " + problem + Rule);
            }

            var model = new KeyedCollectionViewModel();
            model.Items.Add(new("a", "one"));
            using var transport = new InMemoryViewTransport();
            using var bridge = new KeyedCollectionBridge(transport, model, "keyed");
            using (JsonDocument.Parse(transport.Call("keyedSnapshot"))) { }
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
                using (var failure = await NextAsync(transport))
                {
                    var error = failure.RootElement.GetProperty("error");
                    Require(failure.RootElement.GetProperty("__runicFailure").GetInt32() == 1 &&
                        error.GetProperty("message").GetString() == "KeyedCollectionViewModel.rows: " + problem + Rule &&
                        error.GetProperty("detail").GetProperty("message").GetString() == "KeyedCollectionViewModel.rows: " + key + Rule,
                        $"The route did not report {problem}: {failure.RootElement.GetRawText()}");
                }
                RequireFailedRead(transport, "keyedSnapshot", "KeyedCollectionViewModel.rows: " + key + Rule);
                // Once the keys are valid again, the next change publishes a full state.
                undo();
                using var recovered = await NextAsync(transport);
                Require(recovered.RootElement.GetProperty("rows").GetArrayLength() == 1 && !recovered.RootElement.TryGetProperty("__runicDelta", out _),
                    $"The route did not recover with a full state after {problem}.");
            }
            model.Items.Add(new("b", "two"));
            using (var frame = await NextAsync(transport))
                Require(frame.RootElement.GetProperty("__runicDelta").GetInt32() == 1, "Frames did not resume after the keys were fixed.");

            // Rows shifting under the same duplicate is not a new failure.
            model.Items.Insert(0, new("a", "again"));
            using (var failure = await NextAsync(transport))
                Require(failure.RootElement.TryGetProperty("__runicFailure", out _), "The duplicate key was not reported.");
            model.Items.Insert(0, new("x", "shift"));
            model.Items.Insert(0, new("y", "shift"));
            await Task.Delay(200);
            Require(transport.DrainPublications().Count == 0, "Shifted rows reported the same duplicate key again.");
            model.Items.RemoveAt(2);
            using (var recovered = await NextAsync(transport))
                Require(recovered.RootElement.GetProperty("rows").GetArrayLength() == 4, "The route did not recover after shifted rows.");

            // A command that ran says so when its state cannot be sent.
            using (var reply = JsonDocument.Parse(transport.Call("keyedAddDuplicate")))
                Require(!reply.RootElement.GetProperty("ok").GetBoolean() && reply.RootElement.GetProperty("error").GetProperty("message").GetString()!
                    .StartsWith("The call ran, but the updated state could not be sent: KeyedCollectionViewModel.rows: ", StringComparison.Ordinal),
                    $"A command reply did not say that the command ran: {reply.RootElement.GetRawText()}");
            Require(model.Items.Count == 5, "The command did not run.");
            transport.DrainPublications();

            // A key changed in place: the baseline follows it, so a later valid key is
            // not a false duplicate and a real duplicate is not missed.
            var mutable = new MutableKeyedViewModel();
            using var mutableTransport = new InMemoryViewTransport();
            using var mutableBridge = new MutableKeyedBridge(mutableTransport, mutable, "mutableKeyed");
            using (JsonDocument.Parse(mutableTransport.Call("mutableKeyedSnapshot"))) { }
            mutable.Items[1].Key = "c";
            using (var changed = await NextAsync(mutableTransport))
                Require(!changed.RootElement.TryGetProperty("__runicDelta", out _) && changed.RootElement.GetProperty("rows")[1].GetProperty("key").GetString() == "c",
                    "A key changed in place did not publish a full state.");
            mutable.Items.Add(new("b", "three"));
            using (var added = await NextAsync(mutableTransport))
                Require(added.RootElement.GetProperty("__runicDelta").GetInt32() == 1, "A key freed in place was reported as a duplicate.");
            mutable.Items[2].Key = "a";
            using (var duplicate = await NextAsync(mutableTransport))
                Require(duplicate.RootElement.TryGetProperty("__runicFailure", out _), "A duplicate key made in place was published.");
            mutable.Items[2].Key = "d";
            using (var fixedKey = await NextAsync(mutableTransport))
                Require(fixedKey.RootElement.GetProperty("rows").GetArrayLength() == 3, "A key fixed in place did not publish a full state.");

            var validated = new ValidatedCollectionViewModel();
            using var validatedTransport = new InMemoryViewTransport();
            using var validatedBridge = new ValidatedCollectionBridge(validatedTransport, validated, "validated");
            validated.Items.Add(new("a", "one"));
            validated.Items.Add(new("a", "again"));
            RequireFailedRead(validatedTransport, "validatedSnapshot", "ValidatedCollectionViewModel.rows: rows 0 and 1 have the duplicate key 'a'" + Rule);
            var notices = 0;
            for (var waited = 0; waited < 2000 && notices == 0; waited += 10)
            {
                notices += validatedTransport.DrainPublications().Count(publication => publication.StateJson.Contains("__runicFailure", StringComparison.Ordinal));
                await Task.Delay(10);
            }
            Require(notices == 1, "A validation model did not report its duplicate key.");
        }
        finally { BridgeDiagnostics.IncludeFailureDetail = detail; }
    }

    private static void RequireFailedRead(InMemoryViewTransport transport, string route, string detail)
    {
        using var reply = JsonDocument.Parse(transport.Call(route));
        var root = reply.RootElement;
        Require(!root.GetProperty("ok").GetBoolean() && root.GetProperty("state").ValueKind == JsonValueKind.Null &&
            root.GetProperty("error").GetProperty("detail").GetProperty("message").GetString() == detail,
            $"{route} did not fail with \"{detail}\": {root.GetRawText()}");
    }

    private static async Task<JsonDocument> NextAsync(InMemoryViewTransport transport)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeout)
        {
            var publications = transport.DrainPublications();
            if (publications.Count > 0)
            {
                Require(publications.Count == 1, "A changeset emitted more than one frame.");
                return JsonDocument.Parse(publications[0].StateJson);
            }
            await Task.Delay(5);
        }
        throw new InvalidOperationException("The bridge did not publish a collection frame.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
