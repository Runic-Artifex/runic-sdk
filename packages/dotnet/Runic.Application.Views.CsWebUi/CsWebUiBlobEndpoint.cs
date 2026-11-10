using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CsWebUi;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views.CsWebUi;

// blobs/1 on CS-WebUI. WebUI's file handler sees only a path and answers with one buffered response, so it can neither
// stream a blob nor read an upload. Each window therefore serves its blobs and uploads from a loopback HttpListener of
// its own, at http://localhost:{port}/_runic/blobs/{token} and /_runic/upload:
//
// - The page's origin is WebUI's (another port), so the listener answers CORS for that origin only, and with
//   credentials, as the runtime sends uploads with them. A preflight from another origin, an upload whose Origin is not
//   the window's, and a blob request whose fetch metadata names another site are refused.
// - The Host must be the listener's own host name and port (no DNS rebinding); HttpListener refuses others. Only
//   loopback peers are served, also on Windows, where http.sys would route a remote client that names localhost.
// - Every loopback address that localhost names on the port is the window's: a client that resolves localhost to ::1
//   and one that resolves it to 127.0.0.1 both reach this listener, never another process's (see Listen). After it
//   starts, the endpoint connects to each address and gives up the port unless its own listener answers that very
//   connection (ConfirmOwnership), on every OS.
// - An upload also needs its connection's token, which only the page's own hello received. WebUI's connections are
//   not visible to this listener, so a blob URL is bound to the window, not to a connection.
// - On Windows, HttpListener is http.sys, which lets a user without administrator rights listen on localhost only.
internal sealed class CsWebUiBlobEndpoint : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CsWebUiLoopbackRelay? _relay;
    private readonly BridgeBlobs _blobs;
    private readonly Func<int> _pagePort;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    // The pending ownership check: its path and the client its request came from, as this listener saw it.
    private volatile OwnershipCheck? _check;

    private CsWebUiBlobEndpoint(HttpListener listener, CsWebUiLoopbackRelay? relay, int port, string host, BridgeBlobs blobs, Func<int> pagePort)
    {
        _listener = listener;
        _relay = relay;
        Port = port;
        _blobs = blobs;
        _pagePort = pagePort;
        BaseUrl = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{host}:{port}/"));
        _loop = Task.Run(AcceptAsync);
    }

    internal int Port { get; }
    internal Uri BaseUrl { get; }

    // The loopback address that the relay serves for the listener, or null when the listener holds every address itself;
    // for tests.
    internal IPEndPoint? RelayEndPoint => _relay?.LocalEndPoint;

    // The listener's host name, which the URLs name. URLs name localhost, as WebUI's page does, so the page's loads are
    // same-site. http.sys lets a user without administrator rights listen on localhost only, and listens on every
    // address of it. Elsewhere the listener holds both loopback addresses of localhost; where it cannot, such as when
    // localhost resolves to neither, it binds 127.0.0.1 alone and the URLs name 127.0.0.1.
    private static IEnumerable<string> HostChoices()
    {
        yield return "localhost";
        if (!OperatingSystem.IsWindows()) yield return "127.0.0.1";
    }

    // Starts the window's endpoint and attaches it to the window's blobs; null, after logging why, when no listener
    // could start, so the window opens without blobs and uploads.
    internal static CsWebUiBlobEndpoint? Start(BridgeBlobs blobs, WebUiWindow window, ILogger logger) =>
        Start(blobs, () => (int)window.Port, logger, FreePort);

    // As above, with the page's port and the ports to try; for tests. Without the relay, the listener holds one address
    // of localhost only, as http.sys may, which leaves the other to the ownership check.
    internal static CsWebUiBlobEndpoint? Start(BridgeBlobs blobs, Func<int> pagePort, ILogger logger, Func<int> nextPort, bool relay = true)
    {
        Exception? last = null;
        foreach (var host in HostChoices())
        {
            // The ports given up because another process held or answered one of their loopback addresses, per address.
            var held = new Dictionary<IPAddress, int>();
            var attempts = 0;
            for (; attempts < 5; attempts++)
            {
                var port = nextPort();
                CsWebUiBlobEndpoint? endpoint = null;
                try
                {
                    var (listener, other) = Listen(host, port, relay);
                    endpoint = new CsWebUiBlobEndpoint(listener, other, port, host, blobs, pagePort);
                    endpoint.ConfirmOwnership(host);
                }
                catch (Exception error) when (error is HttpListenerException or SocketException or PlatformNotSupportedException)
                {
                    endpoint?.Close();
                    last = error;
                    if (error is PortHeldException { Address: var address })
                        held[address] = held.GetValueOrDefault(address) + 1;
                    // Another process holds the port, on one of localhost's addresses, or took it between the probe and
                    // the start: try another port. A host name that cannot be held whole: try the next one.
                    if (error is PlatformNotSupportedException) break;
                    continue;
                }
                blobs.Attach(BridgeBlobEndpoint.Below(() => endpoint.BaseUrl));
                return endpoint;
            }
            // A process that holds one address on many ports can push the endpoint off localhost: say so.
            foreach (var (address, count) in held)
                CsWebUiLog.BlobEndpointPortsHeld(logger, address.ToString(), count, attempts, host);
        }
        BlobsLog.EndpointFailed(logger, last!, last!.GetType().Name);
        return null;
    }

    // Another process holds or answers address on the port.
    private sealed class PortHeldException(IPAddress address, int port, string detail)
        : SocketException((int)SocketError.AddressAlreadyInUse, $"Another process holds {address} on port {port}: {detail}.")
    {
        internal IPAddress Address { get; } = address;
    }

    // Listens on host:port. For localhost outside Windows, the managed HttpListener binds only the first address that
    // .NET resolves localhost to (Dns.GetHostAddresses("localhost")[0]): ::1 or 127.0.0.1, depending on the hosts file
    // and the system's address sorting. Browsers and Node resolve localhost themselves, may try ::1 first and fall back
    // to 127.0.0.1 only when ::1 refuses. So the listener takes the first address and a relay of its own listens on
    // the other one, and the port is given up when another process holds either. The URLs then reach this listener on
    // whichever address a client picks, and an upload's token never reaches another process's listener.
    private static (HttpListener Listener, CsWebUiLoopbackRelay? Relay) Listen(string host, int port, bool relay)
    {
        var listener = new HttpListener { IgnoreWriteExceptions = true };
        listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://{host}:{port}/"));
        if (OperatingSystem.IsWindows() || host != "localhost")
        {
            Start(listener);
            return (listener, null);
        }
        var bound = ResolveLocalhost();
        try { Start(listener); }
        catch (HttpListenerException) when (IsHeld(bound, port))
        {
            throw new PortHeldException(bound, port, "the listener could not listen there");
        }
        try
        {
            // HttpListener resolved localhost itself: make sure it holds the address resolved here.
            if (!IsHeld(bound, port))
                throw new PlatformNotSupportedException($"HttpListener did not listen on {bound} for localhost.");
            if (!relay) return (listener, null);
            var other = bound.AddressFamily == AddressFamily.InterNetwork ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            try { return (listener, CsWebUiLoopbackRelay.Start(other, port, new IPEndPoint(bound, port))); }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                throw new PortHeldException(other, port, "the relay could not listen there");
            }
        }
        catch
        {
            listener.Close();
            throw;
        }

        static void Start(HttpListener listener)
        {
            try { listener.Start(); }
            catch
            {
                listener.Close();
                throw;
            }
        }
    }

    // The address HttpListener binds for localhost: the first that .NET resolves it to (HttpEndPointManager), which must
    // be one of the loopback addresses that browsers try.
    private static IPAddress ResolveLocalhost()
    {
        IPAddress[] addresses;
        try { addresses = Dns.GetHostAddresses("localhost"); }
        catch (SocketException error) { throw new PlatformNotSupportedException("localhost does not resolve.", error); }
        if (addresses is [var first, ..] && (first.Equals(IPAddress.Loopback) || first.Equals(IPAddress.IPv6Loopback)))
            return first;
        throw new PlatformNotSupportedException(
            $"localhost resolves to {(addresses.Length == 0 ? "nothing" : addresses[0].ToString())}, not to 127.0.0.1 or ::1.");
    }

    // Whether a listening socket holds address:port. .NET sets SO_REUSEADDR on this bind outside Windows, which Linux and
    // macOS still refuse while another socket listens on the address.
    internal static bool IsHeld(IPAddress address, int port)
    {
        using var probe = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            probe.Bind(new IPEndPoint(address, port));
            return false;
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return true;
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    // Connects to every loopback address the URLs can reach and requires this listener to answer a one-off path over that
    // very connection: the listener must see the connection's own client, directly or through the relay. A connection
    // that another process accepts, or proxies to this listener, fails the check, and so does an address that refuses,
    // where another process could listen later. Covers what the binds cannot show, such as http.sys on Windows, which
    // listens beside a socket another process bound to [::1] on the port before.
    private void ConfirmOwnership(string host)
    {
        IPAddress[] addresses = host == "localhost" ? [IPAddress.Loopback, IPAddress.IPv6Loopback] : [IPAddress.Parse(host)];
        foreach (var address in addresses)
            if (Answers(address, host) is { } failure)
                throw new PortHeldException(address, Port, failure);
    }

    // Null when this listener answered a connection to address; otherwise what happened instead. Bounded by 2 s for the
    // connect and 2 s for the request, whatever the other side does; the answer is not read, as its content proves
    // nothing: what counts is the client this listener saw.
    private string? Answers(IPAddress address, string host)
    {
        Socket socket;
        try { socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp); }
        catch (SocketException error) when (CsWebUiLoopbackRelay.IsMissing(error)) { return null; }
        using (socket)
        {
            var check = new OwnershipCheck("/_runic/owner/" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
            _check = check;
            try
            {
                using (var connecting = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    try { socket.ConnectAsync(new IPEndPoint(address, Port), connecting.Token).AsTask().GetAwaiter().GetResult(); }
                    // No such address on this machine (no IPv6 loopback): no other process can listen there either.
                    catch (SocketException error) when (CsWebUiLoopbackRelay.IsMissing(error) || error.SocketErrorCode == SocketError.NetworkUnreachable)
                    {
                        return null;
                    }
                    catch (SocketException error) { return $"connecting failed ({error.SocketErrorCode})"; }
                    catch (OperationCanceledException) { return "connecting timed out"; }
                }
                var client = (IPEndPoint)socket.LocalEndPoint!;
                socket.SendTimeout = 2000;
                socket.Send(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                    $"GET {check.Path} HTTP/1.1\r\nHost: {host}:{Port}\r\nConnection: close\r\n\r\n")));
                if (!check.Seen.Wait(TimeSpan.FromSeconds(2)))
                    return "the window's listener did not receive the check";
                return check.Seen.Result.Equals(client) ? null : "the window's listener received the check from another client";
            }
            catch (SocketException error) { return $"the check failed ({error.SocketErrorCode})"; }
            finally { _check = null; }
        }
    }

    private sealed class OwnershipCheck(string path)
    {
        private readonly TaskCompletionSource<IPEndPoint> _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Path { get; } = path;
        internal Task<IPEndPoint> Seen => _seen.Task;
        internal void Received(IPEndPoint client) => _seen.TrySetResult(client);
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
            // The relay's connections carry its client, which it checked; the listener sees the relay as their peer.
            var remote = context.Request.RemoteEndPoint;
            var peer = _relay?.ClientOf(remote) ?? remote;
            // http.sys routes by Host only, so a client of another machine that names localhost would reach the
            // listener on Windows: only loopback peers are served.
            if (!CsWebUiLoopbackRelay.IsLoopbackPeer(peer))
            {
                Deny(response, "request from another machine");
            }
            else if (_check is { } check && context.Request.Url?.AbsolutePath == check.Path)
            {
                check.Received(peer!);
                response.StatusCode = 204;
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
            return _pagePort() is var port and > 0 ? string.Create(CultureInfo.InvariantCulture, $"http://localhost:{port}") : null;
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
        Close();
    }

    // Stops listening, without detaching from the window's blobs: also for an endpoint that never attached.
    private void Close()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _relay?.Dispose();
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
