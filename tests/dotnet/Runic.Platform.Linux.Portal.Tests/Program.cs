using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Runic.Platform.Runtime;
using Tmds.DBus.Protocol;

var owner = new Owner();
var transport = new FakeTransport(new(0, ["file:///tmp/a%20file.txt"]));
var picker = new PortalFilePicker(owner, transport);
Check((await picker.SelectAsync(false, null, default)) is { Path: "/tmp/a file.txt", AllowsSiblingReplacement: true, Location: NativeFileLocation.Host }, "local URI");
Check(owner.Released == 1, "parent lease released");
const string exactDirectory = "/tmp/directory space % Ω 文本\nline";
Check((await new PortalFilePicker(owner, new FakeTransport(new(0, ["file:///tmp/directory%20space%20%25%20%CE%A9%20%E6%96%87%E6%9C%AC%0Aline"])))
    .SelectDirectoryAsync(default)) is { Path: exactDirectory }, "directory URI bytes preserve exact Unicode, spaces, percent and line feeds");
Check(PortalFilePicker.ReadExactLocalPath("file://localhost/tmp/%2525") == "/tmp/%25", "directory percent decoding occurs once");
Check(PortalFilePicker.ReadExactLocalPath("file:///tmp/./directory/../name") == "/tmp/./directory/../name", "directory URI does not normalize the selected path");
foreach (string invalid in new[] { "https://example.org/a", "file://remote/tmp/a", "file:///tmp/a#fragment", "file:///tmp/a?query", "file:///tmp/%FF", "file:///tmp/%C0%AF", "file:///tmp/%00", "file:///tmp/%GG", "file:///tmp/%" })
{
    try { await new PortalFilePicker(owner, new FakeTransport(new(0, [invalid]))).SelectDirectoryAsync(default); throw new InvalidOperationException("unsafe or lossy directory URI accepted"); }
    catch (IOException) { }
}
Check(await new PortalFilePicker(owner, new FakeTransport(new(1, []))).SelectDirectoryAsync(default) is null, "directory dismissal");
using (var directoryCancelled = new CancellationTokenSource())
{
    var waiting = new FakeTransport(null);
    var directoryRequest = new PortalFilePicker(owner, waiting).SelectDirectoryAsync(directoryCancelled.Token).AsTask();
    await waiting.Started.Task;
    directoryCancelled.Cancel();
    try { await directoryRequest; throw new InvalidOperationException("directory cancellation ignored"); } catch (OperationCanceledException) { }
    Check(waiting.Cancelled, "directory request drained on cancellation");
}
var directoryOwner = new Owner();
var directoryWaiting = new FakeTransport(null);
var ownerDirectoryRequest = new PortalFilePicker(directoryOwner, directoryWaiting).SelectDirectoryAsync(default).AsTask();
await directoryWaiting.Started.Task;
directoryOwner.Generation = Guid.NewGuid();
try { await ownerDirectoryRequest.WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("directory owner replacement ignored"); }
catch (OwnerClosedException) { }
Check(directoryOwner.Released == 1 && directoryWaiting.Cancelled, "directory owner close releases parent and request");
foreach (string uri in new[] { "https://example.com/a", "file://remote/tmp/a", "file:///tmp/a#fragment", "file:///tmp/a?query" })
{
    try { await new PortalFilePicker(owner, new FakeTransport(new(0, [uri]))).SelectAsync(false, null, default); throw new InvalidOperationException("unsafe URI accepted"); }
    catch (IOException) { }
}
Check(await new PortalFilePicker(owner, new FakeTransport(new(1, []))).SelectAsync(false, null, default) is null, "dismissal");
try { await new PortalFilePicker(new Owner { Identifier = "" }, transport).SelectAsync(false, null, default); throw new InvalidOperationException("unparented fallback accepted"); }
catch (NativeBackendUnavailableException) { }
var stale = new Owner { ChangeGenerationOnExport = true };
var unused = new FakeTransport(new(1, []));
try { await new PortalFilePicker(stale, unused).SelectAsync(false, null, default); throw new InvalidOperationException("stale exported parent accepted"); }
catch (OwnerClosedException) { }
Check(!unused.Started.Task.IsCompleted && stale.Released == 1, "owner replaced during export prevents a portal request");
var pending = new FakeTransport(null);
var replaced = new Owner();
var request = new PortalFilePicker(replaced, pending).SelectAsync(false, null, default).AsTask();
await pending.Started.Task;
replaced.Generation = Guid.NewGuid();
try { await request.WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("replacement ignored"); }
catch (OwnerClosedException) { }
Check(replaced.Released == 1 && pending.Cancelled, "replacement cancels request and releases parent");
using (var cancelled = new CancellationTokenSource())
{
    pending = new FakeTransport(null);
    request = new PortalFilePicker(owner, pending).SelectAsync(false, null, cancelled.Token).AsTask();
    await pending.Started.Task;
    cancelled.Cancel();
    try { await request; throw new InvalidOperationException("cancellation ignored"); } catch (OperationCanceledException) { }
}
string saveDirectory = Path.Combine(Path.GetTempPath(), "runic-portal-save-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(saveDirectory);
try
{
    string path = Path.Combine(saveDirectory, "original.txt");
    await File.WriteAllTextAsync(path, "original");
    var backend = new NativePickerBackend(owner, new PortalFilePicker(owner, new FakeTransport(new(0, [new Uri(path).AbsoluteUri]))));
    Check(backend.SupportsAtomicReplace && PortalPlatformProvider.CreateFileDialogs(owner).SupportsAtomicReplace, "portal dialogs report atomic save support");
    await using (var lifetime = new PresentationLifetime(() => owner.IsAvailable, owner.Generation))
        Check(new PresentationFiles(lifetime, backend).GetSnapshot().Statuses["platform.files.save"] is CapabilityStatus.Available, "facade reports Linux portal save as available");
    var selected = await backend.SaveFileAsync(new SaveFileOptions("original.txt"));
    Check(selected is PickerResult<ISaveFileLease>.Selected, "portal save destination selected");
    await using (var lease = ((PickerResult<ISaveFileLease>.Selected)selected).Value)
    {
        var begun = await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace);
        Check(begun is PlatformResult<IFileWriteTransaction>.Success, "host-path portal selection stages a sibling");
        await using var transaction = ((PlatformResult<IFileWriteTransaction>.Success)begun).Value;
        await transaction.Content.WriteAsync("saved"u8.ToArray());
        Check(await transaction.CommitAsync() is FileCommitResult.Committed, "host-path portal save commits");
    }
    Check(await File.ReadAllTextAsync(path) == "saved" && Directory.GetFiles(saveDirectory).Length == 1, "atomic save replaced the file and left no staging");
}
finally { Directory.Delete(saveDirectory, recursive: true); }

