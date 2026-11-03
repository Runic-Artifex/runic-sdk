using System.Text;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Views;
using TUnit.Core;

// blobs/1: the Window's upload store and blob registry (BridgeBlobs), below the hosts: the upload folder, uploads that
// fail or stall, a close during an upload, and how long blobs stay served.
internal sealed class BlobStoreChecks
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Everyone = Private | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    [Test]
    public async Task AWorldWritableBaseIsNotUsedAndTheWindowFolderIsPrivate()
    {
        if (OperatingSystem.IsWindows()) return;
        using var work = new Work();
        // A base that another user could change: world-writable without the sticky bit, with the former fixed root and a
        // link into the user's files planted in it.
        var hostile = work.Directory("hostile", Everyone);
        Directory.CreateDirectory(Path.Combine(hostile, "runic-uploads"));
        File.CreateSymbolicLink(Path.Combine(hostile, "runic-uploads", "planted"), work.Secret);
        var fallback = work.Directory("fallback", Private);
        using var blobs = Blobs(hostile, fallback);

        var upload = await Store(blobs, "content");
        var folder = blobs.UploadFolder!;
        Check(Path.GetDirectoryName(folder) == fallback, $"The upload folder is below the unsafe base: {folder}");
        Check(File.GetUnixFileMode(folder) == Private && Path.GetFileName(folder).StartsWith("runic-uploads-", StringComparison.Ordinal),
            $"The upload folder is not private: {File.GetUnixFileMode(folder)}");
        Check(Read(upload) == "content", "The upload was not stored.");

        // A sticky world-writable base, as /tmp, is safe; a base that is a link is not.
        var sticky = work.Directory("sticky", Everyone | UnixFileMode.StickyBit);
        Check(BridgeUploadFolder.IsSafeBase(sticky), "A sticky base was refused.");
        var link = Path.Combine(work.Root, "link");
        Directory.CreateSymbolicLink(link, fallback);
        Check(!BridgeUploadFolder.IsSafeBase(link) && !BridgeUploadFolder.IsSafeBase(hostile), "An unsafe base was accepted.");
        // No safe base at all: the upload fails, and frees its slot.
        using var none = Blobs(hostile, link);
        await Refused(none, "content");
        Check(none.UploadCounters == (0, 0), $"A failed folder kept the slot: {none.UploadCounters}");
    }

    [Test]
    public async Task AnUploadIsReadFromItsOwnFileWhenALinkReplacesItsPath()
    {
        using var work = new Work();
        using var blobs = Blobs(work.Directory("base", Private), null);
        var first = await Store(blobs, "uploaded bytes");
        var second = await Store(blobs, "second upload");
        if (!OperatingSystem.IsWindows())
        {
            foreach (var upload in new[] { first, second })
            {
                var path = Path.Combine(blobs.UploadFolder!, upload.Id);
                File.Delete(path);
                File.CreateSymbolicLink(path, work.Secret);
            }
        }
        Check(Read(first) == "uploaded bytes", "A link in place of the upload was followed.");
        var destination = Path.Combine(work.Root, "saved.txt");
        await second.SaveAsAsync(destination);
        Check(new FileInfo(destination).LinkTarget is null && await File.ReadAllTextAsync(destination) == "second upload",
            "SaveAsAsync moved the link instead of the upload.");
        Check(File.Exists(work.Secret) && await File.ReadAllTextAsync(work.Secret) == "secret", "The link's target was changed.");
        Check(!Directory.EnumerateFileSystemEntries(blobs.UploadFolder!).Any(), "A claimed upload left its file or link.");
    }

    [Test]
    public async Task AnUploadThatFailsFreesItsSlotItsBytesAndItsFile()
    {
        using var work = new Work();
        using var blobs = Blobs(work.Directory("base", Private), null);
        // More failures than the window receives at once: none may keep its slot.
        for (var attempt = 0; attempt < BridgeBlobs.MaximumConcurrentUploads + 4; attempt++)
        {
            Exception failure = attempt % 2 == 0 ? new System.Net.HttpListenerException(64, "The client went away.") : new InvalidOperationException("broken");
            await Refused(blobs, new FailingStream(failure));
        }
        Check(blobs.UploadCounters == (0, 0), $"Failed uploads kept slots or bytes: {blobs.UploadCounters}");
        Check(!Directory.EnumerateFileSystemEntries(blobs.UploadFolder!).Any(), "A failed upload left its partial file.");
        Check(Read(await Store(blobs, "after")) == "after", "An upload after the failures was refused.");
    }

    [Test]
    public async Task AStalledUploadIsDroppedAfterItsIdleTimeout()
    {
        using var work = new Work();
        var time = new FakeTimeProvider();
        using var blobs = Blobs(work.Directory("base", Private), null, time);
        var body = new StalledStream("start");
        var stored = blobs.StoreAsync(body, "slow.bin", null, "c1", CancellationToken.None);
        await body.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(blobs.UploadCounters.Receiving == 1, "The stalled upload has no slot.");
        time.Advance(BridgeBlobs.IdleTimeout - TimeSpan.FromSeconds(1));
        Check(!stored.IsCompleted, "The upload was dropped before its idle timeout.");
        time.Advance(TimeSpan.FromSeconds(1));
        var refused = false;
        try { await stored.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (InvalidOperationException error) when (error.Message.Contains("sent nothing", StringComparison.Ordinal)) { refused = true; }
        Check(refused, "A stalled upload was not dropped.");
        Check(blobs.UploadCounters == (0, 0) && !Directory.EnumerateFileSystemEntries(blobs.UploadFolder!).Any(),
            $"The dropped upload kept its slot or file: {blobs.UploadCounters}");
    }

    [Test]
    public async Task AWindowThatClosesDuringAnUploadKeepsItsCountersForTheNextSession()
    {
        using var work = new Work();
        var blobs = Blobs(work.Directory("base", Private), null);
        var body = new StalledStream("partial");
        var stored = blobs.StoreAsync(body, "a.bin", null, "c1", CancellationToken.None);
        await body.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        blobs.Dispose();
        // A WPF View that reopens on the same transport starts a new session on the same store.
        blobs.Configure(null, null);
        body.Fail(new IOException("The client went away."));
        try { await stored.WaitAsync(TimeSpan.FromSeconds(10)); } catch (InvalidOperationException) { }
        Check(blobs.UploadCounters == (0, 0), $"The closed session's upload changed the next session's counters: {blobs.UploadCounters}");
        var next = await Store(blobs, "next");
        Check(blobs.UploadCounters == (0, 4), $"The next session counts its upload wrong: {blobs.UploadCounters}");
        Check(Read(next) == "next" && blobs.UploadCounters == (0, 0), $"The next session cannot read its upload: {blobs.UploadCounters}");
        blobs.Dispose();
    }

    [Test]
    public async Task AnUploadNeverDecodesWithoutItsConnection()
    {
        using var work = new Work();
        using var blobs = Blobs(work.Directory("base", Private), null);
        var upload = await Store(blobs, "x");
        Check(blobs.Find(upload.Id, null) is null && blobs.Find(upload.Id, "c2") is null && blobs.Find(upload.Id, "c1") is not null,
            "An upload decoded without its connection.");
    }

    [Test]
    public void RouteHeldBlobsAreCappedAndABlobCanBeReleased()
    {
        using var blobs = Blobs(null, null);
        var log = new EventLog();
        blobs.Logger = log;
        blobs.MaximumRetainedBlobs = 3;
        var route = new object();
        var results = Enumerable.Range(0, 5).Select(index => RunicBlob.FromBytes(Encoding.UTF8.GetBytes($"r{index}"), "text/plain")).ToArray();
        string[] urls;
        using (BridgeBlobScope.Enter(blobs, route, null))
            urls = [.. results.Select(blob => blobs.Issue(blob, BridgeBlobScope.Current!))];
        Check(blobs.BlobCount == 3, $"The window keeps {blobs.BlobCount} route-held blobs.");
        // One warning for the request that released two blobs, with their number.
        Check(log.Messages.Count(message => message.Id == 1208) == 1 && log.Messages.Single(message => message.Id == 1208).Text.Contains("2 oldest", StringComparison.Ordinal),
            $"Evictions were logged as: {string.Join(" | ", log.Messages.Select(message => $"{message.Id} {message.Text}"))}");
        Check(!blobs.TryResolve(Token(urls[0]), out _) && !blobs.TryResolve(Token(urls[1]), out _) && blobs.TryResolve(Token(urls[4]), out _),
            "The oldest route-held blobs were not the ones released.");

        // A state member's blob is not route-held, and Release ends any blob's URL at once.
        var avatar = RunicBlob.FromBytes("a"u8.ToArray(), "image/png");
        string url;
        using (BridgeBlobScope.Enter(blobs, route, null))
        using (BridgeBlobScope.EnterSlot("avatar"))
            url = blobs.Issue(avatar, BridgeBlobScope.Current!);
        Check(blobs.TryResolve(Token(url), out _), "The avatar is not served.");
        avatar.Release();
        results[4].Release();
        Check(!blobs.TryResolve(Token(url), out _) && !blobs.TryResolve(Token(urls[4]), out _), "A released blob is still served.");
        using (BridgeBlobScope.Enter(blobs, route, null))
        using (BridgeBlobScope.EnterSlot("avatar"))
            Check(blobs.Issue(avatar, BridgeBlobScope.Current!) != url, "A released blob got its old URL again.");
    }

    [Test]
    public async Task ABlobKeepsTheHostsContentSecurityPolicy()
    {
        using var blobs = Blobs(null, null);
        var blob = RunicBlob.FromBytes("<svg/>"u8.ToArray(), "image/svg+xml");
        string url;
        using (BridgeBlobScope.Enter(blobs, new object(), null)) url = blobs.Issue(blob, BridgeBlobScope.Current!);
        var exchange = new RecordingExchange();
        exchange.AppendResponseHeader("Content-Security-Policy", "frame-ancestors 'self'");
        await blobs.ServeBlobAsync(Token(url), exchange);
        var policies = exchange.Headers.Where(header => header.Name == "Content-Security-Policy").Select(header => header.Value).ToArray();
        Check(exchange.StatusCode == 200 && policies.Length == 2 && policies[0] == "frame-ancestors 'self'" && policies[1].Contains("sandbox", StringComparison.Ordinal),
            $"The policies are {string.Join(" | ", policies)}");
    }

    [Test]
    public void TheSweepDeletesOnlyThisUsersFoldersUntouchedForADay()
    {
        using var work = new Work();
        var root = work.Directory("sweep", Private);
        var now = DateTime.UtcNow;
        string Folder(string name, DateTime changed, DateTime? file = null, Action<string>? fill = null)
        {
            var path = Path.Combine(root, name);
            Directory.CreateDirectory(path);
            var inner = Path.Combine(path, "upload");
            File.WriteAllText(inner, name);
            File.SetLastWriteTimeUtc(inner, file ?? changed);
            fill?.Invoke(path);
            Directory.SetLastWriteTimeUtc(path, changed);
            return path;
        }
        // Inside the stale folder, a link to a file outside it: deleting the folder deletes the link, not its target.
        var stale = Folder("runic-uploads-stale", now.AddDays(-2),
            fill: path => File.CreateSymbolicLink(Path.Combine(path, "planted"), work.Secret));
        var fresh = Folder("runic-uploads-fresh", now.AddHours(-23));
        var busy = Folder("runic-uploads-busy", now.AddDays(-2), file: now.AddMinutes(-5));
        var other = Folder("other-folder", now.AddDays(-2));
        // A link of the prefix's name to a folder of the user's that nothing changed for long: never followed.
        var target = work.Directory("target", Private);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        Directory.SetLastWriteTimeUtc(target, now.AddDays(-5));
        var link = Path.Combine(root, "runic-uploads-link");
        Directory.CreateSymbolicLink(link, target);

        BridgeUploadFolder.Sweep(root, now, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Check(!Directory.Exists(stale), "A folder untouched for two days was kept.");
        Check(Directory.Exists(fresh) && Directory.Exists(busy) && Directory.Exists(other), "The sweep deleted a folder it must keep.");
        Check(new DirectoryInfo(link).LinkTarget is not null && File.Exists(Path.Combine(target, "keep.txt")), "The sweep followed a link.");
        Check(File.ReadAllText(work.Secret) == "secret", "The sweep deleted through a link inside a folder.");
    }

    [Test]
    public async Task ABaseOrFolderOfAnotherUserIsNotUsed()
    {
        if (OperatingSystem.IsWindows()) return;
        using var work = new Work();
        var own = work.Directory("own", Private);
        Check(BridgeUploadFolder.LinkStatus(own) is { IsDirectory: true } status && status.Owner == BridgeUploadFolder.EffectiveUser,
            "lstat does not report the owner.");
        Check(BridgeUploadFolder.IsSafeBase(own), "This user's private base was refused.");
        // The root directory is root's and not writable by others: a safe base by its owner and mode.
        Check(BridgeUploadFolder.IsSafeBase("/"), "A root-owned base was refused.");
        var file = Path.Combine(work.Root, "file");
        File.WriteAllText(file, "");
        Check(!BridgeUploadFolder.IsSafeBase(file), "A file was accepted as a base.");

        // The same directories, seen by another effective user, belong to someone else.
        using (BridgeUploadFolder.ActAsUser(4_242_424))
        {
            Check(!BridgeUploadFolder.IsSafeBase(own), "Another user's base was accepted.");
            using var blobs = Blobs(own, own);
            await Refused(blobs, "content");
            Check(blobs.UploadCounters == (0, 0), $"A refused folder kept the slot: {blobs.UploadCounters}");
        }

        // mkdtemp: a new 0700 folder of this user for each window.
        using var first = Blobs(own, null);
        using var second = Blobs(own, null);
        await Store(first, "a");
        await Store(second, "b");
        Check(first.UploadFolder != second.UploadFolder && BridgeUploadFolder.IsPrivateDirectory(first.UploadFolder!)
            && BridgeUploadFolder.IsPrivateDirectory(second.UploadFolder!), "The windows' folders are not new and private.");
        using (BridgeUploadFolder.ActAsUser(4_242_424))
            Check(!BridgeUploadFolder.IsPrivateDirectory(first.UploadFolder!), "A folder of another user counted as private.");
    }

    [Test]
    public async Task OnWindowsTheWindowFolderGrantsOnlyTheUserSystemAndAdministrators()
    {
        if (OperatingSystem.IsWindows()) await CheckWindowsFolderAclAsync();
    }

    // The platform analyzer does not carry an IsWindows guard into lambdas, so the check lives in a Windows-only method.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task CheckWindowsFolderAclAsync()
    {
        using var work = new Work();
        using var blobs = Blobs(work.Directory("base", Private), null);
        await Store(blobs, "content");
        var security = new DirectoryInfo(blobs.UploadFolder!).GetAccessControl();
        var allowed = new[]
        {
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToArray();
        Check(security.AreAccessRulesProtected && rules.All(rule => !rule.IsInherited && allowed.Contains(rule.IdentityReference))
            && allowed.All(identity => rules.Any(rule => rule.IdentityReference == identity)),
            $"The folder's ACL is {string.Join(", ", rules.Select(rule => $"{rule.IdentityReference}:{rule.FileSystemRights}:{rule.IsInherited}"))}");
    }

    private static BridgeBlobs Blobs(string? temporaryBase, string? fallbackBase, TimeProvider? time = null)
    {
        var blobs = new BridgeBlobs { UploadBase = temporaryBase, UploadFallbackBase = fallbackBase, TimeProvider = time ?? TimeProvider.System };
        blobs.Attach(BridgeBlobEndpoint.Below(static () => new Uri("http://blobs.test/")));
        return blobs;
    }

    private static Task<RunicUpload> Store(BridgeBlobs blobs, string content) =>
        blobs.StoreAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)), "file.txt", "text/plain", "c1", CancellationToken.None);

    private static async Task Refused(BridgeBlobs blobs, object body)
    {
        var stream = body as Stream ?? new MemoryStream(Encoding.UTF8.GetBytes((string)body));
        try
        {
            await blobs.StoreAsync(stream, "file.txt", "text/plain", "c1", CancellationToken.None);
        }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("The upload was stored.");
    }

    private static string Read(RunicUpload upload)
    {
        using var stream = upload.OpenReadAsync().AsTask().GetAwaiter().GetResult();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Token(string url) => url[(url.LastIndexOf('/') + 1)..];

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // A test's own directory with a secret file outside the upload folders.
    private sealed class Work : IDisposable
    {
        internal Work()
        {
            Root = System.IO.Directory.CreateTempSubdirectory("runic-blob-store-").FullName;
            Secret = Path.Combine(Root, "secret.txt");
            File.WriteAllText(Secret, "secret");
        }

        internal string Root { get; }
        internal string Secret { get; }

        internal string Directory(string name, UnixFileMode mode)
        {
            var path = Path.Combine(Root, name);
            System.IO.Directory.CreateDirectory(path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
            return path;
        }

        public void Dispose()
        {
            foreach (var directory in System.IO.Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories))
                if (!OperatingSystem.IsWindows() && new DirectoryInfo(directory).LinkTarget is null) File.SetUnixFileMode(directory, Private);
            System.IO.Directory.Delete(Root, recursive: true);
        }
    }

    // Sends a few bytes, then fails as a client that goes away does.
    private sealed class FailingStream(Exception failure) : Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent) throw failure;
            _sent = true;
            "partial"u8.CopyTo(buffer.Span);
            return new(7);
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Sends its first bytes, then neither data nor an end, and ignores cancellation, as a stalled client's stream can.
    private sealed class StalledStream(string first) : Stream
    {
        private readonly TaskCompletionSource<int> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _sent;
        internal TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        internal void Fail(Exception error) => _never.TrySetException(error);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                var bytes = Encoding.UTF8.GetBytes(first);
                bytes.CopyTo(buffer.Span);
                return new(bytes.Length);
            }
            Stalled.TrySetResult();
            return new(_never.Task);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // The events a store logs, by id.
    private sealed class EventLog : Microsoft.Extensions.Logging.ILogger
    {
        internal List<(int Id, string Text)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add((eventId.Id, formatter(state, exception)));
        }
    }

    private sealed class RecordingExchange : IBridgeBlobExchange
    {
        internal List<(string Name, string Value)> Headers { get; } = [];
        public string Method => "GET";
        public string GetRequestHeader(string name) => string.Empty;
        public Stream RequestBody => Stream.Null;
        public int StatusCode { get; set; } = 200;
        public string? ContentType { set { } }
        public long? ContentLength { set { } }
        public void SetResponseHeader(string name, string value)
        {
            Headers.RemoveAll(header => header.Name == name);
            Headers.Add((name, value));
        }
        public void AppendResponseHeader(string name, string value) => Headers.Add((name, value));
        public Stream ResponseBody { get; } = new MemoryStream();
        public CancellationToken RequestAborted => CancellationToken.None;
    }
}
