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
