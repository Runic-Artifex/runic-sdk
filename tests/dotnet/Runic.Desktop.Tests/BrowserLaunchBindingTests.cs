using TUnit.Core;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// SEC-01: another user of a Linux or macOS machine can read an installed browser's command line. It must not let
// them take the surface the browser was launched for.
public sealed partial class BrowserLaunchBindingTests
{
    private const string LaunchedPage = "<!doctype html><script src=\"runic-desktop.js\"></script>";

    [Test]
    public void TheFirstClientWithTheCodeIsBoundAndALaterClientRevokesTheBinding()
    {
        var binding = new BrowserLaunchBinding();
        var target = new Uri("http://127.0.0.1:1/surface/index.html");
        Assert.False(binding.IsActive);
        Assert.True(binding.Admits(0));

        var code = binding.Begin(target);
        Assert.True(binding.IsActive);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", code);
        Assert.False(binding.Admits(1));
        Assert.Equal(BrowserLaunchBinding.Outcome.Refused, binding.Consume(code + "x", 1, out _));
        Assert.Equal(BrowserLaunchBinding.Outcome.Refused, binding.Consume(code, 0, out _));

        Assert.Equal(BrowserLaunchBinding.Outcome.Bound, binding.Consume(code, 1, out var redirect));
        Assert.Equal(target, redirect);
        Assert.True(binding.Admits(1));
        Assert.False(binding.Admits(2));
        Assert.Equal(BrowserLaunchBinding.Outcome.AlreadyBound, binding.Consume(code, 1, out _));

        Assert.Equal(BrowserLaunchBinding.Outcome.Conflict, binding.Consume(code, 2, out var conflictTarget));
        Assert.Null(conflictTarget);
        Assert.False(binding.Admits(1));
        Assert.False(binding.Admits(2));
        Assert.Equal(BrowserLaunchBinding.Outcome.Refused, binding.Consume(code, 1, out _));

        var next = binding.Begin(target);
        Assert.NotEqual(code, next);
        Assert.Equal(BrowserLaunchBinding.Outcome.Refused, binding.Consume(code, 3, out _));
        Assert.Equal(BrowserLaunchBinding.Outcome.Bound, binding.Consume(next, 3, out _));
        Assert.True(binding.Admits(3));
    }

    [Test]
    public void FindsTheOwnerOfTheClientSocketInAProcTable()
    {
        // 127.0.0.1:45000 -> 127.0.0.1:8080 owned by 1001; the server's own socket owned by 1000; an unrelated socket.
        const string table = """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 0100007F:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 1 1 0 100 0 0 10 0
               1: 0100007F:1F90 0100007F:AFC8 01 00000000:00000000 00:00000000 00000000  1000        0 2 1 0 20 4 30 10 -1
               2: 0100007F:AFC8 0100007F:1F90 01 00000000:00000000 00:00000000 00000000  1001        0 3 1 0 20 4 30 10 -1
               3: 0200007F:AFC8 0100007F:1F91 01 00000000:00000000 00:00000000 00000000  1002        0 4 1 0 20 4 30 10 -1
            """;
        var loopback = IPAddress.Loopback;
        Assert.Equal(1001u, LoopbackPeerOwner.Find(new StringReader(table), loopback, 45000, loopback, 8080));
        Assert.Equal(1000u, LoopbackPeerOwner.Find(new StringReader(table), loopback, 8080, loopback, 45000));
        Assert.Null(LoopbackPeerOwner.Find(new StringReader(table), loopback, 45001, loopback, 8080));
        Assert.Equal(1001u, LoopbackPeerOwner.Find(new StringReader(table), loopback.MapToIPv6(), 45000, loopback, 8080));

        const string ipv6 = """
              sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 00000000000000000000000001000000:AFC8 00000000000000000000000001000000:1F90 01 00000000:00000000 00:00000000 00000000  1003        0 5 1 0 20 4 30 10 -1
               1: 0000000000000000FFFF00000100007F:AFC9 0000000000000000FFFF00000100007F:1F90 01 00000000:00000000 00:00000000 00000000  1004        0 6 1 0 20 4 30 10 -1
            """;
        Assert.Equal(1003u, LoopbackPeerOwner.Find(new StringReader(ipv6), IPAddress.IPv6Loopback, 45000, IPAddress.IPv6Loopback, 8080));
        Assert.Equal(1004u, LoopbackPeerOwner.Find(new StringReader(ipv6), loopback, 45001, loopback, 8080));
    }

