using System.Collections.Concurrent;
using System.Net;

namespace Runic.Desktop.Tests;

public sealed class BootstrapAdmissionTests
{
    [Theory]
    [InlineData("runic-desktop.js")]
    [InlineData("webui.js")]
    [InlineData("")]
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

    [Fact]
    public async Task CompatibilityWindowRejectsRebindingHostNames()
    {
        await using var window = new WebUiWindow();
        var url = await window.StartServerAsync("rebinding");
        using var client = new HttpClient();

        using var response = await SendAsync(client, new Uri(url, "webui.js"), $"attacker.test:{url.Port}");

        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("runic-desktop.js", "cross-site", "script")]
    [InlineData("runic-desktop.js", "same-site", "script")]
    [InlineData("runic-desktop.js", "same-origin", "document")]
    [InlineData("webui.js", "cross-site", "script")]
    [InlineData("webui.js", "same-origin", "empty")]
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

    [Theory]
    [InlineData("runic-desktop.js", "same-origin")]
    [InlineData("runic-desktop.js", "none")]
    [InlineData("webui.js", "same-origin")]
    public async Task BootstrapScriptsAdmitSameOriginBrowserLoads(string path, string site)
    {
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync();
        using var client = new HttpClient();

        using var response = await SendAsync(client, new Uri(surface.Url, path), site: site, destination: "script");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("_webui_ws_connect", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
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
