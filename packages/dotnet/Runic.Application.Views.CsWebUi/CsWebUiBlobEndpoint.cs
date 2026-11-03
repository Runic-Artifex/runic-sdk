using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CsWebUi;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views.CsWebUi;

// blobs/1 on CS-WebUI. WebUI's file handler sees only a path and answers with one buffered response, so it can neither
// stream a blob nor read an upload. Each window therefore serves its blobs and uploads from a loopback HttpListener of
// its own, at http://127.0.0.1:{port}/_runic/blobs/{token} and /_runic/upload (localhost on Windows):
//
// - The page's origin is WebUI's (another port), so the listener answers CORS for that origin only, and with
//   credentials, as the runtime sends uploads with them. A preflight from another origin, an upload whose Origin is not
//   the window's, and a blob request whose fetch metadata names another site are refused.
// - The Host must be the listener's own host name and port (no DNS rebinding); HttpListener refuses others. Only
//   loopback peers are served, also on Windows, where http.sys would route a remote client that names localhost.
// - An upload also needs its connection's token, which only the page's own hello received. WebUI's connections are
//   not visible to this listener, so a blob URL is bound to the window, not to a connection.
// - On Windows, HttpListener is http.sys, which lets a user without administrator rights listen on localhost only.
internal sealed class CsWebUiBlobEndpoint : IDisposable
{
    private readonly HttpListener _listener;
    private readonly BridgeBlobs _blobs;
    private readonly WebUiWindow _window;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private CsWebUiBlobEndpoint(HttpListener listener, int port, string host, BridgeBlobs blobs, WebUiWindow window)
    {
        _listener = listener;
        Port = port;
        _blobs = blobs;
        _window = window;
        BaseUrl = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{host}:{port}/"));
        _loop = Task.Run(AcceptAsync);
    }

    internal int Port { get; }
    internal Uri BaseUrl { get; }

