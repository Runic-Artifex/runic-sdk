extern alias cswebui;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
//
// The checks run on every OS. Outside Windows, TheChecksAlsoPassWhereDotNetResolvesLocalhostToIPv4Only runs them again
// in a child process with DOTNET_SYSTEM_NET_DISABLEIPV6=1, where .NET resolves localhost to 127.0.0.1 only and the
// relay holds ::1: the layout where 0.16.0 left ::1 free, whatever the machine's resolver prefers.
internal sealed partial class CsWebUiBlobEndpointChecks
{
    private const int PagePort = 45_678;
    private const string ConnectionKey = "c1";
    // Set in the child process of TheChecksAlsoPassWhereDotNetResolvesLocalhostToIPv4Only.
    private const string IPv4OnlyVariable = "RUNIC_CSWEBUI_ENDPOINT_IPV4_ONLY";
    private static bool IPv4Only => Environment.GetEnvironmentVariable(IPv4OnlyVariable) == "1";

    [Test]
    public async Task UploadsOverEitherLoopbackAddressReachTheWindowsListener()
    {
        using var work = new Work();
        using var blobs = work.Blobs();
        using var endpoint = BlobEndpoint.Start(blobs, static () => PagePort, NullLogger.Instance, FreePort)
            ?? throw new InvalidOperationException("The endpoint did not start.");
        Require(endpoint.BaseUrl.Host == "localhost", $"The endpoint's URLs name {endpoint.BaseUrl.Host}, not localhost.");
        var loopbacks = Loopbacks();
        if (IPv4Only)
        {
            // The layout this run exists for: HttpListener binds localhost to 127.0.0.1, leaving ::1 to the relay.
            var resolved = Dns.GetHostAddresses("localhost");
            Require(resolved is [var first, ..] && first.Equals(IPAddress.Loopback), $"localhost resolves to {string.Join(", ", (object[])resolved)}.");
        }
        var grant = blobs.Grant(ConnectionKey) ?? throw new InvalidOperationException("The hello granted no upload.");
        Require(grant.UploadUrl == new Uri(endpoint.BaseUrl, "_runic/upload"), $"The upload URL is {grant.UploadUrl}.");

        // No other socket can take a connection to either address of the port.
        var squats = await SquatsAsync(endpoint.Port);
        Require(squats.Count == 0, $"Other sockets on port {endpoint.Port}: {string.Join("; ", squats)}");
        foreach (var address in loopbacks)
        {
            var answer = await UploadAsync(address, endpoint.Port, grant.Token, "over " + address);
            Require(answer.Status == 201 && blobs.Find(answer.Id!, ConnectionKey) is not null,
                $"An upload over {address} was answered with {answer.Status}: {answer.Body}");
        }
        // The relay forwards to the listener, which still refuses an upload without the token and from another origin.
        foreach (var address in loopbacks)
        {
            var refused = await UploadAsync(address, endpoint.Port, token: null, "no token");
            var foreign = await UploadAsync(address, endpoint.Port, grant.Token, "foreign origin", origin: "http://attacker.example");
            Require(refused.Status == 403 && foreign.Status == 403,
                $"Over {address}, an upload without the token was answered with {refused.Status} and one of another origin with {foreign.Status}.");
        }
        Require(blobs.UploadCount == loopbacks.Count, $"The window holds {blobs.UploadCount} uploads.");
    }

