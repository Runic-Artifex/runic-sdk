using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

// blobs/1 (specs/application/README.md, "Blobs and uploads"): the blobs and uploads of one Window, which its
// dispatcher owns.
//
// - Blobs. A RunicBlob that a route encodes is registered under a random token, once per instance, and crosses as a
//   URL below the host's blob base. Holders keep it: a state member (route and member name), replaced each time the
//   member is written, or the route itself, for blobs in collection items and results. A blob without holders is
//   released; so is everything a route held when it is released, and everything when the Window closes. The host
//   serves GET and HEAD of a token through ServeBlobAsync.
// - Uploads. A connection whose hello lists blobs/1 gets a token for the host's upload URL; ReceiveUploadAsync stores
//   the request body in a temporary file of the Window, within the per-upload and per-Window limits, under a random
//   id. A RunicUpload decodes only from an id of the request's own connection, and the first claim takes the upload out
//   of the Window. Unclaimed uploads are deleted when their connection closes, after UnclaimedLifetime, and when the
//   Window closes.
//
// Encoding and decoding find the Window through BridgeBlobScope, which ViewModelBridge enters around its requests and
// publications: the generated codecs are static.
internal sealed class BridgeBlobs : IDisposable
{
    internal const string Feature = "blobs/1";
    internal const string UploadTokenHeader = "X-Runic-Upload-Token";
    internal const string UploadNameHeader = "X-Runic-Upload-Name";
    internal const long DefaultMaximumUploadBytes = 256L * 1_048_576;
    internal const long DefaultMaximumWindowUploadBytes = 1_024L * 1_048_576;
    internal const int MaximumConcurrentUploads = 16;
    internal static readonly TimeSpan UnclaimedLifetime = TimeSpan.FromMinutes(10);
    // The response headers of every blob. Active content (HTML, SVG) never runs in the application's origin.
    internal const string ContentSecurityPolicy = "default-src 'none'; img-src data:; media-src data:; style-src 'unsafe-inline'; sandbox";
    internal const string CacheControl = "private, max-age=31536000, immutable";

    private readonly object _gate = new();
    private readonly Dictionary<RunicBlob, BlobEntry> _byBlob = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, BlobEntry> _byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<Holder, BlobEntry[]> _slots = [];
    private readonly Dictionary<object, HashSet<BlobEntry>> _routes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, string> _connectionByToken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokenByConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredUpload> _uploads = new(StringComparer.Ordinal);
    private BridgeBlobEndpoint? _endpoint;
    private string? _directory;
    private long _storedBytes;
    private int _receiving;
    private bool _closed;
    private bool _unavailableLogged;

    // The Window's logger and clock (BridgeDispatcher.Configure).
    internal ILogger Logger { get; set; } = TraceFallbackLogger.Instance;
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    internal long MaximumUploadBytes { get; private set; } = DefaultMaximumUploadBytes;
    internal long MaximumWindowUploadBytes { get; private set; } = DefaultMaximumWindowUploadBytes;

    // Where the uploads of the Window are stored; tests set a directory of their own.
    internal string UploadRoot { get; set; } = Path.Combine(Path.GetTempPath(), "runic-uploads");

    internal BridgeBlobEndpoint? Endpoint
    {
        get { lock (_gate) return _endpoint; }
    }

    internal void Configure(long? maximumUploadBytes, long? maximumWindowUploadBytes)
    {
        MaximumUploadBytes = maximumUploadBytes ?? DefaultMaximumUploadBytes;
        MaximumWindowUploadBytes = maximumWindowUploadBytes ?? DefaultMaximumWindowUploadBytes;
    }

    // The host serves this Window's blobs and uploads at the endpoint from now on.
    internal void Attach(BridgeBlobEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (_gate)
        {
            _endpoint = endpoint;
            // A window session that the transport serves again after a close (a reopened WPF View).
            _closed = false;
        }
    }

