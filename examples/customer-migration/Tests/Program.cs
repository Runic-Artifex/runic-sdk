using System.Text.Json;
using Runic.Application.Platform;
using Runic.Platform;
using CustomerMigration.After;
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

var exportedBefore = before.ExportSavedContact();
before.ApplyContact("{\"name\":\"Review Name\",\"email\":\"review@example.com\",\"company\":\"Studio\"}");
Check(before.IsDirty && before.Name == "Review Name", "Baseline imports into a reviewable dirty draft");
Check(before.ExportSavedContact() == exportedBefore, "Baseline exports captured persisted fields, not unsaved edits");
Check(before.Select(before.Customers[0].Id, discard: true), "Baseline can discard imported draft");
using var store = new CustomerDirectory();
var services = new ServiceCollection();
var testClipboard = new TestClipboard();
var testFiles = new TestFiles();
services.AddRunicPlatform(_ => new PlatformProvider { Clipboard = testClipboard, OwnerAvailable = () => true });
services.AddScoped<IFileDialogs>(_ => testFiles);
TaskCompletionSource? saveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
services.AddSingleton(new CustomerService(store, TimeSpan.Zero,
    token => saveGate?.Task.WaitAsync(token) ?? Task.CompletedTask));
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
Check(busy.Kind == "error" && busy.Payload.TryGetProperty("code", out var busyCode) && busyCode.GetString() == "Busy", $"Concurrent session saves are rejected: {busy.Payload}");
saveGate.SetResult();
saveGate = null;
var saved = await Terminal(operation);
Check(saved.GetProperty("save").GetProperty("status").GetString() == "saved", "Save reports success");
Check(store.Read()[0] == originalStore.Read()[0], "MVVM and Runic produce identical persisted business state");

saveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
started = await Dispatch("SaveCustomer", Draft("Cancelled edit", version: 2));
operation = started.Payload.GetProperty("operationId").GetGuid();
var cancellation = await session.DispatchAsync(Frame("cancelOperation", new { operationId = operation }));
Check(cancellation.Payload.GetProperty("accepted").GetBoolean(), "Cancellation accepted");
var cancelled = await Terminal(operation);
saveGate = null;
Check(cancelled.GetProperty("save").GetProperty("status").GetString() == "cancelled", "Cancellation reaches terminal state");
Check(store.Read()[0].Name == "Alex Updated" && store.Read()[0].Version == 2, "Cancelled work is not committed");

started = await Dispatch("SaveCustomer", Draft("Stale edit"));
var conflict = await Terminal(started.Payload.GetProperty("operationId").GetGuid());
Check(conflict.GetProperty("save").GetProperty("status").GetString() == "failed", "Stale record version fails");
Check(store.Read()[0].Version == 2, "Conflict does not overwrite data");
started = await Dispatch("SaveCustomer", Draft(email: "sam@example.com", version: 2));
var duplicate = await Terminal(started.Payload.GetProperty("operationId").GetGuid());
Check(duplicate.GetProperty("save").GetProperty("issues")[0].GetProperty("field").GetString() == "email", "Uniqueness validation remains in C#");
long transferEpoch = 0;
Task<BridgeHostEnvelope> Transfer(string action, int sequence = 7, int version = 2) => session.DispatchAsync(Frame("dispatch", new { _tag = "TransferContact", action, customerId = store.Read()[0].Id, version, draftSequence = sequence }, transferEpoch)).AsTask();
async Task<JsonElement> NativeTerminal(Guid id)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (true)
    {
        var native = (await events.Reader.ReadAsync(timeout.Token)).Payload.GetProperty("snapshot").GetProperty("native");
        if (native.GetProperty("operationId").ValueKind == JsonValueKind.String && native.GetProperty("operationId").GetGuid() == id && native.GetProperty("status").GetString() != "running") return native;
    }
}
async Task<JsonElement> TransferResult(string action)
{
    var receipt = await Transfer(action);
    Check(receipt.Kind == "receipt", $"Native action admitted: {receipt.Payload}");
    return await NativeTerminal(receipt.Payload.GetProperty("operationId").GetGuid());
}
Check((await TransferResult("import")).GetProperty("status").GetString() == "unavailable", "Missing file provider reports unavailable");
testClipboard.Text = null;
Check((await TransferResult("paste")).GetProperty("status").GetString() == "no-text", "No clipboard text is distinct");
testClipboard.Text = "";
Check((await TransferResult("paste")).GetProperty("status").GetString() == "failed", "Empty clipboard text is invalid JSON, not absent");
testClipboard.Text = "{\"name\":\"Imported Name\",\"email\":\"import@example.com\",\"company\":\"Studio\",\"id\":\"ignored\",\"version\":99}";
var pasted = await TransferResult("paste");
Check(pasted.GetProperty("candidate").GetProperty("name").GetString() == "Imported Name" && pasted.GetProperty("draftSequence").GetInt32() == 7, "Paste carries candidate and captured draft sequence");
Check(store.Read()[0].Name == "Alex Updated", "Import never persists or replaces domain identity");
testClipboard.Text = new string('é', 3000);
Check((await TransferResult("paste")).GetProperty("status").GetString() == "failed", "Clipboard contact validates UTF-8 bytes as well as character limit");
var staleExport = await Transfer("export", version: 1);
Check(staleExport.Kind == "error", "Export rejects a stale confirmed saved revision");
Check((await TransferResult("copy")).GetProperty("status").GetString() == "completed", "Copy reports actual native success");
Check(ContactCodec.Parse(testClipboard.Text!).Name == "Alex Updated", "Copy captures persisted revision, never draft state");
int writes = testClipboard.Writes;
var reconnect = await session.DispatchAsync(Frame("initialize", new { }, 1));
transferEpoch = 1;
Check(reconnect.Payload.GetProperty("customers")[0].GetProperty("version").GetInt32() == 2, "Reconnect recovers committed data without replay");
Check(reconnect.Payload.GetProperty("save").GetProperty("status").GetString() == "failed", "Reconnect retains operation outcome");
Check(testClipboard.Writes == writes, "Reconnect never replays clipboard side effects");
await using var second = ApplicationBridgeSessionFactory.Create(provider);
var secondInitial = await second.DispatchAsync(Frame("initialize", new { }));
Check(secondInitial.Payload.GetProperty("save").GetProperty("status").GetString() == "idle", "Operation state is session scoped");
Check(secondInitial.Payload.GetProperty("customers")[0].GetProperty("version").GetInt32() == 2, "Application data is shared across sessions");

