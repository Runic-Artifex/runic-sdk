using System.Globalization;
using System.Text;

namespace Runic.Application.Views.CsWebUi;

// The limits of LoopbackHttpServer. The defaults are Kestrel's for the head (a request line of 8 KiB, a head of 32 KiB
// and 100 header fields); tests shorten the timeouts.
internal sealed record LoopbackHttpLimits
{
    // The request line, without its CRLF; longer is 414.
    internal int MaxRequestLineBytes { get; init; } = 8 * 1024;
    // The whole head, from the request line to the empty line; longer is 431. Also bounds a chunked body's trailer.
    internal int MaxHeadBytes { get; init; } = 32 * 1024;
    // Header fields of a head, and of a chunked body's trailer; more is 431.
    internal int MaxHeaderCount { get; init; } = 100;
    // A chunk-size line of a chunked body, with its extensions.
    internal int MaxChunkLineBytes { get; init; } = 1024;
    // From the accept to the first byte of the first request: a new connection that sends nothing closes quietly after
    // it, so that idle sockets cannot hold the connection slots for long (DEV-SEC-68a).
    internal TimeSpan FirstByteTimeout { get; init; } = TimeSpan.FromSeconds(5);
    // From the end of a response to the first byte of the next request: an idle keep-alive connection closes quietly
    // after it.
    internal TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    // From the first byte of a head to its end: a head sent a byte at a time (slowloris) is answered 408 after it. Also
    // bounds the discarding of an unread body.
    internal TimeSpan HeadTimeout { get; init; } = TimeSpan.FromSeconds(10);
    // Each write of a response; a client that stops reading loses its connection.
    internal TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(30);
    // Each read of a request body. The endpoint's uploads have their own, shorter idle limit (BridgeBlobs.IdleTimeout).
    internal TimeSpan BodyReadTimeout { get; init; } = TimeSpan.FromSeconds(60);
    // An unread rest of a request body up to this size is read and discarded after the response, so the connection
    // stays usable and the client never meets a reset before it read the response; a longer one closes the connection.
    internal long MaxDrainBytes { get; init; } = 4L * 1_048_576;
    // How long a closing connection keeps reading what the client still sends after the response (a lingering close).
    internal TimeSpan LingerTimeout { get; init; } = TimeSpan.FromSeconds(2);
    internal int MaxRequestsPerConnection { get; init; } = 100;
    internal int MaxConnections { get; init; } = 128;
    // The largest request body the server passes on: a body that sends more fails its read (as the endpoint's upload
    // limit does before it, BridgeBlobs.MaximumUploadBytes). The handler sees the byte after the limit, so its own check
    // answers first.
    internal Func<long> MaxBodyBytes { get; init; } = static () => long.MaxValue;
}

// A parsed request head of LoopbackHttpServer.
internal sealed class LoopbackHttpRequestHead
{
    private readonly List<KeyValuePair<string, string>> _fields;

    internal LoopbackHttpRequestHead(string method, string target, string path, bool http11,
        List<KeyValuePair<string, string>> fields, long? contentLength, bool chunked, bool keepAlive, bool expectContinue)
    {
        Method = method;
        Target = target;
        Path = path;
        Http11 = http11;
        _fields = fields;
        ContentLength = contentLength;
        Chunked = chunked;
        KeepAlive = keepAlive;
        ExpectContinue = expectContinue;
    }

    internal string Method { get; }
    // The request target as sent (origin-form), and its path without the query.
    internal string Target { get; }
    internal string Path { get; }
    internal bool Http11 { get; }
    // Null without Content-Length; never set together with Chunked.
    internal long? ContentLength { get; }
    internal bool Chunked { get; }
    internal bool HasBody => Chunked || ContentLength > 0;
    // The client allows the connection to stay open: HTTP/1.1 without Connection: close. HTTP/1.0 never does.
    internal bool KeepAlive { get; }
    internal bool ExpectContinue { get; }

    // The values of a header field, joined with ", " as HttpListener joins them; an empty string without one.
    internal string Header(string name)
    {
        string? joined = null;
        foreach (var (field, value) in _fields)
        {
            if (!string.Equals(field, name, StringComparison.OrdinalIgnoreCase)) continue;
            joined = joined is null ? value : joined + ", " + value;
        }
        return joined ?? string.Empty;
    }
}

