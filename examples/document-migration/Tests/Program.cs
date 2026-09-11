using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DocumentMigration.Before;
using DocumentMigration.Domain;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Bridge;
using Runic.Application.Generated;
using Runic.Platform;
[assembly: Runic.Application.RunicApplicationManifest("document-tests")]
[assembly: ApplicationBridgeContract("runic.examples.documents", 1, ContractName = "Documents")]

int assertions = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }
var files = new TestFiles();
var service = new DocumentService(files);
var before = new DocumentViewModel(service);
before.Text = "Portable domain ✓";
Check(!before.CanClose(false), "MVVM dirty-close protection");
await before.SaveCommand.ExecuteAsync(null);
Check(before.Status == "saved" && !before.IsDirty, "MVVM save clears captured draft");
var expected = files.Bytes.ToArray();
files.Wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
before.Text = "captured";
var beforeSaving = before.SaveCommand.ExecuteAsync(null);
before.Text = "newer edit";
Check(!before.CanClose(true), "MVVM blocks close even with discard during save");
files.Wait.SetResult();
await beforeSaving;
Check(before.IsDirty && before.Text == "newer edit" && Encoding.UTF8.GetString(files.Bytes) == "captured", "MVVM preserves edits during captured save");
files.Wait = null;
before.Text = "Portable domain ✓";
await before.SaveCommand.ExecuteAsync(null);
var services = new ServiceCollection();
services.AddSingleton(new DocumentService(files));
DocumentsBridgeContract.ConfigureServices(services);
await using var provider = services.BuildServiceProvider();
await using var session = ApplicationBridgeSessionFactory.Create(provider);
var events = Channel.CreateUnbounded<BridgeHostEnvelope>();
session.EventProduced += (_, frame) => events.Writer.TryWrite(frame);
BridgeClientEnvelope Frame(string kind, object payload) => new()
{
    Protocol = DocumentsBridgeContract.ProtocolIdentity, Version = 1,
    ContractFingerprint = DocumentsBridgeContract.Fingerprint, ConnectionEpoch = 0,
    Kind = kind, CommandId = Guid.NewGuid(), SessionId = kind == "initialize" ? null : session.Id.Value,
    Payload = JsonSerializer.SerializeToElement(payload),
};
async Task<JsonElement> Terminal(Guid operation)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (true)
    {
        var frame = await events.Reader.ReadAsync(timeout.Token);
        ApplicationBridgeCodec.EncodeHost(frame); // Include transport frame bounds, not only in-memory dispatch.
        var snapshot = frame.Payload.GetProperty("snapshot");
        if (snapshot.GetProperty("operationId").GetGuid() == operation) return snapshot;
    }
}
await session.DispatchAsync(Frame("initialize", new { }));
async Task<JsonElement> Command(object command)
{
    var result = await session.DispatchAsync(Frame("dispatch", command));
    Check(result.Kind == "receipt", $"Command accepted: {result.Payload}");
    return await Terminal(result.Payload.GetProperty("operationId").GetGuid());
}
var saved = await Command(new { _tag = "SaveDocument", text = "Portable domain ✓", revision = 4 });
Check(saved.GetProperty("status").GetString() == "saved", "Runic save terminal outcome");
Check(saved.GetProperty("capturedRevision").GetInt64() == 4, "Runic preserves captured frontend revision");
Check(files.Bytes.SequenceEqual(expected), "Before/after emit identical UTF-8 bytes");
var opened = await Command(new { _tag = "OpenDocument", revision = 5 });
Check(opened.GetProperty("text").GetString() == "Portable domain ✓", "Runic reopen returns saved content");
Check(files.ActiveLeases == 0, "Native leases disposed after open/save");
files.Dismiss = true;
Check((await Command(new { _tag = "OpenDocument", revision = 5 })).GetProperty("status").GetString() == "dismissed", "Dismissal is distinct");
files.Dismiss = false;
files.Wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
var pending = await session.DispatchAsync(Frame("dispatch", new { _tag = "OpenDocument", revision = 6 }));
var operation = pending.Payload.GetProperty("operationId").GetGuid();
var busy = await session.DispatchAsync(Frame("dispatch", new { _tag = "OpenDocument", revision = 6 }));
Check(busy.Kind == "error", "Concurrent operations rejected");
await session.DispatchAsync(Frame("cancelOperation", new { operationId = operation }));
Check((await Terminal(operation)).GetProperty("status").GetString() == "cancelled", "Cancellation reaches terminal state");
files.Wait = null;
Check((await Command(new { _tag = "OpenDocument", revision = 6 })).GetProperty("status").GetString() == "opened", "Post-cancellation retry succeeds");
files.Unknown = true;
Check((await Command(new { _tag = "SaveDocument", text = "maybe", revision = 7 })).GetProperty("status").GetString()!.StartsWith("commit-unknown:"), "Unknown write outcome preserved");
files.ThrowWriteDispose = true;
var unknownCleanup = await Command(new { _tag = "SaveDocument", text = "maybe", revision = 7 });
Check(unknownCleanup.GetProperty("status").GetString()!.StartsWith("commit-unknown:") && unknownCleanup.GetProperty("cleanupFailed").GetBoolean(), "Cleanup failure preserves uncertain commit");
files.Unknown = false;
var committedCleanup = await Command(new { _tag = "SaveDocument", text = "committed", revision = 7 });
Check(committedCleanup.GetProperty("status").GetString() == "saved" && committedCleanup.GetProperty("cleanupFailed").GetBoolean(), "Transaction cleanup failure preserves acknowledged commit");
files.ThrowWriteDispose = false;
files.ThrowSaveDispose = true;
Check((await service.SaveAsync("committed", CancellationToken.None)) is { Status: "saved", CleanupFailed: true }, "Lease cleanup failure preserves acknowledged commit");
files.ThrowSaveDispose = false;
using (var lateCancel = new CancellationTokenSource())
{
    files.CancelOnReadDispose = lateCancel;
    bool cancelledAfterCleanup = false;
    try { await service.OpenAsync(lateCancel.Token); }
    catch (OperationCanceledException) { cancelledAfterCleanup = true; }
    Check(cancelledAfterCleanup, "Cancellation during read cleanup cannot apply late open");
    files.CancelOnReadDispose = null;
}
var maximum = new string('\u0001', DocumentService.MaximumBytes);
Check((await Command(new { _tag = "SaveDocument", text = maximum, revision = 8 })).GetProperty("status").GetString() == "saved", "Maximum escaped document fits bridge frame limits");
files.Bytes = [0xc3, 0x28];
Check((await Command(new { _tag = "OpenDocument", revision = 8 })).GetProperty("status").GetString() == "invalid-or-inaccessible-document", "Malformed UTF-8 rejected");
files.Bytes = new byte[DocumentService.MaximumBytes + 1];
Check((await Command(new { _tag = "OpenDocument", revision = 8 })).GetProperty("status").GetString() == "too-large", "Oversized read bounded");
Check(files.ActiveLeases == 0, "Failure paths dispose leases");
var resultFiles = new TestFiles();
var resultLauncher = new TestLauncher();
var resultNotifications = new TestNotifications();
await using (var resultService = new DocumentService(resultFiles, resultLauncher, resultNotifications))
{
    Check((await resultService.SaveAsync("result", default)).Status == "saved", "Native result save acknowledged");
    Check(resultFiles.ActiveLeases == 1, "Saved result retains access for later handoff");
    Check((await resultService.LaunchResultAsync(DesktopFileOperation.ChooseApplication, default)).Status == "handoff-requested", "Open with acknowledges handoff without claiming selection");
    Check(resultLauncher.Last == DesktopFileOperation.ChooseApplication, "Application choice remains explicit");
    resultNotifications.Activate("reveal");
    await resultLauncher.Revealed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Check(resultLauncher.Last == DesktopFileOperation.Reveal, "Notification action opens saved result");
}
Check(resultFiles.ActiveLeases == 0 && resultNotifications.Removed, "Presentation completion releases access and withdraws result notification");
Console.WriteLine($"Document migration: {assertions} assertions passed. Simulated providers; no native evidence claimed.");