    // A foreign server on [::1]:A (127.0.0.1:A free) and one on 127.0.0.1:B ([::1]:B free), each answering like the
    // server the CI runs met, 200 without a CORS grant; and two that pass connections on to the other address, where the
    // window's listener will be. The endpoint is offered A to D first and uses none of them, on every OS: it moves on to
    // a port no other process holds. Without the relay, each of A to D is given up either because the listener cannot
    // start there (on Windows, http.sys refuses a port that another socket holds on either address) or because it holds
    // one address only and the ownership check gives the port up: a foreign server answers it, or a proxy passes it on
    // from another client. Outside Windows, HttpListener without the relay cannot hold localhost's other address, so
    // every port fails and the URLs fall back to 127.0.0.1.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task APortThatAnotherProcessHoldsOnEitherLoopbackAddressIsNotUsed(bool relay)
    {
        if (Loopbacks().Count < 2) return; // Without IPv6 loopback, no process can listen on ::1.
        using var work = new Work();
        using var blobs = work.Blobs();
        using var onIPv6 = Foreign.Start(IPAddress.IPv6Loopback, free: IPAddress.Loopback, Kind.Answer);
        using var onIPv4 = Foreign.Start(IPAddress.Loopback, free: IPAddress.IPv6Loopback, Kind.Answer);
        using var proxyOnIPv6 = Foreign.Start(IPAddress.IPv6Loopback, free: IPAddress.Loopback, Kind.Proxy);
        using var proxyOnIPv4 = Foreign.Start(IPAddress.Loopback, free: IPAddress.IPv6Loopback, Kind.Proxy);
        Foreign[] foreign = [onIPv6, onIPv4, proxyOnIPv6, proxyOnIPv4];
        var offered = new Queue<int>(foreign.Select(server => server.Port));
        var logger = new EventLogger();
        using var endpoint = BlobEndpoint.Start(blobs, static () => PagePort, logger,
            () => offered.Count > 0 ? offered.Dequeue() : FreePort(), relay) ?? throw new InvalidOperationException("The endpoint did not start.");
        Require(offered.Count == 0 && foreign.All(server => server.Port != endpoint.Port),
            $"The endpoint listens on port {endpoint.Port}, which a foreign server holds ({string.Join(", ", foreign.Select(server => server.Port))}).");
        var host = relay || OperatingSystem.IsWindows() ? "localhost" : "127.0.0.1";
        Require(endpoint.BaseUrl.Host == host, $"The endpoint's URLs name {endpoint.BaseUrl.Host}, not {host}.");
        if (relay) Require(logger.Events.Count == 0, $"The endpoint logged {string.Join(", ", logger.Events)}.");
        else
        {
            // Without the relay: every port A to D was refused to the listener or failed the ownership check.
            var refused = foreign.Where(server => !HttpListenerStarts(server.Port)).ToList();
            var checkedPorts = foreign.Where(server => server.Received.Contains("/_runic/owner/", StringComparison.Ordinal)).ToList();
            Console.WriteLine($"Ports held by foreign servers ({Environment.OSVersion}): {foreign.Length - refused.Count} let a listener start; " +
                $"{checkedPorts.Count} received the ownership check.");
            Require(foreign.All(server => refused.Contains(server) || checkedPorts.Contains(server)),
                $"Ports A to D: {string.Join("; ", foreign.Select(server => $"{server.Port} {(refused.Contains(server) ? "refused the listener" : "let a listener start")}, received {server.Received.Length} bytes"))}.");
            // Outside Windows, the listener's own address cannot be taken: the ports held on the other one failed the check.
            if (!OperatingSystem.IsWindows())
                Require(checkedPorts.Count >= 2,
                    $"The ownership check reached {foreign.Count(server => server.Received.Length > 0)} foreign servers: {string.Concat(foreign.Select(server => server.Received))}");
        }
        var grant = blobs.Grant(ConnectionKey) ?? throw new InvalidOperationException("The hello granted no upload.");

        // A client as a browser or Node is one: localhost on ::1 first, then on 127.0.0.1 when ::1 refuses.
        var answer = await UploadToLocalhostAsync(grant.UploadUrl, grant.Token);
        Require(answer.Status == 201 && blobs.Find(answer.Id!, ConnectionKey) is not null, $"The upload was answered with {answer.Status}: {answer.Body}");
        // The ownership check's requests do reach the foreign servers; the page's upload does not.
        Require(foreign.All(server => !server.Received.Contains("_runic/upload", StringComparison.Ordinal)),
            $"A foreign server received the upload: {string.Concat(foreign.Select(server => server.Received))}");
    }

