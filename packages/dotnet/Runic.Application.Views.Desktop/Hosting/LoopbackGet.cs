using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Runic.Application.Views.Desktop.Hosting;

// One HTTP/1.1 GET to the guest's own loopback surface, for the component manifest (HostedRegion.ReadComponentModuleAsync).
// It replaces an HttpClient, which brings the managed TLS, X.509 and HTTP client stack into a trimmed or NativeAOT
// application (D072.2, S3); a TcpClient is linked already for the host channel (HostChannelListener). It speaks only what
// a Desktop server answers to a plain GET: http:// loopback only, no proxy, no redirects, no cookies, no content coding,
// Connection: close, and a body framed by Content-Length, chunked transfer coding or the end of the connection. The
// header block and the body are bounded, and the caller's token is the exchange's deadline. One read loop and a
// synchronous parser over what has arrived keep its code small.
internal static class LoopbackGet
{
    internal const int MaximumHeaderBytes = 16_384;

    // The body of a 200 answer to GET url sent with the given Origin, or null for another status, a body over
    // maximumBodyBytes, or an answer that is not valid HTTP/1.1. Connection failures and the deadline throw IOException,
    // SocketException or OperationCanceledException.
    internal static async Task<byte[]?> GetAsync(Uri url, string origin, int maximumBodyBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(origin);
        // Origin is a canonical origin (WebUiWindow.FrameEmbedderHost); nothing may end its header line.
        if (url.Scheme != Uri.UriSchemeHttp || !url.IsLoopback || origin.AsSpan().IndexOfAny('\r', '\n') >= 0) return null;
        var request = "GET " + url.PathAndQuery + " HTTP/1.1\r\nHost: " + url.Authority + "\r\nOrigin: " + origin +
            "\r\nConnection: close\r\n\r\n";
        using var client = new TcpClient();
        await client.ConnectAsync(url.DnsSafeHost, url.Port, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken).ConfigureAwait(false);
        // The header, the body and room for chunk lines and trailers.
        var limit = 2 * (MaximumHeaderBytes + maximumBodyBytes);
        var buffer = new byte[4096];
        var length = 0;
        var response = new Response(maximumBodyBytes);
        while (true)
        {
            if (length == buffer.Length)
            {
                if (length == limit) return null;
                Array.Resize(ref buffer, Math.Min(limit, 2 * length));
            }
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            length += read;
            switch (response.Parse(buffer.AsSpan(0, length), ended: read == 0, out var body))
            {
                case Progress.Complete: return body;
                case Progress.Invalid: return null;
            }
        }
    }

    internal enum Progress
    {
        Incomplete,
        Complete,
        Invalid,
    }

    // The parser of one response, called with everything received so far; it remembers the header once it has read it.
    internal sealed class Response(int maximumBodyBytes)
    {
        private int _headerEnd = -1;
        private int _searched;
        private long? _contentLength;
        private bool _chunked;

        internal Progress Parse(ReadOnlySpan<byte> received, bool ended, out byte[]? body)
        {
            body = null;
            if (_headerEnd < 0)
            {
                var start = Math.Max(0, _searched - 3);
                var end = received[start..].IndexOf("\r\n\r\n"u8);
                _searched = received.Length;
                if (end < 0) return received.Length > MaximumHeaderBytes || ended ? Progress.Invalid : Progress.Incomplete;
                _headerEnd = start + end + 4;
                if (_headerEnd > MaximumHeaderBytes || !ReadHeader(received[..(_headerEnd - 4)])) return Progress.Invalid;
            }
            var content = received[_headerEnd..];
            if (_contentLength is { } declared)
            {
                if (declared > maximumBodyBytes) return Progress.Invalid;
                if (content.Length >= declared)
                {
                    body = content[..(int)declared].ToArray();
                    return Progress.Complete;
                }
                return ended ? Progress.Invalid : Progress.Incomplete;
            }
            if (_chunked)
            {
                var progress = Dechunk(content, maximumBodyBytes, out body);
                return progress == Progress.Incomplete && ended ? Progress.Invalid : progress;
            }
            if (content.Length > maximumBodyBytes) return Progress.Invalid;
            if (!ended) return Progress.Incomplete;
            body = content.ToArray();
            return Progress.Complete;
        }

        // The status line and the framing headers; false when they are not a valid 200 answer with one framing.
        private bool ReadHeader(ReadOnlySpan<byte> header)
        {
            var lines = Encoding.Latin1.GetString(header).Split("\r\n");
            // A bare CR or LF inside the header is not HTTP/1.1.
            if (lines.Any(static line => line.AsSpan().IndexOfAny('\r', '\n') >= 0)) return false;
            // HTTP/1.x 200 Reason
            var status = lines[0].Split(' ', 3);
            if (status.Length < 2 || !status[0].StartsWith("HTTP/1.", StringComparison.Ordinal) || status[1] != "200") return false;
            foreach (var line in lines.AsSpan(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) return false;
                var name = line.AsSpan(0, colon).Trim();
                var value = line.AsSpan(colon + 1).Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                        _contentLength is { } earlier && earlier != length)
                        return false;
                    _contentLength = length;
                }
                else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    // Only chunked alone: a Desktop server applies no other transfer coding.
                    if (_chunked || !value.Equals("chunked", StringComparison.OrdinalIgnoreCase)) return false;
                    _chunked = true;
                }
            }
            // Both framings at once is how messages are smuggled; a Desktop server never sends it.
            return !(_chunked && _contentLength is not null);
        }
    }

    // A chunked body: chunks of a hexadecimal size line (with an ignored extension), the data and CRLF, a last chunk of
    // size 0, then trailer fields (ignored) up to an empty line.
    private static Progress Dechunk(ReadOnlySpan<byte> content, int maximumBodyBytes, out byte[]? body)
    {
        body = null;
        var total = 0;
        var position = 0;
        // First the sizes, so the body is copied once.
        while (true)
        {
            var line = content[position..].IndexOf("\r\n"u8);
            if (line < 0) return content.Length - position > 1024 ? Progress.Invalid : Progress.Incomplete;
            var size = content.Slice(position, line);
            if (size.IndexOf((byte)';') is var semicolon and >= 0) size = size[..semicolon];
            size = size.Trim((byte)' ');
            if (size.Length is 0 or > 8 || !int.TryParse(Encoding.Latin1.GetString(size), NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out var length) || length < 0)
                return Progress.Invalid;
            position += line + 2;
            if (length == 0) break;
            if (total + (long)length > maximumBodyBytes) return Progress.Invalid;
            if (content.Length - position < length + 2) return Progress.Incomplete;
            if (!content.Slice(position + length, 2).SequenceEqual("\r\n"u8)) return Progress.Invalid;
            total += length;
            position += length + 2;
        }
        // Trailer fields up to the empty line.
        while (true)
        {
            var line = content[position..].IndexOf("\r\n"u8);
            if (line < 0) return Progress.Incomplete;
            position += line + 2;
            if (line == 0) break;
        }
        var result = new byte[total];
        var filled = 0;
        position = 0;
        while (filled < total)
        {
            var line = content[position..].IndexOf("\r\n"u8);
            var size = content.Slice(position, line);
            if (size.IndexOf((byte)';') is var semicolon and >= 0) size = size[..semicolon];
            var length = int.Parse(Encoding.Latin1.GetString(size.Trim((byte)' ')), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            position += line + 2;
            content.Slice(position, length).CopyTo(result.AsSpan(filled));
            filled += length;
            position += length + 2;
        }
        body = result;
        return Progress.Complete;
    }
}
