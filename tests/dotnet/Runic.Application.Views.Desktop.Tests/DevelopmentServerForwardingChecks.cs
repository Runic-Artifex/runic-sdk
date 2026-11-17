extern alias cswebui;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using TUnit.Core;
using CsWebUiDevelopmentServer = cswebui::Runic.Application.Views.CsWebUi.CsWebUiDevelopmentServer;
using CsWebUiForwarding = cswebui::Runic.Application.Views.Desktop.DevelopmentServerForwarding;

// dotnet runic dev with a Vite development server: the Window's origin serves www/ only, so what www/ does not have comes
// from the server (#1085), and the listener's root-relative paths, such as the Vite DevTools dock's, redirect to it
// (#1086). Only in Development, only GET and HEAD from loopback, same-origin pages (DevelopmentServerForwarding).
internal sealed class DevelopmentServerForwardingChecks
{
    [Test]
    public void TheServerIsReadOnlyInDevelopmentAndOnlyOnLoopback()
    {
        Func<string, string?> Environment(string? environment, string? server, string? angular = null) => name => name switch
        {
            "DOTNET_ENVIRONMENT" => environment,
            DevelopmentServerForwarding.ViteVariable => server,
            DevelopmentServerForwarding.FrontendVariable => angular,
            _ => null,
        };
        Check(DevelopmentServerForwarding.Resolve(Environment("Development", "http://127.0.0.1:5173/"))?.Origin.AbsoluteUri ==
            "http://127.0.0.1:5173/", "A Vite server on loopback was not used.");
        Check(DevelopmentServerForwarding.Resolve(Environment("Development", null, "http://127.0.0.1:4200"))?.Origin.AbsoluteUri ==
            "http://127.0.0.1:4200/", "An Angular server was not used.");
        Check(DevelopmentServerForwarding.Resolve(Environment("Development", "http://127.0.0.1:5173/app"))?.Origin.AbsoluteUri ==
            "http://127.0.0.1:5173/app/", "A base path was not kept as a folder.");
        Check(DevelopmentServerForwarding.Resolve(Environment("Production", "http://127.0.0.1:5173/")) is null,
            "A server was used outside Development.");
        Check(DevelopmentServerForwarding.Resolve(Environment(null, "http://127.0.0.1:5173/")) is null,
            "A server was used without an environment.");
        // DEV-SEC-61: only the address dev binds; localhost may resolve to ::1, where another user could listen.
        foreach (var refused in new[] { "http://localhost:5173/", "http://[::1]:5173/", "http://127.0.0.2:5173/", "http://192.168.1.2:5173/", "http://example.com:5173/", "https://127.0.0.1:5173/",
            "http://user@127.0.0.1:5173/", "http://127.0.0.1:5173/?x=1", "file:///tmp/x", "5173", "" })
            Check(DevelopmentServerForwarding.Resolve(Environment("Development", refused)) is null,
                $"'{refused}' was used as the development server.");
    }

    [Test]
    public void OnlySameOriginReadsFromLoopbackAreAdmitted()
    {
        var loopback = IPAddress.Loopback;
        Check(DevelopmentServerForwarding.Admits("GET", loopback, "127.0.0.1:4000", "same-origin"), "A same-origin image was refused.");
        Check(DevelopmentServerForwarding.Admits("HEAD", IPAddress.IPv6Loopback, "[::1]:4000", null),
            "A HEAD request without fetch metadata was refused.");
        Check(DevelopmentServerForwarding.Admits("GET", IPAddress.Parse("::ffff:127.0.0.1"), "localhost:4000", "none"),
            "An IPv4-mapped loopback peer was refused.");
        Check(!DevelopmentServerForwarding.Admits("POST", loopback, "127.0.0.1:4000", "same-origin"), "A POST was admitted.");
        Check(!DevelopmentServerForwarding.Admits("GET", IPAddress.Parse("192.168.1.2"), "127.0.0.1:4000", "same-origin"),
            "Another machine was admitted.");
        Check(!DevelopmentServerForwarding.Admits("GET", loopback, "rebind.example:4000", "same-origin"),
            "A DNS-rebinding host was admitted.");
        Check(!DevelopmentServerForwarding.Admits("GET", loopback, "127.0.0.1:4000", "cross-site"), "Another site was admitted.");
        Check(!DevelopmentServerForwarding.Admits("GET", loopback, "127.0.0.1:4000", "same-site"),
            "A same-site page of another origin was admitted.");
    }

