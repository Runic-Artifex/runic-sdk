using TUnit.Core;
using System.Buffers.Binary;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;

namespace Runic.Desktop.Tests;

public sealed class SessionRevocationTests
{
    [Test]
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

    [Test]
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

    [Test]
    public async Task SessionCloseDrainsOverlappingCallbacksBeforeDisconnectedEvent()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var window = new WebUiWindow();
        var entered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = 0;
        using var events = window.Bind(string.Empty, e =>
        {
            if (e.EventType == WebUiEventType.Disconnected)
                disconnected.TrySetResult(Volatile.Read(ref finished) == 1);
        });
        using var held = window.BindAsync("held", async (e, _) =>
        {
            entered.TrySetResult(e.SessionIdentifier);
            // Ignore transport cancellation, as an accepted operation may be
            // performing recovery before releasing its application resources.
            await release.Task;
            Volatile.Write(ref finished, 1);
            return WebUiResult.None;
        });
        using var close = window.BindAsync("close", async (e, token) =>
        {
            await e.CloseSessionAsync(token);
            closed.TrySetResult();
            return WebUiResult.None;
        });
        var url = await window.StartServerAsync("overlapping session close");
        var token = await GetTokenAsync(url, deadline.Token);
        using var socket = await ConnectAsync(url, token, deadline.Token);
        try
        {
            await SendCallAsync(socket, token, 1, "held", deadline.Token);
            var sessionId = await entered.Task.WaitAsync(deadline.Token);
            var drainStarted = window.CallbackDrainStarted(sessionId);
            Assert.False(drainStarted.IsCompleted);
            await SendCallAsync(socket, token, 2, "close", deadline.Token);
            await closed.Task.WaitAsync(deadline.Token);
            // The revoked receive loop has reached the point where it would deliver
            // disconnection, while the first callback is still deliberately blocked.
            await drainStarted.WaitAsync(deadline.Token);
            Assert.False(disconnected.Task.IsCompleted);
            release.TrySetResult();
            Assert.True(await disconnected.Task.WaitAsync(deadline.Token));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Test]
    public async Task CapabilityCanDisposeItsOwnSurfaceWithoutDeadlockingDisconnectedEvent()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var host = await DesktopHost.StartAsync();
        var surface = await host.CreateSurfaceAsync();
        var surfaceUrl = surface.Url;
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = surface.RegisterCapability("dispose", async (invocation, _) =>
        {
            await invocation.Surface.DisposeAsync();
            disposed.TrySetResult();
            return PresentationResult.None;
        });
        var connection = await ConnectToSurfaceAsync(surfaceUrl, deadline.Token);
        using var socket = connection.Socket;
        var token = connection.Token;

        await SendCallAsync(socket, token, 1, "dispose", deadline.Token);

