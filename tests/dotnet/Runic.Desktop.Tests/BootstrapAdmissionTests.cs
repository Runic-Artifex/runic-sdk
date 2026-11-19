using TUnit.Core;
using System.Collections.Concurrent;
using System.Net;

namespace Runic.Desktop.Tests;

public sealed class BootstrapAdmissionTests
{
    [Test]
    [DisplayName("LoopbackListenerRejectsRebindingHostNames(path: $path)")]
    [Arguments("runic-desktop.js")]
    [Arguments("webui.js")]
    [Arguments("")]
    public async Task LoopbackListenerRejectsRebindingHostNames(string path)
    {
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync();
        using var client = new HttpClient();

        using var rebound = await SendAsync(client, new Uri(surface.Url, path), $"attacker.test:{surface.Url.Port}");
        using var otherPort = await SendAsync(client, new Uri(surface.Url, path), "127.0.0.1:1");
        using var named = await SendAsync(client, new Uri(surface.Url, path), $"localhost:{surface.Url.Port}");

        Assert.Equal(HttpStatusCode.MisdirectedRequest, rebound.StatusCode);
        Assert.Empty(await rebound.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.MisdirectedRequest, otherPort.StatusCode);
        Assert.NotEqual(HttpStatusCode.MisdirectedRequest, named.StatusCode);
    }

    [Test]
    public async Task CompatibilityWindowRejectsRebindingHostNames()
    {
        await using var window = new WebUiWindow();
        var url = await window.StartServerAsync("rebinding");
        using var client = new HttpClient();

        using var response = await SendAsync(client, new Uri(url, "webui.js"), $"attacker.test:{url.Port}");

        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
    }

    [Test]
    [DisplayName("BootstrapScriptsRejectForeignBrowserLoads(path: $path, site: $site, destination: $destination)")]
    [Arguments("runic-desktop.js", "cross-site", "script")]
    [Arguments("runic-desktop.js", "same-site", "script")]
    [Arguments("runic-desktop.js", "same-origin", "document")]
    [Arguments("webui.js", "cross-site", "script")]
    [Arguments("webui.js", "same-origin", "empty")]
    public async Task BootstrapScriptsRejectForeignBrowserLoads(string path, string site, string destination)
    {
        var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { DiagnosticSink = diagnostics.Enqueue });
        await using var surface = await host.CreateSurfaceAsync();
        using var client = new HttpClient();

        using var response = await SendAsync(client, new Uri(surface.Url, path), site: site, destination: destination);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Equal("bootstrap-origin-not-allowed", Assert.Single(diagnostics).Code);
    }

    [Test]
    [DisplayName("BootstrapScriptsAdmitSameOriginBrowserLoads(path: $path, site: $site)")]
    [Arguments("runic-desktop.js", "same-origin")]
    [Arguments("runic-desktop.js", "none")]
    [Arguments("webui.js", "same-origin")]
    public async Task BootstrapScriptsAdmitSameOriginBrowserLoads(string path, string site)
    {
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync();
        using var client = new HttpClient();

        using var response = await SendAsync(client, new Uri(surface.Url, path), site: site, destination: "script");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("_webui_ws_connect", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Test]
    public async Task ExplicitAdditionalOriginMayEmbedTheBootstrapScript()
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            Security = new DesktopSecurityPolicy { AdditionalOrigins = [new Uri("http://localhost:5173")] },
        });
        await using var surface = await host.CreateSurfaceAsync();
        using var client = new HttpClient();
        var url = new Uri(surface.Url, "runic-desktop.js");

        using var admitted = await SendAsync(client, url, site: "cross-site", destination: "script", referrer: "http://localhost:5173/");
        using var unlisted = await SendAsync(client, url, site: "cross-site", destination: "script", referrer: "http://localhost:5174/");
        using var anonymous = await SendAsync(client, url, site: "cross-site", destination: "script");

        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unlisted.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
    }

    [Test]
    public async Task ServedThroughOriginIsAdmittedAndCarriesTheBridge()
    {
        // #1187 (D077.14): dotnet runic dev --browser serves a serve-only Window's pages through its own loopback address,
        // which the surface admits, and the page's Bridge connects to that address's port.
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { ServedThrough = new Uri("http://127.0.0.1:9224/") });
        using var client = new HttpClient();

        using var bootstrap = await SendAsync(client, new Uri(surface.Url, "runic-desktop.js"), site: "cross-site", destination: "script",
            referrer: "http://127.0.0.1:9224/");
        using var other = await SendAsync(client, new Uri(surface.Url, "runic-desktop.js"), site: "cross-site", destination: "script",
            referrer: "http://127.0.0.1:9225/");
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Contains("endpoint.port = 9224;", await bootstrap.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var bridge = await SendAsync(client, new Uri(surface.Url, "webui.js"), site: "cross-site", destination: "script",
            referrer: "http://127.0.0.1:9224/");
        Assert.Equal(HttpStatusCode.OK, bridge.StatusCode);
        Assert.Contains("const PORT=9224;", await bridge.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Without it, the page connects to the surface's own port.
        await using var plain = await host.CreateSurfaceAsync();
        using var own = await SendAsync(client, new Uri(plain.Url, "webui.js"), site: "same-origin", destination: "script");
        Assert.Contains($"const PORT={plain.Url.Port};", await own.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Uri url,
        string? host = null,
        string? site = null,
        string? destination = null,
        string? referrer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (host is not null) request.Headers.Host = host;
        if (site is not null) request.Headers.Add("Sec-Fetch-Site", site);
        if (destination is not null) request.Headers.Add("Sec-Fetch-Dest", destination);
        if (referrer is not null) request.Headers.Referrer = new Uri(referrer);
        return client.SendAsync(request);
    }
}