    // The host stops serving the endpoint; blob URLs are then empty and hellos grant no uploads.
    internal void Detach()
    {
        lock (_gate) _endpoint = null;
    }

    // ---- Blobs ----

    // The URL of a blob that a route encodes, registering it for the scope's holder. An empty string when the host
    // serves no blobs.
    internal string Issue(RunicBlob blob, BridgeBlobScope scope)
    {
        BridgeBlobEndpoint? endpoint;
        BlobEntry entry;
        lock (_gate)
        {
            endpoint = _endpoint;
            if (_closed) return string.Empty;
            if (!_byBlob.TryGetValue(blob, out entry!))
            {
                entry = new BlobEntry(NewToken(), blob);
                _byBlob.Add(blob, entry);
                _byToken.Add(entry.Token, entry);
            }
            if (scope.Slot is { } slot) slot.Add(entry);
            else Hold(entry, new Holder(scope.Route, null));
        }
        var blobBase = endpoint?.BlobBase();
        if (blobBase is null)
        {
            if (!_unavailableLogged)
            {
                _unavailableLogged = true;
                BlobsLog.BlobsUnavailable(Logger);
            }
            return string.Empty;
        }
        return blobBase.AbsoluteUri + entry.Token;
    }

    // A state member was written: its blobs are now exactly these.
    internal void ReplaceSlot(object route, string slot, List<BlobEntry> entries)
    {
        var holder = new Holder(route, slot);
        lock (_gate)
        {
            if (_closed) return;
            var next = entries.Count == 0 ? [] : entries.Distinct().ToArray();
            if (_slots.Remove(holder, out var previous))
                foreach (var entry in previous)
                    if (Array.IndexOf(next, entry) < 0) Unhold(entry, holder);
            foreach (var entry in next) Hold(entry, holder);
            if (next.Length > 0) _slots[holder] = next;
        }
    }

    // A route was released: so is everything it held.
    internal void ReleaseRoute(object route)
    {
        lock (_gate)
        {
            if (!_routes.Remove(route, out var entries)) return;
            foreach (var entry in entries)
            {
                entry.Holders.RemoveWhere(holder => ReferenceEquals(holder.Route, route));
                if (entry.Holders.Count == 0) Release(entry);
            }
            foreach (var holder in _slots.Keys.Where(holder => ReferenceEquals(holder.Route, route)).ToArray())
                _slots.Remove(holder);
        }
    }

    internal bool TryResolve(string token, out RunicBlob blob)
    {
        lock (_gate)
        {
            if (_byToken.TryGetValue(token, out var entry))
            {
                blob = entry.Blob;
                return true;
            }
        }
        blob = null!;
        return false;
    }

    // The number of blobs the Window serves now, for tests.
    internal int BlobCount
    {
        get { lock (_gate) return _byToken.Count; }
    }

    private void Hold(BlobEntry entry, Holder holder)
    {
        entry.Holders.Add(holder);
        if (!_routes.TryGetValue(holder.Route, out var held)) _routes.Add(holder.Route, held = new(ReferenceEqualityComparer.Instance));
        held.Add(entry);
    }

    private void Unhold(BlobEntry entry, Holder holder)
    {
        entry.Holders.Remove(holder);
        if (!entry.Holders.Any(other => ReferenceEquals(other.Route, holder.Route)) &&
            _routes.TryGetValue(holder.Route, out var held))
        {
            held.Remove(entry);
            if (held.Count == 0) _routes.Remove(holder.Route);
        }
        if (entry.Holders.Count == 0) Release(entry);
    }

    private void Release(BlobEntry entry)
    {
        _byBlob.Remove(entry.Blob);
        _byToken.Remove(entry.Token);
    }

    // ---- Uploads ----

