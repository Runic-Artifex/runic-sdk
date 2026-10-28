using System.Text.Json;
using Runic.Application.Testing;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class DataShapeTests
{
    [Test]
    public async Task DataShapesReachTheWireAndPublishNestedChangesAsync()
    {
        var model = new DataShapeViewModel();
        using var host = new RunicWindowTestHost<DataShapeViewModel>(model,
            (transport, content, vm) => new DataShapeBridge(transport, vm, content: content), new RunicWindowTestHostOptions { RootRoute = "dataShape", ViewLocator = new TestViewLocator() });
        const string Write = "\"kind\":\"write\",\"route\":\"dataShape\",\"member\":\"exact-id\"";
        foreach (var malformed in new[] { "", ",\"requestId\":\"missing\"", ",\"requestId\":\"overflow\",\"expectedVersion\":999999999999999999999999" })
        {
            using var reply = JsonDocument.Parse(host.Request("{" + Write + malformed + "}"));
            Require(!reply.RootElement.GetProperty("ok").GetBoolean()
                && reply.RootElement.GetProperty("error").GetProperty("kind").GetString() == "rejected",
                "Malformed checked write escaped the normal rejected response.");
        }
        using var snapshot = host.RawSnapshot();
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
        // Read converts scalars only; a record or DTO property names the JSON alternative.
        var typed = host.Root.Snapshot();
        Require(typed.Read(vm => vm.ExactId) == 9007199254740993L, "Read did not convert an exact Int64.");
        foreach (var misuse in new Action[] { () => typed.Read(vm => vm.Whole), () => typed.Read(vm => vm.Payload) })
        {
            try
            {
                misuse();
                throw new InvalidOperationException("Read converted a non-scalar property.");
            }
            catch (NotSupportedException error)
            {
                Require(error.Message.Contains("DataShapeViewModel.", StringComparison.Ordinal)
                    && error.Message.Contains("Get(vm => vm.", StringComparison.Ordinal),
                    $"Read misuse did not name the property and the alternative: {error.Message}");
            }
        }
        Require(typed.Get(vm => vm.Whole).GetProperty("__proto__").GetString() == "whole", "Get did not return the DTO JSON.");
        host.Root.Set(vm => vm.Optional, 4).ThrowIfFailed();
        Require(model.Optional == 4, "Nullable scalar setter did not use generated codec decoding.");

        _ = host.Root.TakePublications();
        var item = model.Items[0];
        item.Name = "changed";
        Require(await PublishedAsync(host.Root, publication => publication.Json.Contains("changed", StringComparison.Ordinal)),
            "A nested observable DTO item change did not publish a frame.");
        model.Items.Remove(item);
        Require(await PublishedAsync(host.Root, publication => publication.Sets.Any(set => set.Field == "items" && set.Value.GetArrayLength() == 0)),
            "Removing the item did not publish the new collection.");
        // Protocol 4 compares members at publication, so a removed item's change publishes nothing either way; the
        // revision stays and no frame follows.
        using var beforeRemovedChange = host.RawSnapshot();
        item.Name = "removed";
        using var afterRemovedChange = host.RawSnapshot();
        Require(beforeRemovedChange.RootElement.GetProperty("revision").GetInt64()
            == afterRemovedChange.RootElement.GetProperty("revision").GetInt64() && !await PublishedAsync(host.Root, _ => true, 100),
            "A removed observable DTO item published a change.");

        _ = host.Root.TakePublications();
        var nestedItem = model.Groups[0].Items[0];
        nestedItem.Name = "nested-changed";
        Require(await PublishedAsync(host.Root, publication => publication.Json.Contains("nested-changed", StringComparison.Ordinal)),
            "A nested observable collection item change did not publish a frame.");
        model.Groups.RemoveAt(0);
        Require(await PublishedAsync(host.Root, publication => publication.Sets.Any(set => set.Field == "groups" && set.Value.GetArrayLength() == 0)),
            "Removing a nested observable collection owner did not publish the new collection.");
        using var beforeNestedRemovedChange = host.RawSnapshot();
        nestedItem.Name = "nested-removed";
        using var afterNestedRemovedChange = host.RawSnapshot();
        Require(beforeNestedRemovedChange.RootElement.GetProperty("revision").GetInt64()
            == afterNestedRemovedChange.RootElement.GetProperty("revision").GetInt64() && !await PublishedAsync(host.Root, _ => true, 100),
            "A removed nested observable collection item published a change.");
    }

    private static async Task<bool> PublishedAsync(RunicViewDriver<DataShapeViewModel> driver, Func<RunicViewPublication, bool> predicate,
        int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (driver.TakePublications().Any(predicate)) return true;
            await Task.Delay(10);
        }
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
