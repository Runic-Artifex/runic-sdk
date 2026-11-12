using TUnit.Core;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// runic-host/1 region/1, frame admission (host-protocol.md): a host page frames a guest's page through a one-time frame
// launch code, which the guest exchanges for a frame token in the redirect's fragment; the page offers the token on its
// Bridge WebSocket. No cookie; frame-ancestors names only the host page's origin; the frame is served cross-site.
public sealed class FrameLaunchBindingTests
{
    private const string Page = "<!doctype html><title>Check</title><script src=\"webui.js\"></script>";
    private const string Embedder = "http://127.0.0.1:38200";

    private sealed class ManualTime : TimeProvider
    {
        internal long Now { get; set; } = 1;

        public override long GetTimestamp() => Now;
    }

    [Test]
    public void TheCodeIsSpentOnceAndASecondUseRevokesTheToken()
    {
        var time = new ManualTime();
        var binding = new FrameLaunchBinding();
        var listener = new Uri("http://[::1]:40117/w1/");
        var target = new Uri(listener, "index.html");
        Assert.False(binding.IsActive);
        binding.Enter();
        Assert.True(binding.IsActive);
        Assert.Null(binding.Embedder);
        Assert.Null(binding.ExpectedProtocol);

        var code = binding.Begin(target, Embedder, listener, measuresHeight: false, time);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", code);
        Assert.Equal(Embedder, binding.Embedder);
        Assert.Null(binding.ExpectedProtocol);
        Assert.Equal(FrameLaunchBinding.Outcome.Refused, binding.Consume(code + "x", time, out _, out _));

        Assert.Equal(FrameLaunchBinding.Outcome.Bound, binding.Consume(code, time, out var token, out var redirect));
        Assert.Equal(target, redirect);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token!);
        Assert.NotEqual(code, token);
        Assert.Equal("runic-desktop." + token, binding.ExpectedProtocol);

        // Whoever presents the spent code, even the frame itself, revokes the token.
        Assert.Equal(FrameLaunchBinding.Outcome.Conflict, binding.Consume(code, time, out var none, out _));
        Assert.Null(none);
        Assert.Null(binding.ExpectedProtocol);
        Assert.Equal(FrameLaunchBinding.Outcome.Refused, binding.Consume(code, time, out _, out _));

        // A new attachment replaces everything; the earlier code admits nothing.
        var next = binding.Begin(target, Embedder, listener, measuresHeight: true, time);
        Assert.True(binding.MeasuresHeight);
        Assert.Equal(FrameLaunchBinding.Outcome.Refused, binding.Consume(code, time, out _, out _));
        Assert.Equal(FrameLaunchBinding.Outcome.Bound, binding.Consume(next, time, out var nextToken, out _));
        Assert.NotEqual(token, nextToken);

