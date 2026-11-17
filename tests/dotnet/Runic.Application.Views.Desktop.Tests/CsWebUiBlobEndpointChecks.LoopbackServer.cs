extern alias cswebui;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Runic.Application.Views;
using TUnit.Core;
using BlobEndpoint = cswebui::Runic.Application.Views.CsWebUi.CsWebUiBlobEndpoint;
using BlobRequest = cswebui::Runic.Application.Views.CsWebUi.ICsWebUiBlobRequest;
using LoopbackLimits = cswebui::Runic.Application.Views.CsWebUi.LoopbackHttpLimits;
using LoopbackParser = cswebui::Runic.Application.Views.CsWebUi.LoopbackHttpParser;
using LoopbackRelay = cswebui::Runic.Application.Views.CsWebUi.CsWebUiLoopbackRelay;
using LoopbackServer = cswebui::Runic.Application.Views.CsWebUi.LoopbackHttpServer;

// The blob endpoint's own loopback HTTP/1.1 server outside Windows (LoopbackHttpServer), which replaced the managed
// HttpListener there: its parser against malformed, oversized and smuggling heads; the server through real sockets
// (keep-alive, pipelining, chunked bodies, timeouts, limits, shutdown); and the endpoint through it (ranges, HEAD,
// conditional requests, chunked uploads, a refused upload's body). The parser checks run on every OS; the socket checks
// outside Windows, where the server is in use.
internal sealed partial class CsWebUiBlobEndpointChecks
{
    private const string Authority = "localhost:8080";

    // Heads the parser refuses, with the status it answers.
    private static readonly (string Name, string Head, int Status)[] RefusedHeads =
    [
        ("bare LF line ends", "GET / HTTP/1.1\nHost: localhost:8080\n\n", 400),
        ("a bare LF in a field", "GET / HTTP/1.1\r\nHost: localhost:8080\r\nX: a\nb\r\n\r\n", 400),
        ("a bare CR in a field", "GET / HTTP/1.1\r\nHost: localhost:8080\r\nX: a\rb\r\n\r\n", 400),
        ("an obsolete line folding", "GET / HTTP/1.1\r\nHost: localhost:8080\r\nX: a\r\n b\r\n\r\n", 400),
        ("whitespace before the colon", "GET / HTTP/1.1\r\nHost : localhost:8080\r\n\r\n", 400),
        ("a field name that is not a token", "GET / HTTP/1.1\r\nHost: localhost:8080\r\nX(y): 1\r\n\r\n", 400),
        ("a NUL in a value", "GET / HTTP/1.1\r\nHost: localhost:8080\r\nX: a\0b\r\n\r\n", 400),
        ("a DEL in a value", "GET / HTTP/1.1\r\nHost: localhost:8080\r\nX: a\u007fb\r\n\r\n", 400),
        ("two spaces in the request line", "GET  / HTTP/1.1\r\nHost: localhost:8080\r\n\r\n", 400),
        ("a tab in the request line", "GET\t/ HTTP/1.1\r\nHost: localhost:8080\r\n\r\n", 400),
        ("no version", "GET /\r\nHost: localhost:8080\r\n\r\n", 400),
        ("HTTP/2.0", "GET / HTTP/2.0\r\nHost: localhost:8080\r\n\r\n", 505),
        ("the HTTP/2 preface", "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n", 505),
        ("a lower-case version", "GET / http/1.1\r\nHost: localhost:8080\r\n\r\n", 400),
        ("HTTP/0.9", "GET /\r\n\r\n", 400),
        ("a method that is not a token", "G@T / HTTP/1.1\r\nHost: localhost:8080\r\n\r\n", 400),
        ("an absolute-form target", "GET http://localhost:8080/ HTTP/1.1\r\nHost: localhost:8080\r\n\r\n", 400),
        ("an asterisk target", "OPTIONS * HTTP/1.1\r\nHost: localhost:8080\r\n\r\n", 400),
        ("a dot-dot segment", "GET /_runic/blobs/../upload HTTP/1.1\r\nHost: localhost:8080\r\n\r\n", 400),
        ("an escaped dot-dot segment", "GET /_runic/blobs/%2E%2e/upload HTTP/1.1\r\nHost: localhost:8080\r\n\r\n", 400),
        ("a dot segment", "GET /_runic/./upload HTTP/1.1\r\nHost: localhost:8080\r\n\r\n", 400),
        ("a broken percent escape", "GET /a%zz HTTP/1.1\r\nHost: localhost:8080\r\n\r\n", 400),
        ("a fragment", "GET /a#b HTTP/1.1\r\nHost: localhost:8080\r\n\r\n", 400),
        ("a non-ASCII target", "GET /é HTTP/1.1\r\nHost: localhost:8080\r\n\r\n", 400),
        ("no Host", "GET / HTTP/1.1\r\n\r\n", 400),
        ("HTTP/1.0 without Host", "GET / HTTP/1.0\r\n\r\n", 400),
        ("another host (DNS rebinding)", "GET / HTTP/1.1\r\nHost: rebound.example:8080\r\n\r\n", 400),
        ("another port", "GET / HTTP/1.1\r\nHost: localhost:8081\r\n\r\n", 400),
        ("no port", "GET / HTTP/1.1\r\nHost: localhost\r\n\r\n", 400),
        ("a trailing dot", "GET / HTTP/1.1\r\nHost: localhost.:8080\r\n\r\n", 400),
        ("an address for a name", "GET / HTTP/1.1\r\nHost: 127.0.0.1:8080\r\n\r\n", 400),
        ("Host twice", "GET / HTTP/1.1\r\nHost: localhost:8080\r\nHost: rebound.example:8080\r\n\r\n", 400),
        ("Content-Length and Transfer-Encoding",
            "POST / HTTP/1.1\r\nHost: localhost:8080\r\nContent-Length: 5\r\nTransfer-Encoding: chunked\r\n\r\n", 400),
        ("Transfer-Encoding and Content-Length",
            "POST / HTTP/1.1\r\nHost: localhost:8080\r\nTransfer-Encoding: chunked\r\nContent-Length: 5\r\n\r\n", 400),
        ("Content-Length twice, equal", "POST / HTTP/1.1\r\nHost: localhost:8080\r\nContent-Length: 5\r\nContent-Length: 5\r\n\r\n", 400),
        ("Content-Length as a list", "POST / HTTP/1.1\r\nHost: localhost:8080\r\nContent-Length: 5, 5\r\n\r\n", 400),
        ("a signed Content-Length", "POST / HTTP/1.1\r\nHost: localhost:8080\r\nContent-Length: +5\r\n\r\n", 400),
        ("a negative Content-Length", "POST / HTTP/1.1\r\nHost: localhost:8080\r\nContent-Length: -1\r\n\r\n", 400),
        ("a hexadecimal Content-Length", "POST / HTTP/1.1\r\nHost: localhost:8080\r\nContent-Length: 0x5\r\n\r\n", 400),
        ("an overflowing Content-Length", "POST / HTTP/1.1\r\nHost: localhost:8080\r\nContent-Length: 99999999999999999999\r\n\r\n", 400),
        ("an empty Content-Length", "POST / HTTP/1.1\r\nHost: localhost:8080\r\nContent-Length:\r\n\r\n", 400),
        ("Transfer-Encoding twice", "POST / HTTP/1.1\r\nHost: localhost:8080\r\nTransfer-Encoding: chunked\r\nTransfer-Encoding: chunked\r\n\r\n", 400),
        ("chunked twice in one field", "POST / HTTP/1.1\r\nHost: localhost:8080\r\nTransfer-Encoding: chunked, chunked\r\n\r\n", 501),
        ("gzip, chunked", "POST / HTTP/1.1\r\nHost: localhost:8080\r\nTransfer-Encoding: gzip, chunked\r\n\r\n", 501),
        ("identity", "POST / HTTP/1.1\r\nHost: localhost:8080\r\nTransfer-Encoding: identity\r\n\r\n", 501),
        ("a padded coding name", "POST / HTTP/1.1\r\nHost: localhost:8080\r\nTransfer-Encoding: chunked;q=1\r\n\r\n", 501),
        ("Transfer-Encoding in HTTP/1.0", "POST / HTTP/1.0\r\nHost: localhost:8080\r\nTransfer-Encoding: chunked\r\n\r\n", 400),
        ("Origin twice", "POST / HTTP/1.1\r\nHost: localhost:8080\r\nOrigin: http://localhost:1\r\nOrigin: http://evil.example\r\n\r\n", 400),
        ("Sec-Fetch-Site twice", "GET / HTTP/1.1\r\nHost: localhost:8080\r\nSec-Fetch-Site: cross-site\r\nSec-Fetch-Site: cross-site\r\n\r\n", 400),
        ("the upload token twice",
            $"POST / HTTP/1.1\r\nHost: localhost:8080\r\n{BridgeBlobs.UploadTokenHeader}: a\r\n{BridgeBlobs.UploadTokenHeader}: b\r\n\r\n", 400),
        ("another expectation", "POST / HTTP/1.1\r\nHost: localhost:8080\r\nExpect: something\r\n\r\n", 417),
        ("too many fields", "GET / HTTP/1.1\r\nHost: localhost:8080\r\n" + string.Concat(Enumerable.Range(0, 100).Select(index => $"X-{index}: 1\r\n")) + "\r\n", 431),
        ("a head over 32 KiB", "GET / HTTP/1.1\r\nHost: localhost:8080\r\nX: " + new string('a', 33 * 1024) + "\r\n\r\n", 431),
        ("a request line over 8 KiB", "GET /" + new string('a', 8 * 1024) + " HTTP/1.1\r\nHost: localhost:8080\r\n\r\n", 414),
    ];