    // DEV-SEC-57, 58, 59, 60: a target keeps the server's authority and base path, and names no server endpoint but the
    // dock's, whatever the path held.
    [Test]
    public void TargetsStayOnTheServerAndNameNoEndpoint()
    {
        var forwarding = new DevelopmentServerForwarding(new Uri("http://127.0.0.1:5173/app/"));
        Check(forwarding.DocumentTarget("/brand/my logo.svg", "?v=1")?.AbsoluteUri == "http://127.0.0.1:5173/app/brand/my%20logo.svg?v=1",
            "A document-relative path did not map below the server's base.");
        Check(forwarding.DocumentTarget("/brand/", null)?.AbsoluteUri == "http://127.0.0.1:5173/app/brand/",
            "A trailing slash was refused.");
        Check(forwarding.RootTarget("/__devtools-assets/vite-plus.svg", null)?.AbsoluteUri ==
            "http://127.0.0.1:5173/__devtools-assets/vite-plus.svg", "The dock's icon did not map to the server's root.");
        Check(forwarding.RootTarget("/__devtools/__connection.json", null)?.AbsoluteUri ==
            "http://127.0.0.1:5173/__devtools/__connection.json", "The dock's connection file did not map to the server's root.");
        Check(forwarding.RootTarget("/brand/logo.svg", "?v=2")?.AbsoluteUri == "http://127.0.0.1:5173/brand/logo.svg?v=2",
            "A root-relative file did not map to the server's root.");
        Check(forwarding.DocumentTarget("/a%2F..%2Fb", null)?.AbsoluteUri == "http://127.0.0.1:5173/app/a%252F..%252Fb",
            "A literal percent sign was decoded.");
        Check(forwarding.DocumentTarget("/a", "?x#y") is null, "A query with a fragment was kept.");
        // (decoded path, path as received)
        var refused = new (string Path, string? Raw)[]
        {
            ("//h/x", "//h/x"), ("///h/x", "///h/x"), ("//h:8080/x", "//h:8080/x"), ("//169.254.169.254/latest/meta-data/", null),
            ("/%2Fh/x".Replace("%2F", "/"), "/%2Fh/x"), ("//h/x", "/%2F%2Fh/x"), ("/%2Fh/x", "/%2Fh/x"), ("//h/x", "/%2F%2Fh/x"),
            ("/a//b", "/a//b"), ("/a\\b", "/a\\b"), ("/a\\b", "/a%5Cb"), ("/../x", "/../x"), ("/a/./b", null), ("/", "/"), ("", ""),
            ("/__open-in-editor", "/__open-in-editor"), ("//__open-in-editor", "//__open-in-editor"),
            ("//__open-in-editor", "/%2F__open-in-editor"), ("/__runic/sources", "/__runic/sources"),
            ("/__runic/state", "/__runic/state"), ("/__runic-translations/message", null), ("/@fs/etc/passwd", "/@fs/etc/passwd"),
            ("/@vite/client", null), ("/__DEVTOOLS/x", null),
        };
        foreach (var (path, raw) in refused)
        {
            Check(forwarding.DocumentTarget(path, "?file=x", raw) is null, $"'{raw ?? path}' gave a document target.");
            Check(forwarding.RootTarget(path, "?file=x", raw) is null, $"'{raw ?? path}' gave a root target.");
        }
        // The dock's endpoints exist at the server's root only.
        Check(forwarding.DocumentTarget("/__devtools/embedded.js", null) is null, "The dock's script mapped below the base.");
        Check(DevelopmentServerForwarding.IsDockPath("/__devtools/embedded.js") &&
            !DevelopmentServerForwarding.IsDockPath("/__runic/state"), "The dock's paths were not told apart.");
    }

