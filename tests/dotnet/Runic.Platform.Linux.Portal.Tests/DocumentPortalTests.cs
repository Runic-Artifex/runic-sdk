using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Runic.Platform.Runtime;
using Tmds.DBus.Protocol;
using TUnit.Core;
using TUnit.Core.Enums;

// Saves through the real xdg-document-portal FUSE view a Flatpak application gets
// (<mount>/by-app/<app-id>/<doc-id>/<name>), with the production picker, location
// classification and leases. Only the FileChooser response is supplied by the test.
// Every document added here is deleted again. Without a document portal or FUSE
// this reports a skip with its reason, never a pass.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed partial class DocumentPortalTests
{
    private const string AppId = "org.runic.PortalTests";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test, RunOn(OS.Linux), DisplayName("document portal atomic save")]
    public async Task AtomicSaveAsync()
    {
        var owner = new Owner();
        if (!File.Exists("/dev/fuse")) { Skip("/dev/fuse is absent"); return; }
        if (DBusAddress.Session is not { } address) { Skip("no session bus"); return; }
        using var connection = new DBusConnection(address);
        await connection.ConnectAsync().AsTask().WaitScaledAsync(Timeout);
        if (!await connection.CallMethodAsync(Bus(connection, "NameHasOwner"), static (message, _) => message.GetBodyReader().ReadBool()).WaitScaledAsync(Timeout))
        {
            // An isolated bus (dbus-run-session) may activate its own document portal,
            // but never one that would replace the desktop session's mount.
            if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { } runtime && DocumentPortalPaths.IsFuse(Path.Combine(runtime, "doc")))
            { Skip("another session's document portal owns the mount; run on that session bus"); return; }
            // Not scaled: a portal that cannot be activated in 10 s skips the test.
            try { await connection.CallMethodAsync(Bus(connection, "StartServiceByName")).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) when (error is DBusExceptionBase or TimeoutException) { Skip("the document portal cannot be activated: " + error.GetType().Name); return; }
        }
        string? mount = await DocumentPortalPaths.QueryMountPointAsync(default);
        if (mount is null) { Skip("org.freedesktop.portal.Documents.GetMountPoint is unavailable"); return; }
        uint version = await connection.CallMethodAsync(GetVersion(connection), static (message, _) => message.GetBodyReader().ReadVariantValue().GetUInt32()).WaitScaledAsync(Timeout);
        if (version < 4) { Skip($"Documents version {version} has no AddNamedFull"); return; }

        string root = Path.Combine(Path.GetTempPath(), "runic-document-portal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var documents = new List<string>();
        try
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
            string target = Path.Combine(root, "save-target.txt");
            await File.WriteAllTextAsync(target, "original");
            File.SetUnixFileMode(target, mode);
            File.CreateSymbolicLink(Path.Combine(root, "link.txt"), target);
            string[] listing = ["link.txt", "save-target.txt"];

            string existing = await AddAsync(connection, mount, root, "save-target.txt", documents);
            Check(await DocumentPortalPaths.Session.ClassifyAsync(existing, default) == NativeFileLocation.DocumentPortal,
                "the real document view is classified from GetMountPoint and FUSE");
            long inode = Inode(target);
            Check(await SaveAsync(owner, existing, "replaced") is FileCommitResult.Committed, "document save commits");
            Check(await File.ReadAllTextAsync(target) == "replaced" && Inode(target) != inode && File.GetUnixFileMode(target) == mode,
                "the host file is replaced atomically with its mode kept");
            Check(Listing(root).SequenceEqual(listing), "no .xdp-* or .runic-* file remains on the host");
            Check(await SaveAsync(owner, existing, "again") is FileCommitResult.Committed && await File.ReadAllTextAsync(target) == "again",
                "the document survives its replacement");

            string created = await AddAsync(connection, mount, root, "new.txt", documents);
            Check(await SaveAsync(owner, created, "created") is FileCommitResult.Committed && await File.ReadAllTextAsync(Path.Combine(root, "new.txt")) == "created",
                "a new-file document commits with RENAME_NOREPLACE");
            listing = ["link.txt", "new.txt", "save-target.txt"];

            // The view shows a host symlink as absent. No-replace keeps it.
            string link = await AddAsync(connection, mount, root, "link.txt", documents);
            Check(await SaveAsync(owner, link, "through link") is FileCommitResult.NotCommitted { Code: PlatformFailureCode.Conflict },
                "a hidden host symlink is a conflict");
            Check(new FileInfo(Path.Combine(root, "link.txt")).LinkTarget == target && await File.ReadAllTextAsync(target) == "again" && Listing(root).SequenceEqual(listing),
                "the host symlink and its target are intact");

            // An abandoned transaction removes its portal tempfile.
            await using (var lease = await SelectAsync(owner, created))
            {
                var begun = await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace);
                Check(begun is PlatformResult<IFileWriteTransaction>.Success, "staging in the document view");
                await ((PlatformResult<IFileWriteTransaction>.Success)begun).Value.Content.WriteAsync("abandoned"u8.ToArray());
            }
            Check(Listing(root).SequenceEqual(listing) && await File.ReadAllTextAsync(Path.Combine(root, "new.txt")) == "created", "an abort leaves no host file");

            // A document app: open the document view, save it back three times and reopen it, see a host write as a
            // conflict, overwrite after reading, and reopen it from its bookmark (the view's path) through the view.
            await File.WriteAllTextAsync(Path.Combine(root, "document.txt"), "original");
            listing = ["document.txt", "link.txt", "new.txt", "save-target.txt"];
            string view = await AddAsync(connection, mount, root, "document.txt", documents);
            string host = Path.Combine(root, "document.txt");
            System.Collections.Immutable.ImmutableArray<byte> bookmark;
            await using (var document = await OpenDocumentAsync(owner, view))
            {
                Check(document.LocalPath == view && await ReadAsync(document) == "original", "the opened document view reads the host file");
                for (int index = 1; index <= 3; index++)
                {
                    Check(await WriteAsync(document, $"save {index}") is FileCommitResult.Committed && await File.ReadAllTextAsync(host) == $"save {index}",
                        $"document save {index} replaces the host file");
                    Check(await ReadAsync(document) == $"save {index}", $"reopen {index} through the view");
                }
                await File.WriteAllTextAsync(host, "host edit");
                Check(await WriteAsync(document, "lost") is FileCommitResult.NotCommitted { Code: PlatformFailureCode.Conflict } && await File.ReadAllTextAsync(host) == "host edit",
                    "a host write is a conflict");
                Check(await ReadAsync(document) == "host edit" && await WriteAsync(document, "overwrite") is FileCommitResult.Committed
                    && await File.ReadAllTextAsync(host) == "overwrite", "read then write overwrites");
                var bookmarked = await document.CreateBookmarkAsync();
                bookmark = (bookmarked as PlatformResult<System.Collections.Immutable.ImmutableArray<byte>>.Success)?.Value ?? throw new InvalidOperationException("bookmark: " + bookmarked);
                Check(System.Text.Encoding.UTF8.GetString(bookmark.AsSpan()) == view, "the bookmark is the document view's path");
            }
            var reopened = await new PathFileBookmarks(DocumentPortalPaths.Session.ClassifyAsync).OpenDocumentAsync(bookmark);
            await using (var document = (reopened as PlatformResult<IDocumentFileLease>.Success)?.Value ?? throw new InvalidOperationException("reopen: " + reopened))
                Check(await ReadAsync(document) == "overwrite" && await WriteAsync(document, "from bookmark") is FileCommitResult.Committed
                    && await File.ReadAllTextAsync(host) == "from bookmark", "the bookmark reopens and saves the document view");
            Check(Listing(root).SequenceEqual(listing), "document saves leave no .xdp-* or .runic-* file on the host");

            await CallAsync(connection, "RevokePermissions", (ref MessageWriter writer) => { writer.WriteString(Id(existing)); writer.WriteString(AppId); writer.WriteArray(Write); });
            await using (var lease = await SelectAsync(owner, existing))
                Check(await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace) is PlatformResult<IFileWriteTransaction>.Unavailable { Reason: PlatformUnavailableReason.AtomicReplaceUnavailable },
                    "a revoked write grant reports AtomicReplaceUnavailable");
            Check(await File.ReadAllTextAsync(target) == "again" && Listing(root).SequenceEqual(listing), "a refused save changes nothing on the host");
            Console.WriteLine($"PASS document portal {version} atomic save: replace, new file, hidden symlink, abort, document saves, bookmark and revoked grant.");
        }
        finally
        {
            foreach (string id in documents)
            {
                try { await CallAsync(connection, "Delete", (ref MessageWriter writer) => writer.WriteString(id)); }
                catch (Exception error) { Console.Error.WriteLine($"Could not delete test document {id}: {error.Message}"); }
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<ISaveFileLease> SelectAsync(IPortalWindowOwner owner, string documentPath)
    {
        var backend = new NativePickerBackend(owner, new PortalFilePicker(owner, new UriTransport(new Uri(documentPath).AbsoluteUri)));
        var selected = await backend.SaveFileAsync(new(Path.GetFileName(documentPath)));
        return selected is PickerResult<ISaveFileLease>.Selected(var lease) ? lease : throw new InvalidOperationException("Document selection failed: " + selected);
    }

    private static async Task<IDocumentFileLease> OpenDocumentAsync(IPortalWindowOwner owner, string documentPath)
    {
        var backend = new NativePickerBackend(owner, new PortalFilePicker(owner, new UriTransport(new Uri(documentPath).AbsoluteUri)));
        var selected = await backend.OpenDocumentAsync(new());
        return selected is PickerResult<IDocumentFileLease>.Selected(var lease) ? lease : throw new InvalidOperationException("Document open failed: " + selected);
    }

    private static async Task<string> ReadAsync(IDocumentFileLease document)
    {
        var read = await document.OpenReadAsync();
        if (read is not PlatformResult<Stream>.Success(var stream)) throw new InvalidOperationException("Document read failed: " + read);
        await using (stream)
        using (var reader = new StreamReader(stream))
            return await reader.ReadToEndAsync();
    }

    private static async Task<FileCommitResult> WriteAsync(ISaveFileLease lease, string text)
    {
        var begun = await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace);
        if (begun is not PlatformResult<IFileWriteTransaction>.Success(var transaction)) throw new InvalidOperationException("Document staging failed: " + begun);
        await using (transaction)
        {
            await transaction.Content.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text));
            return await transaction.CommitAsync();
        }
    }

    private static async Task<FileCommitResult> SaveAsync(IPortalWindowOwner owner, string documentPath, string text)
    {
        await using var lease = await SelectAsync(owner, documentPath);
        var begun = await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace);
        if (begun is not PlatformResult<IFileWriteTransaction>.Success(var transaction)) throw new InvalidOperationException("Document staging failed: " + begun);
        await using (transaction)
        {
            await transaction.Content.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text));
            return await transaction.CommitAsync();
        }
    }

    // A transient (non-persistent) document granting this test application read and write.
    private static async Task<string> AddAsync(DBusConnection connection, string mount, string directory, string name, List<string> documents)
    {
        using var parent = new SafeFileHandle(Open(directory, 0x200000 | 0x80000), ownsHandle: true); // O_PATH | O_CLOEXEC
        if (parent.IsInvalid) throw new IOException("Cannot open the test directory.");
        string id = await connection.CallMethodAsync(Message(connection, "AddNamedFull", "hayusas", (ref MessageWriter writer) =>
        {
            writer.WriteHandle(parent);
            writer.WriteArray(System.Text.Encoding.UTF8.GetBytes(name + "\0"));
            writer.WriteUInt32(0);
            writer.WriteString(AppId);
            writer.WriteArray(ReadWrite);
        }), static (message, _) => message.GetBodyReader().ReadString()).WaitScaledAsync(Timeout);
        documents.Add(id);
        return Path.Combine(mount, "by-app", AppId, id, name);
    }

    private delegate void BodyWriter(ref MessageWriter writer);
    private static readonly string[] ReadWrite = ["read", "write"], Write = ["write"];

    private static string Id(string documentPath) => Path.GetFileName(Path.GetDirectoryName(documentPath)!);

    private static Task CallAsync(DBusConnection connection, string member, BodyWriter body) =>
        connection.CallMethodAsync(Message(connection, member, member == "Delete" ? "s" : "ssas", body)).WaitScaledAsync(Timeout);

    private static MessageBuffer Message(DBusConnection connection, string member, string signature, BodyWriter body)
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

    private static MessageBuffer Bus(DBusConnection connection, string member)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus", @interface: "org.freedesktop.DBus",
            member: member, signature: member == "NameHasOwner" ? "s" : "su");
        writer.WriteString("org.freedesktop.portal.Documents");
        if (member != "NameHasOwner") writer.WriteUInt32(0);
        return writer.CreateMessage();
    }

    private static MessageBuffer GetVersion(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.portal.Documents", path: "/org/freedesktop/portal/documents",
            @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss");
        writer.WriteString("org.freedesktop.portal.Documents");
        writer.WriteString("version");
        return writer.CreateMessage();
    }

    private static string[] Listing(string directory) =>
        [.. Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions { AttributesToSkip = 0 }).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    private static long Inode(string path)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("stat", ["-c", "%i", path]) { RedirectStandardOutput = true })!;
        string text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Skip(string reason) => TUnit.Core.Skip.Test("document portal atomic save: " + reason + ".");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    private sealed class UriTransport(string uri) : IPortalTransport
    {
        public ValueTask<PortalResponse> RequestAsync(string parent, string method, string argument, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PortalResponse(0, [uri]));
    }
}
