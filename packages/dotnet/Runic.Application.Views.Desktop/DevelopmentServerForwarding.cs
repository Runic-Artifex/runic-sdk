using System.Net;
using System.Net.Http.Headers;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// Forwards what a Window's own origin cannot serve to the frontend's development server, the internal contract between
/// <c>dotnet runic dev</c> and the Runic Desktop and CS-WebUI hosts (#1085, #1086). With a Vite or Angular development
/// server, <c>dev</c> writes a development document into <c>www/</c> that loads the page's modules from the server, and
/// the Window's origin serves only <c>www/</c>. A URL that the page builds at run time against its document, such as
/// <c>&lt;img src="brand/logo.svg"&gt;</c> or the Vite DevTools dock's <c>/__devtools-assets/</c> icons, would 404 there.
/// </summary>
/// <remarks>
/// <para>
/// Forwarding is on only in the <c>Development</c> environment, when <c>dev</c> set <see cref="ViteVariable"/> or
/// <see cref="FrontendVariable"/> to an <c>http:</c> origin on a loopback address. It runs after the Window's content and
/// the application's resources had no answer, so it never replaces a file of <c>www/</c>.
/// </para>
/// <para>
/// A path relative to a Window's document is forwarded (proxied), so the page keeps its own origin for it. A
/// root-relative path outside every Window's path, such as the dock's <c>/__devtools/</c>, is redirected instead
/// (<see cref="RootTarget"/>), so the files loaded there resolve their own URLs, and the dock its WebSocket, against
/// the server.
/// </para>
/// <para>
/// It adds no reach beyond the development server's own: only <c>GET</c> and <c>HEAD</c>, only from a loopback peer
/// that names a loopback <c>Host</c>, and only same-origin or user-initiated fetches (<c>Sec-Fetch-Site</c>), so neither
/// another machine nor another site, nor a DNS-rebinding page, reaches the server through the Window. Requests carry no
/// cookies, credentials or <c>Origin</c>, a redirect names only the server, redirects are not followed, and only successful answers are passed on, with
/// their type and cache headers. The server's own checks (its file-system allow list) still apply. Vite's
/// <c>/__open-in-editor</c>, a <c>GET</c> with a side effect, is never forwarded. WebSockets are not forwarded: Vite's
/// client connects to the server itself.
/// </para>
/// </remarks>
internal sealed class DevelopmentServerForwarding
{
    internal const string ViteVariable = "RUNIC_APPLICATION_VITE_DEV_SERVER";
    internal const string FrontendVariable = "RUNIC_APPLICATION_FRONTEND_DEV_SERVER";

    private const int MaximumQueryLength = 4096;
    private static readonly TimeSpan ResponseHeadersTimeout = TimeSpan.FromSeconds(30);

