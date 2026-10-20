using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Linux.Portal.Thumbnails;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

// Every input of the Linux thumbnails, so tests point them at a temporary cache and a private bus and nothing reads the
// process environment after construction.
internal sealed record LinuxThumbnailOptions
{
    /// <summary>The thumbnail cache: <c>$XDG_CACHE_HOME/thumbnails</c>.</summary>
    internal required string CacheDirectory { get; init; }
    /// <summary>The MIME database that names the file's type for the thumbnailer.</summary>
    internal required DesktopApplicationsOptions Applications { get; init; }
    /// <summary>The session bus address; null for the session bus.</summary>
    internal string? BusAddress { get; init; }
    internal string Service { get; init; } = LinuxThumbnails.ServiceName;
    /// <summary>Whether the process runs in Flatpak or Snap, where the cache and the thumbnailer need permissions.</summary>
    internal bool Sandboxed { get; init; }
    /// <summary>How long a request waits for the thumbnailer before it reads the cache again.</summary>
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    internal static LinuxThumbnailOptions FromProcess()
    {
        var applications = DesktopApplicationsOptions.FromProcess();
        bool flatpak = File.Exists("/.flatpak-info");
        // Flatpak points XDG_CACHE_HOME into the application's own directory; --filesystem=xdg-cache/thumbnails exposes
        // the host's cache at its host path, which HOST_XDG_CACHE_HOME names when the host sets XDG_CACHE_HOME.
        string? cache = Absolute(flatpak ? "HOST_XDG_CACHE_HOME" : "XDG_CACHE_HOME");
        return new LinuxThumbnailOptions
        {
            CacheDirectory = Path.Combine(cache ?? Path.Combine(applications.HomeDirectory, ".cache"), "thumbnails"),
            Applications = applications,
            Sandboxed = applications.Sandboxed,
        };
    }

    private static string? Absolute(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && Path.IsPathFullyQualified(value) ? value : null;
}

// Thumbnails from the freedesktop.org thumbnail cache (https://specifications.freedesktop.org/thumbnail-spec/latest/),
// which file managers fill, and on a miss from the session's org.freedesktop.thumbnails.Thumbnailer1 service, such as
// Tumbler. GNOME and KDE run no such service: there only what the file manager already cached is returned.
internal sealed class LinuxThumbnails(LinuxThumbnailOptions options, PortalApplication? application = null) : IDesktopThumbnails
{
    internal const string ServiceName = "org.freedesktop.thumbnails.Thumbnailer1";
    private const string Interface = "org.freedesktop.thumbnails.Thumbnailer1";
    private static readonly DBusObjectPath ObjectPath = new("/org/freedesktop/thumbnails/Thumbnailer1");
    // The specification's flavours, smallest first.
    internal static readonly ImmutableArray<(string Name, int Size)> Flavors = [("normal", 128), ("large", 256), ("x-large", 512), ("xx-large", 1024)];
    private const int MaximumBytes = 16 << 20;
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a];

    private readonly PortalApplication _application = application ?? new PortalApplication();
    private readonly object _gate = new();
    private LinuxDesktopApplications? _types;
    private Task<PlatformResult<DBusConnection>>? _connecting;
    private int _cacheReported, _busReported;

    internal LinuxThumbnailOptions Options => options;

    public ValueTask<PlatformResult<DesktopThumbnail?>> GetThumbnailAsync(string path, int size, CancellationToken cancellationToken = default)
    {
        path = DesktopServiceValidation.ItemPath(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, DesktopThumbnails.MaximumSize);
        cancellationToken.ThrowIfCancellationRequested();
        // The cache reads block, so requests run on the thread pool, as the trash does.
        return new(Task.Run(() => GetCoreAsync(path, size, cancellationToken), cancellationToken));
    }

