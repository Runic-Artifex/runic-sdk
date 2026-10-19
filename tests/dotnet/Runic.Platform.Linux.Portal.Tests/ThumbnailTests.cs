using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Runic.Platform;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Linux.Portal;
using TUnit.Core;

// Thumbnails from a temporary thumbnail cache, never the user's, and from a fake Thumbnailer1 on the private session bus
// of dbus-run-session.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class ThumbnailTests
{
    // gio info prints this URI for the same path (GLib 2.88): ';' and the characters outside RFC 3986's path set are escaped.
    [Test, DisplayName("thumbnails: the cache key is the file: URI as GIO escapes it, hashed with MD5")]
    public void CacheKey()
    {
        Check(LinuxThumbnails.FileUri("/tmp/x/a b!$&'()*+,;=:@~_-.[]{}|^`\"#%<>?ä€")
            == "file:///tmp/x/a%20b!$&'()*+,%3B=:@~_-.%5B%5D%7B%7D%7C%5E%60%22%23%25%3C%3E%3F%C3%A4%E2%82%AC", "GIO's escaping");
        Check(LinuxThumbnails.FileUri("/home/jens/photos/me.png") == "file:///home/jens/photos/me.png", "a plain path");
        // The specification's example.
        Check(LinuxThumbnails.CacheName("file:///home/jens/photos/me.png") == "c6ee772d9e49320e97ec29a7eb5b1697.png", "the specification's example");
        var random = new Random(3);
        for (int length = 0; length < 300; length++)
        {
            var data = new byte[length];
            random.NextBytes(data);
#pragma warning disable CA5351 // The cache's key is MD5; OpenSSL's checks the managed one.
            Check(Md5.Hash(data).AsSpan().SequenceEqual(System.Security.Cryptography.MD5.HashData(data)), $"MD5 of {length} bytes");
#pragma warning restore CA5351
        }
    }

    [Test, DisplayName("thumbnails: the smallest valid flavour at least as large as the request, else the largest smaller one")]
    public async Task FlavourAsync()
    {
        using var cache = new Cache();
        string file = cache.Source("photo.png");
        var thumbnails = cache.CacheOnly();
        Check(await thumbnails.GetThumbnailAsync(file, 64) is PlatformResult<DesktopThumbnail?>.Success(null), "nothing cached");
        cache.Entry("normal", file, 128);
        cache.Entry("large", file, 256);
        cache.Entry("xx-large", file, 1024);
        Check(Size(await thumbnails.GetThumbnailAsync(file, 1)) == 128, "1 px");
        Check(Size(await thumbnails.GetThumbnailAsync(file, 128)) == 128, "128 px");
        Check(Size(await thumbnails.GetThumbnailAsync(file, 129)) == 256, "129 px");
        // x-large is missing: the next larger one.
        Check(Size(await thumbnails.GetThumbnailAsync(file, 300)) == 1024, "300 px");
        File.Delete(cache.EntryPath("xx-large", file));
        // Nothing large enough and no thumbnailer: the largest smaller one.
        Check(Size(await thumbnails.GetThumbnailAsync(file, 1024)) == 256, "1024 px without xx-large");
        var thumbnail = ((PlatformResult<DesktopThumbnail?>.Success)await thumbnails.GetThumbnailAsync(file, 200)).Value!;
        Check(thumbnail.Png.AsSpan().SequenceEqual(File.ReadAllBytes(cache.EntryPath("large", file))) && thumbnail.Height == 128, "the cache file's bytes and size");
        Check(LinuxThumbnails.Flavor(1) == "normal" && LinuxThumbnails.Flavor(128) == "normal" && LinuxThumbnails.Flavor(129) == "large"
            && LinuxThumbnails.Flavor(512) == "x-large" && LinuxThumbnails.Flavor(1024) == "xx-large", "the flavour a thumbnailer is asked for");
    }

    [Test, DisplayName("thumbnails: an entry counts only for this version of the file")]
    public async Task ValidationAsync()
    {
        using var cache = new Cache();
        string file = cache.Source("notes.pdf");
        var thumbnails = cache.CacheOnly();
        var info = new FileInfo(file);
        long mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
        async Task<bool> Found() => await thumbnails.GetThumbnailAsync(file, 128) is PlatformResult<DesktopThumbnail?>.Success({ });

        cache.Entry("normal", file, 128, mtime: (mtime - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Check(!await Found(), "an older modification time");
        cache.Entry("normal", file, 128, mtime: null);
        Check(!await Found(), "no modification time");
        cache.Entry("normal", file, 128, uri: LinuxThumbnails.FileUri(file + "x"));
        Check(!await Found(), "another file's URI");
        cache.Entry("normal", file, 128, size: (info.Length + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Check(!await Found(), "another size");
        cache.Entry("normal", file, 128, size: info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Check(await Found(), "the same size");
        cache.Entry("normal", file, 128, mtime: mtime.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".75");
        Check(await Found(), "a fractional modification time of the same second");
        cache.Entry("normal", file, 128, international: true);
        Check(await Found(), "keys in iTXt chunks");
        File.WriteAllBytes(cache.EntryPath("normal", file), Cache.Png(128, 128, [("Thumb::URI", LinuxThumbnails.FileUri(file)), ("Thumb::MTime", mtime.ToString(System.Globalization.CultureInfo.InvariantCulture))])[..40]);
        Check(!await Found(), "a truncated file");
        File.WriteAllText(cache.EntryPath("normal", file), "not a PNG");
        Check(!await Found(), "not a PNG");

        // Changing the file invalidates its thumbnail.
        cache.Entry("normal", file, 128);
        Check(await Found(), "valid");
        File.SetLastWriteTimeUtc(file, info.LastWriteTimeUtc.AddMinutes(1));
        Check(!await Found(), "after the file changed");
    }

    [Test, DisplayName("thumbnails: a failure entry for this version of the file means no thumbnail")]
    public async Task FailureAsync()
    {
        using var cache = new Cache();
        string file = cache.Source("movie.mkv");
        var thumbnails = cache.CacheOnly();
        cache.Entry("normal", file, 128);
        cache.Entry(Path.Combine("fail", "gnome-thumbnail-factory"), file, 1);
        Check(Size(await thumbnails.GetThumbnailAsync(file, 64)) == 128, "a valid thumbnail wins over a failure entry");
        Check(await thumbnails.GetThumbnailAsync(file, 256) is PlatformResult<DesktopThumbnail?>.Success(null), "the failure, not the smaller thumbnail");
        long mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds();
        cache.Entry(Path.Combine("fail", "gnome-thumbnail-factory"), file, 1, mtime: (mtime - 10).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Check(Size(await thumbnails.GetThumbnailAsync(file, 256)) == 128, "a failure for an older version does not count");
    }

    [Test, DisplayName("thumbnails: paths, links, directories and missing files")]
    public async Task PathsAsync()
    {
        using var cache = new Cache();
        var thumbnails = cache.CacheOnly();
        // The cache is keyed by the escaped URI of the path as given.
        string odd = cache.Source("a b;#%ä.png");
        cache.Entry("normal", odd, 128);
        Check(File.Exists(Path.Combine(cache.Directory, "normal", LinuxThumbnails.CacheName(
            "file://" + cache.Root + "/source/a%20b%3B%23%25%C3%A4.png"))), "the entry is where GIO puts it");
        Check(Size(await thumbnails.GetThumbnailAsync(odd, 128)) == 128, "a name that needs escaping");
        // A link has its own entry, with the target's modification time and size.
        string link = Path.Combine(cache.Root, "source", "link.png");
        File.CreateSymbolicLink(link, odd);
        File.SetLastWriteTimeUtc(odd, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        cache.Entry("normal", link, 128, mtime: new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Check(Size(await thumbnails.GetThumbnailAsync(link, 128)) == 128, "a link");
        Check(await thumbnails.GetThumbnailAsync(Path.Combine(cache.Root, "source"), 128) is PlatformResult<DesktopThumbnail?>.Success(null), "a directory");
        Check(await thumbnails.GetThumbnailAsync(Path.Combine(cache.Root, "missing.png"), 128) is PlatformResult<DesktopThumbnail?>.Failed { Code: PlatformFailureCode.NotFound },
            "a missing file");
        string dangling = Path.Combine(cache.Root, "dangling.png");
        File.CreateSymbolicLink(dangling, Path.Combine(cache.Root, "nothing"));
        Check(await thumbnails.GetThumbnailAsync(dangling, 128) is PlatformResult<DesktopThumbnail?>.Failed { Code: PlatformFailureCode.NotFound }, "a dangling link");
        Check(await Catch(() => thumbnails.GetThumbnailAsync("relative.png", 128).AsTask()) is ArgumentException, "a relative path");
        Check(await Catch(() => thumbnails.GetThumbnailAsync(odd, 0).AsTask()) is ArgumentOutOfRangeException
            && await Catch(() => thumbnails.GetThumbnailAsync(odd, 1025).AsTask()) is ArgumentOutOfRangeException, "a size out of range");
        var unavailable = DesktopThumbnails.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        Check(await unavailable.GetThumbnailAsync(odd, 128) is PlatformResult<DesktopThumbnail?>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured)
            && await Catch(() => unavailable.GetThumbnailAsync(odd, 0).AsTask()) is ArgumentOutOfRangeException
            && await Catch(() => unavailable.GetThumbnailAsync("relative", 1).AsTask()) is ArgumentException, "the unavailable provider checks as a provider does");
        Check(Catch(() => _ = new DesktopThumbnail([], 1, 1)) is ArgumentException && Catch(() => _ = new DesktopThumbnail([1], 0, 1)) is ArgumentOutOfRangeException,
            "a thumbnail checks its values");
    }

    [Test, DisplayName("thumbnails: inside a sandbox a missing cache is diagnosed once")]
    public async Task SandboxAsync()
    {
        using var cache = new Cache();
        var diagnostics = new List<PortalDiagnostic>();
        var thumbnails = new LinuxThumbnails(cache.Options() with { CacheDirectory = Path.Combine(cache.Root, "nothing"), Sandboxed = true },
            new PortalApplication(null, diagnostics.Add));
        string file = cache.Source("photo.png");
        Check(await thumbnails.GetThumbnailAsync(file, 128) is PlatformResult<DesktopThumbnail?>.Success(null)
            && await thumbnails.GetThumbnailAsync(file, 128) is PlatformResult<DesktopThumbnail?>.Success(null), "no cache");
        Check(diagnostics.Count(static d => d.Code == "thumbnail-cache-unavailable") == 1 && !diagnostics.Any(d => d.Message.Contains(file, StringComparison.Ordinal)),
            $"diagnostics: {string.Join(", ", diagnostics.Select(static d => d.Code))}");
    }

    [Test, Explicit, Category("DBus"), DisplayName("thumbnails: a miss asks the thumbnailer for the flavour and reads what it wrote")]
    public async Task ThumbnailerAsync()
    {
        using var cache = new Cache();
        await using var fake = await FakeThumbnailer.StartAsync();
        var thumbnails = cache.WithThumbnailer();
        string file = cache.Source("photo.png");
        // The thumbnailer writes the entry and reports it, with another client's job first, before its reply.
        fake.OnQueue = (handle, uri) =>
        {
            cache.Entry("large", file, 256);
            fake.Emit("Ready", DBusValue.From(handle + 1000), DBusValue.StringArray([uri]));
            fake.Emit("Ready", DBusValue.From(handle), DBusValue.StringArray([uri]));
            fake.Emit("Finished", DBusValue.From(handle));
        };
        Check(Size(await thumbnails.GetThumbnailAsync(file, 200)) == 256, "the generated thumbnail");
        Check(fake.Calls.SequenceEqual([$"Queue {LinuxThumbnails.FileUri(file)} image/png large foreground 0"]), $"calls: {string.Join("; ", fake.Calls)}");
        Check(Size(await thumbnails.GetThumbnailAsync(file, 200)) == 256 && fake.Calls.Count == 1, "a cached thumbnail asks nothing");

        // A thumbnailer error leaves no thumbnail; the smaller cached one is returned.
        string other = cache.Source("other.png");
        cache.Entry("normal", other, 128);
        fake.OnQueue = (handle, uri) =>
        {
            fake.Emit("Error", DBusValue.From(handle), DBusValue.StringArray([uri]), DBusValue.From(1), DBusValue.From("Unsupported"));
            fake.Emit("Finished", DBusValue.From(handle));
        };
        Check(Size(await thumbnails.GetThumbnailAsync(other, 1024)) == 128 && fake.Calls[^1].Contains("xx-large", StringComparison.Ordinal), "an error");
        // A failure entry the thumbnailer writes means none.
        string broken = cache.Source("broken.png");
        cache.Entry("normal", broken, 128);
        fake.OnQueue = (handle, uri) =>
        {
            cache.Entry(Path.Combine("fail", "tumbler"), broken, 1);
            fake.Emit("Error", DBusValue.From(handle), DBusValue.StringArray([uri]), DBusValue.From(0), DBusValue.From("Failed"));
        };
        Check(await thumbnails.GetThumbnailAsync(broken, 256) is PlatformResult<DesktopThumbnail?>.Success(null), "a failure entry");
        Check(fake.Dequeued.IsEmpty, "a reported job is not withdrawn");
    }

    [Test, Explicit, Category("DBus"), DisplayName("thumbnails: a thumbnailer that does not answer is withdrawn after the timeout or on cancellation")]
    public async Task UnansweredAsync()
    {
        using var cache = new Cache();
        await using var fake = await FakeThumbnailer.StartAsync();
        var thumbnails = cache.WithThumbnailer(TimeSpan.FromMilliseconds(500));
        string file = cache.Source("slow.png");
        cache.Entry("normal", file, 128);
        fake.OnQueue = (_, _) => { };
        var started = DateTime.UtcNow;
        Check(Size(await thumbnails.GetThumbnailAsync(file, 512)) == 128 && DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "the smaller one after the timeout");
        await Until(() => fake.Dequeued.Count == 1);
        Check(fake.Dequeued[0] == fake.LastHandle, "the job was withdrawn");

        using var cancel = new CancellationTokenSource();
        fake.OnQueue = (_, _) => cancel.CancelAfter(100);
        Check(await Catch(() => thumbnails.GetThumbnailAsync(file, 512, cancel.Token).AsTask()) is OperationCanceledException, "cancelled");
        await Until(() => fake.Dequeued.Count == 2);

        // The thumbnailer quitting ends the wait.
        fake.OnQueue = (_, _) => _ = Task.Run(fake.StopAsync);
        var slow = new LinuxThumbnails(cache.Options() with { BusAddress = DBusAddress(), Service = FakeThumbnailer.Name, Timeout = TimeSpan.FromMinutes(1) });
        started = DateTime.UtcNow;
        Check(Size(await slow.GetThumbnailAsync(file, 512)) == 128 && DateTime.UtcNow - started < TimeSpan.FromSeconds(20), "the thumbnailer stopped");
    }

    [Test, Explicit, Category("DBus"), DisplayName("thumbnails: without a thumbnailer only the cache answers")]
    public async Task NoThumbnailerAsync()
    {
        using var cache = new Cache();
        var thumbnails = new LinuxThumbnails(cache.Options() with { BusAddress = DBusAddress(), Service = "org.runic.Test.NoThumbnailer" });
        string file = cache.Source("photo.png");
        Check(await thumbnails.GetThumbnailAsync(file, 128) is PlatformResult<DesktopThumbnail?>.Success(null), "nothing cached");
        cache.Entry("normal", file, 128);
        Check(Size(await thumbnails.GetThumbnailAsync(file, 256)) == 128, "the cached one");
    }

    private static int Size(PlatformResult<DesktopThumbnail?> result) =>
        result is PlatformResult<DesktopThumbnail?>.Success({ } thumbnail) ? thumbnail.Width : throw new InvalidOperationException($"No thumbnail: {result}");

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(20); }
    }

    private static string DBusAddress() => Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")
        ?? throw new InvalidOperationException("Run the bus cases under dbus-run-session.");

    private static async Task<Exception?> Catch(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static Exception? Catch(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // A temporary thumbnail cache, source directory and MIME database that knows *.png.
    private sealed class Cache : IDisposable
    {
        private readonly DesktopApplicationsFixture _fixture = new();
        public string Root => _fixture.Root;
        public string Directory => Path.Combine(Root, "cache", "thumbnails");

        public Cache()
        {
            DesktopApplicationsFixture.Write(Path.Combine(_fixture.Share, "mime", "globs2"), "50:image/png:*.png\n");
            System.IO.Directory.CreateDirectory(Path.Combine(Root, "source"));
        }

        public LinuxThumbnailOptions Options() => new()
        {
            CacheDirectory = Directory, Applications = _fixture.Options(), BusAddress = "unix:path=" + Path.Combine(Root, "no-bus"),
        };

        public LinuxThumbnails CacheOnly() => new(Options());

        public LinuxThumbnails WithThumbnailer(TimeSpan? timeout = null) =>
            new(Options() with { BusAddress = DBusAddress(), Service = FakeThumbnailer.Name, Timeout = timeout ?? TimeSpan.FromSeconds(10) });

        public string Source(string name)
        {
            string path = Path.Combine(Root, "source", name);
            File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
            return path;
        }

        public string EntryPath(string flavor, string file) => Path.Combine(Directory, flavor, LinuxThumbnails.CacheName(LinuxThumbnails.FileUri(file)));

        // Writes a thumbnail with the file's URI and modification time unless the case supplies others; null omits a key.
        public void Entry(string flavor, string file, int width, string? uri = "", string? mtime = "", string? size = null, bool international = false)
        {
            var keys = new List<(string, string)>();
            if (uri is not null) keys.Add(("Thumb::URI", uri.Length > 0 ? uri : LinuxThumbnails.FileUri(file)));
            if (mtime is not null)
                keys.Add(("Thumb::MTime", mtime.Length > 0 ? mtime : new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (size is not null) keys.Add(("Thumb::Size", size));
            DesktopApplicationsFixture.Write(EntryPath(flavor, file), Png(width, width / 2 > 0 ? width / 2 : 1, [.. keys], international));
        }

        // A PNG's chunk structure with the text keys; the provider reads no pixels and checks no CRCs.
        public static byte[] Png(int width, int height, (string Key, string Value)[] keys, bool international = false)
        {
            var png = new List<byte> { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a };
            void Chunk(string type, byte[] body)
            {
                var length = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
                png.AddRange(length); png.AddRange(Encoding.ASCII.GetBytes(type)); png.AddRange(body); png.AddRange(new byte[4]);
            }
            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8; header[9] = 6;
            Chunk("IHDR", header);
            foreach (var (key, value) in keys)
                Chunk(international ? "iTXt" : "tEXt", international
                    ? [.. Encoding.Latin1.GetBytes(key), 0, 0, 0, .. "en"u8, 0, 0, .. Encoding.UTF8.GetBytes(value)]
                    : [.. Encoding.Latin1.GetBytes(key), 0, .. Encoding.Latin1.GetBytes(value)]);
            Chunk("IDAT", [0x78, 0x9c, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
            Chunk("IEND", []);
            return [.. png];
        }

        public void Dispose() => _fixture.Dispose();
    }

    // Thumbnailer1 as Tumbler offers it: Queue returns a handle and the case decides which signals follow.
    private sealed class FakeThumbnailer : DBusObject, IAsyncDisposable
    {
        public const string Name = "org.runic.Test.Thumbnailer1";
        private DBusConnection _connection = null!;
        private IAsyncDisposable? _export;
        private uint _next = 41;
        public List<string> Calls { get; } = [];
        public ImmutableList<uint> Dequeued { get; private set; } = [];
        public uint LastHandle { get; private set; }
        public Action<uint, string> OnQueue { get; set; } = static (_, _) => { };

        public static async Task<FakeThumbnailer> StartAsync()
        {
            var fake = new FakeThumbnailer();
            fake._connection = ((PlatformResult<DBusConnection>.Success)await DBusConnection.ConnectToAddressAsync(DBusAddress())).Value;
            fake._export = fake._connection.Export("/org/freedesktop/thumbnails/Thumbnailer1", fake);
            if (await fake._connection.RequestNameAsync(Name, DBusRequestNameOptions.DoNotQueue) != DBusRequestNameReply.PrimaryOwner)
                throw new InvalidOperationException("The fake thumbnailer's name is taken.");
            return fake;
        }

        public void Emit(string member, params DBusValue[] arguments) => EmitSignal("org.freedesktop.thumbnails.Thumbnailer1", member, arguments);

        protected override ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context)
        {
            switch (context.Interface, context.Member)
            {
                case ("org.freedesktop.thumbnails.Thumbnailer1", "Queue"):
                    var a = context.Arguments;
                    lock (Calls) Calls.Add($"Queue {string.Join(',', a[0].GetStringArray())} {string.Join(',', a[1].GetStringArray())} {a[2].GetString()} {a[3].GetString()} {a[4].GetUInt32()}");
                    uint handle = LastHandle = Interlocked.Increment(ref _next);
                    OnQueue(handle, a[0].GetStringArray()[0]);
                    return ValueTask.FromResult<ImmutableArray<DBusValue>>([DBusValue.From(handle)]);
                case ("org.freedesktop.thumbnails.Thumbnailer1", "Dequeue"):
                    Dequeued = Dequeued.Add(context.Arguments[0].GetUInt32());
                    return ValueTask.FromResult<ImmutableArray<DBusValue>>([]);
                default:
                    throw new DBusException(DBusException.UnknownMethod, context.Member);
            }
        }

        public async Task StopAsync()
        {
            if (_export is { } export) { _export = null; await export.DisposeAsync(); await _connection.DisposeAsync(); }
        }

        public async ValueTask DisposeAsync() => await StopAsync();
    }
}
