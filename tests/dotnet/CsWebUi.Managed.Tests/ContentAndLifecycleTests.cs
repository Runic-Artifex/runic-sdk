using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using CsWebUi.Managed;

namespace CsWebUi.Managed.Tests;

public sealed class ContentAndLifecycleTests
{
    private const byte Signature = 0xDD;
    private const byte CheckToken = 0xF5;
    private const int HeaderSize = 8;

    [Fact]
    public async Task ServesFoldersIndexesFilesMimeHeadAndNoCache()
    {
        var root = Directory.CreateTempSubdirectory("cs-webui-content-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "index.html"), "<h1>folder</h1>");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "site.css"), "body { color: blue; }");
            Directory.CreateDirectory(Path.Combine(root.FullName, "nested"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "nested", "index.htm"), "nested");

            await using var window = new WebUiWindow();
            var url = await window.StartServerAsync(root.FullName);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { BaseAddress = url };

            using var rootResponse = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.Found, rootResponse.StatusCode);
            Assert.Equal("/index.html", rootResponse.Headers.Location?.OriginalString);

            using var indexResponse = await client.GetAsync("/index.html");
            Assert.Equal("<h1>folder</h1>", await indexResponse.Content.ReadAsStringAsync());
            Assert.True(indexResponse.Headers.CacheControl?.NoCache);
            Assert.True(indexResponse.Headers.CacheControl?.NoStore);
            Assert.True(indexResponse.Headers.CacheControl?.MustRevalidate);
            Assert.True(indexResponse.Headers.CacheControl?.Private);
            Assert.Equal(TimeSpan.Zero, indexResponse.Headers.CacheControl?.MaxAge);
            Assert.Equal("nosniff", indexResponse.Headers.GetValues("X-Content-Type-Options").Single());

            using var cssResponse = await client.GetAsync("/site.css");
            Assert.Equal("text/css", cssResponse.Content.Headers.ContentType?.MediaType);

            using var nestedResponse = await client.GetAsync("/nested");
            Assert.Equal(HttpStatusCode.Found, nestedResponse.StatusCode);
            Assert.Equal("/nested/index.htm", nestedResponse.Headers.Location?.OriginalString);

            using var head = new HttpRequestMessage(HttpMethod.Head, "/site.css");
            using var headResponse = await client.SendAsync(head);
            Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
            Assert.Equal(new FileInfo(Path.Combine(root.FullName, "site.css")).Length, headResponse.Content.Headers.ContentLength);
            Assert.Empty(await headResponse.Content.ReadAsByteArrayAsync());

            using var faviconRedirect = await client.GetAsync("/favicon.ico");
            Assert.Equal(HttpStatusCode.Found, faviconRedirect.StatusCode);
            Assert.Equal("/favicon.svg", faviconRedirect.Headers.Location?.OriginalString);
            using var favicon = await client.GetAsync("/favicon.svg");
            Assert.Equal("image/svg+xml", favicon.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitFileRedirectsWithoutFolderIndexFallback()
    {
        var root = Directory.CreateTempSubdirectory("cs-webui-file-");
        try
        {
            var entry = Path.Combine(root.FullName, "custom page.html");
            await File.WriteAllTextAsync(entry, "explicit");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "index.html"), "fallback");
            Directory.CreateDirectory(Path.Combine(root.FullName, "nested"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "nested", "relative.html"), "relative");

            await using var window = new WebUiWindow();
            var url = await window.StartServerAsync(entry);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { BaseAddress = url };

            using var response = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal("/custom page.html", response.Headers.Location?.OriginalString);
            Assert.Equal("explicit", await client.GetStringAsync("/custom%20page.html"));

            await window.CloseAsync();
            window.SetRootFolder(root.FullName);
            var relativeUrl = await window.StartServerAsync("nested/relative.html");
            using var relativeClient = new HttpClient(handler, disposeHandler: false) { BaseAddress = relativeUrl };
            using var relativeRoot = await relativeClient.GetAsync("/");
            Assert.Equal("/nested/relative.html", relativeRoot.Headers.Location?.OriginalString);
            Assert.Equal("relative", await relativeClient.GetStringAsync("/nested/relative.html"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExternalUrlUsesLocalRefreshPageInServerOnlyMode()
    {
        await using var window = new WebUiWindow();
        var serverUrl = await window.StartServerAsync("https://example.test/path?a=1&b=2");
        using var client = new HttpClient { BaseAddress = serverUrl };

        var html = await client.GetStringAsync("/");
        Assert.Contains("http-equiv=\"refresh\"", html, StringComparison.Ordinal);
        Assert.Contains("https://example.test/path?a=1&amp;b=2", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VirtualContentHasFallbackAndCannotOverrideBridge()
    {
        var root = Directory.CreateTempSubdirectory("cs-webui-virtual-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "physical.txt"), "physical");
            var requested = new ConcurrentQueue<string>();
            await using var window = new WebUiWindow();
            window.SetRootFolder(root.FullName);
            window.SetFileHandler((path, _) =>
            {
                requested.Enqueue(path);
                return ValueTask.FromResult<WebUiContent?>(path switch
                {
                    "/virtual.txt" => new WebUiContent(
                        "virtual"u8.ToArray(),
                        "text/plain; charset=utf-8",
                        headers: new Dictionary<string, string> { ["X-Virtual"] = "yes" }),
                    "/entry.html" => WebUiContent.FromText("entry"),
                    _ => null,
                });
            });

            var url = await window.StartServerAsync("entry.html");
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { BaseAddress = url };

            using var rootResponse = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.Found, rootResponse.StatusCode);
            Assert.Equal("/entry.html", rootResponse.Headers.Location?.OriginalString);
            using var virtualResponse = await client.GetAsync("/virtual.txt");
            Assert.Equal("virtual", await virtualResponse.Content.ReadAsStringAsync());
            Assert.Equal("yes", virtualResponse.Headers.GetValues("X-Virtual").Single());
            Assert.Equal("physical", await client.GetStringAsync("/physical.txt"));

            var bridge = await client.GetStringAsync("/webui.js");
            Assert.Contains("Object.defineProperty(globalThis, \"webui\"", bridge, StringComparison.Ordinal);
            Assert.DoesNotContain("/webui.js", requested);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RejectsTraversalAndSymlinksOutsideRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = Directory.CreateTempSubdirectory("cs-webui-scope-");
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(parent.FullName, "root"));
            var secret = Path.Combine(parent.FullName, "secret.txt");
            await File.WriteAllTextAsync(secret, "secret");
            File.CreateSymbolicLink(Path.Combine(root.FullName, "link.txt"), secret);

            await using var window = new WebUiWindow();
            var url = await window.StartServerAsync(root.FullName);
            using var client = new HttpClient { BaseAddress = url };

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/link.txt")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/%2e%2e/secret.txt")).StatusCode);
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UsesExplicitPortAndRetainsSettingsAcrossRestart()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await using var window = new WebUiWindow();
        window.SetPort((nuint)port);
        window.SetPublic(true);
        var first = await window.StartServerAsync("first");
        Assert.Equal(port, first.Port);
        Assert.Equal((nuint)port, window.Port);
        Assert.True(window.IsPublic);
        Assert.Equal(first, await window.StartServerAsync("ignored while running"));

        await window.CloseAsync();
        var second = await window.StartServerAsync("second");
        Assert.Equal(port, second.Port);
        using var client = new HttpClient { BaseAddress = second };
        Assert.Equal("second", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task ApplicationWaitTracksAllRunningWindowsAndExitClosesThem()
    {
        await using var first = new WebUiWindow();
        await using var second = new WebUiWindow();
        await first.StartServerAsync("first");
        await second.StartServerAsync("second");

        var wait = WebUiApplication.WaitAsync();
        Assert.False(wait.IsCompleted);
        await first.CloseAsync();
        Assert.False(wait.IsCompleted);
        await WebUiApplication.ExitAsync();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(WebUiApplication.IsRunning);
        Assert.Null(second.Url);
    }

    [Fact]
    public async Task SingleClientRejectsConcurrentSocketAndMultiClientOwnsStableClientIds()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        WebUiApplication.SetConfiguration(WebUiConfiguration.MultiClient, false);
        try
        {
            await using var window = new WebUiWindow();
            var ids = Channel.CreateUnbounded<(WebUiEventType Type, nuint Client, nuint Connection)>();
            window.Bind(string.Empty, e => ids.Writer.TryWrite((e.EventType, e.ClientId, e.ConnectionId)));
            var url = await window.StartServerAsync("bridge");
            var cookies = new CookieContainer();
            var token = await GetTokenAsync(url, cookies, timeout.Token);

            using var first = await ConnectAsync(url, cookies, timeout.Token);
            await AuthenticateAsync(first, token, timeout.Token);
            var firstConnected = await ids.Reader.ReadAsync(timeout.Token);
            Assert.Equal(WebUiEventType.Connected, firstConnected.Type);

            using var otherHttpClient = new HttpClient { BaseAddress = url };
            Assert.Equal(HttpStatusCode.Forbidden, (await otherHttpClient.GetAsync("/", timeout.Token)).StatusCode);

            using var rejected = new ClientWebSocket();
            rejected.Options.Cookies = cookies;
            await Assert.ThrowsAsync<WebSocketException>(() => rejected.ConnectAsync(GetWebSocketUrl(url), timeout.Token));

            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", timeout.Token);
            Assert.Equal(WebUiEventType.Disconnected, (await ids.Reader.ReadAsync(timeout.Token)).Type);

            using var reconnected = await ConnectAsync(url, cookies, timeout.Token);
            await AuthenticateAsync(reconnected, token, timeout.Token);
            var secondConnected = await ids.Reader.ReadAsync(timeout.Token);
            Assert.Equal(firstConnected.Client, secondConnected.Client);
            Assert.NotEqual(firstConnected.Connection, secondConnected.Connection);

            WebUiApplication.SetConfiguration(WebUiConfiguration.MultiClient, true);
            using var other = await ConnectAsync(url, new CookieContainer(), timeout.Token);
            await AuthenticateAsync(other, token, timeout.Token);
            var otherConnected = await ids.Reader.ReadAsync(timeout.Token);
            Assert.NotEqual(secondConnected.Client, otherConnected.Client);
        }
        finally
        {
            WebUiApplication.SetConfiguration(WebUiConfiguration.MultiClient, false);
        }
    }

    [Fact]
    public async Task BridgeSessionsRemainOwnedByTheirWindow()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstEvents = Channel.CreateUnbounded<WebUiEventType>();
        var secondEvents = Channel.CreateUnbounded<WebUiEventType>();
        await using var first = new WebUiWindow();
        await using var second = new WebUiWindow();
        first.Bind(string.Empty, e => firstEvents.Writer.TryWrite(e.EventType));
        second.Bind(string.Empty, e => secondEvents.Writer.TryWrite(e.EventType));
        var firstUrl = await first.StartServerAsync("first");
        var secondUrl = await second.StartServerAsync("second");
        var firstCookies = new CookieContainer();
        var secondCookies = new CookieContainer();
        var firstToken = await GetTokenAsync(firstUrl, firstCookies, timeout.Token);
        _ = await GetTokenAsync(secondUrl, secondCookies, timeout.Token);

        using var socket = await ConnectAsync(firstUrl, firstCookies, timeout.Token);
        await AuthenticateAsync(socket, firstToken, timeout.Token);
        Assert.Equal(WebUiEventType.Connected, await firstEvents.Reader.ReadAsync(timeout.Token));
        Assert.False(secondEvents.Reader.TryRead(out _));
    }

    [Fact]
    public async Task DefaultRootIsCopiedByNewWindowsAndRootsRemainWindowOwned()
    {
        var firstRoot = Directory.CreateTempSubdirectory("cs-webui-root-one-");
        var secondRoot = Directory.CreateTempSubdirectory("cs-webui-root-two-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(firstRoot.FullName, "value.txt"), "one");
            await File.WriteAllTextAsync(Path.Combine(secondRoot.FullName, "value.txt"), "two");
            WebUiApplication.SetDefaultRootFolder(firstRoot.FullName);
            await using var first = new WebUiWindow();
            WebUiApplication.SetDefaultRootFolder(secondRoot.FullName);
            await using var second = new WebUiWindow();

            var firstUrl = await first.StartServerAsync(string.Empty);
            var secondUrl = await second.StartServerAsync(string.Empty);
            using var firstClient = new HttpClient { BaseAddress = firstUrl };
            using var secondClient = new HttpClient { BaseAddress = secondUrl };
            Assert.Equal("one", await firstClient.GetStringAsync("/value.txt"));
            Assert.Equal("two", await secondClient.GetStringAsync("/value.txt"));
        }
        finally
        {
            WebUiApplication.SetDefaultRootFolder(Environment.CurrentDirectory);
            firstRoot.Delete(recursive: true);
            secondRoot.Delete(recursive: true);
        }
    }

    private static async Task<uint> GetTokenAsync(Uri url, CookieContainer cookies, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { CookieContainer = cookies };
        using var client = new HttpClient(handler) { BaseAddress = url };
        var bridge = await client.GetStringAsync("/webui.js", cancellationToken);
        const string prefix = "const TOKEN = ";
        var start = bridge.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var end = bridge.IndexOf(';', start);
        return uint.Parse(bridge[start..end], NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static async Task<ClientWebSocket> ConnectAsync(
        Uri url,
        CookieContainer cookies,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.Cookies = cookies;
        await socket.ConnectAsync(GetWebSocketUrl(url), cancellationToken);
        return socket;
    }

    private static Uri GetWebSocketUrl(Uri url) => new UriBuilder(url)
    {
        Scheme = "ws",
        Path = "/_webui_ws_connect",
    }.Uri;

    private static async Task AuthenticateAsync(ClientWebSocket socket, uint token, CancellationToken cancellationToken)
    {
        var packet = new byte[HeaderSize + 1];
        packet[0] = Signature;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), token);
        packet[7] = CheckToken;
        await socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);

        var buffer = new byte[4096];
        var response = new ArrayBufferWriter<byte>();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            response.Write(buffer.AsSpan(0, result.Count));
        }
        while (!result.EndOfMessage);

        Assert.True(response.WrittenCount > HeaderSize);
        Assert.Equal(1, response.WrittenSpan[HeaderSize]);
    }
}
