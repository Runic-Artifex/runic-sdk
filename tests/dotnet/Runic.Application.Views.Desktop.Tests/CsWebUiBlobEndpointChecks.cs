extern alias cswebui;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Runic.Application.Views;
using TUnit.Core;
using BlobEndpoint = cswebui::Runic.Application.Views.CsWebUi.CsWebUiBlobEndpoint;
using LoopbackRelay = cswebui::Runic.Application.Views.CsWebUi.CsWebUiLoopbackRelay;

// blobs/1 on CS-WebUI: the window's blob endpoint names localhost in its URLs, which a browser or Node resolves to ::1
// and 127.0.0.1 and tries in turn. Every loopback address that localhost names on the endpoint's port is the window's:
// a port where another process listens on either address is not used, and no process can listen there afterwards, so a
// blob load, an upload and its token never reach another process (the CI failures of blobs-probe.mjs, "Another origin's
// preflight: {"status":200}", were answers of such a process on ::1).
internal sealed class CsWebUiBlobEndpointChecks
{
    private const int PagePort = 45_678;
    private const string ConnectionKey = "c1";

    [Test]
    public async Task UploadsOverEitherLoopbackAddressReachTheWindowsListener()
    {
        if (OperatingSystem.IsWindows()) return;
        using var work = new Work();
        using var blobs = work.Blobs();
        using var endpoint = BlobEndpoint.Start(blobs, static () => PagePort, NullLogger.Instance, FreePort)
            ?? throw new InvalidOperationException("The endpoint did not start.");
        Require(endpoint.BaseUrl.Host == "localhost", $"The endpoint's URLs name {endpoint.BaseUrl.Host}, not localhost.");
        var grant = blobs.Grant(ConnectionKey) ?? throw new InvalidOperationException("The hello granted no upload.");
        Require(grant.UploadUrl == new Uri(endpoint.BaseUrl, "_runic/upload"), $"The upload URL is {grant.UploadUrl}.");

        foreach (var address in Loopbacks())
        {
            // Another process cannot listen on the address, also with SO_REUSEADDR (and SO_REUSEPORT on Linux).
            Require(!TryListen(address, endpoint.Port, reuse: true, out _), $"Another socket could listen on {address}:{endpoint.Port}.");
            var answer = await UploadAsync(address, endpoint.Port, grant.Token, "over " + address);
            Require(answer.Status == 201 && blobs.Find(answer.Id!, ConnectionKey) is not null,
                $"An upload over {address} was answered with {answer.Status}: {answer.Body}");
        }
        // The relay forwards to the listener, which still refuses an upload without the token and from another origin.
        foreach (var address in Loopbacks())
        {
            var refused = await UploadAsync(address, endpoint.Port, token: null, "no token");
            var foreign = await UploadAsync(address, endpoint.Port, grant.Token, "foreign origin", origin: "http://attacker.example");
            Require(refused.Status == 403 && foreign.Status == 403,
                $"Over {address}, an upload without the token was answered with {refused.Status} and one of another origin with {foreign.Status}.");
        }
        Require(blobs.UploadCount == Loopbacks().Count, $"The window holds {blobs.UploadCount} uploads.");
    }