    private async Task<PlatformResult<DesktopThumbnail?>> GetCoreAsync(string path, int size, CancellationToken cancellationToken)
    {
        var source = Source(path);
        if (source is not PlatformResult<FileStamp?>.Success(var found)) return Forward(source);
        // A directory has no thumbnail.
        if (found is not { } stamp) return None;
        string uri = FileUri(path);
        string name = CacheName(uri);
        var cached = Lookup(name, uri, stamp, size);
        if (cached.AtLeast is { } thumbnail) return new PlatformResult<DesktopThumbnail?>.Success(thumbnail);
        if (cached.Failed) return None;
        if (await GenerateAsync(path, uri, Flavor(size), cancellationToken).ConfigureAwait(false))
        {
            cached = Lookup(name, uri, stamp, size);
            if (cached.AtLeast is { } generated) return new PlatformResult<DesktopThumbnail?>.Success(generated);
            if (cached.Failed) return None;
        }
        return new PlatformResult<DesktopThumbnail?>.Success(cached.Smaller);
    }

    private static readonly PlatformResult<DesktopThumbnail?> None = new PlatformResult<DesktopThumbnail?>.Success(null);

    /// <summary>The flavour a thumbnailer is asked for: the smallest at least as large as the request.</summary>
    internal static string Flavor(int size) => Flavors.First(flavor => flavor.Size >= size).Name;

