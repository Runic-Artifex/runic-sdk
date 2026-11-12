using TUnit.Core;
using System.Collections.Concurrent;
using System.Net;

namespace Runic.Desktop.Tests;

// region-component/1 (trusted component mode): a surface serves its component build (/_runic_component/, which the
// `component` target of @runic-artifex/vite-plugin-runic writes) to the embedding host page's origin alone, with CORS
// for exactly that origin. The component attach (TC3) sets the embedder; these tests set it directly.
public sealed class ComponentServingTests
{
    private const string Embedder = "http://127.0.0.1:5173";

    [Test]
    public async Task ServesTheComponentBuildWithCorsForExactlyTheEmbedder()
    {
        var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
        var root = CreateContent();
        try
        {
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { DiagnosticSink = diagnostics.Enqueue });
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Directory(root, "index.html"),
            });
            surface.SetComponentEmbedder(new Uri(Embedder));
            using var client = new HttpClient();

            using var module = await SendAsync(client, new Uri(surface.Url, "_runic_component/component.js"), Embedder);
            Assert.Equal(HttpStatusCode.OK, module.StatusCode);
            Assert.Equal("export default 1;", await module.Content.ReadAsStringAsync());
            Assert.StartsWith("text/javascript", module.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
            Assert.Equal([Embedder], module.Headers.GetValues("Access-Control-Allow-Origin"));
            Assert.Contains("Origin", module.Headers.Vary);
            Assert.Equal(["cross-origin"], module.Headers.GetValues("Cross-Origin-Resource-Policy"));
            Assert.False(module.Headers.Contains("Set-Cookie"), "A component request gets no client cookie.");

            using var asset = await SendAsync(client, new Uri(surface.Url, "_runic_component/assets/badge.svg"), Embedder);
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.Equal([Embedder], asset.Headers.GetValues("Access-Control-Allow-Origin"));

            using var head = await SendAsync(client, new Uri(surface.Url, "_runic_component/component.js"), Embedder, HttpMethod.Head);
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);

            using var missing = await SendAsync(client, new Uri(surface.Url, "_runic_component/missing.js"), Embedder);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            // The page's own files get no CORS grant, for the embedder neither.
            using var pageScript = await SendAsync(client, new Uri(surface.Url, "app.js"), Embedder);
            Assert.False(pageScript.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.Empty(diagnostics);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [DisplayName("RefusesOtherOrigins(origin: $origin)")]
    [Arguments("http://127.0.0.1:5174")]
    [Arguments("http://localhost:5173")]
    [Arguments("https://127.0.0.1:5173")]
    [Arguments("null")]
    [Arguments("")]
    public async Task RefusesOtherOrigins(string origin)
    {
        var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
        var root = CreateContent();
        try
        {
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { DiagnosticSink = diagnostics.Enqueue });
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Directory(root, "index.html"),
            });
            surface.SetComponentEmbedder(new Uri(Embedder));
            using var client = new HttpClient();

            using var response = await SendAsync(client, new Uri(surface.Url, "_runic_component/component.js"), origin);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("component-origin-not-allowed", diagnostic.Code);
            Assert.Equal(DesktopErrorCategory.OriginDenied, diagnostic.Category);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task RefusesEveryOriginWithoutAnEmbedderAndServesNoPagesOrWrites()
    {
        var root = CreateContent();
        try
        {
            await using var host = await DesktopHost.StartAsync();
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Directory(root, "index.html"),
            });
            using var client = new HttpClient();
            var module = new Uri(surface.Url, "_runic_component/component.js");

            using var before = await SendAsync(client, module, Embedder);
            Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);

            surface.SetComponentEmbedder(new Uri(Embedder));
            using var page = await SendAsync(client, new Uri(surface.Url, "_runic_component/index.html"), Embedder);
            Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
            using var folder = await SendAsync(client, new Uri(surface.Url, "_runic_component/"), Embedder);
            Assert.Equal(HttpStatusCode.NotFound, folder.StatusCode);
            using var post = await SendAsync(client, module, Embedder, HttpMethod.Post);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);

            surface.SetComponentEmbedder(null);
            using var after = await SendAsync(client, module, Embedder);
            Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A browser sends an origin with its default port without the port; the grant repeats the request's serialization.
    [Test]
    public async Task GrantsTheOriginAsTheRequestSerializedIt()
    {
        var root = CreateContent();
        try
        {
            await using var host = await DesktopHost.StartAsync();
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Directory(root, "index.html"),
            });
            surface.SetComponentEmbedder(new Uri("http://localhost:80/"));
            using var client = new HttpClient();

            using var response = await SendAsync(client, new Uri(surface.Url, "_runic_component/component.js"), "http://localhost");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(["http://localhost"], response.Headers.GetValues("Access-Control-Allow-Origin"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateContent()
    {
        var root = Directory.CreateTempSubdirectory("runic-component-").FullName;
        File.WriteAllText(Path.Combine(root, "index.html"), "<p>page</p>");
        File.WriteAllText(Path.Combine(root, "app.js"), "1");
        var component = Directory.CreateDirectory(Path.Combine(root, "_runic_component", "assets")).Parent!.FullName;
        File.WriteAllText(Path.Combine(component, "component.js"), "export default 1;");
        File.WriteAllText(Path.Combine(component, "index.html"), "<p>not served</p>");
        File.WriteAllText(Path.Combine(component, "assets", "badge.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        return root;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, Uri url, string origin, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        if (origin.Length != 0) request.Headers.Add("Origin", origin);
        request.Headers.Add("Sec-Fetch-Mode", "cors");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        return client.SendAsync(request);
    }
}
