using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.WebKit;

/// <summary>
/// Which requests the CDP endpoint answers. A JavaScript debugger runs on the developer's machine and sends no
/// <c>Origin</c>; a web page always does, and DNS rebinding needs a host name. So the endpoint, which can script the
/// Window's page and its Bridge, refuses (403) a <c>Host</c> that is not an IP literal or <c>localhost</c> and a request
/// with an <c>Origin</c> that is not explicitly allowed, before anything else. Chromium checks the same two
/// (<c>--remote-allow-origins</c>); WebKitGTK's own HTTP inspector did not until WebKit bug 330602.
/// </summary>
internal static class CdpRequestPolicy
{
    internal static bool Allows(string? host, string? origin, IReadOnlySet<string> allowedOrigins)
    {
        ArgumentNullException.ThrowIfNull(allowedOrigins);
        if (!IsLoopbackOrLiteralHost(host)) return false;
        return string.IsNullOrEmpty(origin) || allowedOrigins.Contains(origin);
    }

    /// <summary>An IP literal (with or without a port, IPv6 in brackets) or <c>localhost</c>.</summary>
    internal static bool IsLoopbackOrLiteralHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string name = host.Trim();
        if (name.StartsWith('['))
        {
            int close = name.IndexOf(']', StringComparison.Ordinal);
            return close > 1 && IPAddress.TryParse(name[1..close], out _) &&
                (close == name.Length - 1 || (name[close + 1] == ':' && IsPort(name[(close + 2)..])));
        }
        int colon = name.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!IsPort(name[(colon + 1)..])) return false;
            name = name[..colon];
        }
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(name, out IPAddress? address) && !name.Contains(':', StringComparison.Ordinal) && address.AddressFamily == AddressFamily.InterNetwork);
    }

    private static bool IsPort(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is > 0 and <= 65535;
}

/// <summary>The browser-level answers that do not depend on a page.</summary>
internal static class CdpBrowser
{
    internal const string Product = "WebKitGTK (Runic CDP adapter)";

    internal static JsonObject Version() => new()
    {
        ["protocolVersion"] = "1.3",
        ["product"] = Product,
        ["revision"] = "",
        ["userAgent"] = "WebKitGTK",
        ["jsVersion"] = "",
    };
}

/// <summary>
/// The Chrome DevTools Protocol endpoint for the Window's WebKitGTK pages, on loopback: <c>/json/version</c>,
/// <c>/json/list</c> (and <c>/json</c>), and WebSockets at <c>/devtools/browser/&lt;secret&gt;</c> (flat sessions, what
/// js-debug uses) and <c>/devtools/page/&lt;secret&gt;/&lt;id&gt;</c>. The WebSocket paths carry a random secret that
/// only the <c>/json</c> answers reveal, which a page in a browser cannot read. A small HTTP/1.1 reader of its own keeps
/// dotnet-runic free of ASP.NET Core; it answers GET only.
/// </summary>
internal sealed class CdpServer : IAsyncDisposable
{
    private const int MaximumRequestHead = 16 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly TcpListener _listener;
    private readonly WebKitDebugEndpoint _endpoint;
    private readonly IReadOnlySet<string> _allowedOrigins;
    private readonly string _secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private Task _accepting = Task.CompletedTask;

