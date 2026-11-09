using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Runic.Application.Views.Desktop.Hosting;

// What an authentication callback decided about a connection's first message.
internal enum HostHelloVerdict
{
    // Not the host: the listener closes the connection with 1008 and keeps listening.
    Rejected,
    // The host: the listener stops and hands the connection over.
    Accepted,
}

// The guest's own loopback listener for the host channel (host-protocol.md, Channel listener): an HTTP/1.1 WebSocket
// upgrade of GET /_runic_host without Origin and with the listener's own Host, at most 8 connections that have not
// authenticated, each for a limited time, and exactly one authenticated connection, after which it stops listening.
// It needs nothing but the base class library, so it works with every Desktop server.
internal sealed class HostChannelListener : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 8_192;
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly string _host;
    private readonly TimeSpan _unauthenticatedTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly HashSet<TcpClient> _pending = [];
    private int _stopped;

    private HostChannelListener(TcpListener listener, TimeSpan unauthenticatedTimeout)
    {
        _listener = listener;
        _unauthenticatedTimeout = unauthenticatedTimeout;
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        _host = endpoint.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[::1]:{endpoint.Port}"
            : $"127.0.0.1:{endpoint.Port}";
        Url = new Uri($"ws://{_host}{HostProtocol.ChannelPath}");
    }

    internal Uri Url { get; }

    // Listens on 127.0.0.1, or on ::1 where IPv4 loopback is unavailable, on a port the system chooses.
    internal static HostChannelListener Start(TimeSpan unauthenticatedTimeout)
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
        }
        catch (SocketException)
        {
            listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener.Start();
        }
        return new HostChannelListener(listener, unauthenticatedTimeout);
    }

    // Accepts connections until one's first message is accepted by authenticate, then stops listening, closes the other
    // pending connections and returns that connection with its first message. authenticate may throw to stop: the
    // listener then stops and the exception propagates.
    internal async Task<(WebSocket Socket, JsonObject Hello)> AcceptAsync(
        Func<WebSocket, JsonObject, Task<HostHelloVerdict>> authenticate, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var accepted = new TaskCompletionSource<(WebSocket, JsonObject)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = linked.Token.Register(() => accepted.TrySetCanceled(linked.Token));
        var acceptLoop = AcceptLoopAsync(authenticate, accepted, linked.Token);
        try { return await accepted.Task.ConfigureAwait(false); }
        finally
        {
            Stop();
            await acceptLoop.ConfigureAwait(false);
        }
    }

    private async Task AcceptLoopAsync(Func<WebSocket, JsonObject, Task<HostHelloVerdict>> authenticate,
        TaskCompletionSource<(WebSocket, JsonObject)> accepted, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                return;
            }
            lock (_gate)
            {
                if (_stopped != 0 || _pending.Count >= HostProtocol.MaximumUnauthenticatedConnections)
                {
                    client.Dispose();
                    continue;
                }
                _pending.Add(client);
            }
            _ = HandleAsync(client, authenticate, accepted, cancellationToken);
        }
    }

    private async Task HandleAsync(TcpClient client, Func<WebSocket, JsonObject, Task<HostHelloVerdict>> authenticate,
        TaskCompletionSource<(WebSocket, JsonObject)> accepted, CancellationToken cancellationToken)
    {
        var keep = false;
        WebSocket? socket = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_unauthenticatedTimeout);
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            if (!await UpgradeAsync(stream, deadline.Token).ConfigureAwait(false)) return;
            socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
            JsonObject? hello;
            try { hello = await HostChannel.ReceiveAsync(socket, deadline.Token).ConfigureAwait(false); }
            catch (HostProtocolViolationException) { hello = null; }
            if (hello is null || HostProtocol.String(hello, "kind") != "hello")
            {
                await HostChannel.CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "").ConfigureAwait(false);
                return;
            }
            HostHelloVerdict verdict;
            try { verdict = await authenticate(socket, hello).ConfigureAwait(false); }
            catch (Exception error)
            {
                accepted.TrySetException(error);
                return;
            }
            if (verdict == HostHelloVerdict.Rejected)
            {
                await HostChannel.CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "").ConfigureAwait(false);
                return;
            }
            lock (_gate)
            {
                // One connection only: a second accepted hello, which needs the same single-use key, loses.
                if (_stopped != 0 || accepted.Task.IsCompleted) return;
                _pending.Remove(client);
                keep = accepted.TrySetResult((socket, hello));
            }
            if (keep) Stop();
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException or WebSocketException) { }
        finally
        {
            if (!keep)
            {
                lock (_gate) _pending.Remove(client);
                socket?.Dispose();
                client.Dispose();
            }
        }
    }

    // Reads the upgrade request and answers it. False when it was refused; the caller closes the connection.
    private async Task<bool> UpgradeAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[MaximumHeaderBytes];
        var length = 0;
        var one = new byte[1];
        // Byte by byte, so nothing after the header is consumed: the client sends no frame before the response.
        while (true)
        {
            if (length == header.Length) return await RespondAsync(stream, 431, "Request Header Fields Too Large", cancellationToken).ConfigureAwait(false);
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0) return false;
            header[length++] = one[0];
            if (length >= 4 && header[length - 4] == '\r' && header[length - 3] == '\n' && header[length - 2] == '\r' && header[length - 1] == '\n')
                break;
        }
        var lines = Encoding.Latin1.GetString(header, 0, length - 4).Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            return await RespondAsync(stream, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) return await RespondAsync(stream, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var earlier) ? earlier + ", " + value : value;
        }
        if (requestLine[0] != "GET") return await RespondAsync(stream, 405, "Method Not Allowed", cancellationToken).ConfigureAwait(false);
        if (requestLine[1] != HostProtocol.ChannelPath) return await RespondAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
        // A browser sends Origin with every WebSocket upgrade: no web page reaches the channel (T2).
        if (headers.ContainsKey("Origin")) return await RespondAsync(stream, 403, "Forbidden", cancellationToken).ConfigureAwait(false);
        // DNS rebinding names another host; only the listener's own loopback literal is admitted.
        if (!headers.TryGetValue("Host", out var host) || !string.Equals(host, _host, StringComparison.OrdinalIgnoreCase))
            return await RespondAsync(stream, 403, "Forbidden", cancellationToken).ConfigureAwait(false);
        if (headers.ContainsKey("Content-Length") || headers.ContainsKey("Transfer-Encoding") ||
            !headers.TryGetValue("Upgrade", out var upgrade) || !HasToken(upgrade, "websocket") ||
            !headers.TryGetValue("Connection", out var connection) || !HasToken(connection, "upgrade") ||
            !headers.TryGetValue("Sec-WebSocket-Version", out var version) || version != "13" ||
            !headers.TryGetValue("Sec-WebSocket-Key", out var key) || !IsWebSocketKey(key))
            return await RespondAsync(stream, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
#pragma warning disable CA5350 // RFC 6455 defines Sec-WebSocket-Accept with SHA-1; it protects nothing secret.
        var acceptKey = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
#pragma warning restore CA5350
        var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Accept: " + acceptKey + "\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static bool HasToken(string value, string token) =>
        value.Split(',').Any(part => string.Equals(part.Trim(), token, StringComparison.OrdinalIgnoreCase));

    private static bool IsWebSocketKey(string key)
    {
        try { return Convert.FromBase64String(key).Length == 16; }
        catch (FormatException) { return false; }
    }

    private static async Task<bool> RespondAsync(NetworkStream stream, int status, string reason, CancellationToken cancellationToken)
    {
        var response = "HTTP/1.1 " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + reason +
            "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).ConfigureAwait(false);
        return false;
    }

    // Stops listening and closes every connection that has not authenticated.
    internal void Stop()
    {
        TcpClient[] pending;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            pending = [.. _pending];
            _pending.Clear();
        }
        try { _listener.Stop(); }
        catch (SocketException) { }
        _stop.Cancel();
        foreach (var client in pending) client.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        _listener.Dispose();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }
}