    [Test]
    public async Task FindsTheCurrentUserAsTheOwnerOfALiveLoopbackClient()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();
        var remote = (IPEndPoint)accepted.Client.RemoteEndPoint!;
        var local = (IPEndPoint)accepted.Client.LocalEndPoint!;

        Assert.Equal(LoopbackPeerOwner.CurrentUser, LoopbackPeerOwner.Find(remote.Address, remote.Port, local.Address, local.Port));
    }

    [Test]
    public void BrowserArgumentsCarryTheLaunchAddressOnly()
    {
        var launch = new Uri("http://127.0.0.1:12345/surface-1/_runic_launch/code");
        var arguments = WebUiBrowserHost.BuildArguments(
            new WebUiBrowserInstallation(WebUiBrowser.Chrome, "/browser", IsChromiumBased: true),
            launch,
            new WebUiBrowserLaunchOptions(null, "/profile", null, [], false, false, null, null, null, null));
        Assert.Equal($"--app={launch.AbsoluteUri}", arguments[^1]);
    }

    // The reported attack: the surface address, read from the browser's command line, used to be enough to fetch
    // the credential-bearing bootstrap and open the Bridge with a forged Origin.
    [Test]
    public async Task AClientThatReadTheCommandLineCannotTakeTheSurface()
    {
        if (OperatingSystem.IsWindows()) return;

        await using var fixture = await LaunchedSurface.StartAsync();
        var launch = fixture.LaunchUrl;
        // The surface address is in the launch address, or derived from it.
        using var attacker = new Client();
        using (var bootstrap = await attacker.GetAsync(new Uri(fixture.Surface.Url, "runic-desktop.js")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, bootstrap.StatusCode);
            Assert.DoesNotContain("sessionCredential", await bootstrap.Content.ReadAsStringAsync());
        }
        Assert.Contains(fixture.Diagnostics, static diagnostic => diagnostic.Code == "client-not-bound");
        Assert.StartsWith(new Uri(fixture.Surface.Url, "_runic_launch/").AbsoluteUri, launch.AbsoluteUri);

        using var browser = new Client();
        using (var opened = await browser.GetAsync(launch))
        {
            Assert.Equal(HttpStatusCode.SeeOther, opened.StatusCode);
            Assert.Equal(fixture.Surface.Url, opened.Headers.Location);
        }
        var credential = await browser.ReadCredentialAsync(fixture.Surface.Url);
        using (var webui = await browser.GetAsync(new Uri(fixture.Surface.Url, "webui.js")))
        {
            Assert.Equal(HttpStatusCode.OK, webui.StatusCode);
        }
        using (var refused = await attacker.GetAsync(new Uri(fixture.Surface.Url, "webui.js")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        // Even the stolen credential and a forged Origin do not open the Bridge without the browser's cookie.
        await Assert.ThrowsAsync<WebSocketException>(() => attacker.ConnectAsync(fixture.Surface.Url, credential));
        using var socket = await browser.ConnectAsync(fixture.Surface.Url, credential);
        Assert.Equal(WebSocketState.Open, socket.State);

        // A reload of the launch address by the browser itself keeps working.
        using (var reload = await browser.GetAsync(launch))
        {
            Assert.Equal(HttpStatusCode.SeeOther, reload.StatusCode);
        }
        Assert.DoesNotContain(fixture.Diagnostics, static diagnostic => diagnostic.Code == "browser-launch-conflict");
    }

    // Where a peer's user cannot be checked (macOS), whoever opens the launch address first is bound. When the
    // launched browser then arrives with the used code, the surface is taken from both and the application is told.
    [Test]
    public async Task AClientThatUsedTheCodeFirstLosesTheSurfaceWhenTheBrowserArrives()
    {
        if (OperatingSystem.IsWindows()) return;

        await using var fixture = await LaunchedSurface.StartAsync();
        using var attacker = new Client();
        using (var first = await attacker.GetAsync(fixture.LaunchUrl))
        {
            Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);
        }
        var credential = await attacker.ReadCredentialAsync(fixture.Surface.Url);
        using var hijacked = await attacker.ConnectAsync(fixture.Surface.Url, credential);

        using var browser = new Client();
        using (var refused = await browser.GetAsync(fixture.LaunchUrl))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("launch link", await refused.Content.ReadAsStringAsync());
        }

        var conflict = Assert.Single(fixture.Diagnostics, static diagnostic => diagnostic.Code == "browser-launch-conflict");
        Assert.Equal(DesktopErrorCategory.AuthenticationDenied, conflict.Category);
        using (var bootstrap = await attacker.GetAsync(new Uri(fixture.Surface.Url, "runic-desktop.js")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, bootstrap.StatusCode);
        }
        await Assert.ThrowsAsync<WebSocketException>(() => attacker.ConnectAsync(fixture.Surface.Url, credential));
        await Assert.ThrowsAsync<WebSocketException>(() => browser.ConnectAsync(fixture.Surface.Url, credential));
        // The server closes the hijacked session.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[4096];
        while (hijacked.State == WebSocketState.Open)
        {
            try
            {
                var received = await hijacked.ReceiveAsync(buffer, timeout.Token);
                if (received.MessageType == WebSocketMessageType.Close) break;
            }
            catch (WebSocketException)
            {
                break;
            }
        }
        Assert.NotEqual(WebSocketState.Open, hijacked.State);
    }

    [Test]
    public async Task ASurfaceThatAdmitsMultipleClientsKeepsTheEntryOnTheCommandLine()
    {
        if (OperatingSystem.IsWindows()) return;

        await using var fixture = await LaunchedSurface.StartAsync(new DesktopSecurityPolicy
        {
            ClientAdmission = DesktopClientAdmission.Multiple,
        });

        Assert.Equal(fixture.Surface.Url, fixture.LaunchUrl);
        using var client = new Client();
        _ = await client.ReadCredentialAsync(fixture.Surface.Url);
    }

    // The real browser follows the redirect, keeps the cookie and connects the Bridge with it.
    [Test]
    public async Task AnInstalledChromiumConnectsThroughItsLaunchCode()
    {
        if (!OperatingSystem.IsLinux() || BrowserBridgeTests.FindChrome() is not { } chrome) return;

        var folder = Directory.CreateTempSubdirectory("runic-desktop-chromium-");
        try
        {
            File.CreateSymbolicLink(Path.Combine(folder.FullName, ChromeExecutableName()), chrome);
            var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                BrowserFolder = folder.FullName,
                ConnectionTimeout = TimeSpan.FromSeconds(30),
                DiagnosticSink = diagnostics.Enqueue,
            });
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Html("<!doctype html><title>launched</title><script src=\"webui.js\"></script>"),
            });

            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Chrome,
                Hidden = true,
                // As BrowserBridgeTests runs Chromium on CI runners, where its sandbox is unavailable.
                BrowserArguments = "--no-sandbox --disable-gpu --disable-dev-shm-usage",
            });

            // Opening waits for authentication; the connection is counted when its event is dispatched.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (surface.ConnectionCount == 0) await Task.Delay(20, timeout.Token);
            Assert.Equal(1, surface.ConnectionCount);
            Assert.DoesNotContain(diagnostics, static diagnostic =>
                diagnostic.Code is "client-not-bound" or "browser-launch-conflict" or "browser-launch-foreign-user");
        }
        finally
        {
            await BrowserTestProfile.DeleteAsync(folder.FullName, TimeSpan.FromSeconds(10));
        }
    }

    private sealed class LaunchedSurface : IAsyncDisposable
    {
        private readonly DirectoryInfo _folder;
        private readonly DesktopHost _host;

        private LaunchedSurface(
            DirectoryInfo folder,
            DesktopHost host,
            DesktopSurface surface,
            DesktopWindow window,
            Uri launchUrl,
            ConcurrentQueue<DesktopDiagnostic> diagnostics)
        {
            Diagnostics = diagnostics;
            _folder = folder;
            _host = host;
            Surface = surface;
            Window = window;
            LaunchUrl = launchUrl;
        }

        public ConcurrentQueue<DesktopDiagnostic> Diagnostics { get; }

        public DesktopSurface Surface { get; }

        public DesktopWindow Window { get; }

        public Uri LaunchUrl { get; }

        [UnsupportedOSPlatform("windows")]
        public static async Task<LaunchedSurface> StartAsync(DesktopSecurityPolicy? security = null)
        {
            var folder = Directory.CreateTempSubdirectory("runic-desktop-launch-");
            var arguments = Path.Combine(folder.FullName, "arguments");
            var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
            DesktopHost? host = null;
            try
            {
                // A Chrome stand-in that records its command line, as another user would read it, and requests nothing.
                var executable = Path.Combine(folder.FullName, ChromeExecutableName());
                Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
                await File.WriteAllTextAsync(executable, $$"""
                    #!/bin/sh
                    printf '%s\n' "$@" > '{{arguments}}.tmp'
                    mv '{{arguments}}.tmp' '{{arguments}}'
                    exec sleep 60
                    """);
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                host = await DesktopHost.StartAsync(new DesktopHostOptions
                {
                    BrowserFolder = folder.FullName,
                    WaitForConnection = false,
                    DiagnosticSink = diagnostics.Enqueue,
                    Security = security ?? DesktopSecurityPolicy.Default,
                });
                var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(LaunchedPage) });
                var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Chrome });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!File.Exists(arguments))
                {
                    await Task.Delay(20, timeout.Token);
                }
                var app = (await File.ReadAllLinesAsync(arguments, timeout.Token)).Single(static line => line.StartsWith("--app=", StringComparison.Ordinal));
                return new LaunchedSurface(folder, host, surface, window, new Uri(app["--app=".Length..]), diagnostics);
            }
            catch
            {
                if (host is not null) await host.DisposeAsync();
                folder.Delete(recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Window.DisposeAsync();
                await _host.DisposeAsync();
            }
            finally
            {
                _folder.Delete(recursive: true);
            }
        }
    }

    // A non-browser HTTP client with its own cookie jar, like a second browser profile or a script.
    private sealed class Client : IDisposable
    {
        private readonly CookieContainer _cookies = new();
        private readonly HttpClient _http;

        public Client() => _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies, AllowAutoRedirect = false });

        public Task<HttpResponseMessage> GetAsync(Uri url) => _http.GetAsync(url);

        public async Task<string> ReadCredentialAsync(Uri surfaceUrl)
        {
            using var response = await _http.GetAsync(new Uri(surfaceUrl, "runic-desktop.js"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return CredentialPattern().Match(await response.Content.ReadAsStringAsync()).Groups[1].Value is { Length: > 0 } credential
                ? credential
                : throw new InvalidOperationException("The bootstrap carried no credential.");
        }

        public async Task<ClientWebSocket> ConnectAsync(Uri surfaceUrl, string credential)
        {
            var socket = new ClientWebSocket();
            socket.Options.Cookies = _cookies;
            socket.Options.AddSubProtocol($"runic-desktop.{credential}");
            // A non-browser client can claim any Origin.
            socket.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{surfaceUrl.Port}");
            try
            {
                var endpoint = new UriBuilder(new Uri(surfaceUrl, "_webui_ws_connect")) { Scheme = "ws" }.Uri;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(endpoint, timeout.Token);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public void Dispose() => _http.Dispose();
    }

    private static string ChromeExecutableName() => OperatingSystem.IsMacOS()
        ? Path.Combine("Google Chrome.app", "Contents", "MacOS", "Google Chrome")
        : "google-chrome";

    [GeneratedRegex("sessionCredential: \"([^\"]+)\"")]
    private static partial Regex CredentialPattern();
}