    private CdpServer(TcpListener listener, WebKitDebugEndpoint endpoint, IReadOnlySet<string> allowedOrigins)
    {
        _listener = listener;
        _endpoint = endpoint;
        _allowedOrigins = allowedOrigins;
    }

    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Listens on 127.0.0.1:<paramref name="port"/>; null when the port is taken.</summary>
    internal static CdpServer? TryStart(int port, WebKitDebugEndpoint endpoint, IReadOnlySet<string>? allowedOrigins = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            listener.Dispose();
            return null;
        }
        var server = new CdpServer(listener, endpoint, allowedOrigins ?? new HashSet<string>(StringComparer.Ordinal));
        server._accepting = Task.Run(server.AcceptAsync);
        return server;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                // A debugger answers each small message before the next one comes, as at each instrumentation pause:
                // Nagle's algorithm would hold every answer back for the delayed acknowledgement (#1051).
                client.NoDelay = true;
                lock (_connections)
                {
                    _connections.RemoveAll(static task => task.IsCompleted);
                    _connections.Add(Task.Run(() => ServeAsync(client)));
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                HttpRequestHead? request;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    timeout.CancelAfter(RequestTimeout);
                    request = await HttpRequestHead.ReadAsync(stream, MaximumRequestHead, timeout.Token).ConfigureAwait(false);
                }
                if (request is null) return;
                await RouteAsync(request, stream).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException or WebSocketException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task RouteAsync(HttpRequestHead request, NetworkStream stream)
    {
        CdpTrace.Write("http>", $"{request.Method} {request.Path} Host={request.Header("Host")} Origin={request.Header("Origin")}");
        if (!CdpRequestPolicy.Allows(request.Header("Host"), request.Header("Origin"), _allowedOrigins))
        {
            await WriteAsync(stream, 403, "text/plain", "Forbidden: the Runic CDP adapter accepts loopback requests without an Origin.").ConfigureAwait(false);
            return;
        }
        if (request.Method != "GET")
        {
            await WriteAsync(stream, 405, "text/plain", "Method not allowed").ConfigureAwait(false);
            return;
        }
        string host = request.Header("Host")!;
        string path = request.Path.Split('?')[0];
        switch (path)
        {
            case "/json/version":
                await WriteAsync(stream, 200, "application/json", new JsonObject
                {
                    ["Browser"] = CdpBrowser.Product,
                    ["Protocol-Version"] = "1.3",
                    ["User-Agent"] = "WebKitGTK",
                    ["webSocketDebuggerUrl"] = $"ws://{host}/devtools/browser/{_secret}",
                }.ToJsonString()).ConfigureAwait(false);
                return;
            case "/json" or "/json/list":
            {
                var pages = new JsonArray();
                foreach (InspectorTarget target in await _endpoint.ListPagesAsync(_stop.Token).ConfigureAwait(false))
                {
                    pages.Add((JsonNode)new JsonObject
                    {
                        ["description"] = "",
                        ["id"] = target.PageId,
                        ["title"] = target.Name,
                        ["type"] = "page",
                        ["url"] = target.Url,
                        ["webSocketDebuggerUrl"] = $"ws://{host}/devtools/page/{_secret}/{target.PageId}",
                    });
                }
                await WriteAsync(stream, 200, "application/json", pages.ToJsonString()).ConfigureAwait(false);
                return;
            }
        }
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool browser = segments is ["devtools", "browser", _];
        bool page = segments is ["devtools", "page", _, _];
        if ((browser || page) && SecretMatches(segments[2]) && request.IsWebSocketUpgrade(out string? key))
        {
            await WriteUpgradeAsync(stream, key).ConfigureAwait(false);
            using WebSocket socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = true,
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            });
            await using var client = new CdpClient(socket, _endpoint, page ? segments[3] : null);
            await client.RunAsync(_stop.Token).ConfigureAwait(false);
            return;
        }
        await WriteAsync(stream, 404, "text/plain", "Not found").ConfigureAwait(false);
    }

    private bool SecretMatches(string candidate) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(_secret));

    private static async Task WriteUpgradeAsync(Stream stream, string key)
    {
        // RFC 6455 defines the handshake with SHA-1; it is not used for security here.
#pragma warning disable CA5350
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
        byte[] head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(head).ConfigureAwait(false);
    }

    private static async Task WriteAsync(Stream stream, int status, string contentType, string body)
    {
        byte[] content = Encoding.UTF8.GetBytes(body);
        string reason = status switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        byte[] head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status.ToString(CultureInfo.InvariantCulture)} {reason}\r\nContent-Type: {contentType}; charset=UTF-8\r\n" +
            $"Content-Length: {content.Length.ToString(CultureInfo.InvariantCulture)}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head).ConfigureAwait(false);
        await stream.WriteAsync(content).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        Task[] connections;
        lock (_connections) connections = [.. _connections];
        try
        {
            await Task.WhenAll([_accepting, .. connections]).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
        }
        _listener.Dispose();
        _stop.Dispose();
    }
}

/// <summary>The request line and headers of one HTTP/1.1 request.</summary>
internal sealed class HttpRequestHead
{
    private readonly Dictionary<string, string> _headers;

    private HttpRequestHead(string method, string path, Dictionary<string, string> headers)
    {
        Method = method;
        Path = path;
        _headers = headers;
    }

    internal string Method { get; }

    internal string Path { get; }

    internal string? Header(string name) => _headers.GetValueOrDefault(name);

    internal bool IsWebSocketUpgrade(out string key)
    {
        key = Header("Sec-WebSocket-Key") ?? "";
        return key.Length > 0 &&
            string.Equals(Header("Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase) &&
            (Header("Connection") ?? "").Contains("upgrade", StringComparison.OrdinalIgnoreCase) &&
            Header("Sec-WebSocket-Version") == "13";
    }

    /// <summary>Reads up to the blank line that ends the head, byte by byte, so nothing after it is consumed.</summary>
    internal static async Task<HttpRequestHead?> ReadAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(512);
        var one = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            bytes.Add(one[0]);
            if (bytes.Count > limit) return null;
            int count = bytes.Count;
            if (count >= 4 && bytes[count - 4] == '\r' && bytes[count - 3] == '\n' && bytes[count - 2] == '\r' && bytes[count - 1] == '\n') break;
        }
        return Parse(Encoding.Latin1.GetString(bytes.ToArray()));
    }

    internal static HttpRequestHead? Parse(string head)
    {
        string[] lines = head.Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || !requestLine[1].StartsWith('/')) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines[1..])
        {
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) return null;
            string name = line[..colon].Trim();
            // A repeated Host or Origin is ambiguous: refuse the request rather than pick one.
            if (headers.ContainsKey(name) && (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Origin", StringComparison.OrdinalIgnoreCase))) return null;
            headers[name] = line[(colon + 1)..].Trim();
        }
        return new HttpRequestHead(requestLine[0], requestLine[1], headers);
    }
}