// Classification: a document is below the Documents.GetMountPoint mount (or the
// Flatpak alias) and on FUSE. Nothing else may skip host symlink resolution.
string? queriedMount = "/run/user/1000/doc";
int mountQueries = 0;
var fuse = new DocumentPortalPaths(_ => { mountQueries++; return ValueTask.FromResult<string?>(queriedMount); }, _ => true);
var plain = new DocumentPortalPaths(_ => { mountQueries++; return ValueTask.FromResult<string?>(queriedMount); }, _ => false);
Check(await fuse.ClassifyAsync("/run/user/1000/doc/abc123/notes.txt", default) == NativeFileLocation.DocumentPortal, "document below the queried mount");
Check(await fuse.ClassifyAsync("/run/user/1000/doc/by-app/org.example.App/abc123/notes.txt", default) == NativeFileLocation.DocumentPortal, "per-application document view");
Check(await fuse.ClassifyAsync("/run/flatpak/doc/abc123/notes.txt", default) == NativeFileLocation.DocumentPortal, "Flatpak document alias");
Check(await fuse.ClassifyAsync("/run/user/1000/document/abc123/notes.txt", default) == NativeFileLocation.Host, "a sibling prefix is not the mount");
Check(await fuse.ClassifyAsync("/run/user/1000/doc/notes.txt", default) == NativeFileLocation.Host, "the mount root holds no documents");
Check(await fuse.ClassifyAsync("/home/user/notes.txt", default) == NativeFileLocation.Host, "FUSE outside the mount is a host path");
mountQueries = 0;
Check(await plain.ClassifyAsync("/run/user/1000/doc/abc123/notes.txt", default) == NativeFileLocation.Host && mountQueries == 0,
    "a non-FUSE directory is a host path without a portal query");