// The strict HTTP/1.1 request parser of LoopbackHttpServer (RFC 9110, RFC 9112). It accepts what browsers and HTTP
// clients send and refuses everything another parser on the way could read differently:
//
// - Lines end in CRLF. A bare CR or LF anywhere in the head is 400, never a line end.
// - The request line is method SP origin-form SP HTTP/1.1 or HTTP/1.0, with single spaces. The target starts with "/",
//   holds RFC 3986 path and query characters only, valid percent escapes, and no dot segment (also escaped), so the
//   path the endpoint compares is the path the client named.
// - A field is token ":" OWS value OWS, without whitespace before the colon and without obsolete line folding. A value
//   holds visible characters, spaces and tabs only (obs-text is kept as Latin-1).
// - Host appears once and is the listener's own host and port (no DNS rebinding).
// - The body's framing is never ambiguous (no request smuggling): Content-Length appears once, as digits only;
//   Transfer-Encoding appears once, as exactly "chunked", in HTTP/1.1 only; and never both.
// - Fields the endpoint decides on (Origin, Sec-Fetch-Site, the upload headers, Range and others) appear once: a
//   repeated one could be read as either value.
// - Expect is 100-continue or 417.
internal static class LoopbackHttpParser
{
    // Fields that may appear once only.
    private static readonly string[] Singletons =
    [
        "Host", "Content-Length", "Transfer-Encoding", "Content-Type", "Origin", "Referer", "Sec-Fetch-Site", "Sec-Fetch-Mode",
        "Sec-Fetch-Dest", "Range", "If-Range", "Expect", "Upgrade", BridgeBlobs.UploadTokenHeader, BridgeBlobs.UploadNameHeader,
    ];

    // The length of the head at the start of buffer, through its empty line; 0 while it is incomplete; -1 when a bare CR
    // or LF shows that it is malformed. Empty lines before the request line, which RFC 9112 lets a server skip, are part
    // of the head. The server scans incrementally (LoopbackHttpHeadScanner); this is the same scan from the start.
    internal static int HeadLength(ReadOnlySpan<byte> buffer)
    {
        var scanner = new LoopbackHttpHeadScanner();
        return scanner.Advance(buffer);
    }

    // Parses a complete head (HeadLength's bytes) of a request to authority, the listener's "host:port". Null when it is
    // refused, with the status to answer and why.
    internal static LoopbackHttpRequestHead? Parse(ReadOnlySpan<byte> head, string authority, LoopbackHttpLimits limits,
        out int status, out string problem)
    {
        status = 400;
        problem = string.Empty;
        if (head.Length > limits.MaxHeadBytes)
        {
            status = 431;
            problem = "the head is too large";
            return null;
        }
        // Skip the leading empty lines; the head ends with CRLF CRLF (HeadLength).
        while (head.Length >= 2 && head[0] == (byte)'\r' && head[1] == (byte)'\n') head = head[2..];
        var lineEnd = head.IndexOf("\r\n"u8);
        if (lineEnd < 0)
        {
            problem = "the head has no request line";
            return null;
        }
        if (lineEnd > limits.MaxRequestLineBytes)
        {
            status = 414;
            problem = "the request line is too long";
            return null;
        }
        if (!ParseRequestLine(head[..lineEnd], out var method, out var target, out var path, out var http11, ref status, out problem))
            return null;
        head = head[(lineEnd + 2)..];
        var fields = new List<KeyValuePair<string, string>>();
        if (!ParseFields(head, limits, fields, ref status, out problem)) return null;

        foreach (var name in Singletons)
        {
            if (fields.Count(field => string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase)) > 1)
            {
                problem = $"{name} appears more than once";
                return null;
            }
        }
        var lookup = new LoopbackHttpRequestHead(method, target, path, http11, fields, null, false, false, false);
        var host = lookup.Header("Host");
        if (host.Length == 0)
        {
            problem = "the request has no Host";
            return null;
        }
        if (!string.Equals(host, authority, StringComparison.OrdinalIgnoreCase))
        {
            problem = "the Host is not the listener's";
            return null;
        }

        var transferEncoding = Present(fields, "Transfer-Encoding");
        var contentLength = Present(fields, "Content-Length");
        if (transferEncoding && contentLength)
        {
            problem = "the request has both Transfer-Encoding and Content-Length";
            return null;
        }
        var chunked = false;
        if (transferEncoding)
        {
            if (!http11)
            {
                problem = "HTTP/1.0 has no Transfer-Encoding";
                return null;
            }
            if (!string.Equals(lookup.Header("Transfer-Encoding"), "chunked", StringComparison.OrdinalIgnoreCase))
            {
                status = 501;
                problem = "the only transfer coding is chunked";
                return null;
            }
            chunked = true;
        }
        long? length = null;
        if (contentLength)
        {
            if (!TryParseDigits(lookup.Header("Content-Length"), out var value))
            {
                problem = "the Content-Length is not a number";
                return null;
            }
            length = value;
        }