    // The file's modification time and size as the cache records them, following a symbolic link as GIO does; null for a
    // directory.
    private static PlatformResult<FileStamp?> Source(string path)
    {
        try
        {
            var info = new FileInfo(path);
            // FileInfo reports a link's own time and size.
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target) info = target;
            if (info.Exists)
                return new PlatformResult<FileStamp?>.Success(new FileStamp(new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(), info.Length));
            if (Directory.Exists(path)) return new PlatformResult<FileStamp?>.Success(null);
            // Tell a missing file from an unreadable directory; a dangling link is missing too.
            File.GetAttributes(path);
            return new PlatformResult<FileStamp?>.Failed(PlatformFailureCode.NotFound, PlatformDiagnostic.FromErrno(2));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return new PlatformResult<FileStamp?>.Failed(PlatformFailureCode.NotFound, PlatformDiagnostic.FromErrno(2)); }
        catch (UnauthorizedAccessException) { return new PlatformResult<FileStamp?>.Failed(PlatformFailureCode.PermissionDenied, PlatformDiagnostic.FromErrno(13)); }
        catch (IOException error) { return new PlatformResult<FileStamp?>.Failed(PlatformFailureCode.IOError, LinuxDesktopApplications.Errno(error)); }
    }

    private static PlatformResult<DesktopThumbnail?> Forward(PlatformResult<FileStamp?> result) => result switch
    {
        PlatformResult<FileStamp?>.Failed failed => new PlatformResult<DesktopThumbnail?>.Failed(failed.Code, failed.Diagnostic),
        PlatformResult<FileStamp?>.Unavailable(var reason) => new PlatformResult<DesktopThumbnail?>.Unavailable(reason),
        _ => throw new InvalidOperationException(),
    };

    internal readonly record struct FileStamp(long ModifiedSeconds, long Length);

    internal readonly record struct CacheResult(DesktopThumbnail? AtLeast, DesktopThumbnail? Smaller, bool Failed);

    // The smallest valid thumbnail at least as large as the request, else whether a thumbnailer recorded a failure for this
    // version of the file, else the largest smaller one.
    internal CacheResult Lookup(string name, string uri, FileStamp stamp, int size)
    {
        if (!Directory.Exists(options.CacheDirectory) && options.Sandboxed && Interlocked.Exchange(ref _cacheReported, 1) == 0)
            _application.Diagnose("thumbnail-cache-unavailable", "The host's thumbnail cache is not visible inside the sandbox.",
                "Allow it read-only, such as with --filesystem=xdg-cache/thumbnails:ro in Flatpak.");
        DesktopThumbnail? smaller = null;
        foreach (var (flavor, flavorSize) in Flavors)
        {
            var thumbnail = Read(Path.Combine(options.CacheDirectory, flavor, name), uri, stamp);
            if (thumbnail is null) continue;
            if (flavorSize >= size) return new CacheResult(thumbnail, null, false);
            smaller = thumbnail;
        }
        bool failed = false;
        try
        {
            string fail = Path.Combine(options.CacheDirectory, "fail");
            if (Directory.Exists(fail))
                failed = Directory.EnumerateDirectories(fail).Any(directory => Read(Path.Combine(directory, name), uri, stamp) is not null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return new CacheResult(null, failed ? null : smaller, failed);
    }

    // A cache entry that describes this version of the file, or null.
    internal static DesktopThumbnail? Read(string file, string uri, FileStamp stamp)
    {
        byte[] data;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length is < 8 or > MaximumBytes) return null;
            data = File.ReadAllBytes(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        return Validate(data, uri, stamp) is ({ } width, { } height) ? new DesktopThumbnail(ImmutableCollectionsMarshal.AsImmutableArray(data), width, height) : null;
    }

    // The specification's validity check: Thumb::URI names the file and Thumb::MTime is its modification time; a
    // Thumb::Size, which is optional, is its size. The keys are tEXt chunks; an uncompressed iTXt chunk is read as well.
    internal static (int? Width, int? Height) Validate(ReadOnlySpan<byte> png, string uri, FileStamp stamp)
    {
        if (!png.StartsWith(Signature)) return default;
        int width = 0, height = 0;
        string? thumbUri = null, modified = null, length = null;
        int offset = Signature.Length;
        while (offset + 12 <= png.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            if (size > (uint)(png.Length - offset - 12)) return default;
            var type = png.Slice(offset + 4, 4);
            var body = png.Slice(offset + 8, (int)size);
            offset += 12 + (int)size;
            if (type.SequenceEqual("IHDR"u8) && body.Length >= 8)
            {
                width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body), int.MaxValue);
                height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[4..]), int.MaxValue);
            }
            else if (type.SequenceEqual("IEND"u8)) break;
            else if (Text(type, body) is ({ } key, { } value))
            {
                switch (key)
                {
                    case "Thumb::URI": thumbUri ??= value; break;
                    case "Thumb::MTime": modified ??= value; break;
                    case "Thumb::Size": length ??= value; break;
                }
            }
        }
        if (width <= 0 || height <= 0 || thumbUri != uri || modified is null) return default;
        // The specification stores whole seconds; a fractional value, as some thumbnailers write, has the same second.
        if (!decimal.TryParse(modified, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal seconds)
            || decimal.Floor(seconds) != stamp.ModifiedSeconds) return default;
        if (length is not null && (!long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) || bytes != stamp.Length)) return default;
        return (width, height);
    }

    private static (string? Key, string? Value) Text(ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        int separator = body.IndexOf((byte)0);
        if (separator <= 0) return default;
        string key = Encoding.Latin1.GetString(body[..separator]);
        var rest = body[(separator + 1)..];
        if (type.SequenceEqual("tEXt"u8)) return (key, Encoding.Latin1.GetString(rest));
        if (!type.SequenceEqual("iTXt"u8) || rest.Length < 2 || rest[0] != 0) return default;
        rest = rest[2..];
        // The language tag and the translated keyword precede the text.
        for (int i = 0; i < 2; i++)
        {
            int end = rest.IndexOf((byte)0);
            if (end < 0) return default;
            rest = rest[(end + 1)..];
        }
        return (key, Encoding.UTF8.GetString(rest));
    }

    /// <summary>The file: URI that keys the cache, escaped as GIO's <c>g_filename_to_uri</c> escapes it.</summary>
    /// <remarks>Every byte of the UTF-8 path is kept except those outside the unreserved characters and
    /// <c>!$&amp;'()*+,=:@/</c>, which are percent-encoded with upper-case digits.</remarks>
    internal static string FileUri(string path)
    {
        var uri = new StringBuilder("file://", path.Length + 16);
        foreach (byte b in Encoding.UTF8.GetBytes(path))
        {
            if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
                or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~' or (byte)'!' or (byte)'$' or (byte)'&' or (byte)'\'' or (byte)'('
                or (byte)')' or (byte)'*' or (byte)'+' or (byte)',' or (byte)'=' or (byte)':' or (byte)'@' or (byte)'/')
                uri.Append((char)b);
            else uri.Append('%').Append("0123456789ABCDEF"[b >> 4]).Append("0123456789ABCDEF"[b & 15]);
        }
        return uri.ToString();
    }

    /// <summary>The cache file name: the lower-case hexadecimal MD5 digest of the URI, with <c>.png</c>.</summary>
    internal static string CacheName(string uri) => Convert.ToHexStringLower(Md5.Hash(Encoding.UTF8.GetBytes(uri))) + ".png";

    // Asks the thumbnailer for the flavour and waits until it reports the file. True when it was asked, so the cache is
    // read again; a missing, refusing or failing thumbnailer leaves the cache's answer.
    private async Task<bool> GenerateAsync(string path, string uri, string flavor, CancellationToken cancellationToken)
    {
        if (await ConnectAsync(cancellationToken).ConfigureAwait(false) is not PlatformResult<DBusConnection>.Success(var connection)) return false;
        string mime = await ContentTypeAsync(path, cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
        var token = deadline.Token;
        var signals = Channel.CreateUnbounded<DBusSignal>(new UnboundedChannelOptions { SingleReader = true });
        var subscriptions = new List<DBusSignalSubscription>();
        var thumbnailer = new Thumbnailer1(connection, options.Service, ObjectPath);
        uint? handle = null;
        bool reported = false;
        try
        {
            // Subscribe first: the thumbnailer can report the handle before its reply arrives.
            subscriptions.Add(await connection.SubscribeAsync(new DBusSignalFilter { Sender = options.Service, Path = ObjectPath, Interface = Interface }, token)
                .ConfigureAwait(false));
            subscriptions.Add(await connection.SubscribeAsync(new DBusSignalFilter
            { Sender = "org.freedesktop.DBus", Interface = "org.freedesktop.DBus", Member = "NameOwnerChanged", Arg0 = options.Service }, token).ConfigureAwait(false));
            foreach (var subscription in subscriptions) _ = PumpAsync(subscription, signals.Writer, token);
            handle = await thumbnailer.QueueAsync([uri], [mime], flavor, "foreground", 0, cancellationToken: token).ConfigureAwait(false);
            while (!reported)
            {
                var signal = await signals.Reader.ReadAsync(token).ConfigureAwait(false);
                reported = signal.Member switch
                {
                    // The thumbnailer stopped: whatever it finished is in the cache.
                    "NameOwnerChanged" => signal.Arguments is [_, _, var owner] && owner.GetString().Length == 0,
                    "Ready" when Thumbnailer1.ReadReady(signal.Arguments) is var (id, uris) => id == handle && uris.Contains(uri),
                    "Error" when Thumbnailer1.ReadError(signal.Arguments) is var (id, uris, _, _) => id == handle && uris.Contains(uri),
                    "Finished" => Thumbnailer1.ReadFinished(signal.Arguments) == handle,
                    _ => false,
                };
            }
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return handle is not null; }
        catch (DBusException error)
        {
            if (error.ErrorName == DBusException.AccessDenied && options.Sandboxed && Interlocked.Exchange(ref _busReported, 1) == 0)
                _application.Diagnose("thumbnailer-access-denied", "The sandbox does not allow talking to the thumbnailer.",
                    "Allow it, such as with --talk-name=org.freedesktop.thumbnails.Thumbnailer1 in Flatpak.");
            // A malformed signal after the job was queued still leaves the cache to read.
            return handle is not null;
        }
        catch (ChannelClosedException) { return handle is not null; }
        finally
        {
            foreach (var subscription in subscriptions) await subscription.DisposeAsync().ConfigureAwait(false);
            // A request that stopped waiting withdraws its job; nothing waits for the reply.
            if (handle is { } queued && !reported)
                _ = thumbnailer.DequeueAsync(queued, cancellationToken: CancellationToken.None).AsTask().ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static async Task PumpAsync(DBusSignalSubscription subscription, ChannelWriter<DBusSignal> writer, CancellationToken cancellationToken)
    {
        try { await foreach (var signal in subscription.WithCancellation(cancellationToken).ConfigureAwait(false)) writer.TryWrite(signal); }
        catch (Exception) { /* The request stopped waiting or the connection closed; the reader's deadline ends the wait. */ }
    }

    // The thumbnailer chooses its plugin by the file's type, detected as IDesktopApplications.GetContentTypeAsync does.
    private async Task<string> ContentTypeAsync(string path, CancellationToken cancellationToken)
    {
        _types ??= new LinuxDesktopApplications(options.Applications, _application);
        return await _types.GetContentTypeAsync(path, DesktopContentDetection.NameAndContent, cancellationToken).ConfigureAwait(false)
            is PlatformResult<DesktopContentType>.Success(var type) && type.MimeType is { Length: > 0 } mime ? mime : SharedMimeDatabase.OctetStream;
    }

    // One connection for every request, opened on the first cache miss and replaced when it closes or failed. The service
    // lives as long as the application, as its registration does, and the connection with it.
    private Task<PlatformResult<DBusConnection>> ConnectAsync(CancellationToken cancellationToken)
    {
        Task<PlatformResult<DBusConnection>> connecting;
        lock (_gate)
        {
            if (_connecting is null || _connecting.IsCompleted && (!_connecting.IsCompletedSuccessfully
                || _connecting.Result is not PlatformResult<DBusConnection>.Success { Value.Disconnected.IsCancellationRequested: false }))
            {
                if (_connecting is { IsCompletedSuccessfully: true, Result: PlatformResult<DBusConnection>.Success(var closed) })
                    _ = closed.DisposeAsync().AsTask();
                _connecting = options.BusAddress is null
                    ? DBusConnection.ConnectAsync(DBusBus.Session, CancellationToken.None).AsTask()
                    : DBusConnection.ConnectToAddressAsync(options.BusAddress, CancellationToken.None).AsTask();
            }
            connecting = _connecting;
        }
        return connecting.WaitAsync(cancellationToken);
    }
}

// MD5 (RFC 1321) for the cache's file names. Managed, so the cache needs no OpenSSL, which also refuses MD5 in FIPS mode.
internal static class Md5
{
    private static readonly int[] Shifts = [7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21];
    private static readonly uint[] Constants = [.. Enumerable.Range(0, 64).Select(static i => (uint)(long)Math.Floor(Math.Abs(Math.Sin(i + 1)) * 4294967296.0))];

    internal static byte[] Hash(ReadOnlySpan<byte> message)
    {
        int padded = (message.Length + 8) / 64 * 64 + 64;
        var data = new byte[padded];
        message.CopyTo(data);
        data[message.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(padded - 8), (ulong)message.Length * 8);
        uint a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;
        Span<uint> words = stackalloc uint[16];
        for (int block = 0; block < padded; block += 64)
        {
            for (int i = 0; i < 16; i++) words[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(block + i * 4));
            uint a = a0, b = b0, c = c0, d = d0;
            for (int i = 0; i < 64; i++)
            {
                (uint f, int g) = (i / 16) switch
                {
                    0 => ((b & c) | (~b & d), i),
                    1 => ((d & b) | (~d & c), (5 * i + 1) % 16),
                    2 => (b ^ c ^ d, (3 * i + 5) % 16),
                    _ => (c ^ (b | ~d), 7 * i % 16),
                };
                f += a + Constants[i] + words[g];
                a = d; d = c; c = b;
                b += uint.RotateLeft(f, Shifts[i]);
            }
            a0 += a; b0 += b; c0 += c; d0 += d;
        }
        var digest = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(digest, a0);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(4), b0);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(8), c0);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(12), d0);
        return digest;
    }
}
