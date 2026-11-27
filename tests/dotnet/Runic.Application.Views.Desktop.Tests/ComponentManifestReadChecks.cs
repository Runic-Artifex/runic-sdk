using System.Net;
using System.Net.Sockets;
using System.Text;
using Runic.Application.Views.Desktop.Hosting;
using TUnit.Core;

// The guest's read of its component manifest (HostedRegion.ReadComponentModuleAsync) over LoopbackGet, its own HTTP/1.1
// GET without HttpClient (D072.2, S3), against a loopback server that answers with the bytes each case gives. The whole
// component attach runs in the conformance driver's host/1 run and HostedRegionBrowserHostTests.
internal sealed class ComponentManifestReadChecks
{
    private const string Embedder = "http://127.0.0.1:43123";
    private const string Manifest = """{"version":1,"name":"guest","module":"component.js","tag":"guest-00000000"}""";

    private static string Ok(string body, string type = "application/json") =>
        $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";

    private static string Chunked(params string[] chunks) =>
        "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
        string.Concat(chunks.Select(chunk => $"{Encoding.UTF8.GetByteCount(chunk):x}\r\n{chunk}\r\n")) + "0\r\n\r\n";

    // A loopback server that reads one request header block, keeps it, writes response and closes.
    private sealed class OneShotServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task<string> _request;

        internal OneShotServer(IPAddress address, byte[] response, bool hold = false)
        {
            _listener = new TcpListener(address, 0);
            _listener.Start();
            _request = ServeAsync(response, hold);
        }

        internal Uri Root => new($"http://{(_listener.LocalEndpoint is IPEndPoint { AddressFamily: AddressFamily.InterNetworkV6 } ? "[::1]" : "127.0.0.1")}:{((IPEndPoint)_listener.LocalEndpoint).Port}/_runic_component/");

        internal Task<string> Request => _request;

        private async Task<string> ServeAsync(byte[] response, bool hold)
        {
            using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            var stream = client.GetStream();
            var header = new List<byte>();
            var one = new byte[1];
            while (header.Count < 4 || !(header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n'))
            {
                if (await stream.ReadAsync(one).ConfigureAwait(false) == 0) break;
                header.Add(one[0]);
            }
            if (hold)
            {
                // Never answers: the reader's deadline ends the exchange.
                try { await stream.ReadAsync(new byte[1]).ConfigureAwait(false); }
                catch (IOException) { }
                return Encoding.Latin1.GetString([.. header]);
            }
            // The reader may close first, after a limit.
            try
            {
                await stream.WriteAsync(response).ConfigureAwait(false);
                client.Client.Shutdown(SocketShutdown.Send);
            }
            catch (Exception error) when (error is IOException or SocketException) { }
            return Encoding.Latin1.GetString([.. header]);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Dispose();
            try { await _request.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception error) when (error is SocketException or ObjectDisposedException or TimeoutException or IOException) { }
        }
    }

    private static async Task<(Uri? Module, Uri Root, string Request)> ReadAsync(string response, IPAddress? address = null, TimeSpan? timeout = null,
        bool hold = false) =>
        await ReadAsync(Encoding.UTF8.GetBytes(response), address, timeout, hold).ConfigureAwait(false);

    private static async Task<(Uri? Module, Uri Root, string Request)> ReadAsync(byte[] response, IPAddress? address = null, TimeSpan? timeout = null,
        bool hold = false)
    {
        await using var server = new OneShotServer(address ?? IPAddress.Loopback, response, hold);
        var module = await HostedRegion.ReadComponentModuleAsync(server.Root, Embedder, timeout).ConfigureAwait(false);
        return (module, server.Root, await server.Request.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false));
    }

