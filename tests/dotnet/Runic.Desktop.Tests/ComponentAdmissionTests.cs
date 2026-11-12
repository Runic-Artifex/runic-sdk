using TUnit.Core;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// runic-host/1 region-component/1, component admission (host-protocol.md, Component mode): a host page loads a guest's
// component into its own document, and the component's Bridge WebSocket comes from the host page's origin. The guest
// admits it only with the component token from region-attach as its subprotocol and with Origin = embedder, one page at
// a time, while attached; it serves blobs and uploads to the embedder with CORS.
public sealed class ComponentAdmissionTests
{
    [Test]
    public async Task AnEmbeddedHostsComponentComesFromTheSurfacesOwnListenerWhichACspCanName()
    {
        await using var guest = await ComponentSurface.StartAsync();
        var embedder = "http://127.0.0.1:38200";

        var launch = await guest.Surface.BeginComponentAsync(embedder, allowSameSite: true, browser: false, CancellationToken.None);

        Assert.Equal(guest.Surface.Url.Authority, launch.Listener.Authority);
        Assert.True(launch.SameSite);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", launch.Token);
        // Blob and upload addresses follow the component's listener.
        Assert.Equal(launch.Listener, guest.Surface.CurrentUrl);
    }

    [Test]
    public async Task WithoutTheSameSiteTheComponentComesFromTheIpv6Listener()
    {
        if (!Ipv6Loopback) return;
        await using var guest = await ComponentSurface.StartAsync();

        var launch = await guest.Surface.BeginComponentAsync("http://127.0.0.1:38200", allowSameSite: false, browser: false, CancellationToken.None);

        Assert.Equal("[::1]", launch.Listener.Host);
        Assert.False(launch.SameSite);
        // A localhost or [::1] host page is another site than the surface's 127.0.0.1 listener.
        var other = await guest.Surface.BeginComponentAsync("http://localhost:38200", allowSameSite: false, browser: false, CancellationToken.None);
        Assert.Equal(guest.Surface.Url.Authority, other.Listener.Authority);
        Assert.False(other.SameSite);
    }

    // D059.18: a browser host's page runs in the user's browser, which sends the 127.0.0.1 cookies of other local
    // applications: its component comes from [::1], also for a localhost page, and from 127.0.0.1 only for a [::1] page.
    [Test]
    public async Task ABrowserHostsComponentComesFromTheIpv6Listener()
    {
        if (!Ipv6Loopback) return;
        await using var guest = await ComponentSurface.StartAsync();

        foreach (var embedder in (string[])["http://localhost:38200", "http://127.0.0.1:38200"])
        {
            var launch = await guest.Surface.BeginComponentAsync(embedder, allowSameSite: false, browser: true, CancellationToken.None);
            Assert.Equal("[::1]", launch.Listener.Host);
            Assert.False(launch.SameSite);
        }
        var ipv6Page = await guest.Surface.BeginComponentAsync("http://[::1]:38200", allowSameSite: false, browser: true, CancellationToken.None);
        Assert.Equal(guest.Surface.Url.Authority, ipv6Page.Listener.Authority);
        Assert.False(ipv6Page.SameSite);
    }

