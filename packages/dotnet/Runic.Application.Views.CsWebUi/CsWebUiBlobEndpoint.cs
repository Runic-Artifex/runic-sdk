using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CsWebUi;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views.CsWebUi;

// blobs/1 on CS-WebUI. WebUI's file handler sees only a path and answers with one buffered response, so it can neither
// stream a blob nor read an upload. Each window therefore serves its blobs and uploads from a loopback HttpListener of
// its own, at http://localhost:{port}/blobs/{token} and /upload:
//
// - The page's origin is WebUI's (another port), so the listener answers CORS for that origin only, and with
//   credentials, as the runtime sends uploads with them. A preflight from another origin, an upload whose Origin is not
//   the window's, and a blob request whose fetch metadata names another site are refused.
// - The Host must be localhost or 127.0.0.1 at the listener's port (no DNS rebinding); HttpListener refuses others.
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

    private CsWebUiBlobEndpoint(HttpListener listener, int port, BridgeBlobs blobs, WebUiWindow window)
    {
        _listener = listener;
        Port = port;
        _blobs = blobs;
        _window = window;
        BaseUrl = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://localhost:{port}/"));
        _loop = Task.Run(AcceptAsync);
    }

    internal int Port { get; }
    internal Uri BaseUrl { get; }

    // Starts the window's endpoint and attaches it to the window's blobs; null, after logging why, when no listener
    // could start, so the window opens without blobs and uploads.
    internal static CsWebUiBlobEndpoint? Start(BridgeBlobs blobs, WebUiWindow window, ILogger logger)
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://localhost:{port}/"));
            if (!OperatingSystem.IsWindows())
                listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
            listener.IgnoreWriteExceptions = true;
            try
            {
                listener.Start();
            }
            catch (Exception error) when (error is HttpListenerException or SocketException or PlatformNotSupportedException)
            {
                listener.Close();
                // Another process took the port between the probe and the start.
                if (attempt < 4 && error is not PlatformNotSupportedException) continue;
                BlobsLog.EndpointFailed(logger, error, error.GetType().Name);
                return null;
            }
            var endpoint = new CsWebUiBlobEndpoint(listener, port, blobs, window);
            blobs.Attach(BridgeBlobEndpoint.Below(() => endpoint.BaseUrl));
            return endpoint;
        }
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

    // Every request ends with its response closed, or aborted when it failed midway.
    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            await RespondAsync(context).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            try { response.Abort(); } catch (ObjectDisposedException) { }
            return;
        }
        try { response.Close(); }
        catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task RespondAsync(HttpListenerContext context)
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
            await _blobs.ReceiveUploadAsync(new Exchange(context, _stop.Token)).ConfigureAwait(false);
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
        await _blobs.ServeBlobAsync(token!, new Exchange(context, _stop.Token)).ConfigureAwait(false);
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

    // An HttpListener request as a Bridge blob exchange. A response without a length is sent chunked.
    private sealed class Exchange(HttpListenerContext context, CancellationToken stop) : IBridgeBlobExchange
    {
        private bool _lengthSet;

        public string Method => context.Request.HttpMethod;
        public string GetRequestHeader(string name) => context.Request.Headers[name] ?? string.Empty;
        public Stream RequestBody => context.Request.InputStream;

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

        public Stream ResponseBody
        {
            get
            {
                if (!_lengthSet) context.Response.SendChunked = true;
                return context.Response.OutputStream;
            }
        }

        public CancellationToken RequestAborted => stop;
    }
}