        // Detaching revokes the token and keeps region mode, with no framer.
        Assert.True(binding.End());
        Assert.False(binding.End());
        Assert.True(binding.IsActive);
        Assert.Null(binding.Embedder);
        Assert.Null(binding.ExpectedProtocol);
        Assert.Equal(FrameLaunchBinding.Outcome.Refused, binding.Consume(next, time, out _, out _));
    }

    [Test]
    public void AnUnusedCodeExpiresAfterThirtySeconds()
    {
        var time = new ManualTime();
        var binding = new FrameLaunchBinding();
        var listener = new Uri("http://[::1]:40117/");
        var code = binding.Begin(listener, Embedder, listener, measuresHeight: false, time);
        time.Now += (long)(FrameLaunchBinding.CodeLifetime.TotalSeconds * time.TimestampFrequency);
        Assert.Equal(FrameLaunchBinding.Outcome.Refused, binding.Consume(code, time, out _, out _));
        // Expired is spent: it does not come back.
        time.Now = 1;
        Assert.Equal(FrameLaunchBinding.Outcome.Refused, binding.Consume(code, time, out _, out _));
        Assert.Null(binding.ExpectedProtocol);
    }

    [Test]
    [Arguments("http://127.0.0.1:38200", "127.0.0.1")]
    [Arguments("http://[::1]:1", "[::1]")]
    [Arguments("http://localhost:65535", "localhost")]
    [Arguments("http://127.0.0.1:38200/", null)]
    [Arguments("http://127.0.0.1:038200", null)]
    [Arguments("http://127.0.0.1:0", null)]
    [Arguments("http://127.0.0.1:65536", null)]
    [Arguments("http://127.0.0.1", null)]
    [Arguments("https://127.0.0.1:38200", null)]
    [Arguments("http://127.0.0.2:38200", null)]
    [Arguments("http://LOCALHOST:38200", null)]
    [Arguments("http://evil.example:38200", null)]
    [Arguments("http://user@127.0.0.1:38200", null)]
    [Arguments("http://127.0.0.1:38200?x", null)]
    [Arguments("http://[::ffff:127.0.0.1]:38200", null)]
    public void TheEmbedderIsAnOriginOfALoopbackHostPage(string embedder, string? host) =>
        Assert.Equal(host, WebUiWindow.FrameEmbedderHost(embedder));

    [Test]
    public void LaunchCodesAndFrameTokensAreRedacted()
    {
        var code = new string('a', 43);
        Assert.Equal("GET /w1/_runic_frame/…", FrameLaunchBinding.Redact($"GET /w1/_runic_frame/{code}"));
        Assert.Equal("GET /_runic_launch/… and /_runic_launch/…", FrameLaunchBinding.Redact($"GET /_runic_launch/{code} and /_runic_launch/{code}"));
        Assert.Equal("Refused to frame 'http://[::1]:1/index.html#runic-frame=…' because",
            FrameLaunchBinding.Redact($"Refused to frame 'http://[::1]:1/index.html#runic-frame={code}' because"));
        Assert.Equal("/index.html", FrameLaunchBinding.Redact("/index.html"));
    }

    [Test]
    public async Task AFrameIsAdmittedWithItsTokenAloneAndASpentCodeRevokesIt()
    {
        await using var guest = await RegionSurface.StartAsync();
        var surface = guest.Surface;
        surface.EnterRegionMode();
        using var http = Http();

        // Before the first attach no origin may frame the page.
        using (var before = await http.GetAsync(surface.Url))
        {
            Assert.Equal("frame-ancestors 'none'", Single(before, "Content-Security-Policy"));
            Assert.False(before.Headers.Contains("X-Frame-Options"));
        }

        var revoked = 0;
        surface.FrameRevoked += () => Interlocked.Increment(ref revoked);
        var launch = await surface.BeginFrameLaunchAsync(Embedder, allowSameSite: false, measuresHeight: false, CancellationToken.None);
        Assert.Matches(@"^http://(\[::1\]|127\.0\.0\.1):\d+/[A-Za-z0-9_-]+/_runic_frame/[A-Za-z0-9_-]{43}$", launch.Url.AbsoluteUri);
        if (Ipv6Loopback) Assert.Equal("[::1]", launch.Url.Host);
        Assert.Equal(!Ipv6Loopback, launch.SameSite);
        var frame = new Uri(launch.Url, "../");

        // A wrong code is not spent and changes nothing.
        using (var wrong = await http.GetAsync(new Uri(frame, "_runic_frame/" + new string('a', 43))))
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);

        string token;
        using (var redirect = await http.GetAsync(launch.Url))
        {
            Assert.Equal(HttpStatusCode.SeeOther, redirect.StatusCode);
            var location = redirect.Headers.Location!.OriginalString;
            Assert.StartsWith(frame.GetLeftPart(UriPartial.Authority), location);
            Assert.Matches("#runic-frame=[A-Za-z0-9_-]{43}$", location);
            token = location[(location.IndexOf("#runic-frame=", StringComparison.Ordinal) + "#runic-frame=".Length)..];
            Assert.Equal($"frame-ancestors {Embedder}", Single(redirect, "Content-Security-Policy"));
            Assert.Equal("no-referrer", Single(redirect, "Referrer-Policy"));
            Assert.False(redirect.Headers.Contains("X-Frame-Options"));
            Assert.False(redirect.Headers.Contains("Set-Cookie"));
            Assert.Contains("no-store", Single(redirect, "Cache-Control"));
        }

        // The frame's page and its bootstrap: no cookie, no X-Frame-Options, no session credential in the script.
        using (var page = await http.GetAsync(frame))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.False(page.Headers.Contains("Set-Cookie"));
            Assert.False(page.Headers.Contains("X-Frame-Options"));
            Assert.Contains($"frame-ancestors {Embedder}", page.Headers.GetValues("Content-Security-Policy"));
        }
        var script = await http.GetStringAsync(new Uri(frame, "webui.js"));
        Assert.Contains("const FRAME = true;", script);
        Assert.Contains("runic-frame", script);
        Assert.DoesNotContain(token, script);
        Assert.Matches(@"const SESSION_CREDENTIAL = \(\(\) =>", script);

        var origin = frame.GetLeftPart(UriPartial.Authority);
        // Without the token, with another, or from another origin: refused.
        Assert.Equal(HttpStatusCode.Unauthorized, await RefusedAsync(frame, origin, null));
        Assert.Equal(HttpStatusCode.Unauthorized, await RefusedAsync(frame, origin, new string('b', 43)));
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedAsync(frame, Embedder, token));
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedAsync(frame, surface.Url.GetLeftPart(UriPartial.Authority) == origin ? "http://127.0.0.1:1" : surface.Url.GetLeftPart(UriPartial.Authority), token));

        using var connected = await PageAsync(http, frame, origin, token);
        await WaitUntilAsync(() => surface.ConnectionCount == 1);
        // One page at a time: a copy of the token is refused while the page is connected.
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedAsync(frame, origin, token));

        // The spent code again: the token is revoked and the page disconnected.
        using (var again = await http.GetAsync(launch.Url))
            Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Equal(1, Volatile.Read(ref revoked));
        Assert.Contains(guest.Diagnostics, static diagnostic => diagnostic.Code == "frame-launch-conflict");
        await WaitUntilAsync(() => connected.State != WebSocketState.Open || surface.ConnectionCount == 0);
        await WaitUntilAsync(() => surface.ConnectionCount == 0);
        Assert.Equal(HttpStatusCode.Unauthorized, await RefusedAsync(frame, origin, token));
    }

    [Test]
    public async Task AReloadedPageReconnectsWithItsTokenUntilTheRegionDetaches()
    {
        await using var guest = await RegionSurface.StartAsync();
        var surface = guest.Surface;
        surface.EnterRegionMode();
        using var http = Http();
        var launch = await surface.BeginFrameLaunchAsync(Embedder, allowSameSite: true, measuresHeight: true, CancellationToken.None);
        var frame = new Uri(launch.Url, "../");
        var origin = frame.GetLeftPart(UriPartial.Authority);
        var token = await SpendAsync(http, launch.Url);

        var script = await http.GetStringAsync(new Uri(frame, "webui.js"));
        Assert.Contains("const MEASURE_HEIGHT = true;", script);

        using (var first = await PageAsync(http, frame, origin, token))
        {
            await WaitUntilAsync(() => surface.ConnectionCount == 1);
            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        await WaitUntilAsync(() => surface.ConnectionCount == 0);
        // The reload's page.
        using (var second = await PageAsync(http, frame, origin, token))
        {
            await WaitUntilAsync(() => surface.ConnectionCount == 1);
            surface.EndFrameLaunch();
            await WaitUntilAsync(() => surface.ConnectionCount == 0);
        }
        // Refused: the token is revoked, and an IPv6 frame listener no longer serves the detached surface at all.
        Assert.Contains(await RefusedAsync(frame, origin, token), new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        using (var after = await http.GetAsync(surface.Url))
            Assert.Equal("frame-ancestors 'none'", Single(after, "Content-Security-Policy"));
        // A dead frame launch code is refused with 403 on its listener (host-protocol.md, Limits).
        using (var dead = await http.GetAsync(launch.Url))
            Assert.Equal(HttpStatusCode.Forbidden, dead.StatusCode);
        // A new attachment mints a new code and token.
        var next = await surface.BeginFrameLaunchAsync(Embedder, allowSameSite: true, measuresHeight: false, CancellationToken.None);
        Assert.NotEqual(launch.Url, next.Url);
        Assert.NotEqual(token, await SpendAsync(http, next.Url));
    }

    [Test]
    public async Task TheFrameListensOnTheLoopbackFamilyThatTheHostPageDoesNotUse()
    {
        await using var guest = await RegionSurface.StartAsync();
        var surface = guest.Surface;
        surface.EnterRegionMode();
        var ipv4 = surface.Url.GetLeftPart(UriPartial.Authority);

        // An IPv6 host page: the surface's own IPv4 listener is another site.
        var forIpv6 = await surface.BeginFrameLaunchAsync("http://[::1]:38200", allowSameSite: false, measuresHeight: false, CancellationToken.None);
        Assert.Equal(ipv4, forIpv6.Url.GetLeftPart(UriPartial.Authority));
        Assert.False(forIpv6.SameSite);

        if (!Ipv6Loopback) return;
        var forLocalhost = await surface.BeginFrameLaunchAsync("http://localhost:38200", allowSameSite: false, measuresHeight: false, CancellationToken.None);
        Assert.Equal("[::1]", forLocalhost.Url.Host);
        var forIpv4 = await surface.BeginFrameLaunchAsync(Embedder, allowSameSite: false, measuresHeight: false, CancellationToken.None);
        Assert.Equal("[::1]", forIpv4.Url.Host);
        // The earlier attachment's address is revoked.
        using var http = Http();
        using (var stale = await http.GetAsync(forLocalhost.Url))
            Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);

        // The IPv6 listener serves only the frames of surfaces in region mode.
        var other = await guest.Host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        await using (other)
        {
            var onIpv6 = new Uri(forIpv4.Url, other.Url.AbsolutePath);
            using var refused = await http.GetAsync(onIpv6);
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            using var own = await http.GetAsync(other.Url);
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            Assert.Equal("SAMEORIGIN", Single(own, "X-Frame-Options"));
        }
    }

    [Test]
    public async Task WithoutIpv6AnIpv4HostPageGetsASameSiteFrameOnlyWhenItAllowsOne()
    {
        PresentationServers.Decorator = server => new NoIpv6Server(server);
        try
        {
            await using var guest = await RegionSurface.StartAsync();
            var surface = guest.Surface;
            surface.EnterRegionMode();
            await Assert.ThrowsAsync<DesktopFrameCrossSiteUnavailableException>(() =>
                surface.BeginFrameLaunchAsync(Embedder, allowSameSite: false, measuresHeight: false, CancellationToken.None));
            var fallback = await surface.BeginFrameLaunchAsync(Embedder, allowSameSite: true, measuresHeight: false, CancellationToken.None);
            Assert.True(fallback.SameSite);
            Assert.Equal(surface.Url.GetLeftPart(UriPartial.Authority), fallback.Url.GetLeftPart(UriPartial.Authority));
            Assert.Contains(guest.Diagnostics, static diagnostic => diagnostic.Code == "frame-same-site");
            // localhost: 127.0.0.1 is another site.
            var forLocalhost = await surface.BeginFrameLaunchAsync("http://localhost:38200", allowSameSite: false, measuresHeight: false, CancellationToken.None);
            Assert.False(forLocalhost.SameSite);
        }
        finally
        {
            PresentationServers.Decorator = null;
        }
    }

    // A real engine: a host page of 127.0.0.1 frames the guest's page from its frame launch address; the page reads its
    // token from the fragment, connects, and reports its title.
    [Test]
    public async Task AChromiumHostPageFramesTheGuestsPage()
    {
        if (!OperatingSystem.IsLinux() || BrowserBridgeTests.FindChrome() is not { } chrome) return;
        await using var guest = await RegionSurface.StartAsync();
        var surface = guest.Surface;
        surface.EnterRegionMode();
        var titles = new ConcurrentQueue<string>();
        surface.FrameReported += report => { if (report.Title is { } title) titles.Enqueue(title); };

        var folder = Directory.CreateTempSubdirectory("runic-desktop-region-");
        try
        {
            File.CreateSymbolicLink(Path.Combine(folder.FullName, "google-chrome"), chrome);
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                BrowserFolder = folder.FullName,
                ConnectionTimeout = TestTimeouts.Scale(TimeSpan.FromSeconds(30)),
            });
            // The host page's origin is known once its surface listens; the page fetches its frame's address then.
            Uri? frameSource = null;
            var hostPage = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Html("<!doctype html><title>Host</title><script src=\"webui.js\"></script><iframe id=\"region\"></iframe>" +
                    "<script>fetch('frame-source').then(r => r.text()).then(src => document.getElementById('region').src = src);</script>"),
                Resources = (request, _) => ValueTask.FromResult(request.Path == "/frame-source" && frameSource is { } source
                    ? ContentResponse.Text(source.AbsoluteUri, "text/plain")
                    : null),
            });
            await using (hostPage)
            {
                var embedder = hostPage.Url.GetLeftPart(UriPartial.Authority);
                var launch = await surface.BeginFrameLaunchAsync(embedder, allowSameSite: true, measuresHeight: false, CancellationToken.None);
                frameSource = launch.Url;
                await using var window = await hostPage.OpenWindowAsync(new DesktopWindowOptions
                {
                    Browser = BrowserKind.Chrome,
                    Hidden = true,
                    BrowserArguments = "--no-sandbox --disable-gpu --disable-dev-shm-usage",
                });
                await WaitUntilAsync(() => surface.ConnectionCount == 1, TimeSpan.FromSeconds(30));
                await WaitUntilAsync(() => titles.Contains("Check"), TimeSpan.FromSeconds(10));
                Assert.DoesNotContain(guest.Diagnostics, static diagnostic => diagnostic.Code == "frame-launch-conflict");
            }
        }
        finally
        {
            await BrowserTestProfile.DeleteAsync(folder.FullName, TimeSpan.FromSeconds(10));
        }
    }

    private static bool Ipv6Loopback
    {
        get
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }

    private static HttpClient Http() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

    private static string Single(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values) || response.Content.Headers.TryGetValues(header, out values)
            ? string.Join(", ", values)
            : throw new InvalidOperationException($"No {header} header.");

    private static async Task<string> SpendAsync(HttpClient http, Uri launch)
    {
        using var redirect = await http.GetAsync(launch);
        Assert.Equal(HttpStatusCode.SeeOther, redirect.StatusCode);
        var location = redirect.Headers.Location!.OriginalString;
        return location[(location.IndexOf("#runic-frame=", StringComparison.Ordinal) + "#runic-frame=".Length)..];
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri frame, string origin, string? token)
    {
        var socket = new ClientWebSocket();
        if (token is not null) socket.Options.AddSubProtocol("runic-desktop." + token);
        socket.Options.SetRequestHeader("Origin", origin);
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new UriBuilder(new Uri(frame, "_webui_ws_connect")) { Scheme = "ws" }.Uri, timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // The frame's page: its WebSocket with the token, and the Bridge's token check of its bootstrap, after which the
    // surface counts the connection.
    private static async Task<ClientWebSocket> PageAsync(HttpClient http, Uri frame, string origin, string token)
    {
        var script = await http.GetStringAsync(new Uri(frame, "webui.js"));
        var check = uint.Parse(System.Text.RegularExpressions.Regex.Match(script, @"const TOKEN = (\d+);").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        var socket = await ConnectAsync(frame, origin, token);
        var packet = new byte[WebUiProtocol.HeaderSize + 1];
        packet[0] = WebUiProtocol.Signature;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), check);
        packet[7] = WebUiProtocol.CheckToken;
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
        await socket.SendAsync(packet, WebSocketMessageType.Binary, true, timeout.Token);
        return socket;
    }

    private static async Task<HttpStatusCode> RefusedAsync(Uri frame, string origin, string? token)
    {
        try
        {
            using var socket = await ConnectAsync(frame, origin, token);
            throw new InvalidOperationException("The WebSocket was admitted.");
        }
        catch (WebSocketException error) when (error.InnerException is null or HttpRequestException or WebSocketException)
        {
            return StatusOf(error);
        }
    }

    private static HttpStatusCode StatusOf(WebSocketException error)
    {
        // ClientWebSocket names the status in its message: "... status code '403' ...".
        var match = System.Text.RegularExpressions.Regex.Match(error.Message, @"'(\d{3})'");
        return match.Success ? (HttpStatusCode)int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? limit = null)
    {
        using var timeout = TestTimeouts.Cancellation(limit ?? TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class RegionSurface : IAsyncDisposable
    {
        private RegionSurface(DesktopHost host, DesktopSurface surface, ConcurrentQueue<DesktopDiagnostic> diagnostics)
        {
            Host = host;
            Surface = surface;
            Diagnostics = diagnostics;
        }

        internal DesktopHost Host { get; }

        internal DesktopSurface Surface { get; }

        internal ConcurrentQueue<DesktopDiagnostic> Diagnostics { get; }

        internal static async Task<RegionSurface> StartAsync()
        {
            var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
            var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                WaitForConnection = false,
                DiagnosticSink = diagnostics.Enqueue,
                Security = DesktopSecurityPolicy.Default,
            });
            try
            {
                // A path-based surface, as a hosted guest's windows are.
                var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
                return new RegionSurface(host, surface, diagnostics);
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Surface.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    // A system without IPv6: [::1] cannot be bound.
    private sealed class NoIpv6Server(IPresentationServer server) : IPresentationServer
    {
        public IServiceProvider Services => server.Services;

        public Task<int> StartAsync(IPAddress address, int port, Func<PresentationExchange, Task> dispatch, CancellationToken cancellationToken) =>
            address.Equals(IPAddress.IPv6Loopback)
                ? throw new IOException("IPv6 is turned off.")
                : server.StartAsync(address, port, dispatch, cancellationToken);

        public ValueTask DisposeAsync() => server.DisposeAsync();
    }
}
