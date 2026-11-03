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
        blobs.MaximumRetainedBlobs = 3;
        var route = new object();
        var results = Enumerable.Range(0, 5).Select(index => RunicBlob.FromBytes(Encoding.UTF8.GetBytes($"r{index}"), "text/plain")).ToArray();
        string[] urls;
        using (BridgeBlobScope.Enter(blobs, route, null))
            urls = [.. results.Select(blob => blobs.Issue(blob, BridgeBlobScope.Current!))];
        Check(blobs.BlobCount == 3, $"The window keeps {blobs.BlobCount} route-held blobs.");
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