    // The upload token of a connection whose hello lists blobs/1, or null when the host takes no uploads.
    internal BridgeUploadGrant? Grant(string connectionKey)
    {
        lock (_gate)
        {
            if (_closed || _endpoint?.UploadUrl() is not { } uploadUrl) return null;
            if (!_tokenByConnection.TryGetValue(connectionKey, out var token))
            {
                token = NewToken();
                _tokenByConnection.Add(connectionKey, token);
                _connectionByToken.Add(token, connectionKey);
            }
            return new BridgeUploadGrant(uploadUrl, token, MaximumUploadBytes);
        }
    }

    // A connection closed: its token stops working, and its unclaimed uploads are deleted.
    internal void ConnectionClosed(string connectionKey)
    {
        List<StoredUpload> deleted = [];
        lock (_gate)
        {
            if (_tokenByConnection.Remove(connectionKey, out var token)) _connectionByToken.Remove(token);
            foreach (var upload in _uploads.Values.Where(upload => upload.Connection == connectionKey).ToArray())
                if (TakeUnderGate(upload)) deleted.Add(upload);
        }
        foreach (var upload in deleted) upload.Delete(Logger);
    }

    // A RunicUpload for an id that a request of connection names; null for an id that is unknown, of another
    // connection, claimed or expired.
    internal RunicUpload? Find(string id, string? connection)
    {
        lock (_gate)
        {
            if (!_uploads.TryGetValue(id, out var upload)) return null;
            if (connection is not null && upload.Connection != connection) return null;
            return new RunicUpload(upload.Id, upload.FileName, upload.ContentType, upload.Length, upload);
        }
    }

    // Simulated uploads (Runic.Application.Testing), stored as a page's upload of the connection is.
    internal async Task<RunicUpload> StoreAsync(Stream content, string? fileName, string? contentType, string connection,
        CancellationToken cancellationToken)
    {
        var outcome = await StoreCoreAsync(content, null, fileName, contentType, connection, cancellationToken, simulated: true)
            .ConfigureAwait(false);
        if (outcome.Upload is not { } upload) throw new InvalidOperationException($"The upload was rejected: {outcome.Message}");
        return new RunicUpload(upload.Id, upload.FileName, upload.ContentType, upload.Length, upload);
    }

    internal int UploadCount
    {
        get { lock (_gate) return _uploads.Count; }
    }

    // Receives an upload request whose origin the host has admitted: POST with the connection's token.
    internal async Task ReceiveUploadAsync(IBridgeBlobExchange exchange)
    {
        var token = exchange.GetRequestHeader(UploadTokenHeader);
        string? connection;
        lock (_gate) connection = token.Length == 0 ? null : _connectionByToken.GetValueOrDefault(token);
        if (connection is null)
        {
            BlobsLog.RequestDenied(Logger, "upload", "no valid upload token");
            await WriteErrorAsync(exchange, 403, "forbidden", "The upload token is not valid for an open connection of this window.").ConfigureAwait(false);
            return;
        }
        long? declared = null;
        var lengthHeader = exchange.GetRequestHeader("Content-Length");
        if (lengthHeader.Length > 0)
        {
            if (!long.TryParse(lengthHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            {
                await WriteErrorAsync(exchange, 400, "invalid-request", "The Content-Length is not valid.").ConfigureAwait(false);
                return;
            }
            declared = length;
        }
        var name = exchange.GetRequestHeader(UploadNameHeader);
        string? fileName = null;
        if (name.Length > 0)
        {
            try { fileName = Uri.UnescapeDataString(name); }
            catch (UriFormatException) { fileName = null; }
        }
        var outcome = await StoreCoreAsync(exchange.RequestBody, declared, fileName, exchange.GetRequestHeader("Content-Type"),
            connection, exchange.RequestAborted).ConfigureAwait(false);
        if (outcome.Upload is not { } upload)
        {
            BlobsLog.UploadRejected(Logger, outcome.Reason!, outcome.Message!);
            await WriteErrorAsync(exchange, outcome.Status, outcome.Reason!, outcome.Message!).ConfigureAwait(false);
            return;
        }
        var body = BridgeMessages.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WritePropertyName("upload");
            WriteUpload(writer, upload.Id, upload.FileName, upload.ContentType, upload.Length);
            writer.WriteEndObject();
        });
        await WriteJsonAsync(exchange, 201, body).ConfigureAwait(false);
    }