sealed class TestFiles : IFileDialogs
{
    public byte[] Bytes = [];
    public bool Dismiss, Unknown, ThrowWriteDispose, ThrowSaveDispose;
    public CancellationTokenSource? CancelOnReadDispose;
    public int ActiveLeases;
    public TaskCompletionSource? Wait;
    public async ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken token = default)
    {
        if (Wait is not null) await Wait.Task.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        if (Dismiss) return new PickerResult<IReadFileLease>.Dismissed();
        ActiveLeases++; return new PickerResult<IReadFileLease>.Selected(new Read(this));
    }
    public async ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken token = default)
    {
        if (Wait is not null) await Wait.Task.WaitAsync(token);
        token.ThrowIfCancellationRequested(); ActiveLeases++;
        return new PickerResult<ISaveFileLease>.Selected(new Save(this));
    }
    sealed class Read(TestFiles owner) : IReadFileLease
    {
        private MemoryStream? _stream;
        public string DisplayName => "document.txt";
        public ValueTask<Stream> OpenReadAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<Stream>(_stream = new(owner.Bytes)); }
        public ValueTask DisposeAsync() { _stream?.Dispose(); owner.ActiveLeases--; owner.CancelOnReadDispose?.Cancel(); return ValueTask.CompletedTask; }
    }
    sealed class Save(TestFiles owner) : ISaveFileLease, ILaunchableFileLease
    {
        public ValueTask<PlatformResult<Unit>> LaunchAsync(IDesktopFileLauncher launcher, DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default)
            => launcher.LaunchAsync(Path.Combine(Path.GetTempPath(), "document.txt"), operation, cancellationToken);
        public string DisplayName => "document.txt";
        public ValueTask<PlatformResult<IFileWriteTransaction>> BeginWriteAsync(FileWritePolicy policy, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(new PlatformResult<IFileWriteTransaction>.Success(new Write(owner))); }
        public ValueTask DisposeAsync() { owner.ActiveLeases--; if (owner.ThrowSaveDispose) throw new IOException("cleanup failed"); return ValueTask.CompletedTask; }
    }
    sealed class Write(TestFiles owner) : IFileWriteTransaction
    {
        private readonly MemoryStream _stream = new();
        public Stream Content => _stream;
        public ValueTask<FileCommitResult> CommitAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (owner.Unknown) return ValueTask.FromResult<FileCommitResult>(new FileCommitResult.CommitUnknown(FailureCode.IoError));
            owner.Bytes = _stream.ToArray(); return ValueTask.FromResult<FileCommitResult>(new FileCommitResult.Committed());
        }
        public ValueTask DisposeAsync() { _stream.Dispose(); if (owner.ThrowWriteDispose) throw new IOException("cleanup failed"); return ValueTask.CompletedTask; }
    }
}

sealed class TestLauncher : IDesktopFileLauncher
{
    public DesktopFileOperation Last;
    public TaskCompletionSource Revealed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask<PlatformResult<Unit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default)
    { Last = operation; if (operation == DesktopFileOperation.Reveal) Revealed.TrySetResult(); return ValueTask.FromResult<PlatformResult<Unit>>(new PlatformResult<Unit>.Success(new())); }
}
sealed class TestNotifications : IDesktopNotifications
{
    public event EventHandler<DesktopNotificationActivation>? Activated;
    public DesktopNotification? Sent;
    public bool Removed;
    public void Activate(string action) => Activated?.Invoke(this, new(Sent!.Id, action));
    public ValueTask<PlatformResult<Unit>> RequestPermissionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<PlatformResult<Unit>>(new PlatformResult<Unit>.Success(new()));
    public ValueTask<PlatformResult<Unit>> ShowAsync(DesktopNotification notification, CancellationToken cancellationToken = default) { Sent = notification; return RequestPermissionAsync(cancellationToken); }
    public ValueTask<PlatformResult<Unit>> RemoveAsync(string id, CancellationToken cancellationToken = default) { Removed = id == Sent?.Id; return RequestPermissionAsync(cancellationToken); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