        var expectContinue = false;
        var expect = lookup.Header("Expect");
        if (Present(fields, "Expect"))
        {
            if (!string.Equals(expect, "100-continue", StringComparison.OrdinalIgnoreCase))
            {
                status = 417;
                problem = "the only expectation is 100-continue";
                return null;
            }
            expectContinue = http11;
        }

        var keepAlive = http11;
        foreach (var option in lookup.Header("Connection").Split(','))
            if (string.Equals(option.Trim(' ', '\t'), "close", StringComparison.OrdinalIgnoreCase))
                keepAlive = false;

        return new LoopbackHttpRequestHead(method, target, path, http11, fields, length, chunked, keepAlive, expectContinue);
    }

    private static bool ParseRequestLine(ReadOnlySpan<byte> line, out string method, out string target, out string path,
        out bool http11, ref int status, out string problem)
    {
        method = target = path = string.Empty;
        http11 = false;
        problem = string.Empty;
        var first = line.IndexOf((byte)' ');
        var last = line.LastIndexOf((byte)' ');
        if (first <= 0 || last == first)
        {
            problem = "the request line is not method, target and version";
            return false;
        }
        var methodBytes = line[..first];
        var targetBytes = line[(first + 1)..last];
        var version = line[(last + 1)..];
        if (methodBytes.Length > 32 || !IsToken(methodBytes))
        {
            problem = "the method is not a token";
            return false;
        }
        if (version.SequenceEqual("HTTP/1.1"u8)) http11 = true;
        else if (!version.SequenceEqual("HTTP/1.0"u8))
        {
            if (version.StartsWith("HTTP/"u8)) status = 505;
            problem = "the version is not HTTP/1.1 or HTTP/1.0";
            return false;
        }
        if (!IsOriginForm(targetBytes, out var pathLength))
        {
            problem = "the target is not a valid path";
            return false;
        }
        method = Encoding.ASCII.GetString(methodBytes);
        target = Encoding.ASCII.GetString(targetBytes);
        path = target[..pathLength];
        if (HasDotSegment(path))
        {
            problem = "the path has a dot segment";
            return false;
        }
        return true;
    }

    private static bool ParseFields(ReadOnlySpan<byte> block, LoopbackHttpLimits limits, List<KeyValuePair<string, string>> fields,
        ref int status, out string problem)
    {
        problem = string.Empty;
        while (true)
        {
            var end = block.IndexOf("\r\n"u8);
            if (end < 0)
            {
                problem = "the head does not end with an empty line";
                return false;
            }
            if (end == 0) return true;
            var line = block[..end];
            block = block[(end + 2)..];
            if (fields.Count == limits.MaxHeaderCount)
            {
                status = 431;
                problem = "the head has too many fields";
                return false;
            }
            if (!TryParseField(line, out var name, out var value, out problem)) return false;
            fields.Add(new(name, value));
        }
    }

    // A field line: token ":" OWS value OWS. Also for a chunked body's trailer.
    internal static bool TryParseField(ReadOnlySpan<byte> line, out string name, out string value, out string problem)
    {
        name = value = string.Empty;
        problem = string.Empty;
        if (line.Length > 0 && line[0] is (byte)' ' or (byte)'\t')
        {
            problem = "a field is folded";
            return false;
        }
        var colon = line.IndexOf((byte)':');
        if (colon <= 0 || !IsToken(line[..colon]))
        {
            problem = "a field name is not a token";
            return false;
        }
        var raw = line[(colon + 1)..];
        foreach (var character in raw)
        {
            if (character is (byte)'\t' or >= 0x20 and not 0x7F) continue;
            problem = "a field value holds a control character";
            return false;
        }
        name = Encoding.ASCII.GetString(line[..colon]);
        value = Encoding.Latin1.GetString(raw.Trim(" \t"u8));
        return true;
    }

    private static bool Present(List<KeyValuePair<string, string>> fields, string name) =>
        fields.Exists(field => string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase));

    // 1*DIGIT, at most 18 digits (no sign, no list, no whitespace inside, no overflow).
    internal static bool TryParseDigits(string text, out long value)
    {
        value = 0;
        if (text.Length is 0 or > 18) return false;
        foreach (var character in text)
            if (!char.IsAsciiDigit(character)) return false;
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    // tchar of RFC 9110.
    internal static bool IsToken(ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty) return false;
        foreach (var character in text)
        {
            if (char.IsAsciiLetterOrDigit((char)character)) continue;
            if ("!#$%&'*+-.^_`|~"u8.Contains(character)) continue;
            return false;
        }
        return true;
    }

    // origin-form: "/" path ["?" query] with RFC 3986 characters and valid percent escapes; pathLength is the path's.
    private static bool IsOriginForm(ReadOnlySpan<byte> target, out int pathLength)
    {
        pathLength = target.Length;
        if (target.IsEmpty || target[0] != (byte)'/') return false;
        var query = false;
        for (var index = 0; index < target.Length; index++)
        {
            var character = target[index];
            if (character == (byte)'%')
            {
                if (index + 2 >= target.Length || !char.IsAsciiHexDigit((char)target[index + 1]) || !char.IsAsciiHexDigit((char)target[index + 2]))
                    return false;
                index += 2;
                continue;
            }
            if (character == (byte)'?' && !query)
            {
                query = true;
                pathLength = index;
                continue;
            }
            if (char.IsAsciiLetterOrDigit((char)character) || "-._~!$&'()*+,;=:@/"u8.Contains(character) || query && character == (byte)'?')
                continue;
            return false;
        }
        return true;
    }

    // A "." or ".." segment, also percent-encoded, which a URL parser on the way would remove.
    private static bool HasDotSegment(string path)
    {
        foreach (var segment in path.Split('/'))
        {
            var decoded = segment.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase);
            if (decoded is "." or "..") return true;
        }
        return false;
    }

    // A chunk-size line of a chunked body: 1*HEXDIG, at most 15 digits, then optional extensions (";" and visible
    // characters, which are ignored). Null when malformed.
    internal static long? ParseChunkSize(ReadOnlySpan<byte> line)
    {
        var digits = 0;
        while (digits < line.Length && char.IsAsciiHexDigit((char)line[digits])) digits++;
        if (digits is 0 or > 15) return null;
        var rest = line[digits..];
        if (!rest.IsEmpty)
        {
            // BWS before the extension is allowed (RFC 9112 7.1.1); nothing else may follow the size.
            rest = rest.TrimStart(" \t"u8);
            if (rest.IsEmpty || rest[0] != (byte)';') return null;
            foreach (var character in rest)
                if (character is not ((byte)'\t' or >= 0x20 and < 0x7F)) return null;
        }
        return long.Parse(Encoding.ASCII.GetString(line[..digits]), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }
}

