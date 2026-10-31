using TUnit.Core;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Runic.Desktop;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// The Bridge handshake from the server's side (#35). A browser's page opens the
// WebSocket and sends a token check when the socket's open event runs. These
// tests stand in for the page with raw sockets and run the window on their own
// clock, so the timing is deterministic.
public sealed class BridgeHandshakeTests
{
    private readonly TestOutput output = new();

    private const byte Signature = 0xDD;
    private const byte CheckToken = 0xF5;
    private const int HeaderSize = 8;

    [Test]
    public async Task ASilentSocketIsClosedAfterTheHandshakeTimeout()
    {
        await using var fixture = await HandshakeFixture.StartAsync(connectionTimeoutSeconds: 10, handshakeTimeout: TimeSpan.FromMilliseconds(500));

        // The first socket opens but its page never sends the token check, as when
        // the open event is withheld or the page is replaced before it runs.
        using var silent = await ConnectAsync(fixture.Url, fixture.Cookies, fixture.Timeout);
        await fixture.Time.WaitForTimerAsync(TimeSpan.FromMilliseconds(500), fixture.Timeout);
        fixture.Time.Advance(TimeSpan.FromMilliseconds(500));
        var closed = await silent.ReceiveAsync(new byte[16], fixture.Timeout);
        Assert.Equal(WebSocketMessageType.Close, closed.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, silent.CloseStatus);

        // The page's bridge retries every 500 ms, as webui.js does after a close.
        using var retried = await ConnectWithRetriesAsync(fixture.Url, fixture.Cookies, fixture.Timeout);
        await SendCheckTokenAsync(retried, fixture.Token, fixture.Timeout);
        await fixture.Showing.WaitAsync(fixture.Timeout);
    }