queriedMount = null;
Check(await fuse.ClassifyAsync("/run/user/1000/doc/abc123/notes.txt", default) == NativeFileLocation.Host, "an unconfirmed mount is a host path");
queriedMount = "/srv/runtime/doc/";
Check(await fuse.ClassifyAsync("/srv/runtime/doc/abc123/notes.txt", default) == NativeFileLocation.DocumentPortal, "an atypical XDG_RUNTIME_DIR mount comes from the portal");
var documentPicker = new PortalFilePicker(owner, new FakeTransport(new(0, ["file:///srv/runtime/doc/abc123/notes.txt"])), documents: fuse);
Check(await documentPicker.SelectAsync(true, "notes.txt", default) is { AllowsSiblingReplacement: true, Location: NativeFileLocation.DocumentPortal }, "save selection carries its document location");
if (OperatingSystem.IsLinux()) await DocumentPortalTests.RunAsync(owner);
Console.WriteLine("PASS portal ownership, cancellation, local URI and access-grant checks.");
if (args.Contains("--settings"))
{
    await using var settings = PortalPlatformProvider.CreateSettings();
    Console.WriteLine(await settings.ReadAsync());
}
if (!args.Contains("--dbus")) return;
await DesktopPortalTests.RunAsync();
await InhibitionTests.RunAsync();
await PortalRecoveryTests.RunAsync();
using var service = new DBusConnection(DBusAddress.Session!);
await service.ConnectAsync();
var fake = new PortalService(service);
service.AddMethodHandler(fake);
var realTransport = new PortalTransport(destination: service.UniqueName!);
var directoryWireResult = await realTransport.RequestAsync("x11:1234", "SelectDirectory", "", default);
Check(directoryWireResult.Code == 0 && fake.DirectorySelection, "directory option encoded as true");
Check(fake.Call == "org.freedesktop.portal.FileChooser.OpenFile(ssa{sv}) handle_token,modal,directory" && fake.Argument == "Open directory",
    "directory picking uses FileChooser.OpenFile with directory option");