    [Test]
    public async Task AdmitsTheComponentWithItsTokenAndTheEmbeddersOriginOnly()
    {
        await using var guest = await ComponentSurface.StartAsync();
        var hostPage = "http://127.0.0.1:38200";
        var launch = await guest.Surface.BeginComponentAsync(hostPage, allowSameSite: true, browser: false, CancellationToken.None);
        var listener = launch.Listener;

        // Another origin, even with the token: a browser cannot forge Origin.
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedAsync(listener, "http://127.0.0.1:38201", launch.Token));
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedAsync(listener, "http://localhost:38200", launch.Token));
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedAsync(listener, "null", launch.Token));
        // The embedder without the token, or with another one: a process that is not a browser can send any Origin.
        Assert.Equal(HttpStatusCode.Unauthorized, await RefusedAsync(listener, hostPage, null));
        Assert.Equal(HttpStatusCode.Unauthorized, await RefusedAsync(listener, hostPage, new string('x', 43)));
        Assert.Contains(guest.Diagnostics, static diagnostic => diagnostic.Code == "origin-not-allowed");
        Assert.Contains(guest.Diagnostics, static diagnostic => diagnostic.Code == "credential-required");

        // Both: admitted. The component's packets carry no window token; its token check with 0 is accepted.
        using var page = await ComponentPageAsync(listener, hostPage, launch.Token);
        await WaitUntilAsync(() => guest.Surface.ConnectionCount == 1);
        // One page at a time: a copy of the token is useless while the component is connected.
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedAsync(listener, hostPage, launch.Token));

        // A detach revokes the token and closes the page; no origin is a component's any more.
        guest.Surface.EndComponent();
        await WaitUntilAsync(() => guest.Surface.ConnectionCount == 0);
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedAsync(listener, hostPage, launch.Token));
    }

    [Test]
    public async Task ANewAttachRevokesTheEarlierTokenAndAFrameAttachEndsTheComponent()
    {
        await using var guest = await ComponentSurface.StartAsync();
        var hostPage = "http://127.0.0.1:38200";
        var first = await guest.Surface.BeginComponentAsync(hostPage, allowSameSite: true, browser: false, CancellationToken.None);
        using var page = await ComponentPageAsync(first.Listener, hostPage, first.Token);
        await WaitUntilAsync(() => guest.Surface.ConnectionCount == 1);

        var second = await guest.Surface.BeginComponentAsync(hostPage, allowSameSite: true, browser: false, CancellationToken.None);
        Assert.NotEqual(first.Token, second.Token);
        await WaitUntilAsync(() => guest.Surface.ConnectionCount == 0);
        Assert.Equal(HttpStatusCode.Unauthorized, await RefusedAsync(second.Listener, hostPage, first.Token));

        // A frame attach replaces the component: its token and its build's CORS grant end, and only the frame's own
        // origin may connect.
        await guest.Surface.BeginFrameLaunchAsync(hostPage, allowSameSite: true, measuresHeight: false, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedAsync(second.Listener, hostPage, second.Token));
        using var http = Http();
        using var module = await SendAsync(http, new Uri(second.Listener, "_runic_component/component.js"), hostPage);
        Assert.Equal(HttpStatusCode.Forbidden, module.StatusCode);
    }

    [Test]
    public async Task TheComponentTokenIsNotAFrameTokenNorTheSessionCredential()
    {
        await using var guest = await ComponentSurface.StartAsync();
        var hostPage = "http://127.0.0.1:38200";
        var launch = await guest.Surface.BeginComponentAsync(hostPage, allowSameSite: true, browser: false, CancellationToken.None);
        // The component token admits nothing on a frame of the guest's own origin, and no frame launch is pending.
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedAsync(launch.Listener, launch.Listener.GetLeftPart(UriPartial.Authority), launch.Token));
        using var http = Http();
        // Pages carry no credential in region mode, and no component page is framed: frame-ancestors 'none'.
        using var document = await http.GetAsync(launch.Listener);
        Assert.Equal("frame-ancestors 'none'", string.Join(", ", document.Headers.GetValues("Content-Security-Policy")));
        Assert.False(document.Headers.Contains("Set-Cookie"));
    }

    [Test]
    public async Task ServesBlobsAndUploadsToTheEmbedderWithCors()
    {
        await using var guest = await ComponentSurface.StartAsync();
        var hostPage = "http://127.0.0.1:38200";
        var served = new ConcurrentQueue<string?>();
        using var blobs = guest.Surface.ServeBlobs((context, token) =>
        {
            served.Enqueue(token);
            context.StatusCode = (int)HttpStatusCode.OK;
            return Task.CompletedTask;
        });
        var launch = await guest.Surface.BeginComponentAsync(hostPage, allowSameSite: true, browser: false, CancellationToken.None);
        using var http = Http();
        var blob = new Uri(launch.Listener, "_runic/blobs/b1");
        var upload = new Uri(launch.Listener, "_runic/upload");

        // A CORS fetch of the embedder: granted for exactly its origin.
        using (var fetched = await SendAsync(http, blob, hostPage))
        {
            Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
            Assert.Equal([hostPage], fetched.Headers.GetValues("Access-Control-Allow-Origin"));
            Assert.Equal(["cross-origin"], fetched.Headers.GetValues("Cross-Origin-Resource-Policy"));
        }
        // The host page's <img>: no Origin, cross-site when the component is served cross-site. The URL is the capability.
        using (var embedded = await SendAsync(http, blob, null))
            Assert.Equal(HttpStatusCode.OK, embedded.StatusCode);
        // Another origin's CORS request.
        using (var foreign = await SendAsync(http, blob, "http://127.0.0.1:38201"))
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

        // An upload's preflight and POST: the embedder alone; the upload token is the handler's to check.
        using (var preflight = await SendAsync(http, upload, hostPage, HttpMethod.Options, preflight: true))
        {
            Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
            Assert.Equal([hostPage], preflight.Headers.GetValues("Access-Control-Allow-Origin"));
            Assert.Equal(["true"], preflight.Headers.GetValues("Access-Control-Allow-Credentials"));
            Assert.Equal(["POST"], preflight.Headers.GetValues("Access-Control-Allow-Methods"));
            Assert.Contains("X-Runic-Upload-Token", string.Join(",", preflight.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.Ordinal);
        }
        using (var foreignPreflight = await SendAsync(http, upload, "http://127.0.0.1:38201", HttpMethod.Options, preflight: true))
            Assert.Equal(HttpStatusCode.Forbidden, foreignPreflight.StatusCode);
        using (var posted = await SendAsync(http, upload, hostPage, HttpMethod.Post))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
            Assert.Equal([hostPage], posted.Headers.GetValues("Access-Control-Allow-Origin"));
        }
        using (var foreignPost = await SendAsync(http, upload, "http://127.0.0.1:38201", HttpMethod.Post))
            Assert.Equal(HttpStatusCode.Forbidden, foreignPost.StatusCode);
        using (var anonymousPost = await SendAsync(http, upload, null, HttpMethod.Post))
            Assert.Equal(HttpStatusCode.Forbidden, anonymousPost.StatusCode);
        Assert.Equal(["b1", "b1", null], served);

        // After the detach, a cross-site blob request is refused again.
        guest.Surface.EndComponent();
        using var after = await SendAsync(http, blob, hostPage);
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
    }

    // A real engine: a Runic host page with a Content-Security-Policy that names http://127.0.0.1:* and
    // ws://127.0.0.1:*, as the security guide says, imports the guest's component from the guest's origin, built on the
    // real @runic-artifex/views `component` entry, and connects it with the token; the guest counts the connection.
    [Test]
    public async Task AChromiumHostPageLoadsAndConnectsTheGuestsComponent()
    {
        if (!OperatingSystem.IsLinux() || BrowserBridgeTests.FindChrome() is not { } chrome) return;
        if (FindViewsBuild() is not { } views) return;
        var content = Directory.CreateTempSubdirectory("runic-component-e2e-");
        var folder = Directory.CreateTempSubdirectory("runic-desktop-component-");
        try
        {
            var componentFolder = Directory.CreateDirectory(Path.Combine(content.FullName, "_runic_component", "views"));
            foreach (var file in Directory.EnumerateFiles(views, "*.js")) File.Copy(file, Path.Combine(componentFolder.FullName, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(content.FullName, "index.html"), "<!doctype html><title>Guest</title>");
            File.WriteAllText(Path.Combine(content.FullName, "_runic_component", "component.js"), """
                import { defineRunicComponent } from "./views/component.js";
                const definition = defineRunicComponent({
                  tag: "guest-e2e-0a1b2c3d",
                  mount({ container }) { container.textContent = "Guest component"; },
                });
                export const tag = definition.tag;
                """);
            await using var guest = await ComponentSurface.StartAsync(new DesktopContent.Directory(content.FullName, "index.html"));

            File.CreateSymbolicLink(Path.Combine(folder.FullName, "google-chrome"), chrome);
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                BrowserFolder = folder.FullName,
                ConnectionTimeout = TestTimeouts.Scale(TimeSpan.FromSeconds(30)),
            });
            DesktopComponentLaunch? launch = null;
            var reports = new ConcurrentQueue<string>();
            var hostPage = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Html("<!doctype html><title>Host</title><script src=\"webui.js\"></script><script type=\"module\" src=\"host.js\"></script>"),
                ContentSecurityPolicy = "default-src 'self'; script-src 'self' http://127.0.0.1:*; connect-src 'self' ws://127.0.0.1:*",
                Resources = (request, _) => ValueTask.FromResult<ContentResponse?>(request.Path switch
                {
                    "/host.js" => ContentResponse.Text("""
                        const endpoint = await (await fetch("endpoint")).json();
                        try {
                          const exports = await import(endpoint.module);
                          const element = document.createElement(exports.tag);
                          document.body.append(element);
                          element.connect({ url: endpoint.url, token: endpoint.token });
                          fetch(element.shadowRoot.textContent === "Guest component" ? "report/mounted" : "report/empty");
                        } catch (error) {
                          console.error(error);
                          fetch("report/failed");
                        }
                        """, "text/javascript"),
                    "/endpoint" when launch is { } current => ContentResponse.Text(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        module = new Uri(current.Listener, "_runic_component/component.js").AbsoluteUri,
                        url = new UriBuilder(new Uri(current.Listener, "_webui_ws_connect")) { Scheme = "ws" }.Uri.AbsoluteUri,
                        token = current.Token,
                    }), "application/json"),
                    _ when request.Path.StartsWith("/report/", StringComparison.Ordinal) => Report(request.Path["/report/".Length..]),
                    _ => null,
                }),
            });
            ContentResponse? Report(string outcome)
            {
                reports.Enqueue(outcome);
                return ContentResponse.Text("", "text/plain");
            }
            await using (hostPage)
            {
                launch = await guest.Surface.BeginComponentAsync(hostPage.Url.GetLeftPart(UriPartial.Authority), allowSameSite: true, browser: false, CancellationToken.None);
                await using var window = await hostPage.OpenWindowAsync(new DesktopWindowOptions
                {
                    Browser = BrowserKind.Chrome,
                    Hidden = true,
                    BrowserArguments = "--no-sandbox --disable-gpu --disable-dev-shm-usage",
                });
                await WaitUntilAsync(() => !reports.IsEmpty, TimeSpan.FromSeconds(30));
                Assert.Equal(["mounted"], reports);
                await WaitUntilAsync(() => guest.Surface.ConnectionCount == 1, TimeSpan.FromSeconds(30));
                Assert.DoesNotContain(guest.Diagnostics, static diagnostic => diagnostic.Code is "component-origin-not-allowed" or "origin-not-allowed" or "credential-required");
            }
        }
        finally
        {
            await BrowserTestProfile.DeleteAsync(folder.FullName, TimeSpan.FromSeconds(10));
            content.Delete(recursive: true);
        }
    }

    // The build of @runic-artifex/views in this checkout (CI's native job builds it before these tests), or null.
    private static string? FindViewsBuild()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "RunicSdk.slnx"))) continue;
            var dist = Path.Combine(directory.FullName, "packages", "web", "views", "dist");
            return File.Exists(Path.Combine(dist, "component.js")) ? dist : null;
        }
        return null;
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

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, Uri url, string? origin, HttpMethod? method = null, bool preflight = false)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        if (origin is not null) request.Headers.Add("Origin", origin);
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        if (preflight)
        {
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "content-type,x-runic-upload-name,x-runic-upload-token");
        }
        return client.SendAsync(request);
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri listener, string origin, string? token)
    {
        var socket = new ClientWebSocket();
        if (token is not null) socket.Options.AddSubProtocol("runic-desktop." + token);
        socket.Options.SetRequestHeader("Origin", origin);
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new UriBuilder(new Uri(listener, "_webui_ws_connect")) { Scheme = "ws" }.Uri, timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // The component's connection: its WebSocket with the token and its token check, whose packet token is 0.
    private static async Task<ClientWebSocket> ComponentPageAsync(Uri listener, string origin, string token)
    {
        var socket = await ConnectAsync(listener, origin, token);
        var packet = new byte[WebUiProtocol.HeaderSize + 1];
        packet[0] = WebUiProtocol.Signature;
        packet[7] = WebUiProtocol.CheckToken;
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
        await socket.SendAsync(packet, WebSocketMessageType.Binary, true, timeout.Token);
        return socket;
    }

    private static async Task<HttpStatusCode> RefusedAsync(Uri listener, string origin, string? token)
    {
        try
        {
            using var socket = await ConnectAsync(listener, origin, token);
            throw new InvalidOperationException("The WebSocket was admitted.");
        }
        catch (WebSocketException error) when (error.InnerException is null or HttpRequestException or WebSocketException)
        {
            var match = System.Text.RegularExpressions.Regex.Match(error.Message, @"'(\d{3})'");
            return match.Success ? (HttpStatusCode)int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? limit = null)
    {
        using var timeout = TestTimeouts.Cancellation(limit ?? TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class ComponentSurface : IAsyncDisposable
    {
        private ComponentSurface(DesktopHost host, DesktopSurface surface, ConcurrentQueue<DesktopDiagnostic> diagnostics)
        {
            Host = host;
            Surface = surface;
            Diagnostics = diagnostics;
        }

        internal DesktopHost Host { get; }

        internal DesktopSurface Surface { get; }

        internal ConcurrentQueue<DesktopDiagnostic> Diagnostics { get; }

        internal static async Task<ComponentSurface> StartAsync(DesktopContent? content = null)
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
                var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
                {
                    Content = content ?? new DesktopContent.Html("<!doctype html><title>Guest</title>"),
                });
                surface.EnterRegionMode();
                return new ComponentSurface(host, surface, diagnostics);
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
}