    private async Task<StoreOutcome> StoreCoreAsync(Stream body, long? declared, string? fileName, string? contentType,
        string connection, CancellationToken cancellationToken, bool simulated = false)
    {
        var limit = MaximumUploadBytes;
        if (declared > limit)
            return StoreOutcome.Rejected(413, "too-large", $"The upload is larger than the window's limit of {limit} bytes.");
        string directory;
        lock (_gate)
        {
            if (_closed) return StoreOutcome.Rejected(410, "released", "The window closed.");
            if (_receiving >= MaximumConcurrentUploads)
                return StoreOutcome.Rejected(429, "capacity", $"The window already receives {MaximumConcurrentUploads} uploads.");
            if (_storedBytes + (declared ?? 0) > MaximumWindowUploadBytes)
                return StoreOutcome.Rejected(507, "capacity", "The window's uploads that no command claimed use its whole upload space.");
            _receiving++;
            directory = _directory ??= CreateDirectory();
        }
        var id = RunicUploadIds.Create();
        var path = Path.Combine(directory, id);
        long received = 0;
        var reserved = 0L;
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                BufferSize = 0,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(path, options))
            {
                var buffer = new byte[81_920];
                while (true)
                {
                    var read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    received += read;
                    if (received > limit)
                        return Fail(StoreOutcome.Rejected(413, "too-large", $"The upload is larger than the window's limit of {limit} bytes."));
                    lock (_gate)
                    {
                        if (_closed) return Fail(StoreOutcome.Rejected(410, "released", "The window closed."));
                        if (_storedBytes + read > MaximumWindowUploadBytes)
                            return Fail(StoreOutcome.Rejected(507, "capacity", "The window's uploads that no command claimed use its whole upload space."));
                        _storedBytes += read;
                        reserved += read;
                    }
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            if (declared is { } expected && expected != received)
                return Fail(StoreOutcome.Rejected(400, "invalid-request", "The upload ended before its Content-Length."));
            var type = contentType is { Length: > 0 } && IsMediaType(contentType) ? contentType : "application/octet-stream";
            var upload = new StoredUpload(this, id, connection, RunicUpload.SanitizeFileName(fileName ?? "upload"), type, received, path);
            lock (_gate)
            {
                _receiving--;
                if (_closed || !simulated && !_tokenByConnection.ContainsKey(connection))
                {
                    _storedBytes -= reserved;
                    upload.Delete(Logger);
                    return StoreOutcome.Rejected(410, "released", "The connection closed.");
                }
                _uploads.Add(id, upload);
                upload.Expiry = TimeProvider.CreateTimer(static state => ((StoredUpload)state!).Expire(), upload,
                    UnclaimedLifetime, Timeout.InfiniteTimeSpan);
            }
            return StoreOutcome.Stored(upload);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            if (error is not OperationCanceledException) BlobsLog.UploadReceiveFailed(Logger, error, error.GetType().Name);
            return Fail(error is OperationCanceledException
                ? StoreOutcome.Rejected(400, "cancelled", "The upload was cancelled.")
                : StoreOutcome.Rejected(500, "failed", "The upload could not be stored."));
        }

        StoreOutcome Fail(StoreOutcome outcome)
        {
            lock (_gate)
            {
                _receiving--;
                _storedBytes -= reserved;
            }
            TryDelete(path, Logger);
            return outcome;
        }
    }

    private string CreateDirectory()
    {
        var root = UploadRoot;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var directory = Path.Combine(root, NewToken());
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }

    // Takes an unclaimed upload out of the Window; true when this call took it.
    private bool TakeUnderGate(StoredUpload upload)
    {
        if (!_uploads.Remove(upload.Id)) return false;
        upload.Expiry?.Dispose();
        _storedBytes -= upload.Length;
        return true;
    }