testFiles.ReadText = "{\"name\":\"File Contact\",\"email\":\"file@example.com\",\"company\":\"Studio\"}";
var imported = await TransferResult("import");
Check(imported.GetProperty("candidate").GetProperty("name").GetString() == "File Contact", "Selected file returns bounded editable fields through the real bridge");
Check(testFiles.ReadDisposed, "Selected file lease is disposed after import");
testFiles.ReadCleanupGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
testFiles.ReadCleanupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
var lateRead = await Transfer("import");
Guid lateReadId = lateRead.Payload.GetProperty("operationId").GetGuid();
await testFiles.ReadCleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
await session.DispatchAsync(Frame("cancelOperation", new { operationId = lateReadId }, 1));
testFiles.ReadCleanupGate.SetResult();
var cancelledRead = await NativeTerminal(lateReadId);
Check(cancelledRead.GetProperty("status").GetString() == "cancelled" && cancelledRead.GetProperty("candidate").ValueKind == JsonValueKind.Null, "Cancellation during read lease cleanup rejects the late candidate");
testFiles.ReadCleanupGate = null;
testFiles.ReadCleanupEntered = null;
testFiles.ReadText = new string('x', 4097);
Check((await TransferResult("import")).GetProperty("status").GetString() == "failed", "Oversized selected file fails before producing a candidate");
testFiles.Dismiss = true;
Check((await TransferResult("import")).GetProperty("status").GetString() == "dismissed", "Picker dismissal remains distinct from cancellation");
testFiles.Dismiss = false;
testFiles.SaveEnabled = true;
testFiles.Commit = new FileCommitResult.CommitUnknown(FailureCode.IoError);
Check((await TransferResult("export")).GetProperty("status").GetString() == "uncertain", "Uncertain native commit is never described as cancelled or retryable");
testFiles.Commit = new FileCommitResult.Committed();
testFiles.FailCleanup = true;
var cleanupOutcome = await TransferResult("export");
Check(cleanupOutcome.GetProperty("status").GetString() == "completed", "Cleanup failure preserves acknowledged native commit");
Check(cleanupOutcome.GetProperty("cleanupFailed").GetBoolean(), "Cleanup failure remains independently visible after acknowledged commit");
testFiles.FailCleanup = false;
testFiles.SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
var cancelledPicker = await Transfer("export");
Guid cancelledPickerId = cancelledPicker.Payload.GetProperty("operationId").GetGuid();
await session.DispatchAsync(Frame("cancelOperation", new { operationId = cancelledPickerId }, 1));
Check((await NativeTerminal(cancelledPickerId)).GetProperty("status").GetString() == "cancelled", "Cancellation while picker is pending remains cancelled");
testFiles.SaveGate = null;
testFiles.CommitGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
testFiles.CommitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
var lateCancel = await Transfer("export");
Guid lateCancelId = lateCancel.Payload.GetProperty("operationId").GetGuid();
await testFiles.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
await session.DispatchAsync(Frame("cancelOperation", new { operationId = lateCancelId }, 1));
testFiles.CommitGate.SetResult();
Check((await NativeTerminal(lateCancelId)).GetProperty("status").GetString() == "completed", "Cancellation after native commit begins preserves actual committed outcome");
testFiles.CommitGate = null;
testFiles.CommitEntered = null;
// Hold a selected save dialog while a real bridge domain save advances the record.
testFiles.SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
var exporting = await Transfer("export");
var exportOperation = exporting.Payload.GetProperty("operationId").GetGuid();
started = await session.DispatchAsync(Frame("dispatch", new { _tag = "SaveCustomer", draft = Draft("Later Saved", version: 2) }, 1));
await Terminal(started.Payload.GetProperty("operationId").GetGuid());
testFiles.SaveGate.SetResult();
Check((await NativeTerminal(exportOperation)).GetProperty("status").GetString() == "completed", "Held export completes after concurrent save");
Check(ContactCodec.Parse(testFiles.Written!).Name == "Alex Updated", "Export bytes retain the originally confirmed revision while a newer save commits");
Check(testFiles.SaveDisposed && testFiles.TransactionDisposed, "Save lease and transaction are released");

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