    // Foreign servers that accept the ownership check and then send a byte now and then without ever answering, on
    // [::1]:A and 127.0.0.1:B: each check is bounded (2 s to connect, 2 s for the request), so they cannot stall the
    // window's start, and the endpoint uses neither port.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AForeignServerThatNeverAnswersCannotStallTheEndpoint(bool relay)
    {
        if (Loopbacks().Count < 2) return;
        using var work = new Work();
        using var blobs = work.Blobs();
        using var onIPv6 = Foreign.Start(IPAddress.IPv6Loopback, free: IPAddress.Loopback, Kind.Stall);
        using var onIPv4 = Foreign.Start(IPAddress.Loopback, free: IPAddress.IPv6Loopback, Kind.Stall);
        var offered = new Queue<int>([onIPv6.Port, onIPv4.Port]);
        var started = Stopwatch.StartNew();
        using var endpoint = BlobEndpoint.Start(blobs, static () => PagePort, NullLogger.Instance,
            () => offered.Count > 0 ? offered.Dequeue() : FreePort(), relay) ?? throw new InvalidOperationException("The endpoint did not start.");
        Require(started.Elapsed < TimeSpan.FromSeconds(30), $"The endpoint took {started.Elapsed} to start.");
        Require(offered.Count == 0 && endpoint.Port != onIPv6.Port && endpoint.Port != onIPv4.Port,
            $"The endpoint listens on port {endpoint.Port}, which a foreign server holds ({onIPv6.Port}, {onIPv4.Port}).");
        await Task.CompletedTask;
    }

    // A process that holds ::1 on every port the endpoint tries pushes it off localhost; the warning names the cause, on
    // every OS: on Windows, where http.sys refuses each port, as where the relay or the ownership check gives it up.
    [Test]
    public async Task AnAddressThatAnotherProcessHoldsOnEveryPortTriedIsLogged()
    {
        if (Loopbacks().Count < 2) return;
        using var work = new Work();
        using var blobs = work.Blobs();
        var squatted = Enumerable.Range(0, 5).Select(_ => Foreign.Start(IPAddress.IPv6Loopback, free: IPAddress.Loopback, Kind.Answer)).ToList();
        try
        {
            var offered = new Queue<int>(squatted.Select(server => server.Port));
            var logger = new EventLogger();
            using var endpoint = BlobEndpoint.Start(blobs, static () => PagePort, logger,
                () => offered.Count > 0 ? offered.Dequeue() : FreePort());
            Require(logger.Events.Contains("2103 CsWebUiBlobEndpointPortsHeld: Another process holds ::1 on 5 of the 5 ports the window's blob endpoint tried for localhost, so it gave up localhost."),
                $"The endpoint logged {string.Join(" | ", logger.Events)}.");
            // Windows has no fallback host; elsewhere the URLs name 127.0.0.1, on a port no foreign server holds.
            if (OperatingSystem.IsWindows()) Require(endpoint is null, $"The endpoint started on {endpoint?.BaseUrl}.");
            else Require(endpoint is { BaseUrl.Host: "127.0.0.1" } && squatted.All(server => server.Port != endpoint.Port),
                $"The endpoint started on {endpoint?.BaseUrl.ToString() ?? "nothing"}.");
        }
        finally
        {
            foreach (var server in squatted) server.Dispose();
        }
        await Task.CompletedTask;
    }