fake.FileChooserVersion = 2;
fake.Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
try { await realTransport.RequestAsync("x11:1234", "SelectDirectory", "", default); throw new InvalidOperationException("old portal accepted directory request"); }
catch (NativeBackendUnavailableException) { }
Check(!fake.Called.Task.IsCompleted, "old portal never receives a misleading file request");
fake.FileChooserVersion = 3;
foreach (bool oldHandle in new[] { false, true })
{
    fake.OldHandle = oldHandle;
    fake.AutoRespond = true;
    var result = await realTransport.RequestAsync("x11:1234", "SaveFile", "saved.txt", default);
    Check(result.Code == 0 && result.Uris.Single() == "file:///tmp/saved.txt", "real D-Bus early response and returned handle");
    Check(fake.Parent == "x11:1234" && fake.CurrentName == "saved.txt", "parent and filename encoded");
    Check(fake.Call == "org.freedesktop.portal.FileChooser.SaveFile(ssa{sv}) handle_token,modal,current_name" && fake.Argument == "Save file",
        "SaveFile wire shape and option order");
}
fake.MalformedUris = true;
try { await new PortalFilePicker(owner, realTransport).SelectAsync(true, "saved.txt", default); throw new InvalidOperationException("malformed portal URIs accepted"); }
catch (IOException) { }
fake.MalformedUris = false;
var uriResult = await realTransport.RequestAsync("x11:1234", "OpenURI", "https://example.org/", default);
Check(uriResult.Code == 0 && fake.Argument == "https://example.org/", "OpenURI request encoding");
Check(fake.Call == "org.freedesktop.portal.OpenURI.OpenURI(ssa{sv}) handle_token", "OpenURI wire shape");
fake.AutoRespond = false;
fake.Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
using (var cancellation = new CancellationTokenSource())
{
    var waiting = realTransport.RequestAsync("x11:1234", "OpenFile", "", cancellation.Token).AsTask();
    await fake.Called.Task.WaitAsync(TimeSpan.FromSeconds(3));
    cancellation.Cancel();
    try { await waiting.WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("D-Bus cancellation ignored"); } catch (OperationCanceledException) { }
    await fake.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Check(fake.Call == "org.freedesktop.portal.FileChooser.OpenFile(ssa{sv}) handle_token,modal" && fake.Argument == "Open file", "OpenFile wire shape");
    Check(fake.ClosedPath == fake.RequestPath && fake.ClosedCall == "org.freedesktop.portal.Request.Close()", "Request.Close targets the returned handle");
}
fake.Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
var disconnected = realTransport.RequestAsync("x11:1234", "OpenFile", "", default).AsTask();
await fake.Called.Task.WaitAsync(TimeSpan.FromSeconds(3));
service.Dispose();
try { await disconnected.WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("portal disconnection ignored"); }
catch (DBusExceptionBase) { }
catch (System.Threading.Channels.ChannelClosedException) { }
Console.WriteLine("PASS real D-Bus protocol: early response, legacy handle, save options, Request.Close.");
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class Owner : IPortalWindowOwner
{
    public Guid Generation { get; set; } = Guid.NewGuid();
    public bool IsAvailable { get; set; } = true;
    public string Identifier { get; set; } = "x11:1234";
    public int Released;
    public bool ChangeGenerationOnExport;
    // Models GTK3's single Wayland export per window: a retained parent blocks later requests.
    private int _exported;
    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask<PortalParentLease> ExportParentAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _exported, 1) != 0)
            throw new InvalidOperationException("A previous request retained the exported parent.");
        if (ChangeGenerationOnExport) Generation = Guid.NewGuid();
        return ValueTask.FromResult<PortalParentLease>(new Parent(this));
    }
    private sealed class Parent(Owner owner) : PortalParentLease
    {
        private int _disposed;
        public override string Identifier => owner.Identifier;
        public override ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) { owner.Released++; Volatile.Write(ref owner._exported, 0); }
            return ValueTask.CompletedTask;
        }
    }
}
sealed class FakeTransport(PortalResponse? result) : IPortalTransport
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Cancelled;
    public async ValueTask<PortalResponse> RequestAsync(string parent, string method, string argument, CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        if (result is not null) return result;
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        catch (OperationCanceledException) { Cancelled = true; throw; }
        throw new InvalidOperationException();
    }
}
sealed class PortalService(DBusConnection connection) : IPathMethodHandler
{
    private static readonly string[] ResponseUris = ["file:///tmp/saved.txt"];
    public string Path => "/org/freedesktop/portal/desktop";
    public bool HandlesChildPaths => true;
    public bool AutoRespond = true;
    public bool OldHandle;
    public bool MalformedUris;
    public bool DirectorySelection;
    public uint FileChooserVersion = 3;
    public string? Parent;
    public string? Argument;
    public string? CurrentName;
    public string? Call, RequestPath, ClosedCall, ClosedPath;
    public TaskCompletionSource Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.Request.MemberAsString == "Get")
        {
            var propertyReader = context.Request.GetBodyReader();
            CheckProperty(propertyReader.ReadString() == "org.freedesktop.portal.FileChooser" && propertyReader.ReadString() == "version");
            using var propertyReply = context.CreateReplyWriter("v");
            propertyReply.WriteVariant(VariantValue.UInt32(FileChooserVersion));
            context.Reply(propertyReply.CreateMessage());
            return ValueTask.CompletedTask;
        }
        if (context.Request.MemberAsString == "Close")
        {
            ClosedCall = $"{context.Request.InterfaceAsString}.Close({context.Request.SignatureAsString})";
            ClosedPath = context.Request.PathAsString;
            using var reply = context.CreateReplyWriter(null);
            context.Reply(reply.CreateMessage());
            Closed.TrySetResult();
            return ValueTask.CompletedTask;
        }
        var reader = context.Request.GetBodyReader();
        Parent = reader.ReadString();
        Argument = reader.ReadString();
        string token = "";
        var keys = new List<string>();
        var dictionary = reader.ReadDictionaryStart();
        while (reader.HasNext(dictionary))
        {
            string key = reader.ReadString();
            keys.Add(key);
            var value = reader.ReadVariantValue();
            if (key == "handle_token") token = value.GetString();
            if (key == "current_name") CurrentName = value.GetString();
            if (key == "directory") DirectorySelection = value.GetBool();
        }
        Call = $"{context.Request.InterfaceAsString}.{context.Request.MemberAsString}({context.Request.SignatureAsString}) {string.Join(',', keys)}";
        string path = Path + "/request/" + context.Request.SenderAsString![1..].Replace('.', '_') + "/" + (OldHandle ? "legacy" : token);
        RequestPath = path;
        if (AutoRespond)
        {
            using var signal = connection.GetMessageWriter();
            signal.WriteSignalHeader(path: path, @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
            signal.WriteUInt32(0);
            var dict = signal.WriteDictionaryStart();
            signal.WriteDictionaryEntryStart(); signal.WriteString("uris"); signal.WriteVariant(MalformedUris ? VariantValue.String(ResponseUris[0]) : VariantValue.Array(ResponseUris));
            signal.WriteDictionaryEnd(dict);
            connection.TrySendMessage(signal.CreateMessage());
        }
        using var writer = context.CreateReplyWriter("o");
        writer.WriteObjectPath(new ObjectPath(path));
        context.Reply(writer.CreateMessage());
        Called.TrySetResult();
        return ValueTask.CompletedTask;
    }
    private static void CheckProperty(bool expected) { if (!expected) throw new InvalidOperationException("Unexpected portal property query."); }
}
