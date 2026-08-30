using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Runic.Desktop;

namespace Runic.Assets.Desktop;

/// <summary>Adapts authoritative asset snapshots to Runic Desktop request-scoped delivery.</summary>
public static class DesktopAssetExtensions
{
    /// <summary>Creates a Desktop content handler over the source's current immutable snapshots.</summary>
    public static ContentHandler ToDesktopContentHandler(
        this IAssetSource source,
        DesktopAssetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source is not IAssetSnapshotSource snapshots)
            throw new ArgumentException("Desktop delivery requires atomic descriptor-and-stream snapshots.", nameof(source));
        DesktopAssetOptions selected = options ?? new();
        return (request, cancellationToken) => HandleAsync(
            snapshots,
            request,
            selected.EnableSinglePageApplicationFallback,
            cancellationToken);
    }

    private static async ValueTask<ContentResponse?> HandleAsync(
        IAssetSnapshotSource source,
        ContentRequest request,
        bool spaFallback,
        CancellationToken cancellationToken)
    {
        if (!StringComparer.Ordinal.Equals(request.Method, "GET") &&
            !StringComparer.Ordinal.Equals(request.Method, "HEAD"))
            return Error(405);
        if (!TryResolve(source.Manifest, request.Path, spaFallback, out AssetDescriptor? descriptor))
            return Error(404);

        AssetReadSnapshot? snapshot = null;
        try
        {
            snapshot = await source.OpenSnapshotAsync(descriptor!.RelativePath, cancellationToken).ConfigureAwait(false);
            AssetDescriptor current = snapshot.Descriptor;
            IReadOnlyDictionary<string, string> headers = Headers(current);
            if (MatchesIfNoneMatch(Header(request, "If-None-Match"), current.EntityTag))
            {
                await snapshot.DisposeAsync().ConfigureAwait(false);
                snapshot = null;
                return new ContentResponse(ReadOnlyMemory<byte>.Empty, current.MediaType, 304, headers);
            }

            long start = 0;
            long length = current.Length;
            AssetRangeResult range = MatchesIfRange(Header(request, "If-Range"), current.EntityTag)
                ? ParseRange(Header(request, "Range"), current.Length, out start, out length)
                : AssetRangeResult.Ignore;
            if (range == AssetRangeResult.Unsatisfiable)
            {
                await snapshot.DisposeAsync().ConfigureAwait(false);
                snapshot = null;
                return new ContentResponse(
                    ReadOnlyMemory<byte>.Empty,
                    current.MediaType,
                    416,
                    With(headers, "Content-Range", $"bytes */{current.Length.ToString(CultureInfo.InvariantCulture)}"));
            }
            bool hasRange = range == AssetRangeResult.Satisfiable;
            if (!hasRange)
            {
                start = 0;
                length = current.Length;
            }
            if (hasRange)
            {
                headers = With(
                    headers,
                    "Content-Range",
                    $"bytes {start.ToString(CultureInfo.InvariantCulture)}-{checked(start + length - 1).ToString(CultureInfo.InvariantCulture)}/{current.Length.ToString(CultureInfo.InvariantCulture)}");
            }

            if (StringComparer.Ordinal.Equals(request.Method, "HEAD"))
            {
                await snapshot.DisposeAsync().ConfigureAwait(false);
                snapshot = null;
                return ContentResponse.Stream(
                    static _ => ValueTask.FromResult<Stream>(Stream.Null),
                    current.MediaType,
                    hasRange ? 206 : 200,
                    length,
                    headers);
            }

            await SkipAsync(snapshot.Content, start, cancellationToken).ConfigureAwait(false);
            var body = new SnapshotRangeStream(snapshot, length);
            snapshot = null;
            return ContentResponse.Stream(
                _ => ValueTask.FromResult<Stream>(body),
                current.MediaType,
                hasRange ? 206 : 200,
                length,
                headers);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (snapshot is not null) await snapshot.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch
        {
            if (snapshot is not null) await snapshot.DisposeAsync().ConfigureAwait(false);
            return Error(500);
        }
    }

    private static Dictionary<string, string> Headers(AssetDescriptor descriptor) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ETag"] = descriptor.EntityTag,
            ["Cache-Control"] = descriptor.CacheControl,
            ["Accept-Ranges"] = "bytes",
            ["X-Content-Type-Options"] = "nosniff",
        };

    private static Dictionary<string, string> With(
        IReadOnlyDictionary<string, string> source,
        string name,
        string value)
    {
        var result = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase) { [name] = value };
        return result;
    }

    private static string Header(ContentRequest request, string name) =>
        request.Headers.TryGetValue(name, out string? value) ? value : string.Empty;

    private static bool TryResolve(
        AssetManifest manifest,
        string path,
        bool spaFallback,
        out AssetDescriptor? descriptor)
    {
        descriptor = null;
        if (string.IsNullOrEmpty(path) || StringComparer.Ordinal.Equals(path, "/"))
        {
            descriptor = manifest.EntryPoint;
            return true;
        }
        try
        {
            string normalized = AssetPath.Normalize(path[0] == '/' ? path[1..] : path);
            if (manifest.TryGetAsset(normalized, out descriptor) && descriptor is not null) return true;
            if (spaFallback && !HasFileExtension(normalized))
            {
                descriptor = manifest.EntryPoint;
                return true;
            }
        }
        catch (ArgumentException)
        {
        }
        return false;
    }

    private static async Task SkipAsync(Stream stream, long count, CancellationToken cancellationToken)
    {
        if (count == 0) return;
        var buffer = new byte[16 * 1024];
        long remaining = count;
        while (remaining > 0)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("The asset ended before its declared range offset.");
            remaining -= read;
        }
    }

    private static bool MatchesIfNoneMatch(string value, string entityTag)
    {
        foreach (string candidate in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (candidate == "*" || StringComparer.Ordinal.Equals(RemoveWeakPrefix(candidate), entityTag)) return true;
        return false;
    }

    private static bool MatchesIfRange(string value, string entityTag) =>
        string.IsNullOrEmpty(value) || StringComparer.Ordinal.Equals(value.Trim(), entityTag);

    private static string RemoveWeakPrefix(string value) =>
        value.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? value[2..].TrimStart() : value;

    private static AssetRangeResult ParseRange(string value, long assetLength, out long start, out long length)
    {
        start = 0;
        length = 0;
        if (string.IsNullOrEmpty(value) ||
            !value.StartsWith("bytes=", StringComparison.Ordinal) ||
            value.Contains(',', StringComparison.Ordinal)) return AssetRangeResult.Ignore;
        string range = value["bytes=".Length..];
        int separator = range.IndexOf('-');
        if (separator < 0 || separator != range.LastIndexOf('-')) return AssetRangeResult.Ignore;
        string left = range[..separator];
        string right = range[(separator + 1)..];
        if (left.Length == 0)
        {
            if (!long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out long suffix) || suffix <= 0)
                return AssetRangeResult.Ignore;
            if (assetLength == 0) return AssetRangeResult.Unsatisfiable;
            length = Math.Min(suffix, assetLength);
            start = assetLength - length;
            return AssetRangeResult.Satisfiable;
        }
        if (!long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 0)
            return AssetRangeResult.Ignore;
        if (start >= assetLength) return AssetRangeResult.Unsatisfiable;
        if (right.Length == 0)
        {
            length = assetLength - start;
            return AssetRangeResult.Satisfiable;
        }
        if (!long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out long end) || end < start)
            return AssetRangeResult.Ignore;
        end = Math.Min(end, assetLength - 1);
        length = checked(end - start + 1);
        return AssetRangeResult.Satisfiable;
    }

    private static bool HasFileExtension(string path)
    {
        int nameStart = path.LastIndexOf('/') + 1;
        int dot = path.LastIndexOf('.');
        return dot > nameStart && dot < path.Length - 1;
    }

    private static ContentResponse Error(int statusCode) =>
        new(ReadOnlyMemory<byte>.Empty, "text/plain; charset=utf-8", statusCode);

    private enum AssetRangeResult { Ignore, Satisfiable, Unsatisfiable }

    private sealed class SnapshotRangeStream : Stream
    {
        private readonly AssetReadSnapshot _snapshot;
        private long _remaining;
        private int _disposed;

        internal SnapshotRangeStream(AssetReadSnapshot snapshot, long remaining)
        {
            _snapshot = snapshot;
            _remaining = remaining;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _snapshot.Content.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0) return 0;
            int read = await _snapshot.Content.ReadAsync(
                buffer[..(int)Math.Min(buffer.Length, _remaining)],
                cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("The asset ended before its declared length.");
            _remaining -= read;
            return read;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) _snapshot.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                await _snapshot.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
