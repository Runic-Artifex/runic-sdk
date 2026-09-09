using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Runic.Platform.Runtime;
using Tmds.DBus.Protocol;

var owner = new Owner();
var transport = new FakeTransport(new(0, ["file:///tmp/a%20file.txt"]));
var picker = new PortalFilePicker(owner, transport);
Check((await picker.SelectAsync(false, null, default)) is { Path: "/tmp/a file.txt", AllowsSiblingReplacement: false }, "local URI and conservative grant");
Check(owner.Released == 1, "parent lease released");
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
    var selected = await backend.SaveFileAsync(new SaveFileOptions("original.txt"));
    Check(selected is PickerResult<ISaveFileLease>.Selected, "portal save destination selected");
    await using var lease = ((PickerResult<ISaveFileLease>.Selected)selected).Value;
    Check(await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace) is PlatformResult<IFileWriteTransaction>.Unavailable { Reason: UnavailableReason.AtomicReplaceUnavailable }, "portal grant does not authorize atomic sibling replacement");
    Check(await File.ReadAllTextAsync(path) == "original" && Directory.GetFiles(saveDirectory).Length == 1, "unsupported save preserves file and creates no sibling staging");
}
finally { Directory.Delete(saveDirectory, recursive: true); }
Console.WriteLine("PASS portal ownership, cancellation, local URI and access-grant checks.");
if (args.Contains("--settings"))
{
    await using var settings = PortalPlatformProvider.CreateSettings();
    Console.WriteLine(await settings.ReadAsync());
}
if (!args.Contains("--dbus")) return;
await DesktopPortalTests.RunAsync();
using var service = new DBusConnection(DBusAddress.Session!);
await service.ConnectAsync();
var fake = new PortalService(service);
service.AddMethodHandler(fake);
var realTransport = new PortalTransport(destination: service.UniqueName!);
foreach (bool oldHandle in new[] { false, true })
{
    fake.OldHandle = oldHandle;
    fake.AutoRespond = true;
    var result = await realTransport.RequestAsync("x11:1234", "SaveFile", "saved.txt", default);
    Check(result.Code == 0 && result.Uris.Single() == "file:///tmp/saved.txt", "real D-Bus early response and returned handle");
    Check(fake.Parent == "x11:1234" && fake.CurrentName == "saved.txt", "parent and filename encoded");
}
var uriResult = await realTransport.RequestAsync("x11:1234", "OpenURI", "https://example.org/", default);
Check(uriResult.Code == 0 && fake.Argument == "https://example.org/", "OpenURI request encoding");
fake.AutoRespond = false;
fake.Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
using (var cancellation = new CancellationTokenSource())
{
    var waiting = realTransport.RequestAsync("x11:1234", "OpenFile", "", cancellation.Token).AsTask();
    await fake.Called.Task.WaitAsync(TimeSpan.FromSeconds(3));
    cancellation.Cancel();
    try { await waiting.WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("D-Bus cancellation ignored"); } catch (OperationCanceledException) { }
    await fake.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
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
    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask<PortalParentLease> ExportParentAsync(CancellationToken cancellationToken = default)
    {
        if (ChangeGenerationOnExport) Generation = Guid.NewGuid();
        return ValueTask.FromResult<PortalParentLease>(new Parent(this));
    }
    private sealed class Parent(Owner owner) : PortalParentLease
    {
        public override string Identifier => owner.Identifier;
        public override ValueTask DisposeAsync() { owner.Released++; return ValueTask.CompletedTask; }
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
    public string? Parent;
    public string? Argument;
    public string? CurrentName;
    public TaskCompletionSource Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.Request.MemberAsString == "Close")
        {
            using var reply = context.CreateReplyWriter(null);
            context.Reply(reply.CreateMessage());
            Closed.TrySetResult();
            return ValueTask.CompletedTask;
        }
        var reader = context.Request.GetBodyReader();
        Parent = reader.ReadString();
        Argument = reader.ReadString();
        string token = "";
        var dictionary = reader.ReadDictionaryStart();
        while (reader.HasNext(dictionary))
        {
            string key = reader.ReadString();
            var value = reader.ReadVariantValue();
            if (key == "handle_token") token = value.GetString();
            if (key == "current_name") CurrentName = value.GetString();
        }
        string path = Path + "/request/" + context.Request.SenderAsString![1..].Replace('.', '_') + "/" + (OldHandle ? "legacy" : token);
        if (AutoRespond)
        {
            using var signal = connection.GetMessageWriter();
            signal.WriteSignalHeader(path: path, @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
            signal.WriteUInt32(0);
            var dict = signal.WriteDictionaryStart();
            signal.WriteDictionaryEntryStart(); signal.WriteString("uris"); signal.WriteVariant(VariantValue.Array(ResponseUris));
            signal.WriteDictionaryEnd(dict);
            connection.TrySendMessage(signal.CreateMessage());
        }
        using var writer = context.CreateReplyWriter("o");
        writer.WriteObjectPath(new ObjectPath(path));
        context.Reply(writer.CreateMessage());
        Called.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