    internal bool Take(StoredUpload upload)
    {
        lock (_gate) return TakeUnderGate(upload);
    }

    internal bool Contains(StoredUpload upload)
    {
        lock (_gate) return _uploads.TryGetValue(upload.Id, out var current) && ReferenceEquals(current, upload);
    }

    internal void Expired(StoredUpload upload)
    {
        if (!Take(upload)) return;
        BlobsLog.UploadExpired(Logger, upload.Length);
        upload.Delete(Logger);
    }


    // ---- Serving ----

    // Serves GET or HEAD of a blob token whose request the host has admitted.
    internal async Task ServeBlobAsync(string token, IBridgeBlobExchange exchange)
    {
        exchange.SetResponseHeader("X-Content-Type-Options", "nosniff");
        exchange.SetResponseHeader("Content-Security-Policy", ContentSecurityPolicy);
        exchange.SetResponseHeader("Cross-Origin-Resource-Policy", "same-site");
        exchange.SetResponseHeader("Referrer-Policy", "no-referrer");
        if (!IsToken(token) || !TryResolve(token, out var blob))
        {
            exchange.SetResponseHeader("Cache-Control", "no-store");
            exchange.StatusCode = 404;
            return;
        }
        var head = string.Equals(exchange.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
        var entityTag = "\"" + token + "\"";
        exchange.SetResponseHeader("Cache-Control", CacheControl);
        exchange.SetResponseHeader("ETag", entityTag);
        if (exchange.GetRequestHeader("If-None-Match") is { Length: > 0 } match &&
            (match.Trim() == "*" || match.Split(',').Any(candidate => candidate.Trim().TrimStart('W', '/') == entityTag)))
        {
            exchange.StatusCode = 304;
            return;
        }
        Stream stream;
        try
        {
            stream = await blob.OpenReadAsync(exchange.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (exchange.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error)
        {
            BlobsLog.BlobServeFailed(Logger, error, error.GetType().Name);
            exchange.SetResponseHeader("Cache-Control", "no-store");
            exchange.StatusCode = 500;
            return;
        }
        await using (stream.ConfigureAwait(false))
        {
            exchange.ContentType = blob.ContentType;
            exchange.SetResponseHeader("Content-Disposition", ContentDisposition(blob));
            long? length = stream.CanSeek ? stream.Length - stream.Position : blob.Length;
            long start = 0;
            long? count = length;
            if (stream.CanSeek && length is { } total)
            {
                exchange.SetResponseHeader("Accept-Ranges", "bytes");
                var range = exchange.GetRequestHeader("Range");
                if (range.Length > 0 && (exchange.GetRequestHeader("If-Range") is not { Length: > 0 } ifRange || ifRange == entityTag))
                {
                    if (TryParseRange(range, total, out start, out var rangeCount))
                    {
                        if (rangeCount < 0)
                        {
                            exchange.SetResponseHeader("Content-Range", $"bytes */{total}");
                            exchange.StatusCode = 416;
                            return;
                        }
                        count = rangeCount;
                        exchange.StatusCode = 206;
                        exchange.SetResponseHeader("Content-Range", string.Create(CultureInfo.InvariantCulture,
                            $"bytes {start}-{start + rangeCount - 1}/{total}"));
                        stream.Seek(start, SeekOrigin.Current);
                    }
                }
            }
            if (count is { } known) exchange.ContentLength = known;
            if (exchange.StatusCode is not 206) exchange.StatusCode = 200;
            if (head) return;
            try
            {
                var buffer = new byte[81_920];
                var remaining = count ?? long.MaxValue;
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), exchange.RequestAborted).ConfigureAwait(false);
                    if (read == 0) break;
                    await exchange.ResponseBody.WriteAsync(buffer.AsMemory(0, read), exchange.RequestAborted).ConfigureAwait(false);
                    remaining -= read;
                }
                // A stream shorter than its declared length: the response cannot end well, so it is aborted.
                if (count is not null && remaining > 0) throw new IOException("The blob's stream ended before its length.");
            }
            catch (OperationCanceledException) when (exchange.RequestAborted.IsCancellationRequested)
            {
            }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
                BlobsLog.BlobServeFailed(Logger, error, error.GetType().Name);
                throw;
            }
        }
    }

    // One range of RFC 9110: bytes=a-b, bytes=a- or bytes=-n. Several ranges, or another unit, are ignored and the
    // whole content is sent. An unsatisfiable range sets count to -1.
    internal static bool TryParseRange(string header, long total, out long start, out long count)
    {
        start = 0;
        count = 0;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var spec = header.AsSpan(6).Trim();
        if (spec.Contains(',')) return false;
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;
        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        if (first.Length == 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix)) return false;
            if (suffix == 0 || total == 0) { count = -1; return true; }
            start = Math.Max(0, total - suffix);
            count = total - start;
            return true;
        }
        if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out start)) return false;
        long end = total - 1;
        if (last.Length > 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start) return false;
            end = Math.Min(end, total - 1);
        }
        if (start >= total) { count = -1; return true; }
        count = end - start + 1;
        return true;
    }

    internal static string ContentDisposition(RunicBlob blob)
    {
        var kind = blob.Disposition == RunicBlobDisposition.Attachment ? "attachment" : "inline";
        if (blob.FileName is not { } name) return kind;
        var ascii = new StringBuilder(name.Length);
        foreach (var character in name)
            ascii.Append(character is >= ' ' and < (char)127 and not '"' and not '\\' and not '%' ? character : '_');
        return $"{kind}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
    }

    internal static bool IsToken(string token) =>
        token.Length == 32 && token.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsMediaType(string value)
    {
        try { RunicBlob.ValidateContentType(value); return true; }
        catch (ArgumentException) { return false; }
    }

    private static string NewToken() => RunicUploadIds.Create();

    internal static void WriteUpload(Utf8JsonWriter writer, string id, string name, string type, long size)
    {
        writer.WriteStartObject();
        writer.WriteString("id", id);
        writer.WriteString("name", name);
        writer.WriteString("type", type);
        writer.WriteNumber("size", size);
        writer.WriteEndObject();
    }

    internal static Task WriteErrorAsync(IBridgeBlobExchange exchange, int status, string reason, string message) =>
        WriteJsonAsync(exchange, status, BridgeMessages.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", false);
            writer.WriteStartObject("error");
            writer.WriteString("kind", reason is "released" ? "released" : "rejected");
            writer.WriteString("reason", reason);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }));

    private static async Task WriteJsonAsync(IBridgeBlobExchange exchange, int status, byte[] body)
    {
        exchange.SetResponseHeader("Cache-Control", "no-store");
        exchange.SetResponseHeader("X-Content-Type-Options", "nosniff");
        exchange.StatusCode = status;
        exchange.ContentType = "application/json; charset=utf-8";
        exchange.ContentLength = body.Length;
        try { await exchange.ResponseBody.WriteAsync(body, exchange.RequestAborted).ConfigureAwait(false); }
        catch (OperationCanceledException) when (exchange.RequestAborted.IsCancellationRequested) { }
        catch (IOException) { }
    }

    internal static void TryDelete(string path, ILogger logger)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            BlobsLog.CleanupFailed(logger, error, error.GetType().Name);
        }
    }

    // The Window closed: every blob is released and every upload that no command claimed is deleted.
    public void Dispose()
    {
        StoredUpload[] uploads;
        string? directory;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _byBlob.Clear();
            _byToken.Clear();
            _slots.Clear();
            _routes.Clear();
            _connectionByToken.Clear();
            _tokenByConnection.Clear();
            uploads = [.. _uploads.Values];
            _uploads.Clear();
            _storedBytes = 0;
            directory = _directory;
            _directory = null;
            _unavailableLogged = false;
        }
        foreach (var upload in uploads)
        {
            upload.Expiry?.Dispose();
            upload.Delete(Logger);
        }
        if (directory is null) return;
        // A claimed upload that is still being read is deleted when its stream closes (DeleteOnClose).
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                BlobsLog.CleanupFailed(Logger, error, error.GetType().Name);
        }
    }

    internal readonly record struct Holder(object Route, string? Slot);

    internal sealed class BlobEntry(string token, RunicBlob blob)
    {
        internal string Token { get; } = token;
        internal RunicBlob Blob { get; } = blob;
        internal HashSet<Holder> Holders { get; } = [];
    }

    private readonly record struct StoreOutcome(StoredUpload? Upload, int Status, string? Reason, string? Message)
    {
        internal static StoreOutcome Stored(StoredUpload upload) => new(upload, 201, null, null);
        internal static StoreOutcome Rejected(int status, string reason, string message) => new(null, status, reason, message);
    }
}

