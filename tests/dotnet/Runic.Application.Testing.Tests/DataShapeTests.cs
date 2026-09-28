using System.Text.Json;
using Runic.Application.Testing;

namespace Runic.Application.Testing.Tests;

internal static class DataShapeTests
{
    internal static async Task RunAsync()
    {
        var model = new DataShapeViewModel();
        using var host = new RunicWindowTestHost<DataShapeViewModel>(model, "dataShape",
            (transport, content, vm) => new DataShapeBridge(transport, vm, content: content), new TestViewLocator());
        foreach (var malformed in new[] { "{}", "{\"requestId\":\"missing\"}", "{\"requestId\":\"overflow\",\"expectedVersion\":999999999999999999999999}" })
        {
            using var reply = JsonDocument.Parse(host.Transport.Call("dataShapeWriteExactId", new(StringValue: malformed)));
            Require(!reply.RootElement.GetProperty("ok").GetBoolean()
                && reply.RootElement.GetProperty("error").GetProperty("kind").GetString() == "rejected",
                "Malformed checked write escaped the normal rejected response.");
        }
        using var snapshot = host.Snapshot();
        var state = snapshot.RootElement.GetProperty("state");
        Require(state.GetProperty("exact-id").GetString() == "9007199254740993", "Root RunicAlias was not emitted as an exact Int64 string.");
        Require(state.GetProperty("amount").GetString() == "1234567890.123456789", "Exact decimal state was not emitted as a string.");
        Require(state.GetProperty("duration").GetString() == "00:00:12.3456789", "Invariant TimeSpan state was not emitted in constant format.");
        Require(state.GetProperty("optional").ValueKind == JsonValueKind.Null, "Nullable scalar state did not retain null.");
        Require(state.GetProperty("items")[0].GetProperty("source").GetString() == "base"
            && state.GetProperty("items")[0].GetProperty("retry-after").GetInt32() == 3
            && state.GetProperty("items")[0].GetProperty("__proto__").GetString() == "first",
            "Inherited DTO data or an aliased member was omitted.");
        Require(state.GetProperty("payload").GetProperty("$case").GetString() == "text", "Closed union discriminator was not emitted.");
        Require(state.GetProperty("money").GetString() == "12.50", "Custom codec state was not emitted.");
        Require(state.GetProperty("optionalItems")[0].ValueKind == JsonValueKind.Null
            && state.GetProperty("optionalItems")[1].GetProperty("__proto__").GetString() == "optional-item",
            "A nullable collection item did not preserve its declared element type.");
        Require(state.GetProperty("lookup").GetProperty("__proto__").GetProperty("__proto__").GetString() == "prototype-safe",
            "A dictionary key named __proto__ was not retained on the C# wire payload.");
        _ = host.Transport.Call("dataShapeSetOptional", new(StringValue: "4"));
        Require(model.Optional == 4, "Nullable scalar setter did not use generated codec decoding.");

        _ = host.Transport.DrainPublications();
        var item = model.Items[0];
        item.Name = "changed";
        Require(await PublishedAsync(host.Transport, json => json.Contains("changed", StringComparison.Ordinal)),
            "A nested observable DTO item change did not publish a new snapshot.");
        model.Items.Remove(item);
        Require(await PublishedAsync(host.Transport, json => { using var document = JsonDocument.Parse(json); return document.RootElement.GetProperty("items").GetArrayLength() == 0; }),
            "Removing the item did not publish the new collection.");
        using var beforeRemovedChange = host.Snapshot();
        item.Name = "removed";
        using var afterRemovedChange = host.Snapshot();
        Require(beforeRemovedChange.RootElement.GetProperty("state").GetProperty("revision").GetInt64()
            == afterRemovedChange.RootElement.GetProperty("state").GetProperty("revision").GetInt64(),
            "A removed observable DTO item remained subscribed.");

        _ = host.Transport.DrainPublications();
        var nestedItem = model.Groups[0].Items[0];
        nestedItem.Name = "nested-changed";
        Require(await PublishedAsync(host.Transport, json => json.Contains("nested-changed", StringComparison.Ordinal)),
            "A nested observable collection item change did not publish a new snapshot.");
        model.Groups.RemoveAt(0);
        Require(await PublishedAsync(host.Transport, json =>
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("groups").GetArrayLength() == 0;
        }), "Removing a nested observable collection owner did not publish the new snapshot.");
        using var beforeNestedRemovedChange = host.Snapshot();
        nestedItem.Name = "nested-removed";
        using var afterNestedRemovedChange = host.Snapshot();
        Require(beforeNestedRemovedChange.RootElement.GetProperty("state").GetProperty("revision").GetInt64()
            == afterNestedRemovedChange.RootElement.GetProperty("state").GetProperty("revision").GetInt64(),
            "A removed nested observable collection item remained subscribed.");
    }

    private static async Task<bool> PublishedAsync(InMemoryViewTransport transport, Func<string, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (transport.DrainPublications().Any(publication => publication.Route == "dataShape" && predicate(publication.StateJson))) return true;
            await Task.Delay(10);
        }
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
