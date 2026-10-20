using Runic.Desktop;
using Runic.Platform;
using Runic.Platform.Linux.Gtk4;
using Runic.Platform.Linux.Portal;
using Runic.Platform.Runtime;
using Tmds.DBus.Protocol;
using Microsoft.Win32.SafeHandles;
using System.Runtime.Versioning;

[SupportedOSPlatform("linux")]
internal static partial class DirectorySmoke
{
    private static DBusConnection? _connection;
    internal static IDisposable StartPortal()
    {
        var connection = new DBusConnection(DBusAddress.Session!);
        try
        {
            connection.ConnectAsync().AsTask().GetAwaiter().GetResult();
            if (!connection.TryRequestNameAsync("org.freedesktop.portal.Desktop", RequestNameOptions.None).GetAwaiter().GetResult())
                throw new InvalidOperationException("Run the directory smoke in an isolated dbus-run-session.");
            _connection = connection;
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    // Run under dbus-run-session. This controls only the portal service boundary;
    // the window, owner export, production provider and leases are real.
    internal static async Task RunAsync(INativeWindowOwner owner, IDesktopWindowHost host)
    {
        var connection = _connection ?? throw new InvalidOperationException("Start the controlled portal before GTK initialization.");
        string root = Path.Combine(Path.GetTempPath(), "runic-gtk4-directory-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "space % Ω 文本\nline");
        Directory.CreateDirectory(path);
        try
        {
            var portal = new DirectoryPortal(connection, new Uri(path).AbsoluteUri);
            connection.AddMethodHandler(portal);
            var backend = PortalPlatformProvider.CreateFileDialogs(Gtk4PlatformProvider.CreatePortalWindowOwner(owner));
            await using var lifetime = new PresentationLifetime(() => owner.IsAvailable, owner.Generation);
            var files = new PresentationFiles(lifetime, backend);
            Check(files.GetSnapshot().Statuses["platform.directories.open"] is CapabilityStatus.Available, "directory capability");
            var selection = await files.OpenDirectoryAsync(new());
            Check(selection is PickerResult<IDirectoryLease>.Selected, "directory selection: " + selection);
            var selected = (PickerResult<IDirectoryLease>.Selected)selection;
            Check(selected.Value.LocalPath == path && Directory.Exists(selected.Value.LocalPath), "exact acquired directory access");
            Check(portal.Parent?.StartsWith("x11:", StringComparison.Ordinal) == true || portal.Parent?.StartsWith("wayland:", StringComparison.Ordinal) == true, "real GTK4 parent");
            Check(portal.Directory && portal.Title == "Open directory", "production FileChooser directory option");
            await selected.Value.DisposeAsync();
            await SaveAsync(files, portal, root);

            portal.ResponseCode = 1;
            Check(await files.OpenDirectoryAsync(new()) is PickerResult<IDirectoryLease>.Dismissed, "user dismissal");
            portal.ResponseCode = 0;
            portal.Hold = true;
            portal.Reset();
            using (var cancelled = new CancellationTokenSource())
            {
                var request = files.OpenDirectoryAsync(new(), cancelled.Token).AsTask();
                await portal.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancelled.Cancel();
                try { await request; throw new InvalidOperationException("Caller cancellation was ignored."); }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
                await portal.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            // An actual native owner disappearing stops the request even before
            // the presentation facade begins shutdown.
            portal.Reset();
            var closingRequest = files.OpenDirectoryAsync(new()).AsTask();
            await portal.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.CloseAsync();
            Check(await closingRequest.WaitAsync(TimeSpan.FromSeconds(5)) is PickerResult<IDirectoryLease>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed }, "native owner closure");
            await portal.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await lifetime.DisposeAsync();
            Check(await files.OpenDirectoryAsync(new()) is PickerResult<IDirectoryLease>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed }, "directory shutdown drained");
            Console.WriteLine("PASS GTK4 directory access: production portal wire request, exact local path, dismissal, cancellation, native owner close and drain (controlled portal service).");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // SaveFile through the production provider: a host path (what an unsandboxed
    // application receives) and a document-portal view (what a Flatpak receives).
    private static async Task SaveAsync(PresentationFiles files, DirectoryPortal portal, string root)
    {
        Check(files.GetSnapshot().Statuses["platform.files.save"] is CapabilityStatus.Available, "save capability");
        string folder = Path.Combine(root, "save");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, "save-target.txt");
        await File.WriteAllTextAsync(target, "original");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(target, mode);
        portal.SaveUri = new Uri(target).AbsoluteUri;
        long inode = Inode(target);
        Check(await SaveTextAsync(files, "replaced") is FileCommitResult.Committed, "host-path save commits");
        Check(portal.Method == "SaveFile" && portal.Title == "Save file" && portal.CurrentName == "save-target.txt" && portal.Modal
            && (portal.Parent?.StartsWith("x11:", StringComparison.Ordinal) == true || portal.Parent?.StartsWith("wayland:", StringComparison.Ordinal) == true),
            "production SaveFile request: real parent, modal and current_name");
        Check(await File.ReadAllTextAsync(target) == "replaced" && Inode(target) != inode && File.GetUnixFileMode(target) == mode
            && Directory.GetFileSystemEntries(folder).Length == 1, "host-path save replaced the inode, kept the mode and left no staging");

        portal.ResponseCode = 1;
        Check(await files.SaveFileAsync(new("save-target.txt")) is PickerResult<ISaveFileLease>.Dismissed, "save dismissal");
        portal.ResponseCode = 0;
        portal.Hold = true;
        portal.Reset();
        using (var cancelled = new CancellationTokenSource())
        {
            var request = files.SaveFileAsync(new("save-target.txt"), cancelled.Token).AsTask();
            await portal.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            try { await request; throw new InvalidOperationException("Save cancellation was ignored."); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            await portal.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        portal.Hold = false;
        Console.WriteLine("PASS GTK4 save: production SaveFile request, host-path atomic replacement, dismissal and cancellation (controlled portal service).");

        if (await AddDocumentAsync(folder, "save-target.txt") is not var (connection, id, document))
            return;
        try
        {
            portal.SaveUri = new Uri(document).AbsoluteUri;
            inode = Inode(target);
            Check(await SaveTextAsync(files, "document") is FileCommitResult.Committed, "document-portal save commits");
            Check(await File.ReadAllTextAsync(target) == "document" && Inode(target) != inode && File.GetUnixFileMode(target) == mode
                && Directory.GetFileSystemEntries(folder).Length == 1, "document-portal save replaced the host inode, kept the mode and left no .xdp-* file");
            Console.WriteLine("PASS GTK4 document-portal save: real xdg-document-portal FUSE view, atomic host rename.");
        }
        finally
        {
            await Documents(connection, "Delete", "s", (ref MessageWriter writer) => writer.WriteString(id));
            connection.Dispose();
        }
    }

    private static async Task<FileCommitResult> SaveTextAsync(PresentationFiles files, string text)
    {
        var selection = await files.SaveFileAsync(new("save-target.txt"));
        Check(selection is PickerResult<ISaveFileLease>.Selected, "save selection: " + selection);
        await using var lease = ((PickerResult<ISaveFileLease>.Selected)selection).Value;
        var begun = await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace);
        Check(begun is PlatformResult<IFileWriteTransaction>.Success, "save staging: " + begun);
        await using var transaction = ((PlatformResult<IFileWriteTransaction>.Success)begun).Value;
        await transaction.Content.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text));
        return await transaction.CommitAsync();
    }

    // Exports the file to the document portal for a test application and returns
    // the per-application view a Flatpak receives. This isolated bus may activate
    // its own document portal, but never one that would take over the desktop
    // session's mount: run with a private XDG_RUNTIME_DIR to cover this locally.
    private static async Task<(DBusConnection, string, string)?> AddDocumentAsync(string folder, string name)
    {
        const string app = "org.runic.Gtk4Smoke";
        var connection = new DBusConnection(DBusAddress.Session!);
        try
        {
            await connection.ConnectAsync();
            string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!File.Exists("/dev/fuse") || runtime is null) return Skip("FUSE or XDG_RUNTIME_DIR is unavailable");
            if (File.ReadAllLines("/proc/self/mounts").Any(line => line.Split(' ')[1] == Path.Combine(runtime, "doc")))
                return Skip("another session's document portal owns the mount; use a private XDG_RUNTIME_DIR");
            try
            {
                await connection.CallMethodAsync(StartDocuments(connection)).WaitAsync(TimeSpan.FromSeconds(10));
                byte[] mount = await connection.CallMethodAsync(DocumentsMessage(connection, "GetMountPoint", null, (ref MessageWriter _) => { }),
                    static (message, _) => message.GetBodyReader().ReadArrayOfByte()).WaitAsync(TimeSpan.FromSeconds(5));
                using var parent = new SafeFileHandle(Open(folder, 0x200000 | 0x80000), ownsHandle: true); // O_PATH | O_CLOEXEC
                string id = await connection.CallMethodAsync(DocumentsMessage(connection, "AddNamedFull", "hayusas", (ref MessageWriter writer) =>
                {
                    writer.WriteHandle(parent);
                    writer.WriteArray(System.Text.Encoding.UTF8.GetBytes(name + "\0"));
                    writer.WriteUInt32(0); // Transient: never stored in the permission store.
                    writer.WriteString(app);
                    writer.WriteArray(ReadWrite);
                }), static (message, _) => message.GetBodyReader().ReadString()).WaitAsync(TimeSpan.FromSeconds(5));
                string root = System.Text.Encoding.UTF8.GetString(mount).TrimEnd('\0');
                return (connection, id, Path.Combine(root, "by-app", app, id, name));
            }
            catch (Exception error) when (error is DBusExceptionBase or TimeoutException) { return Skip("the document portal is unavailable: " + error.GetType().Name); }
        }
        catch { connection.Dispose(); throw; }

        (DBusConnection, string, string)? Skip(string reason)
        {
            connection.Dispose();
            Console.WriteLine("SKIP GTK4 document-portal save: " + reason + ".");
            return null;
        }
    }

    private static readonly string[] ReadWrite = ["read", "write"];
    private delegate void BodyWriter(ref MessageWriter writer);

    private static Task Documents(DBusConnection connection, string member, string signature, BodyWriter body) =>
        connection.CallMethodAsync(DocumentsMessage(connection, member, signature, body)).WaitAsync(TimeSpan.FromSeconds(5));

    private static MessageBuffer DocumentsMessage(DBusConnection connection, string member, string? signature, BodyWriter body)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: "org.freedesktop.portal.Documents", path: "/org/freedesktop/portal/documents",
                @interface: "org.freedesktop.portal.Documents", member: member, signature: signature);
            body(ref writer);
            return writer.CreateMessage();
        }
        finally { writer.Dispose(); }
    }

    private static MessageBuffer StartDocuments(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus", @interface: "org.freedesktop.DBus",
            member: "StartServiceByName", signature: "su");
        writer.WriteString("org.freedesktop.portal.Documents");
        writer.WriteUInt32(0);
        return writer.CreateMessage();
    }

    private static long Inode(string path)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("stat", ["-c", "%i", path]) { RedirectStandardOutput = true })!;
        string text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "open", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    private static void Check(bool value, string scenario) { if (!value) throw new InvalidOperationException("Directory smoke failed: " + scenario); }

    private sealed class DirectoryPortal(DBusConnection connection, string uri) : IPathMethodHandler
    {
        public string Path => "/org/freedesktop/portal/desktop";
        public bool HandlesChildPaths => true;
        internal volatile bool Hold;
        internal bool Directory, Modal;
        internal volatile uint ResponseCode;
        internal string? Parent, Title, Method, CurrentName, SaveUri;
        // Handlers run on the bus connection's thread. A request's handler completes the Called it read when the request
        // arrived: one that finishes after its caller already went on and called Reset (#339) must not complete the next
        // request's Called, or the smoke cancels that request before it reaches the portal and waits for a Close that
        // never comes.
        private volatile TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Called => _called;
        internal TaskCompletionSource Closed => _closed;
        internal void Reset()
        { _called = new(TaskCreationOptions.RunContinuationsAsynchronously); _closed = new(TaskCreationOptions.RunContinuationsAsynchronously); }

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request.MemberAsString == "Get")
            {
                using var property = context.CreateReplyWriter("v");
                property.WriteVariant(VariantValue.UInt32(3));
                context.Reply(property.CreateMessage());
                return ValueTask.CompletedTask;
            }
            if (context.Request.MemberAsString == "Close")
            {
                using var close = context.CreateReplyWriter(null);
                context.Reply(close.CreateMessage());
                Closed.TrySetResult();
                return ValueTask.CompletedTask;
            }
            if (context.Request.InterfaceAsString != "org.freedesktop.portal.FileChooser" || context.Request.MemberAsString is not ("OpenFile" or "SaveFile"))
            {
                context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", "This test service implements FileChooser only.");
                return ValueTask.CompletedTask;
            }
            var called = _called;
            bool hold = Hold;
            uint responseCode = ResponseCode;
            Method = context.Request.MemberAsString;
            Modal = false;
            CurrentName = null;
            var reader = context.Request.GetBodyReader();
            Parent = reader.ReadString();
            Title = reader.ReadString();
            string token = "";
            var options = reader.ReadDictionaryStart();
            while (reader.HasNext(options))
            {
                string key = reader.ReadString();
                var value = reader.ReadVariantValue();
                if (key == "handle_token") token = value.GetString();
                if (key == "directory") Directory = value.GetBool();
                if (key == "modal") Modal = value.GetBool();
                if (key == "current_name") CurrentName = value.GetString();
            }
            string requestPath = Path + "/request/" + context.Request.SenderAsString![1..].Replace('.', '_') + "/" + token;
            using var reply = context.CreateReplyWriter("o");
            reply.WriteObjectPath(new ObjectPath(requestPath));
            context.Reply(reply.CreateMessage());
            if (!hold)
            {
                using var response = connection.GetMessageWriter();
                response.WriteSignalHeader(path: requestPath, @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
                response.WriteUInt32(responseCode);
                var results = response.WriteDictionaryStart();
                response.WriteDictionaryEntryStart();
                response.WriteString("uris");
                response.WriteVariant(VariantValue.Array(new[] { Method == "SaveFile" ? SaveUri! : uri }));
                response.WriteDictionaryEnd(results);
                connection.TrySendMessage(response.CreateMessage());
            }
            called.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