    [Test]
    public async Task TheRelayEndsWithTheEndpoint()
    {
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
                if (Listen(address, endpoint.Port, Reuse.None) is { } socket)
                {
                    socket.Dispose();
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
        if (Loopbacks().Count < 2) return;
        // A relay whose target nobody listens on: the client's connection is reset, not left open.
        using var target = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        target.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)target.LocalEndPoint!).Port;
        using var relay = LoopbackRelay.Start(IPAddress.IPv6Loopback, 0, new IPEndPoint(IPAddress.Loopback, port))
            ?? throw new InvalidOperationException("The relay did not start.");
        Require(await ReadFromRelayAsync(relay) == 0, "The client read bytes from a relay without a listener.");
    }

    // HttpListener sees the relay as the peer of a relayed connection, so the relay serves loopback clients only.
    [Test]
    public async Task TheRelayResetsAClientOfAnotherMachine()
    {
        if (Loopbacks().Count < 2) return;
        using var target = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        target.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        target.Listen();
        var port = ((IPEndPoint)target.LocalEndPoint!).Port;
        var accepted = 0;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try { (await target.AcceptAsync()).Dispose(); }
                catch (Exception error) when (error is SocketException or ObjectDisposedException) { return; }
                Interlocked.Increment(ref accepted);
            }
        });
        // A relay that reads every client's address as one of another machine, as when the system routes such packets
        // to a loopback address (route_localnet on Linux).
        using (var remote = LoopbackRelay.Start(IPAddress.IPv6Loopback, 0, new IPEndPoint(IPAddress.Loopback, port),
            static _ => new IPEndPoint(IPAddress.Parse("10.9.0.2"), 40_000)) ?? throw new InvalidOperationException("The relay did not start."))
        {
            Require(await ReadFromRelayAsync(remote) == 0, "A client of another machine read bytes from the relay.");
            await Task.Delay(200);
            Require(Volatile.Read(ref accepted) == 0, "The relay passed a client of another machine on to its listener.");
        }
        // The control: the same relay with the client's real address passes it on.
        using (var local = LoopbackRelay.Start(IPAddress.IPv6Loopback, 0, new IPEndPoint(IPAddress.Loopback, port))
            ?? throw new InvalidOperationException("The relay did not start."))
        {
            using var client = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(local.LocalEndPoint);
            for (var attempt = 0; Volatile.Read(ref accepted) == 0; attempt++)
            {
                if (attempt == 100) throw new InvalidOperationException("The relay did not pass a loopback client on.");
                await Task.Delay(50);
            }
        }
    }

    [Test]
    [Arguments("127.0.0.1", true)]
    [Arguments("127.1.2.3", true)]
    [Arguments("::1", true)]
    [Arguments("::ffff:127.0.0.1", true)]
    [Arguments("::ffff:127.9.9.9", true)]
    [Arguments("10.9.0.2", false)]
    [Arguments("192.168.1.10", false)]
    [Arguments("::ffff:10.9.0.2", false)]
    [Arguments("fe80::1", false)]
    [Arguments("::", false)]
    [Arguments("0.0.0.0", false)]
    [Arguments("2001:db8::1", false)]
    public async Task OnlyLoopbackPeersAreRelayed(string address, bool loopback)
    {
        Require(LoopbackRelay.IsLoopbackPeer(new IPEndPoint(IPAddress.Parse(address), 1234)) == loopback,
            $"{address} is {(loopback ? "not " : "")}taken for a loopback peer.");
        Require(!LoopbackRelay.IsLoopbackPeer(null), "A client without an address is taken for a loopback peer.");
        await Task.CompletedTask;
    }

    // The checks above, again where .NET resolves localhost to 127.0.0.1 only (DOTNET_SYSTEM_NET_DISABLEIPV6=1). With a
    // resolver that names ::1 first, as on most machines, 0.16.0's prefixes (localhost and 127.0.0.1) held both addresses
    // already, so only this run shows that ::1 is the window's where .NET puts 127.0.0.1 first. http.sys on Windows does
    // not resolve localhost through .NET.
    [Test]
    public async Task TheChecksAlsoPassWhereDotNetResolvesLocalhostToIPv4Only()
    {
        if (OperatingSystem.IsWindows() || IPv4Only) return;
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("The test host has no path.");
        var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // Run through dotnet, the test host is the assembly; otherwise it is the apphost itself.
        if (Path.GetFileNameWithoutExtension(host) == "dotnet") start.ArgumentList.Add(typeof(CsWebUiBlobEndpointChecks).Assembly.Location);
        foreach (var argument in new[] { "--treenode-filter", $"/*/*/{nameof(CsWebUiBlobEndpointChecks)}/*", "--minimum-expected-tests", "10",
                     "--progress", "off", "--output", "detailed", "--timeout", "3m" })
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_SYSTEM_NET_DISABLEIPV6"] = "1";
        start.Environment[IPv4OnlyVariable] = "1";
        using var child = Process.Start(start) ?? throw new InvalidOperationException("The child test host did not start.");
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromMinutes(4));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            child.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"The child test host did not finish: {await output}{await errors}");
        }
        Require(child.ExitCode == 0, $"The checks failed with DOTNET_SYSTEM_NET_DISABLEIPV6=1 (exit {child.ExitCode}):\n{await output}{await errors}");
    }

    private static async Task<int> ReadFromRelayAsync(LoopbackRelay relay)
    {
        using var client = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(relay.LocalEndPoint);
        var buffer = new byte[16];
        try { return await client.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (SocketException) { return 0; }
    }

    // The loopback addresses of this machine: 127.0.0.1, and ::1 where it has IPv6 loopback.
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
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            // What a process that squats the ::1 ports leaves: the checks cannot run here, which is not a defect of the
            // endpoint (it falls back and logs event 2103).
            throw new InvalidOperationException("Another process holds every ephemeral port of ::1 on this machine (binding [::1]:0 failed), so these checks cannot run here.", error);
        }
        return addresses;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    // Whether an HttpListener for localhost starts on port, as the endpoint's does without the relay.
    private static bool HttpListenerStarts(int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try
        {
            listener.Start();
            return true;
        }
        catch (HttpListenerException)
        {
            return false;
        }
        finally
        {
            listener.Close();
        }
    }

    [Flags]
    private enum Reuse { None = 0, Address = 1, Port = 2 }

    // Every way another process could try to listen on port: each loopback address, IPv4 loopback mapped to IPv6, and the
    // wildcards (IPv6 dual-stack and IPv6 only), each plain, with SO_REUSEADDR and with SO_REUSEPORT too (Linux, macOS).
    // Returns the attempts that listen on a loopback address of the window, or that take a connection to one.
    private static async Task<List<string>> SquatsAsync(int port)
    {
        var loopbacks = Loopbacks();
        var attempts = new List<(IPAddress Address, bool DualMode, bool Exact)>();
        foreach (var address in loopbacks) attempts.Add((address, false, true));
        attempts.Add((IPAddress.Any, false, false));
        if (loopbacks.Count > 1)
        {
            attempts.Add((IPAddress.Parse("::ffff:127.0.0.1"), true, true));
            attempts.Add((IPAddress.IPv6Any, true, false));
            attempts.Add((IPAddress.IPv6Any, false, false));
        }
        Reuse[] reuses = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
            ? [Reuse.None, Reuse.Address, Reuse.Address | Reuse.Port]
            : [Reuse.None, Reuse.Address];
        var found = new List<string>();
        var report = new StringBuilder();
        foreach (var (address, dualMode, exact) in attempts)
        foreach (var reuse in reuses)
        {
            var name = $"{address}{(address.AddressFamily == AddressFamily.InterNetworkV6 ? dualMode ? " (dual-stack)" : " (IPv6 only)" : "")} with {reuse}";
            using var squat = Listen(address, port, reuse, dualMode);
            report.Append(name).Append(": ").Append(squat is null ? "refused" : "listened").Append("; ");
            if (squat is null) continue;
            // The window's addresses are exact: no other socket may listen on them at all. A wildcard may listen where the
            // system allows it beside more specific sockets, as BSD and macOS do, but must not receive their connections.
            if (exact && !OperatingSystem.IsWindows())
            {
                found.Add($"{name} listened");
                continue;
            }
            foreach (var loopback in loopbacks)
            {
                using var client = new Socket(loopback.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await client.ConnectAsync(new IPEndPoint(loopback, port)).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) when (error is SocketException or TimeoutException) { continue; }
                if (squat.Poll(TimeSpan.FromMilliseconds(500), SelectMode.SelectRead))
                    found.Add($"{name} took a connection to {loopback}");
            }
        }
        // What each attempt did, for the record of what the OS allows (on Windows, a listen that takes no connection passes).
        Console.WriteLine($"Squats on port {port} ({Environment.OSVersion}): {report}");
        return found;
    }

    private static Socket? Listen(IPAddress address, int port, Reuse reuse, bool dualMode = false)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6) socket.DualMode = dualMode;
            if (reuse.HasFlag(Reuse.Address)) socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            // SO_REUSEPORT: Linux (SOL_SOCKET 1, 15) and BSD and macOS (SOL_SOCKET 0xffff, 0x200) share a port between
            // sockets that all set it.
            if (reuse.HasFlag(Reuse.Port))
            {
                if (OperatingSystem.IsLinux()) socket.SetRawSocketOption(1, 15, BitConverter.GetBytes(1));
                else socket.SetRawSocketOption(0xffff, 0x200, BitConverter.GetBytes(1));
            }
            socket.Bind(new IPEndPoint(address, port));
            socket.Listen();
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
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

    private enum Kind { Answer, Proxy, Stall }

    // A server of another process on one loopback address: answers every request with 200 and no CORS grant; as a proxy,
    // passes each connection on to the same port of the other loopback address; or stalls, sending a byte now and then
    // and never an answer. Records what it received.
    private sealed class Foreign : IDisposable
    {
        private readonly Socket _socket;
        private readonly Kind _kind;
        private readonly IPEndPoint _other;
        private readonly CancellationTokenSource _stop = new();
        private readonly StringBuilder _received = new();

        private Foreign(Socket socket, Kind kind, IPEndPoint other)
        {
            _socket = socket;
            _kind = kind;
            _other = other;
            _ = Task.Run(AcceptAsync);
        }

        internal int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;
        internal string Received
        {
            get { lock (_received) return _received.ToString(); }
        }

        // Listens on address, on a port that is free on the other address too.
        internal static Foreign Start(IPAddress address, IPAddress free, Kind kind)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(address, 0));
                socket.Listen();
                var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
                if (!BlobEndpoint.IsHeld(free, port)) return new Foreign(socket, kind, new IPEndPoint(free, port));
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
                _ = Task.Run(() => _kind switch
                {
                    Kind.Proxy => ProxyAsync(client, _other),
                    Kind.Stall => StallAsync(client),
                    _ => AnswerAsync(client),
                });
            }
        }

        private async Task AnswerAsync(Socket client)
        {
            using (client)
            {
                var buffer = new byte[8192];
                try
                {
                    var read = await client.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(5));
                    Record(buffer, read);
                    await client.SendAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), SocketFlags.None);
                }
                catch (Exception error) when (error is SocketException or TimeoutException) { }
            }
        }

        private async Task StallAsync(Socket client)
        {
            using (client)
            {
                var buffer = new byte[8192];
                try
                {
                    var read = await client.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                    Record(buffer, read);
                    await client.SendAsync("HTTP/1.1 200 OK\r\n"u8.ToArray(), SocketFlags.None, _stop.Token);
                    while (true)
                    {
                        await Task.Delay(200, _stop.Token);
                        await client.SendAsync("X"u8.ToArray(), SocketFlags.None, _stop.Token);
                    }
                }
                catch (Exception error) when (error is SocketException or OperationCanceledException or ObjectDisposedException) { }
            }
        }

        private async Task ProxyAsync(Socket client, IPEndPoint upstream)
        {
            using (client)
            using (var target = new Socket(upstream.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                try
                {
                    await target.ConnectAsync(upstream).WaitAsync(TimeSpan.FromSeconds(5));
                    await Task.WhenAll(PumpAsync(client, target, record: true), PumpAsync(target, client, record: false));
                }
                catch (Exception error) when (error is SocketException or TimeoutException or ObjectDisposedException) { }
            }
        }

        private async Task PumpAsync(Socket from, Socket to, bool record)
        {
            var buffer = new byte[8192];
            try
            {
                while (await from.ReceiveAsync(buffer, SocketFlags.None) is var read and > 0)
                {
                    if (record) Record(buffer, read);
                    await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None);
                }
                to.Shutdown(SocketShutdown.Send);
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                from.Dispose();
                to.Dispose();
            }
        }

        private void Record(byte[] buffer, int count)
        {
            lock (_received) _received.Append(Encoding.ASCII.GetString(buffer, 0, count));
        }

        public void Dispose()
        {
            _stop.Cancel();
            _socket.Dispose();
        }
    }

    // Records each event as "{id} {name}: {message}".
    private sealed class EventLogger : ILogger
    {
        private readonly List<string> _events = [];

        internal List<string> Events
        {
            get { lock (_events) return [.. _events]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var error = exception switch
            {
                null => "",
                HttpListenerException listener => $" ({listener.GetType().Name} {listener.ErrorCode}: {listener.Message})",
                _ => $" ({exception.GetType().Name}: {exception.Message})",
            };
            lock (_events) _events.Add($"{eventId.Id} {eventId.Name}: {formatter(state, exception)}{error}");
        }
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