    [Test]
    public async Task ASilentSocketYieldsTheOnlyConnectionToANewerOne()
    {
        // The handshake timeout is longer than the connection timeout, so only the
        // newer socket's arrival can free the connection, once the silent one had its
        // two-second grace.
        await using var fixture = await HandshakeFixture.StartAsync(connectionTimeoutSeconds: 10, handshakeTimeout: TimeSpan.FromMinutes(1));
        using var silent = await ConnectAsync(fixture.Url, fixture.Cookies, fixture.Timeout);
        await fixture.Time.WaitForTimerAsync(TimeSpan.FromMinutes(1), fixture.Timeout);
        using (var early = TestClients.WebSocket(fixture.Cookies))
        {
            await Assert.ThrowsAsync<WebSocketException>(() => early.ConnectAsync(WebSocketUrl(fixture.Url), fixture.Timeout));
        }

        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        using var newer = await ConnectAsync(fixture.Url, fixture.Cookies, fixture.Timeout);
        await SendCheckTokenAsync(newer, fixture.Token, fixture.Timeout);

        await fixture.Showing.WaitAsync(fixture.Timeout);
        await silent.ReceiveAsync(new byte[16], fixture.Timeout);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, silent.CloseStatus);
    }

    [Test]
    public async Task AnAuthenticatedSocketKeepsTheOnlyConnection()
    {
        await using var fixture = await HandshakeFixture.StartAsync(connectionTimeoutSeconds: 10, handshakeTimeout: TimeSpan.FromMilliseconds(100));
        using var first = await ConnectAsync(fixture.Url, fixture.Cookies, fixture.Timeout);
        await fixture.Time.WaitForTimerAsync(TimeSpan.FromMilliseconds(100), fixture.Timeout);
        await SendCheckTokenAsync(first, fixture.Token, fixture.Timeout);
        await fixture.Showing.WaitAsync(fixture.Timeout);
        // Past the handshake timeout: it no longer applies to the authenticated socket.
        fixture.Time.Advance(TimeSpan.FromMilliseconds(300));

        using var second = TestClients.WebSocket(fixture.Cookies);
        await Assert.ThrowsAsync<WebSocketException>(() => second.ConnectAsync(WebSocketUrl(fixture.Url), fixture.Timeout));
        Assert.Equal(WebSocketState.Open, first.State);
    }

    [Test]
    public async Task ATimeoutDescribesWhatTheServerSawOfTheHandshake()
    {
        // Without the deadline (as before #35's fix), the silent socket keeps the
        // only slot, every retry is rejected, and the show call times out with the
        // message seen on Windows CI.
        await using var fixture = await HandshakeFixture.StartAsync(connectionTimeoutSeconds: 2, handshakeTimeout: TimeSpan.Zero);
        using var silent = await ConnectAsync(fixture.Url, fixture.Cookies, fixture.Timeout);
        using (var retry = TestClients.WebSocket(fixture.Cookies))
        {
            await Assert.ThrowsAsync<WebSocketException>(() => retry.ConnectAsync(WebSocketUrl(fixture.Url), fixture.Timeout));
        }

        var timeout = await fixture.TimeOutAsync(TimeSpan.FromSeconds(2));
        output.WriteLine(timeout.Message);

        Assert.Contains("the Bridge WebSocket opened, but authentication did not complete", timeout.Message);
        Assert.Matches(@"\+\d+\.\d{3}s WebSocket 1 opened", timeout.Message);
        Assert.Contains("WebSocket rejected: another WebSocket holds the only connection", timeout.Message);
        Assert.DoesNotContain("WebSocket 1: first message", timeout.Message);
        Assert.DoesNotContain("WebSocket 2 opened", timeout.Message);
    }

    [Test]
    public async Task ATimeoutBeforeAnyRequestSaysWhatTheWebViewDid()
    {
        // I-176: on Windows runners a WebView2 page sometimes never reached the server, and the timeout said "nothing
        // recorded". The WebView2 hosts now record the controller's creation and the first navigation in the log.
        await using var fixture = await HandshakeFixture.StartAsync(connectionTimeoutSeconds: 2, handshakeTimeout: TimeSpan.FromSeconds(10));
        var progress = ((EmbeddedHostTests.RecordingHost)fixture.Factory.Host!).Options!.StartupProgress!;
        progress("WebView2 controller created");
        progress("WebView2 navigation started");

        var timeout = await fixture.TimeOutAsync(TimeSpan.FromSeconds(2));
        output.WriteLine(timeout.Message);
        Assert.Contains("did not connect within 2 seconds: no request reached the server", timeout.Message);
        // At the timeout the window also says whether its server still accepted a loopback connection and how long a
        // small write to the profile's (or the temporary folder's) disk took, to tell a stalled WebView from a stalled machine.
        Assert.Matches(
            @"Bridge handshake: \+\d+\.\d{3}s WebView2 controller created; \+\d+\.\d{3}s WebView2 navigation started; " +
            @"\+\d+\.\d{3}s at the timeout: loopback connect \d+ ms; (profile|temporary) folder write \d+ ms\.$",
            timeout.Message);
    }

    [Test]
    public async Task APageThatReachesTheServerLateGetsTheWholeTimeoutToAuthenticate()
    {
        // On Windows runners a browser sometimes starts slowly: Chromium requested its page 27.4 s into a 30 s
        // timeout, opened the WebSocket 18 ms later and was closed 2.5 s after that, while it connected (I-180).
        // Here the page is requested halfway through a 4 s timeout and authenticates after the launch deadline.
        await using var fixture = await HandshakeFixture.StartAsync(connectionTimeoutSeconds: 4, handshakeTimeout: TimeSpan.FromSeconds(10));
        await fixture.Time.WaitForTimerAsync(TimeSpan.FromSeconds(4), fixture.Timeout);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        using (var client = TestClients.Http(fixture.Url, fixture.Cookies))
        {
            await client.GetStringAsync("/", fixture.Timeout);
        }

        // Past the launch deadline, but within 4 s of the first request: the wait sleeps on until 6 s.
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        await fixture.Time.WaitForTimerAsync(TimeSpan.FromSeconds(1), fixture.Timeout);
        Assert.False(fixture.Showing.IsCompleted, "The presentation gave up on a page that was still connecting.");
        using var socket = await ConnectAsync(fixture.Url, fixture.Cookies, fixture.Timeout);
        await SendCheckTokenAsync(socket, fixture.Token, fixture.Timeout);
        await fixture.Showing.WaitAsync(fixture.Timeout);
    }

    [Test]
    public async Task APageThatStopsAfterItsFirstRequestTimesOutAWholeTimeoutLater()
    {
        await using var fixture = await HandshakeFixture.StartAsync(connectionTimeoutSeconds: 2, handshakeTimeout: TimeSpan.FromSeconds(10));
        await fixture.Time.WaitForTimerAsync(TimeSpan.FromSeconds(2), fixture.Timeout);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        using (var client = TestClients.Http(fixture.Url, fixture.Cookies))
        {
            await client.GetStringAsync("/", fixture.Timeout);
        }

        // At the launch deadline the wait sleeps on until 2 s after the request.
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var timeout = await fixture.TimeOutAsync(TimeSpan.FromSeconds(1));
        output.WriteLine(timeout.Message);
        Assert.Contains("did not connect within 2 seconds of its first request: the page was requested, but not /webui.js", timeout.Message);
    }

    [Test]
    public async Task AProbeOfTheListenerDoesNotMoveTheDeadline()
    {
        // A development tool or scanner probes each new loopback listener: t3code's preview discovery sends GET / with
        // User-Agent node. It is not the page, so the deadline stays 2 s after the start instead of 2 s after the probe.
        await using var fixture = await HandshakeFixture.StartAsync(connectionTimeoutSeconds: 2, handshakeTimeout: TimeSpan.FromSeconds(10));
        await fixture.Time.WaitForTimerAsync(TimeSpan.FromSeconds(2), fixture.Timeout);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await TestClients.ProbeAsync(fixture.Url, fixture.Timeout);

        var timeout = await fixture.TimeOutAsync(TimeSpan.FromSeconds(1));
        output.WriteLine(timeout.Message);
        Assert.Contains(
            "did not connect within 2 seconds: no request with the window's cookie or launch code reached the server, " +
            "1 without them did, the first GET / (",
            timeout.Message);
        Assert.Matches(@"\+\d+\.\d{3}s GET / requested without the window's cookie or launch code", timeout.Message);
    }

    [Test]
    public async Task AProbeOfTheListenerNeitherEndsNorExtendsAWebViewsProgress()
    {
        // D053.18: before any request from the page, a WebView still loading it extends the deadline up to 4 timeouts
        // (event 3021). A probe is not the page's request: it neither ends that extension, which a page's request does,
        // nor moves the deadline itself.
        var time = new ManualTime();
        var logger = new CapturingLogger();
        await using var fixture = await HandshakeFixture.StartAsync(
            connectionTimeoutSeconds: 2, handshakeTimeout: TimeSpan.FromSeconds(10), time, logger,
            shown: options => options.StartupSignal!(WebViewStartupSignal.NavigationStarted));

        await time.WaitForTimerAsync(TimeSpan.FromSeconds(2), fixture.Timeout);
        time.Advance(TimeSpan.FromSeconds(1));
        await TestClients.ProbeAsync(fixture.Url, fixture.Timeout);
        time.Advance(TimeSpan.FromSeconds(1));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(6), fixture.Timeout);
        Assert.Equal(3021, Assert.Single(logger.Entries).EventId.Id);

        time.Advance(TimeSpan.FromSeconds(6));
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => fixture.Showing.WaitAsync(fixture.Timeout));
        output.WriteLine(timeout.Message);
        Assert.Contains(
            "The embedded WebView did not connect within 8 seconds: no request with the window's cookie or launch code " +
            "reached the server, 1 without them did, the first GET /",
            timeout.Message);
        Assert.Contains("connection timeout was extended to its limit of 4 times the timeout.", timeout.Message);
    }

    [Test]
    public async Task AWebViewStillLoadingThePageKeepsTheDeadlineGoingUntilItConnects()
    {
        // I-176: during Windows servicing a fresh WebView2 profile took 5-25 s between "navigation started" and its
        // first request, past the 15 s default. A WebView whose first navigation is still in progress extends the
        // timeout. The window's clock is the test's, so the wait runs past its 2 s timeout without sleeping.
        var time = new ManualTime();
        var logger = new CapturingLogger();
        await using var fixture = await HandshakeFixture.StartAsync(
            connectionTimeoutSeconds: 2, handshakeTimeout: TimeSpan.FromSeconds(10), time, logger,
            shown: options => options.StartupSignal!(WebViewStartupSignal.NavigationStarted));

        // The wait sleeps until its 2 s timeout, which it reports as extended, then until the 8 s limit.
        await time.WaitForTimerAsync(fixture.Timeout);
        time.Advance(TimeSpan.FromSeconds(2));
        await time.WaitForTimerAsync(fixture.Timeout);
        var extended = Assert.Single(logger.Entries);
        Assert.Equal(3021, extended.EventId.Id);
        Assert.Equal(LogLevel.Warning, extended.Level);
        Assert.Equal(
            "The embedded WebView had requested nothing after 2 seconds but was still making progress (its first " +
            "navigation started 2 seconds earlier and had not ended); waiting up to 8 seconds for it.",
            extended.Message);

        time.Advance(TimeSpan.FromSeconds(3));
        Assert.False(fixture.Showing.IsCompleted, "The presentation gave up on a WebView that was still loading.");
        using (var client = TestClients.Http(fixture.Url, fixture.Cookies))
        {
            await client.GetStringAsync("/", fixture.Timeout);
        }
        using var socket = await ConnectAsync(fixture.Url, fixture.Cookies, fixture.Timeout);
        await SendCheckTokenAsync(socket, fixture.Token, fixture.Timeout);
        await fixture.Showing.WaitAsync(fixture.Timeout);
    }

    [Test]
    public async Task AWebViewThatNeverRequestsItsPageTimesOutAtTheLimit()
    {
        var time = new ManualTime();
        await using var fixture = await HandshakeFixture.StartAsync(
            connectionTimeoutSeconds: 2, handshakeTimeout: TimeSpan.FromSeconds(10), time, new CapturingLogger(),
            shown: options =>
            {
                options.StartupProgress!("WebView2 navigation started");
                options.StartupSignal!(WebViewStartupSignal.NavigationStarted);
            });

        await time.WaitForTimerAsync(fixture.Timeout);
        time.Advance(TimeSpan.FromSeconds(2));
        await time.WaitForTimerAsync(fixture.Timeout);
        time.Advance(TimeSpan.FromSeconds(6));

        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => fixture.Showing);
        output.WriteLine(timeout.Message);
        Assert.Contains(
            "The embedded WebView did not connect within 8 seconds: no request reached the server, although the WebView " +
            "was still making progress (its first navigation started 8 seconds earlier and had not ended); the 2-second " +
            "connection timeout was extended to its limit of 4 times the timeout.",
            timeout.Message);
        Assert.Matches(
            @"Bridge handshake: \+\d+\.\d{3}s WebView2 navigation started; \+\d+\.\d{3}s timeout extended: its first navigation " +
            @"started 2 seconds earlier and had not ended; ",
            timeout.Message);
    }

    [Test]
    public async Task AWrongTokenIsReportedAndItsSocketClosed()
    {
        await using var fixture = await HandshakeFixture.StartAsync(connectionTimeoutSeconds: 2, handshakeTimeout: TimeSpan.FromMilliseconds(200));
        using var socket = await ConnectAsync(fixture.Url, fixture.Cookies, fixture.Timeout);
        await SendCheckTokenAsync(socket, unchecked(fixture.Token + 1), fixture.Timeout);

        // Read at once: the server aborts the socket if the close isn't answered within
        // 2 s, and on Windows that reset discards frames the test hasn't read yet.
        var closed = ReceiveReplyThenCloseAsync(socket, fixture.Timeout);
        // The server answers the check with a false result, then closes the socket.
        Assert.Equal(WebSocketMessageType.Binary, await closed);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);

        var timeout = await fixture.TimeOutAsync(TimeSpan.FromSeconds(2));
        output.WriteLine(timeout.Message);

        Assert.Contains("WebSocket 1: first message, 9 bytes (command 0xF5)", timeout.Message);
        Assert.Contains("WebSocket 1: token check, token did not match", timeout.Message);
        Assert.Contains("WebSocket 1: closed by the server: the token did not match", timeout.Message);
        Assert.DoesNotContain("closed by the server: no token check", timeout.Message);
    }

    [Test]
    public async Task AWrongTokenSocketGivesTheConnectionToAValidOne()
    {
        // The handshake timeout is long, so only the rejection can free the connection.
        await using var fixture = await HandshakeFixture.StartAsync(connectionTimeoutSeconds: 10, handshakeTimeout: TimeSpan.FromMinutes(1));
        using var wrong = await ConnectAsync(fixture.Url, fixture.Cookies, fixture.Timeout);
        await SendCheckTokenAsync(wrong, unchecked(fixture.Token + 1), fixture.Timeout);
        var wrongClosed = ReadUntilCloseAsync(wrong, fixture.Timeout);

        // The page's bridge retries every 500 ms. A valid retry gets the connection at
        // once after the rejection, well inside the (scaled) 3 s budget here.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(fixture.Timeout);
        budget.CancelAfter(TestTimeouts.Scale(TimeSpan.FromSeconds(3)));
        using var retried = await ConnectWithRetriesAsync(fixture.Url, fixture.Cookies, budget.Token, fixture.Showing);
        await SendCheckTokenAsync(retried, fixture.Token, budget.Token);

        await fixture.Showing.WaitAsync(budget.Token);
        await wrongClosed;
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, wrong.CloseStatus);
        Assert.Equal(WebSocketState.Open, retried.State);
    }

    private static async Task<WebSocketMessageType> ReceiveReplyThenCloseAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var reply = await socket.ReceiveAsync(new byte[16], cancellationToken);
        await ReadUntilCloseAsync(socket, cancellationToken);
        return reply.MessageType;
    }

    private static async Task ReadUntilCloseAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
        }
    }

    private static async Task<ClientWebSocket> ConnectWithRetriesAsync(
        Uri url,
        CookieContainer cookies,
        CancellationToken cancellationToken,
        Task? until = null)
    {
        while (true)
        {
            var socket = TestClients.WebSocket(cookies);
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

    // The page's WebSocket, with the cookie its first response gave it.
    private static async Task<ClientWebSocket> ConnectAsync(Uri url, CookieContainer cookies, CancellationToken cancellationToken)
    {
        var socket = TestClients.WebSocket(cookies);
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

    // A window on the test's clock, so its connection deadline and Bridge handshake timeout run out only when a case
    // advances the clock, with its servers isolated from requests outside the test (TestClients).
    private sealed class HandshakeFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(30));
        private readonly TimeSpan _previousHandshakeTimeout = WebUiApplication.BridgeHandshakeTimeout;
        private readonly IDisposable _isolation = TestClients.Isolate();

        private HandshakeFixture(WebUiWindow window, EmbeddedHostTests.RecordingHostFactory factory, ManualTime time)
        {
            Window = window;
            Factory = factory;
            Time = time;
        }

        public WebUiWindow Window { get; }
        public EmbeddedHostTests.RecordingHostFactory Factory { get; }
        public ManualTime Time { get; }
        public Uri Url { get; private set; } = null!;
        // The page's cookies: the window's client cookie from the fixture's first request (TestClients).
        public CookieContainer Cookies { get; } = new();
        public uint Token { get; private set; }
        public Task Showing { get; private set; } = Task.CompletedTask;
        public CancellationToken Timeout => _timeout.Token;

        // The window runs on a host's runtime options with the clock and logger, as DesktopHost's do.
        public static async Task<HandshakeFixture> StartAsync(
            int connectionTimeoutSeconds,
            TimeSpan handshakeTimeout,
            ManualTime? time = null,
            ILogger? logger = null,
            Action<WebUiEmbeddedHostOptions>? shown = null)
        {
            time ??= new ManualTime();
            var factory = new EmbeddedHostTests.RecordingHostFactory { Shown = shown };
            HandshakeFixture? fixture = null;
            try
            {
                fixture = new HandshakeFixture(
                    new WebUiWindow(host: null, pathBase: string.Empty, runtimeOptions: new PresentationSurfaceRuntimeOptions(
                        null, null, factory, WaitForConnection: true, TimeSpan.FromSeconds(connectionTimeoutSeconds),
                        DiagnosticSink: null, logger) { TimeProvider = time }),
                    factory, time);
                WebUiApplication.SetEmbeddedHostFactory(factory);
                WebUiApplication.BridgeHandshakeTimeout = handshakeTimeout;
                fixture.Url = await fixture.Window.StartServerAsync("<script src=\"webui.js\"></script>", fixture.Timeout);
                using var client = TestClients.Http(fixture.Url, fixture.Cookies);
                fixture.Token = ExtractToken(await client.GetStringAsync("/webui.js", fixture.Timeout));
                fixture.Showing = fixture.Window.ShowWebViewAsync("<script src=\"webui.js\"></script>", fixture.Timeout);
                // Connect only after the presentation began waiting, so every socket counts.
                while (factory.Host is not { IsOpen: true } && !fixture.Showing.IsCompleted)
                {
                    await Task.Delay(10, fixture.Timeout);
                }
                return fixture;
            }
            catch
            {
                if (fixture is not null) await fixture.DisposeAsync();
                throw;
            }
        }

        // Runs the connection deadline out: once the wait sleeps for the remaining time, moves the clock past it and
        // returns the presentation's timeout.
        public async Task<TimeoutException> TimeOutAsync(TimeSpan remaining)
        {
            await Time.WaitForTimerAsync(remaining, Timeout);
            Time.Advance(remaining);
            return await Assert.ThrowsAsync<TimeoutException>(() => Showing.WaitAsync(Timeout));
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
                WebUiApplication.SetEmbeddedHostFactory(null);
                _isolation.Dispose();
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
