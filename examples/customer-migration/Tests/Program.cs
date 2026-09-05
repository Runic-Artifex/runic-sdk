using System.Text.Json;
using System.Threading.Channels;
using CustomerMigration.Before;
using CustomerMigration.Domain;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Bridge;
using Runic.Application.Generated;

[assembly: ApplicationBridgeContract("runic.examples.customers", 1, ContractName = "Customers")]

int assertions = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }
using var originalStore = new CustomerDirectory();
var before = new CustomerEditorViewModel(new(originalStore, TimeSpan.Zero));
Check(!before.SaveCommand.CanExecute(null), "Unchanged baseline cannot save");
before.Name = "";
Check(before.HasErrors && !before.SaveCommand.CanExecute(null), "Baseline validates edits");
before.Name = "Alex Updated";
Check(before.SaveCommand.CanExecute(null), "Valid dirty baseline can save");
Check(!before.Select(before.Customers[1].Id), "Baseline guards dirty navigation");
await before.SaveCommand.ExecuteAsync(null);
Check(before.Status == "Saved" && !before.IsDirty, "Baseline save completes and clears dirty state");

using var store = new CustomerDirectory();
var services = new ServiceCollection();
services.AddSingleton(new CustomerService(store, TimeSpan.FromMilliseconds(30)));
CustomersBridgeContract.ConfigureServices(services);
await using var provider = services.BuildServiceProvider();
await using var session = ApplicationBridgeSessionFactory.Create(provider);
var events = Channel.CreateUnbounded<BridgeHostEnvelope>();
session.EventProduced += (_, frame) => events.Writer.TryWrite(frame);
BridgeClientEnvelope Frame(string kind, object payload, long epoch = 0) => new()
{
    Protocol = CustomersBridgeContract.ProtocolIdentity,
    Version = 1,
    ContractFingerprint = CustomersBridgeContract.Fingerprint,
    ConnectionEpoch = epoch,
    Kind = kind,
    CommandId = Guid.NewGuid(),
    SessionId = kind == "initialize" ? null : session.Id.Value,
    Payload = JsonSerializer.SerializeToElement(payload),
};
object Draft(string name = "Alex Updated", string email = "alex@example.com", int version = 1) => new { id = CustomerDirectory.Seeds()[0].Id, name, email, company = "Northstar Studio", version };
Task<BridgeHostEnvelope> Dispatch(string tag, object draft) => session.DispatchAsync(Frame("dispatch", new { _tag = tag, draft })).AsTask();
async Task<JsonElement> Terminal(Guid operation)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (true)
    {
        var frame = await events.Reader.ReadAsync(timeout.Token);
        var snapshot = frame.Payload.GetProperty("snapshot");
        var save = snapshot.GetProperty("save");
        if (save.GetProperty("operationId").GetGuid() == operation && save.GetProperty("status").GetString() != "saving") return snapshot;
    }
}
var initial = await session.DispatchAsync(Frame("initialize", new { }));
Check(initial.Payload.GetProperty("customers").GetArrayLength() == 3, "Runic initializes customer state");
var invalid = await Dispatch("ValidateCustomer", Draft(""));
Check(invalid.Payload.GetProperty("issues")[0].GetProperty("field").GetString() == "name", "Runic exposes shared validation");
var rejected = await Dispatch("SaveCustomer", Draft(""));
Check(rejected.Kind == "error" && rejected.Payload.GetProperty("code").GetString() == "Validation", "Invalid save rejected by backend");
Check(store.Read()[0].Version == 1, "Invalid draft never changes persisted data");
var started = await Dispatch("SaveCustomer", Draft());
Check(started.Kind == "receipt", $"Save admitted: {started.Payload}");
Guid operation = started.Payload.GetProperty("operationId").GetGuid();
var busy = await Dispatch("SaveCustomer", Draft());
Check(busy.Kind == "error" && busy.Payload.GetProperty("code").GetString() == "Busy", "Concurrent session saves are rejected");
var saved = await Terminal(operation);
Check(saved.GetProperty("save").GetProperty("status").GetString() == "saved", "Save reports success");
Check(store.Read()[0] == originalStore.Read()[0], "MVVM and Runic produce identical persisted business state");

started = await Dispatch("SaveCustomer", Draft("Cancelled edit", version: 2));
operation = started.Payload.GetProperty("operationId").GetGuid();
var cancellation = await session.DispatchAsync(Frame("cancelOperation", new { operationId = operation }));
Check(cancellation.Payload.GetProperty("accepted").GetBoolean(), "Cancellation accepted");
var cancelled = await Terminal(operation);
Check(cancelled.GetProperty("save").GetProperty("status").GetString() == "cancelled", "Cancellation reaches terminal state");
Check(store.Read()[0].Name == "Alex Updated" && store.Read()[0].Version == 2, "Cancelled work is not committed");

started = await Dispatch("SaveCustomer", Draft("Stale edit"));
var conflict = await Terminal(started.Payload.GetProperty("operationId").GetGuid());
Check(conflict.GetProperty("save").GetProperty("status").GetString() == "failed", "Stale record version fails");
Check(store.Read()[0].Version == 2, "Conflict does not overwrite data");
started = await Dispatch("SaveCustomer", Draft(email: "sam@example.com", version: 2));
var duplicate = await Terminal(started.Payload.GetProperty("operationId").GetGuid());
Check(duplicate.GetProperty("save").GetProperty("issues")[0].GetProperty("field").GetString() == "email", "Uniqueness validation remains in C#");
var reconnect = await session.DispatchAsync(Frame("initialize", new { }, 1));
Check(reconnect.Payload.GetProperty("customers")[0].GetProperty("version").GetInt32() == 2, "Reconnect recovers committed data without replay");
Check(reconnect.Payload.GetProperty("save").GetProperty("status").GetString() == "failed", "Reconnect retains operation outcome");
await using var second = ApplicationBridgeSessionFactory.Create(provider);
var secondInitial = await second.DispatchAsync(Frame("initialize", new { }));
Check(secondInitial.Payload.GetProperty("save").GetProperty("status").GetString() == "idle", "Operation state is session scoped");
Check(secondInitial.Payload.GetProperty("customers")[0].GetProperty("version").GetInt32() == 2, "Application data is shared across sessions");

string folder = Path.Combine(Path.GetTempPath(), "runic-customers-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    string file = Path.Combine(folder, "customers.json");
    using (var disk = new CustomerDirectory(file)) await disk.SaveAsync(new(CustomerDirectory.Seeds()[0].Id, "Persisted Customer", "alex@example.com", "Studio", 1), CancellationToken.None);
    using (var reopened = new CustomerDirectory(file)) Check(reopened.Read()[0].Name == "Persisted Customer", "Data survives process/store restart");
    Check(Directory.GetFiles(folder, "*.tmp").Length == 0, "Atomic save cleans temporary files");
}
finally { Directory.Delete(folder, true); }
Check(!typeof(CustomerMigration.After.CustomerFeature).Assembly.GetReferencedAssemblies().Any(a => a.Name!.Contains("CommunityToolkit")), "Runic feature has no Toolkit dependency");
Console.WriteLine($"Customer migration: {assertions} assertions passed.");
