using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Runic.Platform.Runtime;
using Tmds.DBus.Protocol;

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
    public string? Method;
    public PortalChooserOptions? Chooser;
    public ValueTask<PortalResponse> RequestAsync(string parent, string method, string argument, PortalChooserOptions? chooser, CancellationToken cancellationToken)
    {
        Method = method;
        Chooser = chooser;
        return RequestAsync(parent, method, argument, cancellationToken);
    }
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
    public string[] Uris = ResponseUris;
    // Every option of the last FileChooser request except the handle token, as D-Bus values.
    public Dictionary<string, VariantValue> Options = [];
    public string? Call, RequestPath, ClosedCall, ClosedPath;
    public volatile TaskCompletionSource Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        // Complete the Called the test had set when this request arrived: a handler that finishes after the test moved on and
        // replaced Called must not report the next request (#339).
        var called = Called;
        var reader = context.Request.GetBodyReader();
        Parent = reader.ReadString();
        Argument = reader.ReadString();
        string token = "";
        var keys = new List<string>();
        var options = new Dictionary<string, VariantValue>();
        var dictionary = reader.ReadDictionaryStart();
        while (reader.HasNext(dictionary))
        {
            string key = reader.ReadString();
            keys.Add(key);
            var value = reader.ReadVariantValue();
            if (key != "handle_token") options[key] = value;
            if (key == "handle_token") token = value.GetString();
            if (key == "current_name") CurrentName = value.GetString();
            if (key == "directory") DirectorySelection = value.GetBool();
        }
        Options = options;
        Call = $"{context.Request.InterfaceAsString}.{context.Request.MemberAsString}({context.Request.SignatureAsString}) {string.Join(',', keys)}";
        string path = Path + "/request/" + context.Request.SenderAsString![1..].Replace('.', '_') + "/" + (OldHandle ? "legacy" : token);
        RequestPath = path;
        if (AutoRespond)
        {
            using var signal = connection.GetMessageWriter();
            signal.WriteSignalHeader(path: path, @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
            signal.WriteUInt32(0);
            var dict = signal.WriteDictionaryStart();
            signal.WriteDictionaryEntryStart(); signal.WriteString("uris"); signal.WriteVariant(MalformedUris ? VariantValue.String(Uris[0]) : VariantValue.Array(Uris));
            signal.WriteDictionaryEnd(dict);
            connection.TrySendMessage(signal.CreateMessage());
        }
        using var writer = context.CreateReplyWriter("o");
        writer.WriteObjectPath(new ObjectPath(path));
        context.Reply(writer.CreateMessage());
        called.TrySetResult();
        return ValueTask.CompletedTask;
    }
    private static void CheckProperty(bool expected) { if (!expected) throw new InvalidOperationException("Unexpected portal property query."); }
}
