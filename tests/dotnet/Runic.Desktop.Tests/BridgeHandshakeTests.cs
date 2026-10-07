using System.Buffers.Binary;
using System.Globalization;
using System.Net.WebSockets;
using Runic.Desktop;

namespace Runic.Desktop.Tests;

// The Bridge handshake from the server's side (#35). A browser's page opens the
// WebSocket and sends a token check when the socket's open event runs. These
// tests stand in for the page with raw sockets, so the timing is deterministic.
public sealed class BridgeHandshakeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const byte Signature = 0xDD;
    private const byte CheckToken = 0xF5;
    private const int HeaderSize = 8;

    [Fact]
    public async Task ASilentSocketIsClosedAfterTheHandshakeTimeout()
    {
        await using var fixture = await HandshakeFixture.StartAsync(connectionTimeoutSeconds: 10, handshakeTimeout: TimeSpan.FromMilliseconds(500));

        // The first socket opens but its page never sends the token check, as when
        // the open event is withheld or the page is replaced before it runs.
        using var silent = await ConnectAsync(fixture.Url, fixture.Timeout);

        // The page's bridge retries every 500 ms, as webui.js does after a close.
        using var retried = await ConnectWithRetriesAsync(fixture.Url, fixture.Timeout);
        await SendCheckTokenAsync(retried, fixture.Token, fixture.Timeout);

        await fixture.Showing.WaitAsync(fixture.Timeout);
        var closed = await silent.ReceiveAsync(new byte[16], fixture.Timeout);
        Assert.Equal(WebSocketMessageType.Close, closed.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, silent.CloseStatus);
    }

    [Fact]
    public async Task ASilentSocketYieldsTheOnlyConnectionToANewerOne()
    {
        // The handshake timeout is longer than the connection timeout, so only the
        // newer socket's arrival can free the connection.
        await using var fixture = await HandshakeFixture.StartAsync(connectionTimeoutSeconds: 10, handshakeTimeout: TimeSpan.FromMinutes(1));
        using var silent = await ConnectAsync(fixture.Url, fixture.Timeout);

        using var retried = await ConnectWithRetriesAsync(fixture.Url, fixture.Timeout, fixture.Showing);
        await SendCheckTokenAsync(retried, fixture.Token, fixture.Timeout);

        await fixture.Showing.WaitAsync(fixture.Timeout);
        await silent.ReceiveAsync(new byte[16], fixture.Timeout);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, silent.CloseStatus);
    }

    [Fact]
    public async Task AnAuthenticatedSocketKeepsTheOnlyConnection()
    {
        await using var fixture = await HandshakeFixture.StartAsync(connectionTimeoutSeconds: 10, handshakeTimeout: TimeSpan.FromMilliseconds(100));
        using var first = await ConnectAsync(fixture.Url, fixture.Timeout);
        await SendCheckTokenAsync(first, fixture.Token, fixture.Timeout);
        await fixture.Showing.WaitAsync(fixture.Timeout);
        await Task.Delay(300, fixture.Timeout);

        using var second = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() => second.ConnectAsync(WebSocketUrl(fixture.Url), fixture.Timeout));
        Assert.Equal(WebSocketState.Open, first.State);
    }

    [Fact]
    public async Task ATimeoutDescribesWhatTheServerSawOfTheHandshake()
    {
        // Without the deadline (as before #35's fix), the silent socket keeps the
        // only slot, every retry is rejected, and the show call times out with the
        // message seen on Windows CI.
        await using var fixture = await HandshakeFixture.StartAsync(connectionTimeoutSeconds: 2, handshakeTimeout: TimeSpan.Zero);
        using var silent = await ConnectAsync(fixture.Url, fixture.Timeout);

        var retrying = ConnectWithRetriesAsync(fixture.Url, fixture.Timeout, fixture.Showing);
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => fixture.Showing);
        output.WriteLine(timeout.Message);
        await Assert.ThrowsAnyAsync<Exception>(() => retrying);

        Assert.Contains("the Bridge WebSocket opened, but authentication did not complete", timeout.Message);
        Assert.Matches(@"\+\d+\.\d{3}s WebSocket 1 opened", timeout.Message);
        Assert.Contains("WebSocket rejected: another WebSocket holds the only connection", timeout.Message);
        Assert.DoesNotContain("WebSocket 1: first message", timeout.Message);
    }

    [Fact]
    public async Task ARejectedTokenIsReportedAndNotRetried()
    {
        await using var fixture = await HandshakeFixture.StartAsync(connectionTimeoutSeconds: 2, handshakeTimeout: TimeSpan.FromMilliseconds(200));
        using var socket = await ConnectAsync(fixture.Url, fixture.Timeout);
        await SendCheckTokenAsync(socket, unchecked(fixture.Token + 1), fixture.Timeout);

        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => fixture.Showing);
        output.WriteLine(timeout.Message);

        Assert.Contains("WebSocket 1: first message, 9 bytes (command 0xF5)", timeout.Message);
        Assert.Contains("WebSocket 1: token check, token did not match", timeout.Message);
        // The deadline only closes sockets that never checked a token.
        Assert.DoesNotContain("closed by the server", timeout.Message);
    }

    private static async Task<ClientWebSocket> ConnectWithRetriesAsync(
        Uri url,
        CancellationToken cancellationToken,
        Task? until = null)
    {
        while (true)
        {
            var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(WebSocketUrl(url), cancellationToken);
                return socket;
            }
            catch (WebSocketException) when (!cancellationToken.IsCancellationRequested && until?.IsCompleted != true)
            {
                socket.Dispose();
                await Task.Delay(500, cancellationToken);
            }
        }
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri url, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(WebSocketUrl(url), cancellationToken);
        return socket;
    }

    private static Uri WebSocketUrl(Uri url) => new UriBuilder(url) { Scheme = "ws", Path = "/_webui_ws_connect" }.Uri;

    private static Task SendCheckTokenAsync(ClientWebSocket socket, uint token, CancellationToken cancellationToken)
    {
        var packet = new byte[HeaderSize + 1];
        packet[0] = Signature;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), token);
        packet[7] = CheckToken;
        return socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);
    }

    private sealed class HandshakeFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
        private readonly TimeSpan _previousHandshakeTimeout;

        private HandshakeFixture(TimeSpan previousHandshakeTimeout) => _previousHandshakeTimeout = previousHandshakeTimeout;

        public WebUiWindow Window { get; } = new();
        public Uri Url { get; private set; } = null!;
        public uint Token { get; private set; }
        public Task Showing { get; private set; } = Task.CompletedTask;
        public CancellationToken Timeout => _timeout.Token;

        public static async Task<HandshakeFixture> StartAsync(nuint connectionTimeoutSeconds, TimeSpan handshakeTimeout)
        {
            var fixture = new HandshakeFixture(WebUiApplication.BridgeHandshakeTimeout);
            var factory = new EmbeddedHostTests.RecordingHostFactory();
            WebUiApplication.SetEmbeddedHostFactory(factory);
            WebUiApplication.SetConnectionTimeout(connectionTimeoutSeconds);
            WebUiApplication.BridgeHandshakeTimeout = handshakeTimeout;
            fixture.Url = await fixture.Window.StartServerAsync("<script src=\"webui.js\"></script>", fixture.Timeout);
            using var client = new HttpClient { BaseAddress = fixture.Url };
            fixture.Token = ExtractToken(await client.GetStringAsync("/webui.js", fixture.Timeout));
            fixture.Showing = fixture.Window.ShowWebViewAsync("<script src=\"webui.js\"></script>", fixture.Timeout);
            // Connect only after the presentation began waiting, so every socket counts.
            while (factory.Host is not { IsOpen: true } && !fixture.Showing.IsCompleted)
            {
                await Task.Delay(10, fixture.Timeout);
            }
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Window.DisposeAsync();
            }
            finally
            {
                WebUiApplication.BridgeHandshakeTimeout = _previousHandshakeTimeout;
                WebUiApplication.SetConnectionTimeout(15);
                WebUiApplication.SetEmbeddedHostFactory(null);
                _timeout.Dispose();
            }
        }

        private static uint ExtractToken(string bridge)
        {
            const string prefix = "const TOKEN = ";
            var start = bridge.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
            return uint.Parse(bridge[start..bridge.IndexOf(';', start)], NumberStyles.None, CultureInfo.InvariantCulture);
        }
    }
}
