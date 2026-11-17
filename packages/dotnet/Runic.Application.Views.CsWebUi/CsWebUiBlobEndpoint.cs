using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CsWebUi;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views.CsWebUi;

// blobs/1 on CS-WebUI. WebUI's file handler sees only a path and answers with one buffered response, so it can neither
// stream a blob nor read an upload. Each window therefore serves its blobs and uploads from a loopback listener of its
// own, at http://localhost:{port}/_runic/blobs/{token} and /_runic/upload. The listener is http.sys (HttpListener) on
// Windows and Runic's own loopback HTTP/1.1 server elsewhere (LoopbackHttpServer), whose parser refuses what it does not
// need: the managed HttpListener of other systems keeps the TLS stack and an HttpClient in every NativeAOT application.
//
// - The page's origin is WebUI's (another port), so the listener answers CORS for that origin only, and with
//   credentials, as the runtime sends uploads with them. A preflight from another origin, an upload whose Origin is not
//   the window's, and a blob request whose fetch metadata names another site are refused.
// - The Host must be the listener's own host name and port (no DNS rebinding); both listeners refuse others. Only
//   loopback peers are served, also on Windows, where http.sys would route a remote client that names localhost.
// - Every loopback address that localhost names on the port is the window's: a client that resolves localhost to ::1
//   and one that resolves it to 127.0.0.1 both reach this listener, never another process's (see Listen). After it
//   starts, the endpoint connects to each address and gives up the port unless its own listener answers that very
//   connection (ConfirmOwnership), on every OS. A port where the listener cannot start because another process holds
//   it, as http.sys refuses one where another socket holds either address, is given up the same way: the endpoint
//   tries another port and, when every port tried is held, says so (event 2103). On Windows, a port that a URL
//   reservation of http.sys names is given up as well (event 2104): without one, http.sys refuses every other process, of any user, a
//   URL under the listener's (CsWebUiUrlReservations).
// - An upload also needs its connection's token, which only the page's own hello received. WebUI's connections are
//   not visible to this listener, so a blob URL is bound to the window, not to a connection.
// - On Windows, HttpListener is http.sys, which lets a user without administrator rights listen on localhost only.
internal sealed class CsWebUiBlobEndpoint : IDisposable
{
    private readonly ICsWebUiBlobListener _listener;
    private readonly CsWebUiLoopbackRelay? _relay;
    private readonly BridgeBlobs _blobs;
    private readonly Func<int> _pagePort;
    private bool _closed;
    // The pending ownership check: its path and the client its request came from, as this listener saw it.
    private volatile OwnershipCheck? _check;