    [Test, NotInParallel(Order = 35)]
    public async Task AWindowForwardsWhatWwwDoesNotHaveAndRedirectsTheListenersRoot()
    {
        using var cancellation = TestTimeouts.Cancellation(TimeSpan.FromSeconds(30));
        string root = Directory.CreateTempSubdirectory("runic-dev-server-").FullName;
        await using var server = FakeDevelopmentServer.Start();
        try
        {
            string www = Path.Combine(root, "www");
            Write(www, "index.html", "<p>development document</p>");
            Write(www, "runic-views.js", "// host client");

            await using var provider = Services();
            await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                WindowHostFactory = new NativeDispatchFactory(),
                WaitForConnection = false,
            });
            var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
            {
                WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
            })
            {
                ReadEnvironment = name => name switch
                {
                    "DOTNET_ENVIRONMENT" => "Development",
                    DevelopmentServerForwarding.ViteVariable => server.Origin.AbsoluteUri,
                    _ => null,
                },
            };
            await using var window = await host.OpenWindowAsync<ProbeWindow>(new RunicWindowOptions { ContentRoot = www });
            var surface = ((DesktopBridgeWindow<OrderedCollectionModel>)window.Host).Surface;
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

            // www/ first: the server is not asked.
            Check(await http.GetStringAsync(new Uri(surface.Url, "runic-views.js"), cancellation.Token) == "// host client",
                "www/ did not serve its own file.");
            Check(server.Requests.IsEmpty, $"The server was asked for a file of www/: {string.Join(", ", server.Requests)}.");

            // #1085: a URL relative to the document comes from the server, with its type, without the page's cookies.
            using (var request = Get(new Uri(surface.Url, "brand/logo.svg?v=2"), "same-origin"))
            {
                request.Headers.Add("Cookie", "secret=1");
                using var logo = await http.SendAsync(request, cancellation.Token);
                Check(logo.StatusCode == HttpStatusCode.OK && await logo.Content.ReadAsStringAsync(cancellation.Token) == "<svg/>",
                    $"The image was not forwarded: {logo.StatusCode}.");
                Check(logo.Content.Headers.ContentType?.MediaType == "image/svg+xml",
                    $"The image's type was {logo.Content.Headers.ContentType}.");
                Check(logo.Headers.CacheControl?.NoCache == true, "The server's Cache-Control was not passed on.");
                // The surface may set its own session cookie; the server's never passes.
                Check(!(logo.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(cookie => cookie.StartsWith("server=", StringComparison.Ordinal))),
                    "The server's cookie was passed on.");
            }
            Check(server.Requests.TryDequeue(out var seen) && seen.PathAndQuery == "/brand/logo.svg?v=2" && seen.Cookie is null &&
                seen.Origin is null, $"The server saw {seen}.");
            using (var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, new Uri(surface.Url, "brand/logo.svg")),
                cancellation.Token))
                Check(head.StatusCode == HttpStatusCode.OK, $"HEAD was answered {head.StatusCode}.");
            server.Requests.Clear();

            // What the server does not have, another site's request, a write and the editor launcher: 404, 405.
            using (var missing = await http.SendAsync(Get(new Uri(surface.Url, "missing.svg"), "same-origin"), cancellation.Token))
                Check(missing.StatusCode == HttpStatusCode.NotFound, $"A file the server lacks was answered {missing.StatusCode}.");
            server.Requests.Clear();
            using (var crossSite = await http.SendAsync(Get(new Uri(surface.Url, "brand/logo.svg"), "cross-site"), cancellation.Token))
                Check(crossSite.StatusCode == HttpStatusCode.NotFound, $"Another site's request was answered {crossSite.StatusCode}.");
            using (var editor = await http.SendAsync(Get(new Uri(surface.Url, "__open-in-editor?file=x"), null), cancellation.Token))
                Check(editor.StatusCode == HttpStatusCode.NotFound, $"The editor launcher was answered {editor.StatusCode}.");
            using (var post = await http.PostAsync(new Uri(surface.Url, "brand/logo.svg"), new StringContent(""), cancellation.Token))
                Check(post.StatusCode != HttpStatusCode.OK, "A POST was forwarded.");
            Check(server.Requests.IsEmpty, $"The server was asked: {string.Join(", ", server.Requests)}.");

            // #1086: the dock's root-relative files redirect to the same path on the server, so the dock's connection
            // (and its WebSocket) stay on the server.
            var listener = new Uri(surface.Url.GetLeftPart(UriPartial.Authority));
            using (var connection = await http.SendAsync(Get(new Uri(listener, "/__devtools/__connection.json"), "same-origin"),
                cancellation.Token))
            {
                Check(connection.StatusCode == HttpStatusCode.TemporaryRedirect &&
                    connection.Headers.Location?.AbsoluteUri == new Uri(server.Origin, "/__devtools/__connection.json").AbsoluteUri,
                    $"The dock's connection file was answered {connection.StatusCode} {connection.Headers.Location}.");
            }
            using (var icon = await http.SendAsync(Get(new Uri(listener, "/__devtools-assets/vite-plus.svg"), "cross-site"),
                cancellation.Token))
                Check(icon.StatusCode == HttpStatusCode.NotFound, $"Another site's root request was answered {icon.StatusCode}.");
            using (var rebinding = new HttpRequestMessage(HttpMethod.Get, new Uri(listener, "/__devtools/embedded.js")))
            {
                rebinding.Headers.Host = $"rebind.example:{listener.Port}";
                using var answer = await http.SendAsync(rebinding, cancellation.Token);
                Check(answer.StatusCode != HttpStatusCode.TemporaryRedirect, "A DNS-rebinding host was redirected.");
            }

            // DEV-SEC-57/58/59/60, exactly as a page or WebView sends them: nothing leaves for another host, no redirect
            // names one, and no server endpoint is reached.
            server.Requests.Clear();
            string surfacePath = surface.Url.AbsolutePath.TrimEnd('/');
            foreach (var target in new[]
            {
                $"{surfacePath}//127.0.0.1/x", $"{surfacePath}/%2F%2F127.0.0.1/x", $"{surfacePath}/%252F%252F127.0.0.1/x",
                $"{surfacePath}//127.0.0.1:8080/x", $"{surfacePath}/%2F__open-in-editor?file=src/main.ts",
                $"{surfacePath}//__open-in-editor?file=y", $"{surfacePath}/__open-in-editor?file=y",
                $"{surfacePath}/__runic/sources", $"{surfacePath}/__runic/state", $"{surfacePath}/@fs/etc/passwd",
                $"{surfacePath}/a%5Cb",
            })
            {
                var (status, _) = await RawGetAsync(listener, target, cancellation.Token);
                Check(status is 404 or 400, $"'{target}' was answered {status}.");
            }
            foreach (var target in new[] { "//evil.example/x", "/%2Fevil.example/x", "/%2F%2Fevil.example/x", "//__open-in-editor?file=y",
                "/__open-in-editor?file=y", "/__runic/sources", "/@fs/etc/passwd" })
            {
                var (status, location) = await RawGetAsync(listener, target, cancellation.Token);
                Check(status != 307 || location?.StartsWith(server.Origin.AbsoluteUri + "__devtools", StringComparison.Ordinal) == true,
                    $"'{target}' was redirected to '{location}'.");
                Check(status is 404 or 400 or 421, $"'{target}' was answered {status}.");
            }
            Check(server.Requests.IsEmpty, $"The server was asked: {string.Join(", ", server.Requests)}.");
            var (iconStatus, iconLocation) = await RawGetAsync(listener, "/__devtools-assets/vite-plus.svg", cancellation.Token);
            Check(iconStatus == 307 && iconLocation == new Uri(server.Origin, "/__devtools-assets/vite-plus.svg").AbsoluteUri,
                $"The dock's icon was answered {iconStatus} {iconLocation}.");
            using (var nosniff = await http.SendAsync(Get(new Uri(surface.Url, "brand/logo.svg"), "same-origin"), cancellation.Token))
                Check(nosniff.Headers.TryGetValues("X-Content-Type-Options", out var values) && values.Contains("nosniff"),
                    "A forwarded answer had no nosniff.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test, NotInParallel(Order = 35)]
    public async Task OutsideDevelopmentAWindowAnswers404()
    {
        using var cancellation = TestTimeouts.Cancellation(TimeSpan.FromSeconds(30));
        string root = Directory.CreateTempSubdirectory("runic-dev-server-").FullName;
        await using var server = FakeDevelopmentServer.Start();
        try
        {
            string www = Path.Combine(root, "www");
            Write(www, "index.html", "<p>document</p>");
            await using var provider = Services();
            await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                WindowHostFactory = new NativeDispatchFactory(),
                WaitForConnection = false,
            });
            var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
            {
                WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
            })
            {
                ReadEnvironment = name => name == DevelopmentServerForwarding.ViteVariable ? server.Origin.AbsoluteUri : null,
            };
            await using var window = await host.OpenWindowAsync<ProbeWindow>(new RunicWindowOptions { ContentRoot = www });
            var surface = ((DesktopBridgeWindow<OrderedCollectionModel>)window.Host).Surface;
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using (var logo = await http.GetAsync(new Uri(surface.Url, "brand/logo.svg"), cancellation.Token))
                Check(logo.StatusCode == HttpStatusCode.NotFound, $"The image was answered {logo.StatusCode}.");
            using (var dock = await http.GetAsync(new Uri(surface.Url, "/__devtools/embedded.js"), cancellation.Token))
                Check(dock.StatusCode == HttpStatusCode.NotFound, $"The listener's root was answered {dock.StatusCode}.");
            Check(server.Requests.IsEmpty, "The server was asked outside Development.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // CS-WebUI serves the document and the listener's root from one folder: what it lacks redirects to the server.
    [Test]
    public void ACsWebUiWindowRedirectsWhatItsFolderDoesNotHave()
    {
        string root = Directory.CreateTempSubdirectory("runic-dev-server-").FullName;
        try
        {
            Write(root, "index.html", "<p>document</p>");
            Write(root, "assets/runic-cswebui.js", "// host client");
            var forwarding = new CsWebUiForwarding(new Uri("http://127.0.0.1:5173/"));
            Check(!CsWebUiDevelopmentServer.Respond(root, forwarding, "/index.html", () => true).IsHandled,
                "A file of the folder was not left to WebUI.");
            Check(!CsWebUiDevelopmentServer.Respond(root, forwarding, "/assets", () => true).IsHandled,
                "A folder was not left to WebUI.");
            var redirect = CsWebUiDevelopmentServer.Respond(root, forwarding, "/__devtools/__connection.json", () => true);
            var text = Encoding.ASCII.GetString(redirect.Response.Span);
            Check(redirect.IsHandled && text.StartsWith("HTTP/1.1 307 ", StringComparison.Ordinal) &&
                text.Contains("\r\nLocation: http://127.0.0.1:5173/__devtools/__connection.json\r\n", StringComparison.Ordinal),
                $"The dock's connection file was answered '{text}'.");
            Check(!CsWebUiDevelopmentServer.Respond(root, forwarding, "/brand/logo.svg", () => false).IsHandled,
                "A window reachable from other machines redirected.");
            Check(!CsWebUiDevelopmentServer.Respond(root, forwarding, "/__open-in-editor", () => true).IsHandled,
                "The editor launcher was redirected.");
            Check(!CsWebUiDevelopmentServer.Respond(root, forwarding, "/__runic/sources", () => true).IsHandled,
                "A Runic endpoint was redirected.");
            Check(!CsWebUiDevelopmentServer.Respond(root, forwarding, "//evil.example/x", () => true).IsHandled,
                "An empty segment was redirected.");
            var logo = Encoding.ASCII.GetString(CsWebUiDevelopmentServer.Respond(root, forwarding, "/brand/logo.svg", () => true).Response.Span);
            Check(logo.Contains("\r\nLocation: http://127.0.0.1:5173/brand/logo.svg\r\n", StringComparison.Ordinal),
                $"A page-relative file was answered '{logo}'.");
            Check(!CsWebUiDevelopmentServer.Respond(root, forwarding, "/../outside.txt", () => true).IsHandled,
                "A path outside the folder was redirected.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A GET written byte for byte, so no client normalizes "//" or "%2F"; returns the status and the Location.
    private static async Task<(int Status, string? Location)> RawGetAsync(Uri listener, string target, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(listener.Host, listener.Port, cancellationToken);
        await using var stream = client.GetStream();
        var request = $"GET {target} HTTP/1.1\r\nHost: {listener.Authority}\r\nSec-Fetch-Site: same-origin\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var statusLine = await reader.ReadLineAsync(cancellationToken) ?? "";
        var parts = statusLine.Split(' ');
        int status = parts.Length > 1 && int.TryParse(parts[1], out var code) ? code : 0;
        string? location = null;
        for (var line = await reader.ReadLineAsync(cancellationToken); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync(cancellationToken))
        {
            if (line.StartsWith("Location:", StringComparison.OrdinalIgnoreCase)) location = line["Location:".Length..].Trim();
        }
        return (status, location);
    }

    private static HttpRequestMessage Get(Uri url, string? fetchSite)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (fetchSite is not null) request.Headers.Add("Sec-Fetch-Site", fetchSite);
        return request;
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddScoped<OrderedCollectionModel>();
        services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
            _ => (transport, model) => new OrderedCollectionBridge(transport, model));
        return services.BuildServiceProvider();
    }

    private static void Write(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal sealed record SeenRequest(string Method, string PathAndQuery, string? Cookie, string? Origin);

    // A loopback stand-in for Vite: /brand/logo.svg exists, everything else is 404. It records what it was asked.
    private sealed class FakeDevelopmentServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _loop;

        private FakeDevelopmentServer(HttpListener listener, Uri origin)
        {
            _listener = listener;
            Origin = origin;
            _loop = Task.Run(ServeAsync);
        }

        internal Uri Origin { get; }
        internal ConcurrentQueue<SeenRequest> Requests { get; } = new();

        internal static FakeDevelopmentServer Start()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var origin = new Uri($"http://127.0.0.1:{port}/");
            var listener = new HttpListener();
            listener.Prefixes.Add(origin.AbsoluteUri);
            listener.Start();
            return new FakeDevelopmentServer(listener, origin);
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                var request = context.Request;
                Requests.Enqueue(new SeenRequest(request.HttpMethod, request.Url!.PathAndQuery, request.Headers["Cookie"],
                    request.Headers["Origin"]));
                var response = context.Response;
                if (request.Url.AbsolutePath == "/brand/logo.svg")
                {
                    var body = Encoding.UTF8.GetBytes("<svg/>");
                    response.ContentType = "image/svg+xml";
                    response.Headers["Cache-Control"] = "no-cache";
                    response.Headers["Set-Cookie"] = "server=1";
                    response.ContentLength64 = body.Length;
                    if (request.HttpMethod != "HEAD") await response.OutputStream.WriteAsync(body);
                }
                else response.StatusCode = 404;
                response.Close();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            try { await _loop; } catch (ObjectDisposedException) { }
        }
    }
}