// Where a host serves a Window's blobs and takes its uploads, read when a blob is encoded or a hello granted: a host
// may know its URL only once its window opened. BlobBase returns an absolute URL that ends with '/', which a blob's
// token follows; UploadUrl the URL of uploads. Either returns null while the host serves nothing.
internal sealed record BridgeBlobEndpoint(Func<Uri?> BlobBase, Func<Uri?> UploadUrl)
{
    // The paths of a host that serves both below one base URL, such as a Desktop surface.
    internal const string BlobsPath = "_runic/blobs/";
    internal const string UploadPath = "_runic/upload";

    internal static BridgeBlobEndpoint Below(Func<Uri?> baseUrl) => new(
        () => baseUrl() is { } root ? new Uri(root, BlobsPath) : null,
        () => baseUrl() is { } root ? new Uri(root, UploadPath) : null);
}

// The blobs member of a hello reply.
internal sealed record BridgeUploadGrant(Uri UploadUrl, string Token, long MaximumBytes);

// One HTTP request to a Window's blob or upload endpoint, as a host presents it.
internal interface IBridgeBlobExchange
{
    string Method { get; }
    // The header's value, or an empty string.
    string GetRequestHeader(string name);
    Stream RequestBody { get; }
    int StatusCode { get; set; }
    string? ContentType { set; }
    long? ContentLength { set; }
    void SetResponseHeader(string name, string value);
    Stream ResponseBody { get; }
    CancellationToken RequestAborted { get; }
}

