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
        using var snapshot = host.Snapshot();
        var state = snapshot.RootElement.GetProperty("state");
        Require(state.GetProperty("exact-id").GetString() == "9007199254740993", "Root RunicAlias was not emitted as an exact Int64 string.");
        Require(state.GetProperty("amount").GetString() == "1234567890.123456789", "Exact decimal state was not emitted as a string.");
        Require(state.GetProperty("optional").ValueKind == JsonValueKind.Null, "Nullable scalar state did not retain null.");
        Require(state.GetProperty("items")[0].GetProperty("source").GetString() == "base", "Inherited DTO data was omitted.");
        Require(state.GetProperty("payload").GetProperty("$case").GetString() == "text", "Closed union discriminator was not emitted.");
        Require(state.GetProperty("money").GetString() == "12.50", "Custom codec state was not emitted.");
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