    // The listener's host names, the first one in the URLs. URLs name localhost, as WebUI's page does, so the page's
    // loads are same-site. http.sys lets a user without administrator rights listen on localhost only. Elsewhere the
    // listener also binds 127.0.0.1, and binds it alone where localhost cannot be bound, such as when localhost
    // resolves to ::1 while IPv6 loopback is disabled; the URLs then name 127.0.0.1.
    private static IEnumerable<string[]> HostChoices()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return ["localhost"];
            yield break;
        }
        yield return ["localhost", "127.0.0.1"];
        yield return ["127.0.0.1"];
    }

    // Starts the window's endpoint and attaches it to the window's blobs; null, after logging why, when no listener
    // could start, so the window opens without blobs and uploads.
    internal static CsWebUiBlobEndpoint? Start(BridgeBlobs blobs, WebUiWindow window, ILogger logger)
    {
        Exception? last = null;
        foreach (var hosts in HostChoices())
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var port = FreePort();
                var listener = new HttpListener();
                foreach (var host in hosts) listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://{host}:{port}/"));
                listener.IgnoreWriteExceptions = true;
                try
                {
                    listener.Start();
                }
                catch (Exception error) when (error is HttpListenerException or SocketException or PlatformNotSupportedException)
                {
                    listener.Close();
                    last = error;
                    // Another process took the port between the probe and the start: try another port.
                    if (error is PlatformNotSupportedException) break;
                    continue;
                }
                var endpoint = new CsWebUiBlobEndpoint(listener, port, hosts[0], blobs, window);
                blobs.Attach(BridgeBlobEndpoint.Below(() => endpoint.BaseUrl));
                return endpoint;
            }
        }
        BlobsLog.EndpointFailed(logger, last!, last!.GetType().Name);
        return null;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    // Every request ends with its response closed, or aborted when it failed midway. Its token cancels when the window
    // closes and when a read or write of the request fails, as one does when the client disconnects.
    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        using var aborted = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try
        {
            // http.sys routes by Host only, so a client of another machine that names localhost would reach the
            // listener on Windows: only loopback peers are served.
            if (context.Request.RemoteEndPoint?.Address is not { } peer || !IPAddress.IsLoopback(peer))
            {
                Deny(response, "request from another machine");
            }
            else await RespondAsync(context, aborted).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            try { response.Abort(); } catch (ObjectDisposedException) { }
            return;
        }
        try { response.Close(); }
        catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task RespondAsync(HttpListenerContext context, CancellationTokenSource aborted)
    {
        var response = context.Response;
        var request = context.Request;
        var path = request.Url?.AbsolutePath ?? "/";
        var upload = path == "/" + BridgeBlobEndpoint.UploadPath;
        var blob = path.StartsWith("/" + BridgeBlobEndpoint.BlobsPath, StringComparison.Ordinal);
        var token = blob ? path[(BridgeBlobEndpoint.BlobsPath.Length + 1)..] : null;
        if (!upload && (!blob || token!.Length == 0 || token.Contains('/')))
        {
            response.StatusCode = 404;
            return;
        }
        var origin = request.Headers["Origin"] ?? string.Empty;
        var pageOrigin = PageOrigin();
        var fromPage = origin.Length > 0 && pageOrigin is not null && IsPageOrigin(origin, pageOrigin);
        // Every response grants the page's origin, also one to a request without Origin such as an <img>: the
        // browser caches a blob as immutable and may answer a later fetch of the page from that cached response.
        if (pageOrigin is not null)
        {
            response.Headers["Access-Control-Allow-Origin"] = fromPage ? origin : pageOrigin;
            response.Headers["Access-Control-Allow-Credentials"] = "true";
            response.Headers["Vary"] = "Origin";
        }
        var method = request.HttpMethod;
        if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            if (!fromPage)
            {
                Deny(response, "preflight from another origin");
                return;
            }
            response.Headers["Access-Control-Allow-Methods"] = upload ? "POST" : "GET, HEAD";
            response.Headers["Access-Control-Allow-Headers"] =
                $"Content-Type, {BridgeBlobs.UploadTokenHeader}, {BridgeBlobs.UploadNameHeader}, Range";
            response.Headers["Access-Control-Max-Age"] = "600";
            response.StatusCode = 204;
            return;
        }
        if (upload)
        {
            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 405;
                return;
            }
            if (!fromPage)
            {
                Deny(response, "upload from another origin");
                return;
            }
            await _blobs.ReceiveUploadAsync(new Exchange(context, aborted)).ConfigureAwait(false);
            return;
        }
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 405;
            return;
        }
        // The page is another port of the same host, so its own loads are same-site.
        if (string.Equals(request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase) ||
            origin.Length > 0 && !fromPage)
        {
            Deny(response, "blob request from another site");
            return;
        }
        response.Headers["Access-Control-Expose-Headers"] = "Content-Disposition, Content-Length, Content-Range";
        await _blobs.ServeBlobAsync(token!, new Exchange(context, aborted)).ConfigureAwait(false);
    }

    private void Deny(HttpListenerResponse response, string reason)
    {
        BlobsLog.RequestDenied(_blobs.Logger, "CS-WebUI blob", reason);
        response.StatusCode = 403;
    }

    // The origin WebUI serves the window's page from, or null before it is shown. WebUI reports its URL with the
    // machine's address, while a local browser loads it as localhost: the page is the window's port on a loopback name.
    private string? PageOrigin()
    {
        try
        {
            return _window.Port is var port and > 0 ? string.Create(CultureInfo.InvariantCulture, $"http://localhost:{port}") : null;
        }
        catch (ObjectDisposedException) { return null; }
    }

    // An origin of the window's page: http on the page's port, with a loopback name.
    internal static bool IsPageOrigin(string origin, string pageOrigin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var candidate) || !Uri.TryCreate(pageOrigin, UriKind.Absolute, out var page))
            return false;
        return candidate.Scheme == page.Scheme && candidate.Port == page.Port && candidate.PathAndQuery == "/"
            && candidate.Host is "localhost" or "127.0.0.1" or "[::1]";
    }

    public void Dispose()
    {
        _blobs.Detach();
        _stop.Cancel();
        try { _listener.Close(); }
        catch (ObjectDisposedException) { }
        _stop.Dispose();
    }

    // An HttpListener request as a Bridge blob exchange. A response without a length is sent chunked. HttpListener has
    // no token of the client's disconnect; a failed read or write of the request stands for it and cancels
    // RequestAborted.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001",
        Justification = "The wrapped streams belong to the HttpListener request and response, which HandleAsync closes.")]
    private sealed class Exchange(HttpListenerContext context, CancellationTokenSource aborted) : IBridgeBlobExchange
    {
        private bool _lengthSet;
        private Stream? _requestBody;
        private Stream? _responseBody;

        public string Method => context.Request.HttpMethod;
        public string GetRequestHeader(string name) => context.Request.Headers[name] ?? string.Empty;
        public Stream RequestBody => _requestBody ??= new AbortingStream(context.Request.InputStream, aborted);

        public int StatusCode
        {
            get => context.Response.StatusCode;
            set => context.Response.StatusCode = value;
        }

        public string? ContentType { set => context.Response.ContentType = value; }

        public long? ContentLength
        {
            set
            {
                if (value is not { } length) return;
                context.Response.ContentLength64 = length;
                _lengthSet = true;
            }
        }

        public void SetResponseHeader(string name, string value) => context.Response.Headers[name] = value;
        public void AppendResponseHeader(string name, string value) => context.Response.Headers.Add(name, value);

        public Stream ResponseBody
        {
            get
            {
                if (_responseBody is not null) return _responseBody;
                if (!_lengthSet) context.Response.SendChunked = true;
                return _responseBody = new AbortingStream(context.Response.OutputStream, aborted);
            }
        }

        public CancellationToken RequestAborted => aborted.Token;
    }

    // A request or response stream whose failure cancels the request's token.
    private sealed class AbortingStream(Stream inner, CancellationTokenSource aborted) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { Abort(); throw; }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { Abort(); throw; }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void Abort()
        {
            try { aborted.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}