    [Test]
    public async Task APortThatAnotherProcessHoldsOnEitherLoopbackAddressIsNotUsed()
    {
        if (OperatingSystem.IsWindows()) return;
        var loopbacks = Loopbacks();
        if (loopbacks.Count < 2) return; // Without IPv6 loopback, no process can listen on ::1.
        using var work = new Work();
        using var blobs = work.Blobs();
        // A foreign server on [::1]:A, with 127.0.0.1:A free, and one on 127.0.0.1:B, with [::1]:B free: the endpoint is
        // offered A and B first. Each answers like the server the CI runs met, 200 without a CORS grant, and counts what
        // reaches it.
        using var onIPv6 = Foreign.Start(IPAddress.IPv6Loopback, free: IPAddress.Loopback);
        using var onIPv4 = Foreign.Start(IPAddress.Loopback, free: IPAddress.IPv6Loopback);
        var offered = new Queue<int>([onIPv6.Port, onIPv4.Port]);
        using var endpoint = BlobEndpoint.Start(blobs, static () => PagePort, NullLogger.Instance,
            () => offered.Count > 0 ? offered.Dequeue() : FreePort()) ?? throw new InvalidOperationException("The endpoint did not start.");
        Require(offered.Count == 0 && endpoint.Port != onIPv6.Port && endpoint.Port != onIPv4.Port,
            $"The endpoint listens on port {endpoint.Port}, which a foreign server holds ({onIPv6.Port}, {onIPv4.Port}).");
        var grant = blobs.Grant(ConnectionKey) ?? throw new InvalidOperationException("The hello granted no upload.");

        // A client as a browser or Node is one: localhost on ::1 first, then on 127.0.0.1 when ::1 refuses.
        var answer = await UploadToLocalhostAsync(grant.UploadUrl, grant.Token);
        Require(answer.Status == 201 && blobs.Find(answer.Id!, ConnectionKey) is not null, $"The upload was answered with {answer.Status}: {answer.Body}");
        Require(onIPv6.Requests == 0 && onIPv4.Requests == 0,
            $"The foreign servers received {onIPv6.Requests} and {onIPv4.Requests} requests: {onIPv6.Received}{onIPv4.Received}");
    }

    [Test]
    public async Task TheRelayEndsWithTheEndpoint()
    {
        if (OperatingSystem.IsWindows()) return;
        using var work = new Work();
        using var blobs = work.Blobs();
        var endpoint = BlobEndpoint.Start(blobs, static () => PagePort, NullLogger.Instance, FreePort)
            ?? throw new InvalidOperationException("The endpoint did not start.");
        var relay = endpoint.RelayEndPoint;
        endpoint.Dispose();
        Require(blobs.Grant(ConnectionKey) is null, "The window still grants uploads after its endpoint closed.");
        foreach (var address in Loopbacks())
        {
            for (var attempt = 0; ; attempt++)
            {
                if (TryListen(address, endpoint.Port, reuse: false, out var socket))
                {
                    socket!.Dispose();
                    break;
                }
                if (attempt == 50) throw new InvalidOperationException($"{address}:{endpoint.Port} (relay {relay}) is still held after the endpoint closed.");
                await Task.Delay(100);
            }
        }
    }