    // One client per process: the development server is one loopback origin, so its connections are pooled.
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        UseCookies = false,
        UseProxy = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    });

    // The headers passed back from the development server; everything else (Set-Cookie, CORS, its own policies) stays.
    private static readonly string[] PassedResponseHeaders = ["Cache-Control", "ETag", "Last-Modified"];

    private readonly HttpClient _client;

    internal DevelopmentServerForwarding(Uri origin, HttpClient? client = null)
    {
        Origin = origin;
        _client = client ?? SharedClient.Value;
    }

    /// <summary>The development server's origin, with its base path, such as <c>http://127.0.0.1:5173/</c>.</summary>
    internal Uri Origin { get; }

    /// <summary>
    /// The forwarding of this process: the environment is <c>Development</c> and <c>dev</c> named a loopback
    /// development server. Otherwise <see langword="null"/>.
    /// </summary>
    internal static DevelopmentServerForwarding? Resolve(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (!IsDevelopment(read)) return null;
        var configured = read(ViteVariable);
        if (string.IsNullOrWhiteSpace(configured)) configured = read(FrontendVariable);
        return ParseOrigin(configured) is { } origin ? new DevelopmentServerForwarding(origin) : null;
    }

    /// <summary>An <c>http:</c> origin on a loopback address, with a path ending in <c>/</c>; otherwise null.</summary>
    internal static Uri? ParseOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !IsLoopbackHost(uri.Host))
            return null;
        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        return new UriBuilder(Uri.UriSchemeHttp, uri.Host, uri.Port, path).Uri;
    }

    // The rule of BridgeFailureDetails.IsDevelopment in Runic.Application.Views.
    private static bool IsDevelopment(Func<string, string?> read)
    {
        var name = read("DOTNET_ENVIRONMENT");
        if (string.IsNullOrEmpty(name)) name = read("ASPNETCORE_ENVIRONMENT");
        return string.Equals(name, "Development", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a request may be forwarded: <c>GET</c> or <c>HEAD</c> from a loopback peer, with a loopback (or absent)
    /// <c>Host</c>, a same-origin or user-initiated fetch, and a plain path.
    /// </summary>
    /// <param name="method">The request method.</param>
    /// <param name="peer">The TCP peer, or null where the server does not report it.</param>
    /// <param name="host">The <c>Host</c> header, or null where the server does not report it.</param>
    /// <param name="fetchSite">The <c>Sec-Fetch-Site</c> header, or null when absent or not reported.</param>
    /// <param name="path">The decoded path, starting with <c>/</c>.</param>
    internal static bool Admits(string method, IPAddress? peer, string? host, string? fetchSite, string path)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
            return false;
        if (peer is not null && !IPAddress.IsLoopback(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer)) return false;
        if (host is not null && !IsLoopbackHost(HostName(host))) return false;
        // Browsers send Sec-Fetch-Site with every request; a page of another site gets nothing forwarded.
        if (!string.IsNullOrEmpty(fetchSite) &&
            !string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fetchSite, "none", StringComparison.OrdinalIgnoreCase))
            return false;
        return RelativePath(path) is { } relative &&
            !relative.StartsWith("__open-in-editor", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The server URL of a path relative to a Window's document folder: the development document in <c>www/</c> stands
    /// for the server's document at <see cref="Origin"/>, so <c>/brand/logo.svg</c> of the Window is
    /// <c>brand/logo.svg</c> below <see cref="Origin"/>.
    /// </summary>
    internal Uri? DocumentTarget(string path, string? query) =>
        RelativePath(path) is { } relative && Query(query) is { } suffix
            ? new Uri(Origin, Escape(relative) + suffix)
            : null;

    /// <summary>
    /// The server URL of a root-relative path of the Window's listener, outside every Window's own path, such as the
    /// Vite DevTools dock's <c>/__devtools/</c> and <c>/__devtools-assets/</c>: the same path on the server's host.
    /// </summary>
    internal Uri? RootTarget(string path, string? query) =>
        RelativePath(path) is { } relative && Query(query) is { } suffix
            ? new Uri(Origin, "/" + Escape(relative) + suffix)
            : null;

    /// <summary>
    /// Sends <paramref name="target"/> to the development server, or returns null when it cannot be reached or does
    /// not answer with a success. The caller disposes the result.
    /// </summary>
    internal async Task<Forwarded?> SendAsync(Uri target, bool head, string? accept, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, target);
        if (!string.IsNullOrEmpty(accept)) request.Headers.TryAddWithoutValidation("Accept", accept);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResponseHeadersTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        var status = (int)response.StatusCode;
        if (status is < 200 or > 299 or 204 or 205 or 206)
        {
            response.Dispose();
            return null;
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in PassedResponseHeaders)
        {
            if ((Value(response.Headers, name) ?? Value(response.Content.Headers, name)) is { } value) headers[name] = value;
        }
        return new Forwarded(status, response.Content.Headers.ContentType?.ToString(),
            response.Content.Headers.ContentLength, headers, response);
    }

    private static string? Value(HttpHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    // The path without its leading slash, or null for a path that is not plain: empty, with a dot segment, a backslash,
    // a control character or a colon in its first segment.
    private static string? RelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/') return null;
        var relative = path[1..];
        if (relative.Length == 0) return null;
        foreach (var character in relative)
        {
            if (character is '\\' || char.IsControl(character)) return null;
        }
        foreach (var segment in relative.Split('/'))
        {
            if (segment is "." or "..") return null;
        }
        return relative;
    }

    private static string? Query(string? query)
    {
        if (string.IsNullOrEmpty(query) || query == "?") return string.Empty;
        if (query.Length > MaximumQueryLength || query[0] != '?' || query.Contains('#')) return null;
        foreach (var character in query)
        {
            if (char.IsControl(character) || character == ' ') return null;
        }
        return query;
    }

    private static string Escape(string relative) =>
        string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));

    private static string HostName(string host)
    {
        var value = host.Trim();
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']', StringComparison.Ordinal);
            return end > 0 ? value[1..end] : value;
        }
        var colon = value.LastIndexOf(':');
        return colon >= 0 && value.IndexOf(':') == colon ? value[..colon] : value;
    }

    private static bool IsLoopbackHost(string host)
    {
        var name = host.Trim('[', ']');
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(name, out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>A successful answer of the development server; disposing it releases the connection.</summary>
    internal sealed class Forwarded(int statusCode, string? contentType, long? contentLength,
        IReadOnlyDictionary<string, string> headers, HttpResponseMessage response) : IDisposable
    {
        internal int StatusCode { get; } = statusCode;
        internal string? ContentType { get; } = contentType;
        internal long? ContentLength { get; } = contentLength;
        internal IReadOnlyDictionary<string, string> Headers { get; } = headers;

        /// <summary>Opens the body; disposing the stream releases the response.</summary>
        internal async Task<Stream> OpenAsync(CancellationToken cancellationToken)
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new OwningStream(stream, response);
        }

        public void Dispose() => response.Dispose();
    }

    private sealed class OwningStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
