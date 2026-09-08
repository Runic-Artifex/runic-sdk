using System.Buffers.Binary;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;

namespace Runic.Desktop.Tests;

public sealed class SessionRevocationTests
{
    [Fact]
    public async Task PublicSessionCloseRevokesIgnoringPeerAndFailsPendingScripts()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var window = new WebUiWindow();
        var captured = new TaskCompletionSource<PresentationSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectCount = 0;
        var callsAfterClose = 0;
        using var events = window.Bind(string.Empty, e =>
        {
            if (e.EventType != WebUiEventType.Disconnected) return;
            Interlocked.Increment(ref disconnectCount);
            disconnected.TrySetResult();
        });
        using var capture = window.Bind("capture", e =>
        {
            captured.TrySetResult(new PresentationSession(window, e.SessionIdentifier, e.ConnectionId));
            return WebUiResult.None;
        });
        using var forbidden = window.Bind("forbidden", _ => Interlocked.Increment(ref callsAfterClose));
        var url = await window.StartServerAsync("session revocation");
        var token = await GetTokenAsync(url, deadline.Token);
        using var socket = await ConnectAsync(url, token, deadline.Token);
        await SendCallAsync(socket, token, 1, "capture", deadline.Token);
        _ = await ReceiveAsync(socket, deadline.Token);
        var session = await captured.Task.WaitAsync(deadline.Token);

        var script = window.ExecuteJavaScriptAsync("neverRespond()", cancellationToken: deadline.Token);
        var scriptPacket = await ReceiveAsync(socket, deadline.Token);
        Assert.Equal(0xFE, scriptPacket[7]);
        await session.CloseAsync(deadline.Token);

        // Do not acknowledge the legacy 0xFA command. Attempt another valid call
        // and a token handshake on the same connection, as an adversarial peer.
        try
        {
            await SendCallAsync(socket, token, 2, "forbidden", deadline.Token);
            await SendAsync(socket, Packet(token, 3, 0xF5, [0]), deadline.Token);
            await SendCallAsync(socket, token, 4, "forbidden", deadline.Token);
        }
        catch (WebSocketException) { /* Transport revocation may already be visible. */ }
        await disconnected.Task.WaitAsync(deadline.Token);
        await Assert.ThrowsAsync<IOException>(async () => await script);
        Assert.Equal(0, Volatile.Read(ref callsAfterClose));
        Assert.Equal(1, Volatile.Read(ref disconnectCount));
    }

    [Fact]
    public async Task CallbackCanCloseItsOwnSessionWithoutDeadlockingDisconnectedEvent()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var window = new WebUiWindow();
        var callbackClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectCount = 0;
        using var events = window.Bind(string.Empty, e =>
        {
            if (e.EventType != WebUiEventType.Disconnected) return;
            Interlocked.Increment(ref disconnectCount);
            disconnected.TrySetResult();
        });
        using var close = window.BindAsync("close", async (e, token) =>
        {
            await e.CloseSessionAsync(token);
            callbackClosed.TrySetResult();
            return WebUiResult.None;
        });
        var url = await window.StartServerAsync("self close");
        var token = await GetTokenAsync(url, deadline.Token);
        using var socket = await ConnectAsync(url, token, deadline.Token);
        await SendCallAsync(socket, token, 1, "close", deadline.Token);
        await callbackClosed.Task.WaitAsync(deadline.Token);
        await disconnected.Task.WaitAsync(deadline.Token);
        Assert.Equal(1, Volatile.Read(ref disconnectCount));
    }

    private static async Task<uint> GetTokenAsync(Uri url, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        var bridge = await client.GetStringAsync(new Uri(url, "webui.js"), cancellationToken);
        const string prefix = "const TOKEN = ";
        var start = bridge.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        return uint.Parse(bridge[start..bridge.IndexOf(';', start)], NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri url, uint token, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new UriBuilder(url) { Scheme = "ws", Path = "/_webui_ws_connect" }.Uri, cancellationToken);
        await SendAsync(socket, Packet(token, 0, 0xF5, [0]), cancellationToken);
        var response = await ReceiveAsync(socket, cancellationToken);
        Assert.Equal(1, response[8]);
        return socket;
    }

    private static Task SendCallAsync(ClientWebSocket socket, uint token, ushort id, string name, CancellationToken cancellationToken)
        => SendAsync(socket, Packet(token, id, 0xF9, [.. Encoding.UTF8.GetBytes(name), 0, 0]), cancellationToken);

    private static Task SendAsync(ClientWebSocket socket, byte[] packet, CancellationToken cancellationToken)
        => socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);

    private static byte[] Packet(uint token, ushort id, byte command, byte[] data)
    {
        var packet = new byte[8 + data.Length];
        packet[0] = 0xDD;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), token);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5), id);
        packet[7] = command;
        data.CopyTo(packet, 8);
        return packet;
    }

    private static async Task<byte[]> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return message.ToArray();
    }
}