        await disposed.Task.WaitAsync(deadline.Token);
        // The detached teardown completes once the callback has released its session.
        using var client = new HttpClient();
        while (true)
        {
            using var response = await client.GetAsync(surfaceUrl, deadline.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) break;
            await Task.Delay(20, deadline.Token);
        }
    }

    // A connection subscription is not advertised to the page. An advertised all-events
    // binding makes webui.js cancel every navigation, including location.reload(), and
    // report it as an event instead, so the page could not reload (#196).
    [Test]
    public async Task ConnectionSubscriptionsReachEverySubscriberWithoutAnAllEventsBinding()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync();
        var first = new System.Collections.Concurrent.ConcurrentQueue<PresentationEventKind>();
        var second = new System.Collections.Concurrent.ConcurrentQueue<PresentationEventKind>();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstSubscription = surface.SubscribeConnectionEvents(invocation => first.Enqueue(invocation.Kind));
        using var secondSubscription = surface.SubscribeConnectionEvents(invocation =>
        {
            second.Enqueue(invocation.Kind);
            if (invocation.Kind == PresentationEventKind.Disconnected) disconnected.TrySetResult();
        });

        var (socket, _, reply) = await ConnectToSurfaceAsync(surface.Url, deadline.Token);
        using (socket)
        {
            Assert.Equal(1, reply[8]);
            // The advertised names, NUL-terminated: an all-events binding would add an empty name.
            Assert.Equal("__webui_core_api__,", Encoding.UTF8.GetString(reply, 9, reply.Length - 9).TrimEnd('\0'));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, deadline.Token);
        }

        await disconnected.Task.WaitAsync(deadline.Token);
        PresentationEventKind[] expected = [PresentationEventKind.Connected, PresentationEventKind.Disconnected];
        Assert.Equal(expected, first.ToArray());
        Assert.Equal(expected, second.ToArray());
    }

    // #317: the surface counts its connected pages, and a connection handler already sees its own change, so the
    // last tab of a served surface is the Disconnected event that leaves the count at zero.
    [Test]
    public async Task ConnectionCountFollowsTheConnectionEvents()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Html("<!doctype html>"),
            Security = new DesktopSecurityPolicy { ClientAdmission = DesktopClientAdmission.Multiple },
        });
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(PresentationEventKind Kind, int Count)>();
        var lastClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = surface.SubscribeConnectionEvents(invocation =>
        {
            seen.Enqueue((invocation.Kind, invocation.Surface.ConnectionCount));
            if (invocation.Kind == PresentationEventKind.Disconnected && invocation.Surface.ConnectionCount == 0)
                lastClosed.TrySetResult();
        });
        Assert.Equal(0, surface.ConnectionCount);

        var (first, _, _) = await ConnectToSurfaceAsync(surface.Url, deadline.Token);
        using (first)
        {
            while (seen.IsEmpty) await Task.Delay(10, deadline.Token);
            Assert.Equal(1, surface.ConnectionCount);
            var (second, _, _) = await ConnectToSurfaceAsync(surface.Url, deadline.Token);
            using var secondSocket = second;
            while (seen.Count < 2) await Task.Delay(10, deadline.Token);
            Assert.Equal(2, surface.ConnectionCount);
            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, null, deadline.Token);
            while (seen.Count < 3) await Task.Delay(10, deadline.Token);
            Assert.Equal(1, surface.ConnectionCount);
            Assert.False(lastClosed.Task.IsCompleted);
            await second.CloseAsync(WebSocketCloseStatus.NormalClosure, null, deadline.Token);
        }

        await lastClosed.Task.WaitAsync(deadline.Token);
        Assert.Equal(0, surface.ConnectionCount);
        Assert.Equal(
            [(PresentationEventKind.Connected, 1), (PresentationEventKind.Connected, 2), (PresentationEventKind.Disconnected, 1),
             (PresentationEventKind.Disconnected, 0)],
            seen.ToArray());
    }

    private static async Task<(ClientWebSocket Socket, uint Token, byte[] Reply)> ConnectToSurfaceAsync(
        Uri surfaceUrl,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        var bridge = await client.GetStringAsync(new Uri(surfaceUrl, "webui.js"), cancellationToken);
        var token = ReadToken(bridge);
        const string credentialPrefix = "const SESSION_CREDENTIAL = \"";
        var credentialStart = bridge.IndexOf(credentialPrefix, StringComparison.Ordinal) + credentialPrefix.Length;
        var credential = bridge[credentialStart..bridge.IndexOf('"', credentialStart)];
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader("Origin", $"{surfaceUrl.Scheme}://{surfaceUrl.Authority}");
            socket.Options.AddSubProtocol($"runic-desktop.{credential}");
            await socket.ConnectAsync(
                new UriBuilder(surfaceUrl) { Scheme = "ws", Path = $"{surfaceUrl.AbsolutePath}_webui_ws_connect" }.Uri,
                cancellationToken);
            await SendAsync(socket, Packet(token, 0, 0xF5, [0]), cancellationToken);
            return (socket, token, await ReceiveAsync(socket, cancellationToken));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<uint> GetTokenAsync(Uri url, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        return ReadToken(await client.GetStringAsync(new Uri(url, "webui.js"), cancellationToken));
    }

    private static uint ReadToken(string bridge)
    {
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