    [Test]
    public async Task AManifestWithContentLengthNamesItsModule()
    {
        var (module, root, request) = await ReadAsync(Ok(Manifest));
        Check(module == new Uri(root, "component.js"), $"module {module}");
        var lines = request.Split("\r\n");
        Check(lines[0] == "GET /_runic_component/runic-component.json HTTP/1.1", lines[0]);
        Check(lines.Contains($"Host: {root.Authority}"), "the listener's own Host is sent");
        Check(lines.Contains($"Origin: {Embedder}"), "the embedder's Origin is sent");
        Check(lines.Contains("Connection: close"), "the connection is not kept");
        Check(!lines.Any(line => line.StartsWith("Cookie", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Accept-Encoding", StringComparison.OrdinalIgnoreCase)),
            "no cookie or content coding");
    }

    [Test]
    public async Task AChunkedManifestWithExtensionsAndTrailersIsRead()
    {
        var body = "HTTP/1.1 200 OK\r\nTransfer-Encoding: Chunked\r\n\r\n" +
            $"{Manifest[..10].Length:X};ext=1\r\n{Manifest[..10]}\r\n{Manifest[10..].Length:x}\r\n{Manifest[10..]}\r\n0\r\nX-Trailer: 1\r\n\r\n";
        var (module, root, _) = await ReadAsync(body);
        Check(module == new Uri(root, "component.js"), $"module {module}");
    }

    [Test]
    public async Task AManifestThatEndsWithTheConnectionIsRead()
    {
        var (module, root, _) = await ReadAsync($"HTTP/1.0 200 OK\r\nContent-Type: application/json\r\n\r\n﻿{Manifest}");
        Check(module == new Uri(root, "component.js"), $"module {module}");
    }

    [Test]
    public async Task TheIpv6LoopbackListenerIsRead()
    {
        if (!Socket.OSSupportsIPv6) return;
        try
        {
            using var probe = new TcpListener(IPAddress.IPv6Loopback, 0);
            probe.Start();
        }
        catch (SocketException)
        {
            return;
        }
        var (module, root, request) = await ReadAsync(Ok(Manifest), IPAddress.IPv6Loopback);
        Check(root.Host == "[::1]" && module == new Uri(root, "component.js"), $"module {module}");
        Check(request.Contains($"Host: [::1]:{root.Port}\r\n", StringComparison.Ordinal), "the IPv6 Host is sent");
    }

    [Test]
    [Arguments("not found", "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n")]
    [Arguments("forbidden", "HTTP/1.1 403 Forbidden\r\nContent-Length: 2\r\n\r\n{}")]
    [Arguments("redirect", "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/runic-component.json\r\nContent-Length: 0\r\n\r\n")]
    [Arguments("not http", "SSH-2.0-OpenSSH_9.6\r\n\r\n")]
    [Arguments("HTTP/2 status line", "HTTP/2 200\r\nContent-Length: 2\r\n\r\n{}")]
    [Arguments("header without colon", "HTTP/1.1 200 OK\r\nContent-Length 2\r\n\r\n{}")]
    [Arguments("bare LF", "HTTP/1.1 200 OK\nContent-Length: 2\n\n{}")]
    [Arguments("bad Content-Length", "HTTP/1.1 200 OK\r\nContent-Length: -2\r\n\r\n{}")]
    [Arguments("two Content-Lengths", "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nContent-Length: 3\r\n\r\n{} ")]
    [Arguments("both framings", "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nTransfer-Encoding: chunked\r\n\r\n2\r\n{}\r\n0\r\n\r\n")]
    [Arguments("another transfer coding", "HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip, chunked\r\n\r\n2\r\n{}\r\n0\r\n\r\n")]
    [Arguments("bad chunk size", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n{}\r\n0\r\n\r\n")]
    [Arguments("chunk without CRLF", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n2\r\n{}xx0\r\n\r\n")]
    [Arguments("truncated chunk", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n40\r\n{}")]
    [Arguments("truncated body", "HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n{}")]
    [Arguments("truncated header", "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n")]
    [Arguments("empty answer", "")]
    [DisplayName("AnInvalidAnswerReadsAsNoComponentBuild($name)")]
    public async Task AnInvalidAnswerReadsAsNoComponentBuild(string name, string response)
    {
        var (module, _, _) = await ReadAsync(response);
        Check(module is null, $"{name}: module {module}");
    }

    [Test]
    [Arguments("not JSON", "{\"version\":1,")]
    [Arguments("not an object", "[1]")]
    [Arguments("version 2", """{"version":2,"module":"component.js"}""")]
    [Arguments("no module", """{"version":1}""")]
    [Arguments("not a script", """{"version":1,"module":"component.css"}""")]
    [Arguments("absolute path", """{"version":1,"module":"/component.js"}""")]
    [Arguments("parent segment", """{"version":1,"module":"a/../../x.js"}""")]
    [Arguments("backslash", """{"version":1,"module":"a\\x.js"}""")]
    [Arguments("another origin", """{"version":1,"module":"http://127.0.0.2/x.js"}""")]
    [Arguments("query", """{"version":1,"module":"x.js?a=.js"}""")]
    [DisplayName("AnInvalidManifestReadsAsNoComponentBuild($name)")]
    public async Task AnInvalidManifestReadsAsNoComponentBuild(string name, string manifest)
    {
        var (module, _, _) = await ReadAsync(Ok(manifest));
        Check(module is null, $"{name}: module {module}");
    }

    [Test]
    public async Task TheManifestAndHeaderAreBounded()
    {
        var large = $$"""{"version":1,"module":"component.js","pad":"{{new string('x', HostedRegion.MaximumManifestBytes)}}"}""";
        Check((await ReadAsync(Ok(large))).Module is null, "a Content-Length over 64 KiB was read");
        Check((await ReadAsync(Chunked(large[..40_000], large[40_000..]))).Module is null, "a chunked body over 64 KiB was read");
        Check((await ReadAsync("HTTP/1.1 200 OK\r\n\r\n" + large)).Module is null, "a body to the end of the connection over 64 KiB was read");
        Check((await ReadAsync("HTTP/1.1 200 OK\r\nContent-Length: 99999999999999999999\r\n\r\n{}")).Module is null, "an overflowing Content-Length was read");
        var header = "HTTP/1.1 200 OK\r\n" + string.Concat(Enumerable.Range(0, 200).Select(i => $"X-Pad-{i}: {new string('y', 100)}\r\n"));
        Check((await ReadAsync(header + $"Content-Length: {Manifest.Length}\r\n\r\n{Manifest}")).Module is null, "a header block over 16 KiB was read");
        // Just under the limit, padded with spaces that JSON ignores.
        var fits = Manifest + new string(' ', HostedRegion.MaximumManifestBytes - Manifest.Length);
        var (module, root, _) = await ReadAsync(Ok(fits));
        Check(module == new Uri(root, "component.js"), "a 64 KiB manifest was not read");
    }

    [Test]
    public async Task ASilentServerOrNoServerReadsAsNoComponentBuild()
    {
        var started = TimeProvider.System.GetTimestamp();
        var (module, _, _) = await ReadAsync(Ok(Manifest), timeout: TimeSpan.FromMilliseconds(300), hold: true);
        Check(module is null, "a server that never answered gave a module");
        Check(TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(10), "the deadline did not end the read");

        using var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        Check(await HostedRegion.ReadComponentModuleAsync(new Uri($"http://127.0.0.1:{port}/_runic_component/"), Embedder) is null,
            "a refused connection gave a module");
    }

    // However the answer is split across reads, it stays incomplete until its last byte, and then reads as the manifest.
    [Test]
    [Arguments("Content-Length")]
    [Arguments("chunked")]
    [Arguments("end of connection")]
    [DisplayName("EveryPrefixOfAnAnswerIsIncomplete($framing)")]
    public void EveryPrefixOfAnAnswerIsIncomplete(string framing)
    {
        var answer = Encoding.UTF8.GetBytes(framing switch
        {
            "Content-Length" => Ok(Manifest),
            "chunked" => "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                $"{Manifest[..10].Length:x};a=b\r\n{Manifest[..10]}\r\n{Manifest[10..].Length:x}\r\n{Manifest[10..]}\r\n0\r\nX-Trailer: 1\r\n\r\n",
            _ => "HTTP/1.1 200 OK\r\n\r\n" + Manifest,
        });
        var closeDelimited = framing == "end of connection";
        for (var length = 0; length < answer.Length; length++)
        {
            var response = new LoopbackGet.Response(HostedRegion.MaximumManifestBytes);
            var progress = response.Parse(answer.AsSpan(0, length), ended: false, out _);
            Check(progress == LoopbackGet.Progress.Incomplete, $"{framing}: {length} of {answer.Length} bytes read as {progress}");
            if (!closeDelimited || length < answer.IndexOf((byte)'{'))
                Check(new LoopbackGet.Response(HostedRegion.MaximumManifestBytes).Parse(answer.AsSpan(0, length), ended: true, out _) == LoopbackGet.Progress.Invalid,
                    $"{framing}: {length} of {answer.Length} bytes and the end of the connection did not read as invalid");
        }
        // One parser over growing input, as the read loop calls it.
        var growing = new LoopbackGet.Response(HostedRegion.MaximumManifestBytes);
        byte[]? body = null;
        var last = LoopbackGet.Progress.Incomplete;
        for (var length = 1; length <= answer.Length && last == LoopbackGet.Progress.Incomplete; length++)
            last = growing.Parse(answer.AsSpan(0, length), ended: closeDelimited && length == answer.Length, out body);
        Check(last == LoopbackGet.Progress.Complete && body is not null && Encoding.UTF8.GetString(body) == Manifest,
            $"{framing}: the whole answer read as {last}");
    }

    [Test]
    public async Task OnlyPlainLoopbackHttpIsRequested()
    {
        Check(await LoopbackGet.GetAsync(new Uri("https://127.0.0.1:1/x"), Embedder, 10, CancellationToken.None) is null, "https was requested");
        Check(await LoopbackGet.GetAsync(new Uri("http://192.0.2.1:1/x"), Embedder, 10, CancellationToken.None) is null, "a remote host was requested");
        Check(await LoopbackGet.GetAsync(new Uri("http://127.0.0.1:1/x"), "http://a\r\nX: 1", 10, CancellationToken.None) is null,
            "an Origin with a line break was sent");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