    private CsWebUiBlobEndpoint(ICsWebUiBlobListener listener, CsWebUiLoopbackRelay? relay, int port, string host, BridgeBlobs blobs, Func<int> pagePort)
    {
        _listener = listener;
        _relay = relay;
        Port = port;
        _blobs = blobs;
        _pagePort = pagePort;
        BaseUrl = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{host}:{port}/"));
        listener.Serve(HandleAsync);
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
    // of localhost only outside Windows, which leaves the other to the ownership check.
    internal static CsWebUiBlobEndpoint? Start(BridgeBlobs blobs, Func<int> pagePort, ILogger logger, Func<int> nextPort, bool relay = true)
    {
        Exception? last = null;
        foreach (var host in HostChoices())
        {
            // The ports given up because another process held or answered one of their loopback addresses, per address.
            var held = new Dictionary<string, int>();
            // The ports given up because a URL reservation of http.sys names them, and the reserved prefixes.
            var reserved = 0;
            var reservations = new SortedSet<string>(StringComparer.Ordinal);
            var attempts = 0;
            for (; attempts < 5; attempts++)
            {
                var port = nextPort();
                CsWebUiBlobEndpoint? endpoint = null;
                try
                {
                    var (listener, other) = Listen(host, port, relay, blobs);
                    endpoint = new CsWebUiBlobEndpoint(listener, other, port, host, blobs, pagePort);
                    endpoint.ConfirmOwnership(host);
                }
                catch (Exception error) when (error is SocketException or PlatformNotSupportedException
                    || OperatingSystem.IsWindows() && CsWebUiHttpSysListener.IsListenerError(error))
                {
                    endpoint?.Close();
                    last = error;
                    if (error is PortHeldException { Addresses: var addresses })
                        foreach (var address in addresses)
                            held[address] = held.GetValueOrDefault(address) + 1;
                    if (error is PortReservedException { Prefixes: var prefixes })
                    {
                        reserved++;
                        reservations.UnionWith(prefixes);
                    }
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
                CsWebUiLog.BlobEndpointPortsHeld(logger, address, count, attempts, host);
            // No process holds a reserved port, so a port given up for a reservation is logged on its own.
            if (reserved > 0)
                CsWebUiLog.BlobEndpointPortsReserved(logger, string.Join(", ", reservations), reserved, attempts, host);
        }
        BlobsLog.EndpointFailed(logger, last!, last!.GetType().Name);
        return null;
    }

    // Another process holds or answers the port on addresses: loopback addresses, or the host name where the port is
    // known to be held but no address shows by whom.
    private sealed class PortHeldException(IReadOnlyList<string> addresses, int port, string detail)
        : SocketException((int)SocketError.AddressAlreadyInUse, $"Another process holds {string.Join(" and ", addresses)} on port {port}: {detail}.")
    {
        internal PortHeldException(IPAddress address, int port, string detail) : this([address.ToString()], port, detail) { }

        internal IReadOnlyList<string> Addresses { get; } = addresses;
    }

    // A URL reservation of http.sys names the port: no process need hold it, but the reservation's access list, not
    // http.sys's own rule, decides who may register a URL under the listener's.
    private sealed class PortReservedException(IReadOnlyList<string> prefixes, int port)
        : SocketException((int)SocketError.AddressAlreadyInUse, $"http.sys reserves {string.Join(", ", prefixes)}, which name port {port}.")
    {
        internal IReadOnlyList<string> Prefixes { get; } = prefixes;
    }

    // Listens on host:port. On Windows, http.sys listens on every address of localhost. Elsewhere, the loopback server
    // binds one address: for localhost, the first that .NET resolves it to (Dns.GetHostAddresses("localhost")[0]), ::1
    // or 127.0.0.1 depending on the hosts file and the system's address sorting, as HttpListener did. Browsers and Node
    // resolve localhost themselves, may try ::1 first and fall back to 127.0.0.1 only when ::1 refuses. So a relay of the
    // endpoint's own listens on the other address, and the port is given up when another process holds either. The URLs
    // then reach this listener on whichever address a client picks, and an upload's token never reaches another
    // process's listener.
    private static (ICsWebUiBlobListener Listener, CsWebUiLoopbackRelay? Relay) Listen(string host, int port, bool relay, BridgeBlobs blobs)
    {
        if (OperatingSystem.IsWindows()) return (ListenHttpSys(host, port), null);
        var bound = host == "localhost" ? ResolveLocalhost() : IPAddress.Parse(host);
        LoopbackHttpServer server;
        // The body limit is the window's upload limit, which the upload's own check answers first (LoopbackHttpLimits).
        var limits = new LoopbackHttpLimits { MaxBodyBytes = () => blobs.MaximumUploadBytes };
        // A failed accept is retried; it is logged once per run of failures (event 2105).
        Action<SocketException> acceptFailed = error => CsWebUiLog.BlobEndpointAcceptFailed(blobs.Logger, error, error.SocketErrorCode.ToString());
        try { server = LoopbackHttpServer.Listen(new IPEndPoint(bound, port), host, limits, acceptFailed); }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new PortHeldException(bound, port, "the listener could not listen there");
        }
        if (host != "localhost" || !relay) return (server, null);
        try
        {
            var other = bound.AddressFamily == AddressFamily.InterNetwork ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            try { return (server, CsWebUiLoopbackRelay.Start(other, port, new IPEndPoint(bound, port), acceptFailed: acceptFailed,
                maxConnections: limits.MaxConnections, halfCloseTimeout: limits.LingerTimeout)); }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                throw new PortHeldException(other, port, "the relay could not listen there");
            }
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    // http.sys listens on every address of localhost, and refuses a port where another socket holds either, or one that a
    // URL reservation names to another process.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static CsWebUiHttpSysListener ListenHttpSys(string host, int port)
    {
        // http.sys refuses another process a URL under the listener's, unless a URL reservation names the port.
        if (Reservations(port, CsWebUiUrlReservations.Naming) is [_, ..] reservations)
            throw new PortReservedException(reservations, port);
        try { return CsWebUiHttpSysListener.Start(string.Create(CultureInfo.InvariantCulture, $"http://{host}:{port}/")); }
        catch (HttpListenerException error) when (HeldBy(host, port, error) is { } held)
        {
            throw new PortHeldException(held, port, $"the listener could not listen there (error {error.ErrorCode})");
        }
    }

    // The URL reservations of http.sys that name port, as naming lists them. Without the list, no port can be shown to be
    // free of them, so the endpoint does not start: also where httpapi or its entry points cannot be loaded. The reader is
    // a parameter for tests.
    internal static IReadOnlyList<string> Reservations(int port, Func<int, IReadOnlyList<string>> naming)
    {
        try { return naming(port); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new PlatformNotSupportedException("http.sys did not list its URL reservations.", error);
        }
    }

    // The address the loopback server binds for localhost: the first that .NET resolves it to, as the managed
    // HttpListener did (HttpEndPointManager), which must be one of the loopback addresses that browsers try.
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

    // What held host:port when the listener could not start there: the loopback addresses of host that a bind probe finds
    // held, or else host itself when the error says the port is in use, as for a socket on a wildcard address that a
    // probe of one address may not meet or another http.sys registration of the prefix; null when nothing says so.
    // http.sys reports a port in use as ERROR_SHARING_VIOLATION (32) or, for a registration, ERROR_ALREADY_EXISTS (183).
    private static string[]? HeldBy(string host, int port, HttpListenerException error)
    {
        IPAddress[] addresses = host == "localhost" ? [IPAddress.Loopback, IPAddress.IPv6Loopback] : [IPAddress.Parse(host)];
        var held = addresses.Where(address => IsHeld(address, port)).Select(address => address.ToString()).ToArray();
        if (held.Length > 0) return held;
        var inUse = error.ErrorCode is 32 or 183 or (int)SocketError.AddressAlreadyInUse
            || error.InnerException is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse };
        return inUse ? [host] : null;
    }

    // Whether a listening socket holds address:port; false where the machine has no such address. .NET sets SO_REUSEADDR
    // on this bind outside Windows, which Linux and macOS still refuse while another socket listens on the address; on
    // Windows, a bind without it is refused while another socket holds the address.
    internal static bool IsHeld(IPAddress address, int port)
    {
        try
        {
            using var probe = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(address, port));
            return false;
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return true;
        }
        catch (SocketException error) when (CsWebUiLoopbackRelay.IsMissing(error))
        {
            return false;
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
    // where another process could listen later. Covers what the binds cannot show: a listener that holds one address
    // only, as http.sys would on a Windows that let it listen beside a socket another process bound before. (Windows
    // Server 2025 refuses to start the listener there, which the endpoint treats as a held port.)
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

    // Every request ends with its response sent, or with its connection reset when it failed midway (the listener's).
    private async Task HandleAsync(ICsWebUiBlobRequest request)
    {
        // The relay's connections carry its client, which it checked; the listener sees the relay as their peer.
        var remote = request.RemoteEndPoint;
        var peer = _relay?.ClientOf(remote) ?? remote;
        // http.sys routes by Host only, so a client of another machine that names localhost would reach the listener on
        // Windows: only loopback peers are served.
        if (!CsWebUiLoopbackRelay.IsLoopbackPeer(peer))
        {
            Deny(request, "request from another machine");
        }
        else if (_check is { } check && request.Path == check.Path)
        {
            check.Received(peer!);
            request.StatusCode = 204;
        }
        else await RespondAsync(request).ConfigureAwait(false);
    }

    private async Task RespondAsync(ICsWebUiBlobRequest request)
    {
        var path = request.Path;
        var upload = path == "/" + BridgeBlobEndpoint.UploadPath;
        var blob = path.StartsWith("/" + BridgeBlobEndpoint.BlobsPath, StringComparison.Ordinal);
        var token = blob ? path[(BridgeBlobEndpoint.BlobsPath.Length + 1)..] : null;
        if (!upload && (!blob || token!.Length == 0 || token.Contains('/')))
        {
            request.StatusCode = 404;
            return;
        }
        var origin = request.GetRequestHeader("Origin");
        var pageOrigin = PageOrigin();
        var fromPage = origin.Length > 0 && pageOrigin is not null && IsPageOrigin(origin, pageOrigin);
        // Every response grants the page's origin, also one to a request without Origin such as an <img>: the
        // browser caches a blob as immutable and may answer a later fetch of the page from that cached response.
        if (pageOrigin is not null)
        {
            request.SetResponseHeader("Access-Control-Allow-Origin", fromPage ? origin : pageOrigin);
            request.SetResponseHeader("Access-Control-Allow-Credentials", "true");
            request.SetResponseHeader("Vary", "Origin");
        }
        var method = request.Method;
        if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            if (!fromPage)
            {
                Deny(request, "preflight from another origin");
                return;
            }
            request.SetResponseHeader("Access-Control-Allow-Methods", upload ? "POST" : "GET, HEAD");
            request.SetResponseHeader("Access-Control-Allow-Headers",
                $"Content-Type, {BridgeBlobs.UploadTokenHeader}, {BridgeBlobs.UploadNameHeader}, Range");
            request.SetResponseHeader("Access-Control-Max-Age", "600");
            request.StatusCode = 204;
            return;
        }
        if (upload)
        {
            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                request.StatusCode = 405;
                return;
            }
            if (!fromPage)
            {
                Deny(request, "upload from another origin");
                return;
            }
            await _blobs.ReceiveUploadAsync(request).ConfigureAwait(false);
            return;
        }
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            request.StatusCode = 405;
            return;
        }
        // The page is another port of the same host, so its own loads are same-site.
        if (string.Equals(request.GetRequestHeader("Sec-Fetch-Site"), "cross-site", StringComparison.OrdinalIgnoreCase) ||
            origin.Length > 0 && !fromPage)
        {
            Deny(request, "blob request from another site");
            return;
        }
        request.SetResponseHeader("Access-Control-Expose-Headers", "Content-Disposition, Content-Length, Content-Range");
        await _blobs.ServeBlobAsync(token!, request).ConfigureAwait(false);
    }

    private void Deny(ICsWebUiBlobRequest request, string reason)
    {
        BlobsLog.RequestDenied(_blobs.Logger, "CS-WebUI blob", reason);
        request.StatusCode = 403;
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
        if (_closed) return;
        _closed = true;
        _relay?.Dispose();
        _listener.Dispose();
    }
}