    [Test]
    public async Task TheRelayResetsTheClientWhenTheListenerIsGone()
    {
        if (OperatingSystem.IsWindows() || Loopbacks().Count < 2) return;
        // A relay whose target nobody listens on: the client's connection is reset, not left open.
        using var target = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        target.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)target.LocalEndPoint!).Port;
        using var relay = LoopbackRelay.Start(IPAddress.IPv6Loopback, 0, new IPEndPoint(IPAddress.Loopback, port))
            ?? throw new InvalidOperationException("The relay did not start.");
        using var client = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(relay.LocalEndPoint);
        var buffer = new byte[16];
        int read;
        try { read = await client.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (SocketException) { read = 0; }
        Require(read == 0, $"The client read {read} bytes from a relay without a listener.");
    }

    private static List<IPAddress> Loopbacks()
    {
        List<IPAddress> addresses = [IPAddress.Loopback];
        try
        {
            using var probe = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            addresses.Add(IPAddress.IPv6Loopback);
        }
        catch (SocketException error) when (LoopbackRelay.IsMissing(error)) { }
        return addresses;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static bool TryListen(IPAddress address, int port, bool reuse, out Socket? socket)
    {
        socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (reuse)
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                // SO_REUSEPORT (15): Linux shares a port between sockets that both set it.
                if (OperatingSystem.IsLinux()) socket.SetRawSocketOption(1, 15, BitConverter.GetBytes(1));
            }
            socket.Bind(new IPEndPoint(address, port));
            socket.Listen();
            return true;
        }
        catch (SocketException)
        {
            socket.Dispose();
            socket = null;
            return false;
        }
    }

    private sealed record Answer(int Status, string Body, string? Id);

    // An upload with the page's origin to localhost:port, connected to one address.
    private static Task<Answer> UploadAsync(IPAddress address, int port, string? token, string content, string origin = "http://localhost:45678") =>
        SendUploadAsync(new Uri($"http://localhost:{port}/_runic/upload"), token, content, origin, async (_, cancellationToken) =>
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken); }
            catch { socket.Dispose(); throw; }
            return socket;
        });

    // An upload to the URL, connected as a browser or Node connects to localhost.
    private static Task<Answer> UploadToLocalhostAsync(Uri url, string token) =>
        SendUploadAsync(url, token, "to localhost", "http://localhost:45678", async (_, cancellationToken) =>
        {
            foreach (var address in new[] { IPAddress.IPv6Loopback, IPAddress.Loopback })
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, url.Port), cancellationToken);
                    return socket;
                }
                catch (SocketException)
                {
                    socket.Dispose();
                }
            }
            throw new InvalidOperationException($"No loopback address of {url} accepted a connection.");
        });

    private static async Task<Answer> SendUploadAsync(Uri url, string? token, string content, string origin,
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Socket>> connect)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) => new NetworkStream(await connect(context, cancellationToken), ownsSocket: true),
            PooledConnectionLifetime = TimeSpan.Zero,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(content, Encoding.UTF8, "text/plain") };
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation(BridgeBlobs.UploadNameHeader, "probe.txt");
        if (token is not null) request.Headers.TryAddWithoutValidation(BridgeBlobs.UploadTokenHeader, token);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        string? id = null;
        if (response.StatusCode == HttpStatusCode.Created)
        {
            using var json = JsonDocument.Parse(body);
            id = json.RootElement.GetProperty("upload").GetProperty("id").GetString();
        }
        return new Answer((int)response.StatusCode, body, id);
    }

    // A server of another process on one loopback address: answers every request with 200 and no CORS grant, and records
    // what it received.
    private sealed class Foreign : IDisposable
    {
        private readonly Socket _socket;
        private readonly StringBuilder _received = new();
        private int _requests;

        private Foreign(Socket socket)
        {
            _socket = socket;
            _ = Task.Run(AcceptAsync);
        }

        internal int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;
        internal int Requests => Volatile.Read(ref _requests);
        internal string Received
        {
            get { lock (_received) return _received.ToString(); }
        }

        // Listens on address, on a port that is free on the other address too.
        internal static Foreign Start(IPAddress address, IPAddress free)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(address, 0));
                socket.Listen();
                if (!BlobEndpoint.IsHeld(free, ((IPEndPoint)socket.LocalEndPoint!).Port)) return new Foreign(socket);
                socket.Dispose();
            }
            throw new InvalidOperationException($"No port of {address} is free on {free}.");
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                Socket client;
                try { client = await _socket.AcceptAsync(); }
                catch (Exception error) when (error is SocketException or ObjectDisposedException) { return; }
                Interlocked.Increment(ref _requests);
                using (client)
                {
                    var buffer = new byte[8192];
                    try
                    {
                        var read = await client.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(5));
                        lock (_received) _received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        await client.SendAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), SocketFlags.None);
                    }
                    catch (Exception error) when (error is SocketException or TimeoutException) { }
                }
            }
        }

        public void Dispose() => _socket.Dispose();
    }

    private sealed class Work : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("runic-cswebui-endpoint-");

        internal BridgeBlobs Blobs() => new() { UploadBase = _root.FullName };

        public void Dispose()
        {
            try { _root.Delete(recursive: true); }
            catch (IOException) { }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