sealed class TestClipboard : ITextClipboard
{
    public string? Text { get; set; }
    public int Writes { get; private set; }
    public ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PlatformResult<string?>>(Text?.Length > maximumCharacters ? new PlatformResult<string?>.Failed(FailureCode.TooLarge) : new PlatformResult<string?>.Success(Text));
    }
    public ValueTask<PlatformResult<Unit>> WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Text = text; Writes++;
        return ValueTask.FromResult<PlatformResult<Unit>>(new PlatformResult<Unit>.Success(default));
    }
}

sealed class TestFiles : IFileDialogs
{
    public string? ReadText;
    public bool Dismiss, SaveEnabled, ReadDisposed, SaveDisposed, TransactionDisposed, FailCleanup;
    public string? Written;
    public FileCommitResult Commit = new FileCommitResult.Committed();
    public TaskCompletionSource? SaveGate, CommitGate, CommitEntered, ReadCleanupGate, ReadCleanupEntered;
    public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PickerResult<IReadFileLease>>(Dismiss ? new PickerResult<IReadFileLease>.Dismissed() : ReadText is null ? new PickerResult<IReadFileLease>.Unavailable(UnavailableReason.ProviderNotConfigured) : new PickerResult<IReadFileLease>.Selected(new ReadLease(this, ReadText)));
    }
    public async ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default)
    {
        if (SaveGate is not null) await SaveGate.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return SaveEnabled ? new PickerResult<ISaveFileLease>.Selected(new SaveLease(this)) : new PickerResult<ISaveFileLease>.Unavailable(UnavailableReason.ProviderNotConfigured);
    }
    sealed class ReadLease(TestFiles owner, string text) : IReadFileLease
    {
        public string DisplayName => "contact.json";
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)));
        public async ValueTask DisposeAsync()
        {
            owner.ReadCleanupEntered?.TrySetResult();
            if (owner.ReadCleanupGate is not null) await owner.ReadCleanupGate.Task;
            owner.ReadDisposed = true;
        }
    }
    sealed class SaveLease(TestFiles owner) : ISaveFileLease
    {
        public string DisplayName => "contact.json";
        public ValueTask<PlatformResult<IFileWriteTransaction>> BeginWriteAsync(FileWritePolicy policy, CancellationToken cancellationToken = default)
        {
            if (policy != FileWritePolicy.RequireAtomicReplace) throw new Exception("Atomic replacement required");
            return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(new PlatformResult<IFileWriteTransaction>.Success(new Transaction(owner)));
        }
        public ValueTask DisposeAsync() { owner.SaveDisposed = true; return ValueTask.CompletedTask; }
    }
    sealed class Transaction(TestFiles owner) : IFileWriteTransaction
    {
        readonly MemoryStream stream = new();
        public Stream Content => stream;
        public async ValueTask<FileCommitResult> CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.CommitEntered?.TrySetResult();
            if (owner.CommitGate is not null) await owner.CommitGate.Task;
            owner.Written = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            return owner.Commit;
        }
        public ValueTask DisposeAsync()
        {
            owner.TransactionDisposed = true; stream.Dispose();
            if (owner.FailCleanup) throw new IOException("Intentional cleanup failure after commit");
            return ValueTask.CompletedTask;
        }
    }
}
