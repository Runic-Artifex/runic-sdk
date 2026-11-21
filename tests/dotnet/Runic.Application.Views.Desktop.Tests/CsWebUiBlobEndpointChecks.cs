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

    // #1187 (D077.14, D077.15): under dotnet runic dev --browser, the serve-only window's pages also come from dev's
    // per-session address, http://<label>.localhost:<port>.
    [Test]
    public void TheServeOriginIsThePagesOriginOnlyWhenServingOnly()
    {
        const string origin = "http://k3v9q2m8x4c7w1z5.localhost:9224";
        string? Read(string name, string? value) => name switch
        {
            "RUNIC_APPLICATION_SERVE_ONLY" => "1",
            "RUNIC_APPLICATION_SERVE_ORIGIN" => value,
            _ => null,
        };
        var served = BlobEndpoint.ServeOrigin(name => Read(name, origin));
        Require(served == origin, $"The serve origin was {served}.");
        Require(BlobEndpoint.IsServedThrough(origin, served), "dev's origin was refused.");
        Require(!BlobEndpoint.IsServedThrough("http://k3v9q2m8x4c7w1z5.localhost:9225", served), "another port was admitted.");
        Require(!BlobEndpoint.IsServedThrough("http://other.localhost:9224", served), "another label was admitted.");
        Require(!BlobEndpoint.IsServedThrough("http://127.0.0.1:9224", served), "127.0.0.1 was admitted.");
        Require(BlobEndpoint.IsServedThrough("http://localhost:9224", "http://localhost:9224"), "localhost was refused.");
        Require(BlobEndpoint.ServeOrigin(name => name == "RUNIC_APPLICATION_SERVE_ORIGIN" ? origin : null) is null,
            "The origin counted without serve-only mode.");
        foreach (var value in new[] { "https://a.localhost:9224", "http://a.b.localhost:9224", "http://localhost.evil:9224",
            "http://.localhost:9224", "http://-a.localhost:9224", "http://a_b.localhost:9224", "http://127.0.0.1:9224",
            "http://[::1]:9224", "http://example.com:9224", "http://a.localhost", "http://a.localhost:9224/x", "http://user@a.localhost:9224" })
        {
            // The value's scheme and authority, as a page's Origin would carry them (some are no valid URI).
            string authority = string.Join('/', value.Split('/').Take(3));
            Require(!BlobEndpoint.IsServedThrough(authority, value), $"'{value}' was taken as a serve origin.");
        }
    }

    [Test]
    public async Task UploadsOverEitherLoopbackAddressReachTheWindowsListener()
    {
        using var work = new Work();
        using var blobs = work.Blobs();
        using var endpoint = BlobEndpoint.Start(blobs, static () => PagePort, NullLogger.Instance, new Ports().Next)
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

    // The endpoint's preflight policy, over each loopback address, for the upload and a blob. Before the page is shown,
    // nothing is granted. Then a preflight of the page's origin (localhost, 127.0.0.1 or [::1] on the page's port) gets 204
    // with the methods and headers of the path; one of any other origin, or without one, gets 403 with the page's grant
    // only, which names neither a method nor a header. The endpoint never answers a preflight with 200: the answers that
    // failed blobs-probe.mjs (#1185, "Another origin's preflight: {"status":200}") came from another process on ::1.
    [Test]
    public async Task APreflightIsGrantedToThePageOriginOnly()
    {
        using var work = new Work();
        using var blobs = work.Blobs();
        var pagePort = 0;
        using var endpoint = BlobEndpoint.Start(blobs, () => pagePort, NullLogger.Instance, new Ports().Next)
            ?? throw new InvalidOperationException("The endpoint did not start.");
        var page = $"http://localhost:{PagePort}";
        (string Path, string Method, string Methods)[] paths =
            [("/_runic/upload", "POST", "POST"), ("/_runic/blobs/0123456789abcdef0123456789abcdef", "GET", "GET, HEAD")];
        string?[] foreign =
        [
            null, "null", "http://attacker.example", $"http://localhost:{PagePort + 1}", $"https://localhost:{PagePort}",
            $"http://localhost.attacker.example:{PagePort}", $"http://127.0.0.2:{PagePort}", $"http://localhost:{PagePort}/page",
            "not an origin",
        ];
        string[] pageOrigins = [page, $"http://127.0.0.1:{PagePort}", $"http://[::1]:{PagePort}"];
        var failures = new List<string>();
        foreach (var address in Loopbacks())
        foreach (var (path, method, methods) in paths)
        {
            pagePort = 0;
            var unshown = await PreflightAsync(address, endpoint.Port, path, page, method);
            if (unshown is not { Status: 403, Allow: null, Methods: null, Headers: null })
                failures.Add($"{address} {path}, before the page is shown: {unshown}");
            pagePort = PagePort;
            foreach (var origin in foreign)
            {
                var refused = await PreflightAsync(address, endpoint.Port, path, origin, method);
                if (refused is not { Status: 403, Methods: null, Headers: null, Credentials: "true" } || refused.Allow != page)
                    failures.Add($"{address} {path}, Origin {origin ?? "(none)"}: {refused}");
            }
            foreach (var origin in pageOrigins)
            {
                var granted = await PreflightAsync(address, endpoint.Port, path, origin, method);
                if (granted is not { Status: 204, Credentials: "true" } || granted.Allow != origin || granted.Methods != methods
                    || granted.Headers?.Contains(BridgeBlobs.UploadTokenHeader, StringComparison.OrdinalIgnoreCase) != true)
                    failures.Add($"{address} {path}, the page's Origin {origin}: {granted}");
            }
        }
        Require(failures.Count == 0, $"Preflights answered against the policy:\n{string.Join("\n", failures)}");
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
        var ports = new Ports([.. foreign.Select(server => server.Port)]);
        var logger = new EventLogger();
        using var endpoint = BlobEndpoint.Start(blobs, static () => PagePort, logger, ports.Next, relay)
            ?? throw new InvalidOperationException("The endpoint did not start.");
        Require(ports.FirstAllOffered && foreign.All(server => server.Port != endpoint.Port),
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
        var ports = new Ports(onIPv6.Port, onIPv4.Port);
        var started = Stopwatch.StartNew();
        using var endpoint = BlobEndpoint.Start(blobs, static () => PagePort, NullLogger.Instance, ports.Next, relay)
            ?? throw new InvalidOperationException("The endpoint did not start.");
        Require(started.Elapsed < TimeSpan.FromSeconds(30), $"The endpoint took {started.Elapsed} to start.");
        Require(ports.FirstAllOffered && endpoint.Port != onIPv6.Port && endpoint.Port != onIPv4.Port,
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
            var ports = new Ports([.. squatted.Select(server => server.Port)]);
            var logger = new EventLogger();
            using var endpoint = BlobEndpoint.Start(blobs, static () => PagePort, logger, ports.Next);
            Require(logger.Events.Contains("2103 CsWebUiBlobEndpointPortsHeld: Another process holds ::1 on 5 of the 5 ports the window's blob endpoint tried for localhost, so it gave up localhost."),
                $"The endpoint logged {string.Join(" | ", logger.Events)}.");
            // Windows has no fallback host; elsewhere the URLs name 127.0.0.1, on a port offered after the squatted ones.
            Require(ports.FirstAllOffered, $"The endpoint tried {ports.Offered.Count} ports.");
            if (OperatingSystem.IsWindows()) Require(endpoint is null, $"The endpoint started on {endpoint?.BaseUrl}.");
            else Require(endpoint is { BaseUrl.Host: "127.0.0.1" } && ports.Offered.Skip(squatted.Count).Contains(endpoint.Port)
                    && squatted.All(server => server.Port != endpoint.Port),
                $"The endpoint started on {endpoint?.BaseUrl.ToString() ?? "nothing"} (offered {string.Join(", ", ports.Offered)}).");
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
        var endpoint = BlobEndpoint.Start(blobs, static () => PagePort, NullLogger.Instance, new Ports().Next)
            ?? throw new InvalidOperationException("The endpoint did not start.");
        var relay = endpoint.RelayEndPoint;
        endpoint.Dispose();
        Require(blobs.Grant(ConnectionKey) is null, "The window still grants uploads after its endpoint closed.");
        foreach (var address in Loopbacks())
        {
            var freed = await TestTimeouts.UntilAsync(() =>
            {
                using var socket = Listen(address, endpoint.Port, Reuse.None);
                return socket is not null;
            }, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
            Require(freed, $"{address}:{endpoint.Port} (relay {relay}) is still held after the endpoint closed.");
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
        var kept = await RelayResetsAsync(relay);
        Require(kept is null, $"A relay without a listener did not reset its client: {kept}");
    }

    // HttpListener sees the relay as the peer of a relayed connection, so the relay serves loopback clients only. The
    // listener behind both relays accepts in order, so once it has accepted the control's connection, it has accepted any
    // that the relay of another machine's client made before: none may come before the control's.
    [Test]
    public async Task TheRelayResetsAClientOfAnotherMachine()
    {
        if (Loopbacks().Count < 2) return;
        using var target = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        target.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        target.Listen();
        var port = ((IPEndPoint)target.LocalEndPoint!).Port;
        // The peers the listener accepted, in order; the connections stay open until the end.
        var accepted = new List<Socket>();
        var accepting = Task.Run(async () =>
        {
            while (true)
            {
                Socket connection;
                try { connection = await target.AcceptAsync(); }
                catch (Exception error) when (error is SocketException or ObjectDisposedException) { return; }
                lock (accepted) accepted.Add(connection);
            }
        });
        try
        {
            // A relay that reads every client's address as one of another machine, as when the system routes such
            // packets to a loopback address (route_localnet on Linux).
            using (var remote = LoopbackRelay.Start(IPAddress.IPv6Loopback, 0, new IPEndPoint(IPAddress.Loopback, port),
                static _ => new IPEndPoint(IPAddress.Parse("10.9.0.2"), 40_000)) ?? throw new InvalidOperationException("The relay did not start."))
            {
                var kept = await RelayResetsAsync(remote);
                Require(kept is null, $"The relay did not reset a client of another machine: {kept}");
            }
            // The control: the same relay with the client's real address passes it on.
            using var local = LoopbackRelay.Start(IPAddress.IPv6Loopback, 0, new IPEndPoint(IPAddress.Loopback, port))
                ?? throw new InvalidOperationException("The relay did not start.");
            using var client = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(local.LocalEndPoint);
            var control = (IPEndPoint)client.LocalEndPoint!;
            bool IsControl(Socket connection) => local.ClientOf((IPEndPoint)connection.RemoteEndPoint!) is { } relayed && relayed.Equals(control);
            Require(await TestTimeouts.UntilAsync(() => { lock (accepted) return accepted.Any(IsControl); }, TimeSpan.FromSeconds(5)),
                "The relay did not pass a loopback client on.");
            lock (accepted)
                Require(accepted.Count == 1, $"The relay passed {accepted.Count - 1} client(s) of another machine on to its listener.");
        }
        finally
        {
            target.Dispose();
            await accepting;
            lock (accepted) foreach (var connection in accepted) connection.Dispose();
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

    // Null when the relay ends a client's connection without sending it a byte; otherwise what it did instead. The relay
    // accepts and then resets the connection, which the client may meet in its connect already: the kernel completes the
    // handshake before the relay accepts, and the reset can arrive before the client's connect returns.
    private static async Task<string?> RelayResetsAsync(LoopbackRelay relay)
    {
        using var client = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await client.ConnectAsync(relay.LocalEndPoint);
            var read = await client.ReceiveAsync(new byte[16], SocketFlags.None).WaitScaledAsync(TimeSpan.FromSeconds(10));
            return read == 0 ? null : $"the client read {read} bytes";
        }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
        {
            return null;
        }
        catch (TimeoutException error)
        {
            return $"the connection stayed open ({error.Message})";
        }
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

    // The ports the endpoint is offered: the given ones first, then ports that no socket holds on either loopback address.
    // A port that the kernel hands out for 127.0.0.1 may be one that a foreign server of the test holds on ::1 only, so
    // each later port is checked on ::1 too, and none is one of the given ones.
    private sealed class Ports(params int[] first)
    {
        private readonly Queue<int> _first = new(first);
        private readonly HashSet<int> _given = [.. first];
        private readonly List<int> _offered = [];

        internal List<int> Offered => [.. _offered];
        internal bool FirstAllOffered => _first.Count == 0;

        internal int Next()
        {
            var port = _first.Count > 0 ? _first.Dequeue() : FreeOnBoth();
            _offered.Add(port);
            return port;
        }

        private int FreeOnBoth()
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                // Released for the endpoint, which binds it next; no bind(0) takes a leased port meanwhile.
                var port = TestPorts.Lease().Release();
                if (!_given.Contains(port) && !BlobEndpoint.IsHeld(IPAddress.IPv6Loopback, port)) return port;
            }
            throw new InvalidOperationException("No port of 127.0.0.1 is free on ::1.");
        }
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

    private sealed record PreflightAnswer(int Status, string? Allow, string? Credentials, string? Methods, string? Headers);

    // A CORS preflight to localhost:port, connected to one address, with origin (none when null), asking for method and
    // the upload token's header.
    private static async Task<PreflightAnswer> PreflightAsync(IPAddress address, int port, string path, string? origin, string method)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken); }
                catch { socket.Dispose(); throw; }
                return new NetworkStream(socket, ownsSocket: true);
            },
            PooledConnectionLifetime = TimeSpan.Zero,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Options, new Uri($"http://localhost:{port}{path}"));
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", method);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", BridgeBlobs.UploadTokenHeader.ToLowerInvariant());
        using var response = await client.SendAsync(request);
        string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;
        return new PreflightAnswer((int)response.StatusCode, Header("Access-Control-Allow-Origin"), Header("Access-Control-Allow-Credentials"),
            Header("Access-Control-Allow-Methods"), Header("Access-Control-Allow-Headers"));
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