// An upload stored in the Window's folder.
internal sealed class StoredUpload(BridgeBlobs owner, string id, string connection, string fileName, string contentType,
    long length, string path) : IRunicUploadContent
{
    private int _deleted;
    internal string Id { get; } = id;
    internal string Connection { get; } = connection;
    internal string FileName { get; } = fileName;
    internal string ContentType { get; } = contentType;
    internal long Length { get; } = length;
    internal string Path { get; } = path;
    internal ITimer? Expiry { get; set; }

    public bool IsGone => !owner.Contains(this);

    public bool TryClaim() => owner.Take(this);

    public Stream OpenRead() => new FileStream(Path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.Delete,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose,
    });

    public async ValueTask SaveAsAsync(string path, bool overwrite, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() => File.Move(Path, path, overwrite), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(Path)) Delete(owner.Logger);
        }
    }

    public void Discard()
    {
        if (TryClaim()) Delete(owner.Logger);
    }

    internal void Expire() => owner.Expired(this);

    internal void Delete(ILogger logger)
    {
        if (Interlocked.Exchange(ref _deleted, 1) != 0) return;
        BridgeBlobs.TryDelete(Path, logger);
    }
}

// The Window, route and holder that the blobs and uploads a route encodes or decodes belong to. ViewModelBridge
// enters it around its requests (with the request's connection) and publications; a state member's write enters a slot,
// whose blobs then replace the member's earlier ones.
internal sealed class BridgeBlobScope
{
    private static readonly AsyncLocal<BridgeBlobScope?> CurrentScope = new();