    [Test]
    public async Task TheLoopbackParserRefusesMalformedOversizedAndAmbiguousHeads()
    {
        var limits = new LoopbackLimits();
        var failures = new List<string>();
        foreach (var (name, text, expected) in RefusedHeads)
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            var length = LoopbackParser.HeadLength(bytes);
            if (length == -1)
            {
                // A bare CR or LF: refused before the head is complete, with 400.
                if (expected != 400) failures.Add($"{name}: refused as malformed, not with {expected}");
                continue;
            }
            if (length == 0)
            {
                failures.Add($"{name}: the head never ends");
                continue;
            }
            var head = LoopbackParser.Parse(bytes.AsSpan(0, length), Authority, limits, out var status, out var problem);
            if (head is not null) failures.Add($"{name}: accepted");
            else if (status != expected) failures.Add($"{name}: {status} ({problem}), not {expected}");
        }
        Require(failures.Count == 0, "The parser:\n" + string.Join("\n", failures));
        await Task.CompletedTask;
    }

    [Test]
    public async Task TheLoopbackParserReadsWhatClientsSend()
    {
        var limits = new LoopbackLimits();
        var head = ParseHead("\r\nPOST /_runic/upload?x=1/? HTTP/1.1\r\nHOST: LOCALHOST:8080\r\nContent-Length: 12\r\nX-Runic-Upload-Name: a%20b.txt\r\n" +
            "If-None-Match: \"a\"\r\nIf-None-Match:  \"b\" \t\r\nCookie: k=é\r\n\r\n", limits);
        Require(head is { Method: "POST", Path: "/_runic/upload", Target: "/_runic/upload?x=1/?", Http11: true, ContentLength: 12, Chunked: false, KeepAlive: true },
            "A POST with a Content-Length was not read as sent.");
        Require(head!.Header("if-none-match") == "\"a\", \"b\"" && head.Header("Cookie") == "k=é" && head.Header("Missing") == "",
            $"The fields were read as {head.Header("If-None-Match")} and {head.Header("Cookie")}.");
        var chunked = ParseHead("POST / HTTP/1.1\r\nHost: localhost:8080\r\nTransfer-Encoding: Chunked\r\nExpect: 100-continue\r\nConnection: keep-alive, Close\r\n\r\n", limits);
        Require(chunked is { Chunked: true, ContentLength: null, ExpectContinue: true, KeepAlive: false, HasBody: true }, "A chunked POST was not read as sent.");
        var old = ParseHead("GET /a/b%2F.c HTTP/1.0\r\nHost: localhost:8080\r\n\r\n", limits);
        Require(old is { Http11: false, KeepAlive: false, HasBody: false, Path: "/a/b%2F.c" }, "An HTTP/1.0 GET was not read as sent.");
        // The head is complete only at its empty line; its leading empty lines belong to it.
        Require(LoopbackParser.HeadLength("GET / HTTP/1.1\r\nHost: x\r\n"u8) == 0 && LoopbackParser.HeadLength("\r\n\r\nGET / HTTP/1.1\r\n\r\nrest"u8) == 22,
            "The head's end was found in the wrong place.");
        await Task.CompletedTask;

        static cswebui::Runic.Application.Views.CsWebUi.LoopbackHttpRequestHead? ParseHead(string text, LoopbackLimits limits)
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            var length = LoopbackParser.HeadLength(bytes);
            var head = LoopbackParser.Parse(bytes.AsSpan(0, length), Authority, limits, out var status, out var problem);
            Require(head is not null, $"{text.ReplaceLineEndings("|")} was refused with {status}: {problem}");
            return head;
        }
    }

    [Test]
    [Arguments("0", 0L)]
    [Arguments("1a", 26L)]
    [Arguments("00000000000001A", 26L)]
    [Arguments("ff;name=value", 255L)]
    [Arguments("ff ;name=\"v\"", 255L)]
    [Arguments("", -1L)]
    [Arguments("-1", -1L)]
    [Arguments("+1", -1L)]
    [Arguments("0x10", -1L)]
    [Arguments(" 10", -1L)]
    [Arguments("1 2", -1L)]
    [Arguments("10 ", -1L)]
    [Arguments("1000000000000000", -1L)]
    [Arguments("g", -1L)]
    [Arguments("5;a\u0001", -1L)]
    public async Task TheLoopbackParserReadsChunkSizesStrictly(string line, long expected)
    {
        var size = LoopbackParser.ParseChunkSize(Encoding.Latin1.GetBytes(line));
        Require((size ?? -1) == expected, $"The chunk size line \"{line}\" was read as {size?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "malformed"}, not {expected}.");
        await Task.CompletedTask;
    }

    // ---- The server through real sockets ----

    [Test]
    public async Task TheLoopbackServerKeepsConnectionsAliveAndAnswersPipelinedRequestsInOrder()
    {
        if (OperatingSystem.IsWindows()) return;
        using var server = TestServer(new LoopbackLimits(), out var events);
        using var client = await RawClient.ConnectAsync(server.LocalEndPoint);
        var host = $"localhost:{server.LocalEndPoint.Port}";
        await client.SendAsync($"GET /echo?1 HTTP/1.1\r\nHost: {host}\r\n\r\n");
        var first = await client.ReadResponseAsync();
        await client.SendAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Length: 3\r\n\r\nabc");
        var second = await client.ReadResponseAsync();
        // Two requests in one write: answered in turn, never mixed.
        await client.SendAsync($"GET /echo HTTP/1.1\r\nHost: {host}\r\n\r\nPOST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Length: 2\r\n\r\nxyHEAD /echo HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
        var third = await client.ReadResponseAsync();
        var fourth = await client.ReadResponseAsync();
        var fifth = await client.ReadResponseAsync(head: true);
        var end = await client.EndAsync(TimeSpan.FromSeconds(5));
        Require(first is { Status: 200, Body: "GET /echo 0" } && second is { Status: 200, Body: "POST /echo 3:abc" } && third.Body == "GET /echo 0"
            && fourth.Body == "POST /echo 2:xy" && fifth is { Status: 200, Body: "" } && fifth.Header("Content-Length") == "12"
            && fifth.Header("Connection") == "close" && end == "closed",
            $"The connection answered {first}, {second}, {third}, {fourth}, {fifth} and then {end}.");
        Require(first.Header("Connection") == "" && first.Header("Date").Length > 0, $"The first response's fields: {first.Head}");
    }

    // A body is never read as a request, and a request whose body's length is ambiguous is refused, not guessed.
    [Test]
    public async Task TheLoopbackServerNeverReadsABodyAsARequest()
    {
        if (OperatingSystem.IsWindows()) return;
        using var server = TestServer(new LoopbackLimits(), out var events);
        var host = $"localhost:{server.LocalEndPoint.Port}";
        var smuggled = $"GET /secret HTTP/1.1\r\nHost: {host}\r\n\r\n";
        // A handler that answers without reading the body: the server discards it, and the next request is the next one.
        using (var client = await RawClient.ConnectAsync(server.LocalEndPoint))
        {
            await client.SendAsync($"POST /deny HTTP/1.1\r\nHost: {host}\r\nContent-Length: {smuggled.Length}\r\n\r\n{smuggled}GET /echo HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
            var denied = await client.ReadResponseAsync();
            var next = await client.ReadResponseAsync();
            Require(denied.Status == 403 && next is { Status: 200, Body: "GET /echo 0" } && await client.EndAsync(TimeSpan.FromSeconds(5)) == "closed",
                $"After a denied POST whose body is a request, the connection answered {denied} and {next}.");
        }
        // Content-Length beside Transfer-Encoding: 400, and the connection ends before any byte after the head is read.
        foreach (var framing in new[] { "Content-Length: 4\r\nTransfer-Encoding: chunked", "Transfer-Encoding: chunked\r\nContent-Length: 4",
                     "Transfer-Encoding: chunked\r\nTransfer-Encoding: identity", "Content-Length: 4\r\nContent-Length: 40" })
        {
            using var client = await RawClient.ConnectAsync(server.LocalEndPoint);
            await client.SendAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\n{framing}\r\n\r\n0\r\n\r\n{smuggled}");
            var refused = await client.ReadResponseAsync();
            var end = await client.EndAsync(TimeSpan.FromSeconds(5));
            Require(refused.Status is 400 or 501 && refused.Header("Connection") == "close" && end is "closed" or "reset",
                $"With {framing.ReplaceLineEndings(" | ")}, the server answered {refused} and then {end}.");
        }
        Require(!events.Contains("GET /secret"), $"A smuggled request reached the handler: {string.Join(", ", events)}");
    }

    [Test]
    public async Task TheLoopbackServerReadsChunkedBodiesAndResetsMalformedOnes()
    {
        if (OperatingSystem.IsWindows()) return;
        using var server = TestServer(new LoopbackLimits(), out var events);
        var host = $"localhost:{server.LocalEndPoint.Port}";
        using (var client = await RawClient.ConnectAsync(server.LocalEndPoint))
        {
            await client.SendAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\n\r\n3;ext=1\r\nabc\r\n0A\r\n0123456789\r\n0\r\nX-Trailer: 1\r\n\r\n");
            var answer = await client.ReadResponseAsync();
            // The response of a handler that writes without a length is chunked.
            await client.SendAsync($"GET /stream HTTP/1.1\r\nHost: {host}\r\n\r\n");
            var streamed = await client.ReadResponseAsync();
            Require(answer is { Status: 200, Body: "POST /echo 13:abc0123456789" } && streamed is { Status: 200, Body: "one,two" }
                && streamed.Header("Transfer-Encoding") == "chunked",
                $"A chunked body was answered with {answer}, a streamed response read as {streamed}.");
        }
        foreach (var body in new[] { "zz\r\nabc\r\n0\r\n\r\n", "3\r\nabcdef\r\n0\r\n\r\n", "3\nabc\n0\n\n", "3\r\nabc0\r\n\r\n",
                     "3\r\nabc\r\n0\r\nbad trailer\r\n\r\n", "-3\r\nabc\r\n0\r\n\r\n", "fffffffffffffff\r\nabc" })
        {
            using var client = await RawClient.ConnectAsync(server.LocalEndPoint);
            await client.SendAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\n\r\n{body}", shutdown: true);
            var end = await client.EndAsync(TimeSpan.FromSeconds(10));
            Require(end is "closed" or "reset" && !client.Received.Contains("200 OK", StringComparison.Ordinal),
                $"A malformed chunked body ({body.ReplaceLineEndings("|")}) was answered with {client.Received.ReplaceLineEndings("|")} and then {end}.");
        }
    }

    // Slowloris: a head that arrives a byte at a time, and a connection that sends nothing, end at the head timeout.
    [Test]
    public async Task TheLoopbackServerTimesOutSlowHeadsAndIdleConnections()
    {
        if (OperatingSystem.IsWindows()) return;
        using var server = TestServer(new LoopbackLimits
        {
            HeadTimeout = TimeSpan.FromMilliseconds(600), FirstByteTimeout = TimeSpan.FromMilliseconds(900), IdleTimeout = TimeSpan.FromMilliseconds(1200),
        }, out _);
        using var idle = await RawClient.ConnectAsync(server.LocalEndPoint);
        using var slow = await RawClient.ConnectAsync(server.LocalEndPoint);
        var started = Stopwatch.StartNew();
        using var stop = new CancellationTokenSource();
        var trickle = Task.Run(async () =>
        {
            var text = $"GET /echo HTTP/1.1\r\nHost: localhost:{server.LocalEndPoint.Port}\r\nX: " + new string('a', 1000);
            foreach (var character in text)
            {
                try
                {
                    await slow.SendAsync(character.ToString());
                    await Task.Delay(50, stop.Token);
                }
                catch (Exception error) when (error is SocketException or OperationCanceledException or ObjectDisposedException) { return; }
            }
        });
        var idleEnd = await idle.EndAsync(TimeSpan.FromSeconds(10));
        var slowEnd = await slow.EndAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await trickle;
        Require(idleEnd == "closed" && idle.Received.Length == 0, $"An idle connection ended {idleEnd} after {idle.Received}.");
        Require(slowEnd is "closed" or "reset" && slow.Received.StartsWith("HTTP/1.1 408 ", StringComparison.Ordinal),
            $"A slow head ended {slowEnd} after {slow.Received.ReplaceLineEndings("|")}.");
        Require(started.Elapsed < TimeSpan.FromSeconds(8), $"The slow connections took {started.Elapsed} to end.");
        // A kept-alive connection waits IdleTimeout after a response, then closes quietly.
        using var kept = await RawClient.ConnectAsync(server.LocalEndPoint);
        await kept.SendAsync($"GET /echo HTTP/1.1\r\nHost: localhost:{server.LocalEndPoint.Port}\r\n\r\n");
        var answered = await kept.ReadResponseAsync();
        var idleSince = Stopwatch.StartNew();
        var keptEnd = await kept.EndAsync(TimeSpan.FromSeconds(10));
        Require(answered.Status == 200 && keptEnd == "closed" && idleSince.Elapsed > TimeSpan.FromMilliseconds(1000),
            $"A kept-alive connection was answered {answered} and ended {keptEnd} after {idleSince.Elapsed}.");
    }

    [Test]
    public async Task TheLoopbackServerBoundsHeadsBodiesRequestsAndConnections()
    {
        if (OperatingSystem.IsWindows()) return;
        var limits = new LoopbackLimits { MaxBodyBytes = () => 10, MaxRequestsPerConnection = 2 };
        using var server = TestServer(limits, out var events);
        var host = $"localhost:{server.LocalEndPoint.Port}";
        foreach (var (name, request, status) in new[]
                 {
                     ("a long field", $"GET /echo HTTP/1.1\r\nHost: {host}\r\nX: {new string('a', 40 * 1024)}\r\n\r\n", 431),
                     ("a long target", $"GET /{new string('a', 9 * 1024)} HTTP/1.1\r\nHost: {host}\r\n\r\n", 414),
                     ("a long target, still arriving", $"GET /{new string('a', 9 * 1024)}", 414),
                     ("another host", $"GET /echo HTTP/1.1\r\nHost: rebound.example:{server.LocalEndPoint.Port}\r\n\r\n", 400),
                 })
        {
            using var client = await RawClient.ConnectAsync(server.LocalEndPoint);
            await client.SendAsync(request);
            var answer = await client.ReadResponseAsync();
            Require(answer.Status == status && await client.EndAsync(TimeSpan.FromSeconds(5)) is "closed" or "reset", $"{name}: answered {answer}.");
        }
        // The body limit: the handler reads one byte past it, then the read fails.
        using (var client = await RawClient.ConnectAsync(server.LocalEndPoint))
        {
            await client.SendAsync($"POST /count HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\n\r\n64\r\n{new string('b', 100)}\r\n0\r\n\r\n");
            Require(await client.EndAsync(TimeSpan.FromSeconds(5)) is "closed" or "reset", "A body over the limit did not end the connection.");
            Require(await TestTimeouts.UntilAsync(() => events.Contains("count 11 IOException"), TimeSpan.FromSeconds(5)),
                $"A body over the limit was read as {string.Join(", ", events)}.");
        }
        // Requests per connection: the second response closes it.
        using (var client = await RawClient.ConnectAsync(server.LocalEndPoint))
        {
            await client.SendAsync($"GET /echo HTTP/1.1\r\nHost: {host}\r\n\r\nGET /echo HTTP/1.1\r\nHost: {host}\r\n\r\nGET /echo HTTP/1.1\r\nHost: {host}\r\n\r\n");
            var first = await client.ReadResponseAsync();
            var second = await client.ReadResponseAsync();
            var end = await client.EndAsync(TimeSpan.FromSeconds(5));
            Require(first.Header("Connection") == "" && second.Header("Connection") == "close" && end == "closed" && client.Received.Length == 0,
                $"The connection's limit of 2 requests: {first}, {second}, then {end} {client.Received}.");
        }
        // Connections: at the limit, a request in its handler keeps its connection, and a new connection takes the slot
        // of the oldest waiting one (a server of its own: the connections above linger).
        using var small = TestServer(new LoopbackLimits { MaxConnections = 3 }, out var smallEvents);
        var smallHost = $"localhost:{small.LocalEndPoint.Port}";
        using var busy = await RawClient.ConnectAsync(small.LocalEndPoint);
        await busy.SendAsync($"GET /wait HTTP/1.1\r\nHost: {smallHost}\r\n\r\n");
        Require(await TestTimeouts.UntilAsync(() => smallEvents.Contains("/wait started"), TimeSpan.FromSeconds(5)), "The waiting request did not start.");
        using var oldest = await RawClient.ConnectAsync(small.LocalEndPoint);
        Require(await TestTimeouts.UntilAsync(() => small.OpenConnections == 2, TimeSpan.FromSeconds(5)), $"{small.OpenConnections} connections are open.");
        using var newer = await RawClient.ConnectAsync(small.LocalEndPoint);
        Require(await TestTimeouts.UntilAsync(() => small.OpenConnections == 3, TimeSpan.FromSeconds(5)), $"{small.OpenConnections} connections are open.");
        using (var extra = await RawClient.ConnectAsync(small.LocalEndPoint))
        {
            await extra.SendAsync($"GET /echo HTTP/1.1\r\nHost: {smallHost}\r\n\r\n");
            var answer = await extra.ReadResponseAsync();
            Require(answer is { Status: 200, Body: "GET /echo 0" }, $"A connection over the limit was answered with {answer}.");
        }
        var oldestEnd = await oldest.EndAsync(TimeSpan.FromSeconds(5));
        Require(oldestEnd is "closed" or "reset" && small.OpenConnections <= 3, $"The oldest waiting connection ended {oldestEnd}; {small.OpenConnections} are open.");
        Require(!smallEvents.Contains("/wait aborted"), "The request in its handler lost its connection.");
        await newer.SendAsync($"GET /echo HTTP/1.1\r\nHost: {smallHost}\r\n\r\n");
        Require((await newer.ReadResponseAsync()).Status == 200, "The newer waiting connection was evicted before the oldest.");
    }

    // DEV-SEC-68a: another local user holds every connection slot with idle sockets, and opens new ones as soon as the
    // server drops any. The window's requests still get through: each new connection takes the slot of the oldest
    // connection without a request in its handler, and a browser sends its whole head at once.
    [Test]
    public async Task TheLoopbackServerServesRequestsWhileAnotherUserHoldsEverySlot()
    {
        if (OperatingSystem.IsWindows()) return;
        var limits = new LoopbackLimits();
        using var server = TestServer(limits, out _);
        var host = $"localhost:{server.LocalEndPoint.Port}";
        var held = new ConcurrentQueue<Socket>();
        using var stop = new CancellationTokenSource();
        try
        {
            for (var index = 0; index < limits.MaxConnections; index++)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(server.LocalEndPoint);
                held.Enqueue(socket);
            }
            Require(await TestTimeouts.UntilAsync(() => server.OpenConnections == limits.MaxConnections, TimeSpan.FromSeconds(10)),
                $"{server.OpenConnections} of the attacker's connections are open.");
            Require(await RequestAsync() == 200, "A request while every slot was held idle failed.");
            // The attacker reconnects: it keeps opening connections, more than the limit, and replaces each one the server ends.
            var attacker = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    foreach (var socket in held.ToArray())
                    {
                        if (socket.SafeHandle.IsClosed) continue;
                        try
                        {
                            if (!socket.Poll(0, SelectMode.SelectRead) || socket.Available > 0) continue;
                        }
                        catch (SocketException) { }
                        socket.Dispose();
                    }
                    var live = held.Count(socket => socket.SafeHandle is { IsClosed: false });
                    for (var index = live; index < limits.MaxConnections + 12; index++)
                    {
                        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        try { await socket.ConnectAsync(server.LocalEndPoint, stop.Token); held.Enqueue(socket); }
                        catch (Exception error) when (error is SocketException or OperationCanceledException) { socket.Dispose(); }
                    }
                    try { await Task.Delay(20, stop.Token); } catch (OperationCanceledException) { }
                }
            });
            var outcomes = new List<int>();
            for (var attempt = 0; attempt < 20; attempt++)
            {
                outcomes.Add(await RequestAsync());
                await Task.Delay(50);
            }
            stop.Cancel();
            await attacker;
            Require(outcomes.All(status => status == 200), $"Under a reconnecting attacker, the requests were answered with {string.Join(", ", outcomes)} (0: no answer).");
        }
        finally
        {
            stop.Cancel();
            foreach (var socket in held) socket.Dispose();
        }

        async Task<int> RequestAsync()
        {
            try
            {
                using var client = await RawClient.ConnectAsync(server.LocalEndPoint);
                await client.SendAsync($"GET /echo HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
                return (await client.ReadResponseAsync()).Status;
            }
            catch (Exception error) when (error is SocketException or IOException or TimeoutException) { return 0; }
        }
    }

    // DEV-SEC-70: an accept that fails, as when the process is out of file descriptors, does not end the server or the
    // relay. Each accepts again after a pause and reports the first failure of the run once.
    [Test]
    public async Task TheLoopbackServerAndRelayKeepAcceptingAfterAFailedAccept()
    {
        if (OperatingSystem.IsWindows()) return;
        var reported = new ConcurrentQueue<SocketError>();
        var server = LoopbackServer.Listen(new IPEndPoint(IPAddress.Loopback, 0), "localhost", new LoopbackLimits(), error => reported.Enqueue(error.SocketErrorCode));
        using (server)
        {
            var serverFailures = 3;
            server.AcceptClient = (socket, token) => Interlocked.Decrement(ref serverFailures) >= 0
                ? ValueTask.FromException<Socket>(new SocketException((int)SocketError.TooManyOpenSockets))
                : socket.AcceptAsync(token);
            var events = new ConcurrentBag<string>();
            server.Serve(request => HandleAsync(request, events));
            var host = $"localhost:{server.LocalEndPoint.Port}";
            using (var client = await RawClient.ConnectAsync(server.LocalEndPoint))
            {
                await client.SendAsync($"GET /echo HTTP/1.1\r\nHost: {host}\r\n\r\n");
                var answer = await client.ReadResponseAsync();
                Require(answer.Status == 200, $"After failed accepts, the server answered {answer}.");
            }
            // The relay in front of it, with failing accepts of its own.
            var relayFailures = 2;
            using var relay = LoopbackRelay.Start(IPAddress.Loopback, 0, server.LocalEndPoint, acceptFailed: error => reported.Enqueue(error.SocketErrorCode),
                accept: (socket, token) => Interlocked.Decrement(ref relayFailures) >= 0
                    ? ValueTask.FromException<Socket>(new SocketException((int)SocketError.NoBufferSpaceAvailable))
                    : socket.AcceptAsync(token)) ?? throw new InvalidOperationException("The relay did not start.");
            using (var client = await RawClient.ConnectAsync(relay.LocalEndPoint))
            {
                await client.SendAsync($"GET /echo HTTP/1.1\r\nHost: {host}\r\n\r\n");
                var answer = await client.ReadResponseAsync();
                Require(answer.Status == 200, $"After failed accepts, the relay passed on {answer}.");
            }
            Require(reported.ToArray() is [SocketError.TooManyOpenSockets, SocketError.NoBufferSpaceAvailable],
                $"The failed accepts were reported as {string.Join(", ", reported)}.");
            Require(LoopbackServer.AcceptBackoff(1) == TimeSpan.FromMilliseconds(100) && LoopbackServer.AcceptBackoff(3) == TimeSpan.FromMilliseconds(400)
                && LoopbackServer.AcceptBackoff(50) == TimeSpan.FromSeconds(1), "The accept backoff is not 100 ms doubling to 1 s.");
        }
    }

    // DEV-SEC-76: a report of a failed accept that throws, as a logging provider can while the process is out of
    // descriptors, does not end the accept loop of the server or of the relay.
    [Test]
    public async Task AThrowingAcceptReportDoesNotEndTheAcceptLoops()
    {
        if (OperatingSystem.IsWindows()) return;
        var reports = 0;
        void Throw(SocketException error)
        {
            Interlocked.Increment(ref reports);
            throw new AggregateException(new IOException("The logging provider could not open its file."));
        }
        using var server = LoopbackServer.Listen(new IPEndPoint(IPAddress.Loopback, 0), "localhost", new LoopbackLimits(), Throw);
        var serverFailures = 2;
        server.AcceptClient = (socket, token) => Interlocked.Decrement(ref serverFailures) >= 0
            ? ValueTask.FromException<Socket>(new SocketException((int)SocketError.TooManyOpenSockets))
            : socket.AcceptAsync(token);
        var events = new ConcurrentBag<string>();
        server.Serve(request => HandleAsync(request, events));
        var relayFailures = 2;
        using var relay = LoopbackRelay.Start(IPAddress.Loopback, 0, server.LocalEndPoint, acceptFailed: Throw,
            accept: (socket, token) => Interlocked.Decrement(ref relayFailures) >= 0
                ? ValueTask.FromException<Socket>(new SocketException((int)SocketError.TooManyOpenSockets))
                : socket.AcceptAsync(token)) ?? throw new InvalidOperationException("The relay did not start.");
        foreach (var target in new[] { server.LocalEndPoint, relay.LocalEndPoint })
        {
            using var client = await RawClient.ConnectAsync(target);
            await client.SendAsync($"GET /echo HTTP/1.1\r\nHost: localhost:{server.LocalEndPoint.Port}\r\n\r\n");
            var answer = await client.ReadResponseAsync();
            Require(answer.Status == 200, $"Through {target}, after a report that threw, the answer was {answer}.");
        }
        Require(Volatile.Read(ref reports) == 2, $"The failed accepts were reported {reports} times.");
    }

    // DEV-SEC-79: a relayed client that never closes its socket after the listener closed the connection keeps the
    // relay's two sockets for HalfCloseTimeout only; and at its limit, the relay resets the pair idle longest to make room.
    [Test]
    public async Task TheRelayReleasesHalfClosedPairsAndBoundsItsConnections()
    {
        if (OperatingSystem.IsWindows()) return;
        using var server = TestServer(new LoopbackLimits(), out _);
        var host = $"localhost:{server.LocalEndPoint.Port}";
        using var relay = LoopbackRelay.Start(IPAddress.Loopback, 0, server.LocalEndPoint, halfCloseTimeout: TimeSpan.FromMilliseconds(500))
            ?? throw new InvalidOperationException("The relay did not start.");
        var descriptors = Directory.Exists("/proc/self/fd") ? (Func<int>)(() => Directory.GetFiles("/proc/self/fd").Length) : null;
        var baseline = descriptors?.Invoke() ?? 0;
        const int Held = 50;
        var clients = new List<RawClient>();
        try
        {
            for (var index = 0; index < Held; index++)
            {
                var client = await RawClient.ConnectAsync(relay.LocalEndPoint);
                clients.Add(client);
                await client.SendAsync($"GET /echo HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
                Require((await client.ReadResponseAsync()).Status == 200, "A relayed request failed.");
            }
            // The listener closed each connection; the clients keep theirs open and never close them.
            var released = await TestTimeouts.UntilAsync(() => relay.OpenConnections == 0, TimeSpan.FromSeconds(10));
            Require(released, $"{relay.OpenConnections} half-closed pairs are still open.");
            if (descriptors is not null)
            {
                // The clients' own sockets stay; the relay's two per client are gone.
                var freed = await TestTimeouts.UntilAsync(() => descriptors() <= baseline + Held + 10, TimeSpan.FromSeconds(10));
                Require(freed, $"The process holds {descriptors()} descriptors, {baseline} before {Held} half-closed clients.");
            }
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }

        using var small = LoopbackRelay.Start(IPAddress.Loopback, 0, server.LocalEndPoint, maxConnections: 4)
            ?? throw new InvalidOperationException("The relay did not start.");
        var idle = new List<RawClient>();
        try
        {
            for (var index = 0; index < 4; index++) idle.Add(await RawClient.ConnectAsync(small.LocalEndPoint));
            Require(await TestTimeouts.UntilAsync(() => small.OpenConnections == 4, TimeSpan.FromSeconds(5)), $"{small.OpenConnections} pairs are open.");
            using var extra = await RawClient.ConnectAsync(small.LocalEndPoint);
            await extra.SendAsync($"GET /echo HTTP/1.1\r\nHost: {host}\r\n\r\n");
            var answer = await extra.ReadResponseAsync();
            var oldestEnd = await idle[0].EndAsync(TimeSpan.FromSeconds(5));
            Require(answer.Status == 200 && oldestEnd is "closed" or "reset" && small.OpenConnections <= 4,
                $"Over the relay's limit, the new client got {answer}, the idlest ended {oldestEnd}, and {small.OpenConnections} pairs are open.");
        }
        finally
        {
            foreach (var client in idle) client.Dispose();
        }
    }

    // DEV-SEC-66: a client whose address is not loopback (route_localnet) is reset at accept, before it takes a slot or
    // reaches the parser.
    [Test]
    public async Task TheLoopbackServerResetsAClientOfAnotherMachineAtAccept()
    {
        if (OperatingSystem.IsWindows()) return;
        var server = LoopbackServer.Listen(new IPEndPoint(IPAddress.Loopback, 0), "localhost");
        using (server)
        {
            server.PeerOf = static _ => new IPEndPoint(IPAddress.Parse("10.9.0.2"), 40_000);
            var events = new ConcurrentBag<string>();
            server.Serve(request => HandleAsync(request, events));
            // The reset can surface at the connect, as EPIPE on the send (macOS: SocketError.Shutdown, "Broken pipe"),
            // as ECONNRESET or an abort on the read, or as EOF: each means the server refused the client.
            string end;
            var received = "";
            try
            {
                using var client = await RawClient.ConnectAsync(server.LocalEndPoint);
                try { await client.SendAsync($"GET /echo HTTP/1.1\r\nHost: localhost:{server.LocalEndPoint.Port}\r\n\r\n"); }
                catch (SocketException error) when (IsRefusal(error.SocketErrorCode)) { }
                end = await client.EndAsync(TimeSpan.FromSeconds(5));
                received = client.Received;
            }
            catch (SocketException error) when (IsRefusal(error.SocketErrorCode))
            {
                end = "reset";
            }
            Require(end is "reset" or "closed" && received.Length == 0 && events.IsEmpty && server.OpenConnections == 0,
                $"A client of another machine ended {end} after {received}; the handler saw {string.Join(", ", events)}.");
        }
    }

    // How a platform reports a connection that the other side reset: ECONNRESET, ECONNABORTED, or EPIPE on a send.
    private static bool IsRefusal(SocketError error) =>
        error is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown;

    // DEV-SEC-68b: the head is scanned once as it arrives, however small its pieces (a rescan per read made a 32 KiB head
    // in 8-byte pieces cost about 100 times the CPU of one write). The scanner's count of bytes looked at stays linear.
    [Test]
    public async Task TheLoopbackServerScansAHeadInSmallPiecesInLinearTime()
    {
        var head = Encoding.ASCII.GetBytes("\r\nGET /echo HTTP/1.1\r\nHost: localhost:8080\r\nX: " + new string('a', 32 * 1024 - 100) + "\r\n\r\n");
        var scanner = new cswebui::Runic.Application.Views.CsWebUi.LoopbackHttpHeadScanner();
        var length = 0;
        for (var arrived = 1; arrived <= head.Length && length == 0; arrived++) length = scanner.Advance(head.AsSpan(0, arrived));
        Require(length == head.Length && scanner.Examined <= 2L * head.Length,
            $"A head of {head.Length} bytes, one byte at a time, was found at {length} after looking at {scanner.Examined} bytes.");
        // The same through a server, in 8-byte pieces: answered as a head in one write is.
        if (OperatingSystem.IsWindows()) return;
        using var server = TestServer(new LoopbackLimits(), out _);
        using var client = await RawClient.ConnectAsync(server.LocalEndPoint);
        var text = Encoding.ASCII.GetBytes($"POST /echo HTTP/1.1\r\nHost: localhost:{server.LocalEndPoint.Port}\r\nX: {new string('b', 16 * 1024)}\r\n" +
            "Transfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n0\r\nX-Trailer: " + new string('c', 8 * 1024) + "\r\n\r\n");
        for (var offset = 0; offset < text.Length; offset += 8) await client.SendAsync(text.AsMemory(offset, Math.Min(8, text.Length - offset)));
        var answer = await client.ReadResponseAsync();
        Require(answer is { Status: 200, Body: "POST /echo 3:abc" }, $"A head and trailer sent in 8-byte pieces were answered with {answer}.");
    }

    // A response field cannot carry a line break (no response splitting), and the server sets the framing fields itself.
    [Test]
    public async Task TheLoopbackServerRefusesResponseSplitting()
    {
        if (OperatingSystem.IsWindows()) return;
        using var server = TestServer(new LoopbackLimits(), out var events);
        foreach (var path in new[] { "/split", "/framing" })
        {
            using var client = await RawClient.ConnectAsync(server.LocalEndPoint);
            await client.SendAsync($"GET {path} HTTP/1.1\r\nHost: localhost:{server.LocalEndPoint.Port}\r\n\r\n");
            var end = await client.EndAsync(TimeSpan.FromSeconds(5));
            Require(end is "closed" or "reset" && !client.Received.Contains("Injected", StringComparison.Ordinal) && !client.Received.Contains("200", StringComparison.Ordinal),
                $"{path}: the client received {client.Received.ReplaceLineEndings("|")} and then {end}.");
        }
        Require(events.Contains("/split ArgumentException") && events.Contains("/framing ArgumentException"), $"The handler saw {string.Join(", ", events)}.");
    }

    [Test]
    public async Task TheLoopbackServerSends100ContinueOnlyForABodyItReads()
    {
        if (OperatingSystem.IsWindows()) return;
        using var server = TestServer(new LoopbackLimits(), out _);
        var host = $"localhost:{server.LocalEndPoint.Port}";
        using (var client = await RawClient.ConnectAsync(server.LocalEndPoint))
        {
            await client.SendAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Length: 2\r\nExpect: 100-continue\r\n\r\n");
            var interim = await client.ReadResponseAsync();
            await client.SendAsync("ok");
            var final = await client.ReadResponseAsync();
            Require(interim.Status == 100 && final is { Status: 200, Body: "POST /echo 2:ok" }, $"Expect: 100-continue was answered with {interim} and {final}.");
        }
        using (var client = await RawClient.ConnectAsync(server.LocalEndPoint))
        {
            await client.SendAsync($"POST /deny HTTP/1.1\r\nHost: {host}\r\nContent-Length: 2\r\nExpect: 100-continue\r\n\r\n");
            var denied = await client.ReadResponseAsync();
            Require(denied.Status == 403 && denied.Header("Connection") == "close" && await client.EndAsync(TimeSpan.FromSeconds(5)) == "closed",
                $"A denied request that expected 100-continue was answered with {denied}.");
        }
    }

    [Test]
    public async Task TheLoopbackServerStopsCleanly()
    {
        if (OperatingSystem.IsWindows()) return;
        var server = TestServer(new LoopbackLimits(), out var events);
        var endPoint = server.LocalEndPoint;
        using var waiting = await RawClient.ConnectAsync(endPoint);
        using var idle = await RawClient.ConnectAsync(endPoint);
        await waiting.SendAsync($"GET /wait HTTP/1.1\r\nHost: localhost:{endPoint.Port}\r\n\r\n");
        Require(await TestTimeouts.UntilAsync(() => events.Contains("/wait started"), TimeSpan.FromSeconds(5)), "The request did not start.");
        server.Dispose();
        Require(await TestTimeouts.UntilAsync(() => events.Contains("/wait aborted"), TimeSpan.FromSeconds(5)), "The request was not aborted.");
        var ends = (await waiting.EndAsync(TimeSpan.FromSeconds(5)), await idle.EndAsync(TimeSpan.FromSeconds(5)));
        Require(ends is ("closed" or "reset", "closed" or "reset"), $"The connections ended {ends}.");
        using var again = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        again.Bind(endPoint);
        again.Listen();
    }

    // ---- The endpoint through the server ----

    [Test]
    public async Task TheLoopbackEndpointServesBlobsAsHttpListenerDid()
    {
        if (OperatingSystem.IsWindows()) return;
        using var work = new Work();
        using var blobs = work.Blobs();
        using var endpoint = BlobEndpoint.Start(blobs, static () => PagePort, NullLogger.Instance, new Ports().Next)
            ?? throw new InvalidOperationException("The endpoint did not start.");
        var blob = RunicBlob.FromBytes("0123456789"u8.ToArray(), "text/plain", "digits.txt");
        string url;
        using (BridgeBlobScope.Enter(blobs, new object(), null))
            url = blobs.Issue(blob, BridgeBlobScope.Current!);
        var path = new Uri(url).AbsolutePath;
        var host = $"{endpoint.BaseUrl.Host}:{endpoint.Port}";
        var target = new IPEndPoint(IPAddress.Loopback, endpoint.Port);
        using var client = await RawClient.ConnectAsync(target);
        await client.SendAsync($"GET {path} HTTP/1.1\r\nHost: {host}\r\nOrigin: http://localhost:{PagePort}\r\nRange: bytes=2-4\r\n\r\n");
        var range = await client.ReadResponseAsync();
        await client.SendAsync($"HEAD {path} HTTP/1.1\r\nHost: {host}\r\n\r\n");
        var head = await client.ReadResponseAsync(head: true);
        await client.SendAsync($"GET {path} HTTP/1.1\r\nHost: {host}\r\nIf-None-Match: {head.Header("ETag")}\r\n\r\n");
        var cached = await client.ReadResponseAsync(head: true);
        await client.SendAsync($"GET {path}x HTTP/1.1\r\nHost: {host}\r\n\r\n");
        var missing = await client.ReadResponseAsync();
        await client.SendAsync($"DELETE {path} HTTP/1.1\r\nHost: {host}\r\n\r\n");
        var method = await client.ReadResponseAsync();
        await client.SendAsync($"GET {path} HTTP/1.1\r\nHost: {host}\r\nSec-Fetch-Site: cross-site\r\nConnection: close\r\n\r\n");
        var crossSite = await client.ReadResponseAsync();
        Require(range is { Status: 206, Body: "234" } && range.Header("Content-Range") == "bytes 2-4/10" && range.Header("Access-Control-Allow-Origin") == $"http://localhost:{PagePort}",
            $"A range was answered with {range}: {range.Head}");
        Require(head is { Status: 200, Body: "" } && head.Header("Content-Length") == "10" && head.Header("Content-Type") == "text/plain"
            && head.Header("Content-Disposition").StartsWith("inline; filename=\"digits.txt\"", StringComparison.Ordinal) && head.Header("X-Content-Type-Options") == "nosniff",
            $"HEAD was answered with {head}: {head.Head}");
        Require(cached.Status == 304 && missing.Status == 404 && method.Status == 405 && crossSite.Status == 403,
            $"Conditional, unknown, DELETE and cross-site requests were answered with {cached.Status}, {missing.Status}, {method.Status} and {crossSite.Status}.");
    }

    [Test]
    public async Task TheLoopbackEndpointReceivesChunkedUploadsAndAnswersRefusedOnes()
    {
        if (OperatingSystem.IsWindows()) return;
        using var work = new Work();
        using var blobs = work.Blobs();
        using var endpoint = BlobEndpoint.Start(blobs, static () => PagePort, NullLogger.Instance, new Ports().Next)
            ?? throw new InvalidOperationException("The endpoint did not start.");
        var grant = blobs.Grant(ConnectionKey) ?? throw new InvalidOperationException("The hello granted no upload.");
        var host = $"{endpoint.BaseUrl.Host}:{endpoint.Port}";
        var target = new IPEndPoint(IPAddress.Loopback, endpoint.Port);
        var fields = $"Host: {host}\r\nOrigin: http://localhost:{PagePort}\r\nContent-Type: text/plain\r\n{BridgeBlobs.UploadNameHeader}: chunked.txt\r\n";
        using (var client = await RawClient.ConnectAsync(target))
        {
            // A chunked upload, as a streamed body is sent.
            await client.SendAsync($"POST /_runic/upload HTTP/1.1\r\n{fields}{BridgeBlobs.UploadTokenHeader}: {grant.Token}\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n");
            var stored = await client.ReadResponseAsync();
            Require(stored.Status == 201 && stored.Body.Contains("\"size\":11", StringComparison.Ordinal) && blobs.UploadCount == 1,
                $"A chunked upload was answered with {stored}.");
        }
        // An upload refused before its body is read: the client still receives the answer, however large the body.
        foreach (var length in new[] { 1_000_000, 8_000_000 })
        {
            using var client = await RawClient.ConnectAsync(target);
            await client.SendAsync($"POST /_runic/upload HTTP/1.1\r\n{fields}Content-Length: {length}\r\n\r\n");
            var body = new byte[64 * 1024];
            var sending = Task.Run(async () =>
            {
                for (var sent = 0; sent < length; sent += body.Length)
                {
                    try { await client.SendAsync(body.AsMemory(0, Math.Min(body.Length, length - sent))); }
                    catch (Exception error) when (error is SocketException or ObjectDisposedException) { return; }
                }
            });
            var refused = await client.ReadResponseAsync();
            await sending;
            Require(refused.Status == 403 && refused.Body.Contains("forbidden", StringComparison.Ordinal), $"An upload of {length} bytes without the token was answered with {refused}.");
        }
        // The window's limit answers 413 for a declared length, and the server never passes on more than the limit and a byte.
        blobs.Configure(16, null);
        using (var client = await RawClient.ConnectAsync(target))
        {
            await client.SendAsync($"POST /_runic/upload HTTP/1.1\r\n{fields}{BridgeBlobs.UploadTokenHeader}: {grant.Token}\r\nContent-Length: 17\r\n\r\n{new string('c', 17)}");
            var tooLarge = await client.ReadResponseAsync();
            await client.SendAsync($"POST /_runic/upload HTTP/1.1\r\n{fields}{BridgeBlobs.UploadTokenHeader}: {grant.Token}\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n40\r\n{new string('d', 64)}\r\n0\r\n\r\n");
            var chunkedTooLarge = await client.ReadResponseAsync();
            Require(tooLarge.Status == 413 && chunkedTooLarge.Status == 413, $"Uploads over the limit were answered with {tooLarge} and {chunkedTooLarge}.");
        }
        Require(blobs.UploadCount == 1, $"The window holds {blobs.UploadCount} uploads.");
    }

    // A server for the checks above, whose handler records what it saw in events:
    // /echo reads the body and answers "{method} {path} {length}[:{body}]"; /deny answers 403 unread; /stream writes
    // two parts without a length; /count reads until the body fails; /split and /framing set fields the server refuses;
    // /wait waits for RequestAborted.
    private static LoopbackServer TestServer(LoopbackLimits limits, out ConcurrentBag<string> events)
    {
        var seen = new ConcurrentBag<string>();
        events = seen;
        var server = LoopbackServer.Listen(new IPEndPoint(IPAddress.Loopback, 0), "localhost", limits);
        server.Serve(request => HandleAsync(request, seen));
        return server;
    }

    private static async Task HandleAsync(BlobRequest request, ConcurrentBag<string> events)
    {
        events.Add($"{request.Method} {request.Path}");
        switch (request.Path)
        {
            case "/echo":
            {
                using var body = new MemoryStream();
                await request.RequestBody.CopyToAsync(body);
                var text = body.Length == 0 ? "" : ":" + Encoding.ASCII.GetString(body.ToArray());
                await WriteAsync(request, $"{request.Method} {request.Path} {body.Length}{text}");
                return;
            }
            case "/deny":
                request.StatusCode = 403;
                return;
            case "/stream":
                await request.ResponseBody.WriteAsync("one,"u8.ToArray());
                await request.ResponseBody.WriteAsync("two"u8.ToArray());
                return;
            case "/count":
            {
                var buffer = new byte[7];
                long count = 0;
                try
                {
                    while (await request.RequestBody.ReadAsync(buffer) is var read and > 0) count += read;
                    events.Add($"count {count}");
                }
                catch (IOException)
                {
                    events.Add($"count {count} IOException");
                    throw;
                }
                return;
            }
            case "/split":
            case "/framing":
                try
                {
                    if (request.Path == "/split") request.SetResponseHeader("X-Value", "a\r\nInjected: 1");
                    else request.SetResponseHeader("Content-Length", "0");
                }
                catch (ArgumentException)
                {
                    events.Add($"{request.Path} ArgumentException");
                    throw;
                }
                return;
            case "/wait":
                events.Add("/wait started");
                try { await Task.Delay(Timeout.Infinite, request.RequestAborted); }
                catch (OperationCanceledException) { events.Add("/wait aborted"); throw; }
                return;
            default:
                request.StatusCode = 404;
                return;
        }

        static async Task WriteAsync(BlobRequest request, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            request.ContentType = "text/plain";
            request.ContentLength = bytes.Length;
            if (request.Method != "HEAD") await request.ResponseBody.WriteAsync(bytes);
        }
    }

    private sealed record RawResponse(int Status, string Head, string Body)
    {
        internal string Header(string name)
        {
            foreach (var line in Head.Split("\r\n").Skip(1))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && line[..colon].Equals(name, StringComparison.OrdinalIgnoreCase)) return line[(colon + 1)..].Trim();
            }
            return "";
        }

        public override string ToString() => $"{Status} \"{(Body.Length > 200 ? Body[..200] + "…" : Body)}\"";
    }

    // A client of raw HTTP/1.1 over one socket: what it sends is exactly what the server receives.
    private sealed class RawClient : IDisposable
    {
        private readonly Socket _socket;
        private readonly List<byte> _pending = [];

        private RawClient(Socket socket) => _socket = socket;

        // What arrived after the last response read, as text; filled by EndAsync.
        internal string Received { get; private set; } = "";

        internal static async Task<RawClient> ConnectAsync(IPEndPoint endPoint)
        {
            var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(endPoint).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { socket.Dispose(); throw; }
            return new RawClient(socket);
        }

        internal async Task SendAsync(string text, bool shutdown = false)
        {
            await SendAsync(Encoding.Latin1.GetBytes(text));
            if (shutdown) _socket.Shutdown(SocketShutdown.Send);
        }

        internal async Task SendAsync(ReadOnlyMemory<byte> data)
        {
            while (!data.IsEmpty)
            {
                var sent = await _socket.SendAsync(data, SocketFlags.None);
                data = data[sent..];
            }
        }

        // One response: its head, and its body by Content-Length or chunked (none for HEAD, 1xx, 204 and 304).
        internal async Task<RawResponse> ReadResponseAsync(bool head = false)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            int end;
            while ((end = IndexOf(_pending, "\r\n\r\n"u8)) < 0) await FillAsync(timeout.Token);
            var headText = Encoding.Latin1.GetString(_pending.GetRange(0, end).ToArray());
            _pending.RemoveRange(0, end + 4);
            var status = int.Parse(headText.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
            var response = new RawResponse(status, headText, "");
            if (head || status is < 200 or 204 or 304) return response;
            if (response.Header("Transfer-Encoding") == "chunked")
            {
                var body = new StringBuilder();
                while (true)
                {
                    int lineEnd;
                    while ((lineEnd = IndexOf(_pending, "\r\n"u8)) < 0) await FillAsync(timeout.Token);
                    var size = Convert.ToInt32(Encoding.ASCII.GetString(_pending.GetRange(0, lineEnd).ToArray()), 16);
                    _pending.RemoveRange(0, lineEnd + 2);
                    while (_pending.Count < size + 2) await FillAsync(timeout.Token);
                    body.Append(Encoding.Latin1.GetString(_pending.GetRange(0, size).ToArray()));
                    _pending.RemoveRange(0, size + 2);
                    if (size == 0) return response with { Body = body.ToString() };
                }
            }
            var length = int.Parse(response.Header("Content-Length") is { Length: > 0 } value ? value : "0", System.Globalization.CultureInfo.InvariantCulture);
            while (_pending.Count < length) await FillAsync(timeout.Token);
            var text = Encoding.Latin1.GetString(_pending.GetRange(0, length).ToArray());
            _pending.RemoveRange(0, length);
            return response with { Body = text };
        }

        // Reads until the server ends the connection: "closed" (FIN), "reset", or "open" when it stays open for timeout.
        // Received holds what arrived meanwhile.
        internal async Task<string> EndAsync(TimeSpan timeout)
        {
            using var cancel = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    if (await ReceiveAsync(cancel.Token) == 0) return "closed";
                }
            }
            catch (OperationCanceledException) { return "open"; }
            catch (SocketException error) when (IsRefusal(error.SocketErrorCode)) { return "reset"; }
            finally
            {
                Received = Encoding.Latin1.GetString(_pending.ToArray());
            }
        }

        private async Task FillAsync(CancellationToken cancellationToken)
        {
            if (await ReceiveAsync(cancellationToken) == 0)
                throw new IOException($"The server closed the connection; received {Encoding.Latin1.GetString(_pending.ToArray())}");
        }

        private async Task<int> ReceiveAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];
            var read = await _socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
            _pending.AddRange(buffer.AsSpan(0, read));
            return read;
        }

        private static int IndexOf(List<byte> bytes, ReadOnlySpan<byte> value)
        {
            for (var index = 0; index + value.Length <= bytes.Count; index++)
            {
                var match = true;
                for (var offset = 0; offset < value.Length && match; offset++) match = bytes[index + offset] == value[offset];
                if (match) return index;
            }
            return -1;
        }

        public void Dispose() => _socket.Dispose();
    }
}