// Finds the end of a request head as its bytes arrive, looking at each byte once (DEV-SEC-68b: rescanning the whole
// buffer after every read made a head sent in small pieces cost quadratic time). The buffer it is given must start at
// the head and only grow between calls.
internal struct LoopbackHttpHeadScanner
{
    private int _scanned;
    private int _lineStart;
    private bool _started;
    private int _requestLine;

    // The bytes looked at so far, in all calls; for tests.
    internal long Examined { get; private set; }

    // The length of the request line: complete, or as far as it has arrived.
    internal readonly int RequestLineLength => _started ? _requestLine : _scanned - _lineStart;

    // The head's length once buffer holds all of it, through its empty line; 0 while it is incomplete; -1 at a bare CR or
    // LF.
    internal int Advance(ReadOnlySpan<byte> buffer)
    {
        var index = _scanned;
        while (index < buffer.Length)
        {
            var found = buffer[index..].IndexOfAny((byte)'\r', (byte)'\n');
            if (found < 0)
            {
                Examined += buffer.Length - index;
                index = buffer.Length;
                break;
            }
            Examined += found + 1;
            index += found;
            if (buffer[index] == (byte)'\n') return -1;
            // A CR at the end: its LF is still to come.
            if (index + 1 == buffer.Length) break;
            Examined++;
            if (buffer[index + 1] != (byte)'\n') return -1;
            if (index > _lineStart)
            {
                if (!_started) _requestLine = index - _lineStart;
                _started = true;
            }
            else if (_started) return index + 2;
            index += 2;
            _lineStart = index;
        }
        _scanned = index;
        return 0;
    }
}