    private BridgeBlobScope(BridgeBlobs blobs, object route, string? connection, List<BridgeBlobs.BlobEntry>? slot)
    {
        Blobs = blobs;
        Route = route;
        Connection = connection;
        Slot = slot;
    }

    internal static BridgeBlobScope? Current => CurrentScope.Value;
    internal BridgeBlobs Blobs { get; }
    internal object Route { get; }
    internal string? Connection { get; }
    internal List<BridgeBlobs.BlobEntry>? Slot { get; }

    internal static Restore Enter(BridgeBlobs blobs, object route, string? connection)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = new BridgeBlobScope(blobs, route, connection, null);
        return new Restore(true, previous, null, null);
    }

    // Collects the blobs a state member's write encodes; disposing replaces the member's blobs with them.
    internal static Restore EnterSlot(string member)
    {
        var previous = CurrentScope.Value;
        if (previous is null) return default;
        var slot = new List<BridgeBlobs.BlobEntry>();
        CurrentScope.Value = new BridgeBlobScope(previous.Blobs, previous.Route, previous.Connection, slot);
        return new Restore(true, previous, member, slot);
    }

    // default restores nothing: a slot entered without a scope.
    internal readonly struct Restore(bool active, BridgeBlobScope? previous, string? member, List<BridgeBlobs.BlobEntry>? slot) : IDisposable
    {
        public void Dispose()
        {
            if (!active) return;
            CurrentScope.Value = previous;
            if (member is not null) previous!.Blobs.ReplaceSlot(previous.Route, member, slot!);
        }
    }
}

// Event ids 1200–1209 (README, "Logging and telemetry").
internal static partial class BlobsLog
{
    [LoggerMessage(EventId = 1200, EventName = "BlobServeFailed", Level = LogLevel.Warning,
        Message = "A blob could not be served: {ErrorType}.")]
    internal static partial void BlobServeFailed(ILogger logger, Exception exception, string errorType);

    [LoggerMessage(EventId = 1201, EventName = "UploadRejected", Level = LogLevel.Information,
        Message = "An upload was rejected ({Reason}): {Detail}")]
    internal static partial void UploadRejected(ILogger logger, string reason, string detail);

    [LoggerMessage(EventId = 1202, EventName = "UploadReceiveFailed", Level = LogLevel.Warning,
        Message = "An upload could not be stored: {ErrorType}.")]
    internal static partial void UploadReceiveFailed(ILogger logger, Exception exception, string errorType);

    [LoggerMessage(EventId = 1203, EventName = "UploadExpired", Level = LogLevel.Debug,
        Message = "An upload of {Length} bytes that no command claimed was deleted.")]
    internal static partial void UploadExpired(ILogger logger, long length);

    [LoggerMessage(EventId = 1204, EventName = "BlobRequestDenied", Level = LogLevel.Warning,
        Message = "A {Request} request to the window's blob endpoint was denied: {Reason}.")]
    internal static partial void RequestDenied(ILogger logger, string request, string reason);

    [LoggerMessage(EventId = 1205, EventName = "BlobsUnavailable", Level = LogLevel.Warning,
        Message = "A RunicBlob crossed the Bridge, but this window's host serves no blobs, so its URL is empty.")]
    internal static partial void BlobsUnavailable(ILogger logger);

    [LoggerMessage(EventId = 1206, EventName = "BlobCleanupFailed", Level = LogLevel.Warning,
        Message = "A stored upload could not be deleted: {ErrorType}.")]
    internal static partial void CleanupFailed(ILogger logger, Exception exception, string errorType);

    [LoggerMessage(EventId = 1207, EventName = "BlobEndpointFailed", Level = LogLevel.Warning,
        Message = "The window's blob endpoint could not start: {ErrorType}. Blobs and uploads are unavailable.")]
    internal static partial void EndpointFailed(ILogger logger, Exception exception, string errorType);
}
