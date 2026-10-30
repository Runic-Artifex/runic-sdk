using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Runic.Assets.AspNetCore;
using TUnit.Core;

namespace Runic.Assets.Tests;

// I-056 and SEC-06: RunicAssetFileProvider under UseStaticFiles and UseDirectoryBrowser, RunicAssetResults.Asset in
// minimal APIs, and AssetRoutingOptions.ContentSecurityPolicy on HTML only.
internal sealed class AspNetCoreAdapterTests
{
    private const string Policy = "default-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";

    [Test, DisplayName("RunicAssetFileProvider serves files, ranges and listings to UseStaticFiles")]
    public async Task FileProviderServesStaticFiles()
    {
        AssetArchiveSource source = await ArchiveAsync().ConfigureAwait(false);
        var provider = new RunicAssetFileProvider(source);
        await using WebApplication app = await StartAsync(app =>
        {
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
            app.UseDirectoryBrowser(new DirectoryBrowserOptions { FileProvider = provider, RequestPath = "/browse" });
        }).ConfigureAwait(false);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using (HttpResponseMessage css = await client.GetAsync("/assets/site.css").ConfigureAwait(false))
        {
            Equal(HttpStatusCode.OK, css.StatusCode);
            Equal("body { color: rebeccapurple; }", await css.Content.ReadAsStringAsync().ConfigureAwait(false));
            Equal("text/css", css.Content.Headers.ContentType?.MediaType);
            Equal(new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero), css.Content.Headers.LastModified);
        }

        // UseDefaultFiles finds index.html through GetDirectoryContents.
        using (HttpResponseMessage root = await client.GetAsync("/").ConfigureAwait(false))
            Equal("<!doctype html><p>entry</p>", await root.Content.ReadAsStringAsync().ConfigureAwait(false));
        using (HttpResponseMessage missing = await client.GetAsync("/assets/missing.css").ConfigureAwait(false))
            Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using (var head = new HttpRequestMessage(HttpMethod.Head, "/assets/site.css"))
        using (HttpResponseMessage response = await client.SendAsync(head).ConfigureAwait(false))
        {
            Equal(HttpStatusCode.OK, response.StatusCode);
            Equal(30L, response.Content.Headers.ContentLength);
        }

        using (var range = new HttpRequestMessage(HttpMethod.Get, "/assets/site.css"))
        {
            range.Headers.Range = new RangeHeaderValue(5, 9);
            using HttpResponseMessage response = await client.SendAsync(range).ConfigureAwait(false);
            Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Equal("{ col", await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }

        string listing = await client.GetStringAsync("/browse/assets/").ConfigureAwait(false);
        True(listing.Contains("site.css", StringComparison.Ordinal) && listing.Contains("nested", StringComparison.Ordinal), listing);

        // The provider's own view of the manifest.
        string[] rootEntries = [.. provider.GetDirectoryContents("/").Select(static entry => (entry.IsDirectory ? "dir:" : "file:") + entry.Name)];
        SequenceEqual(new[] { "dir:assets", "file:index.html" }, rootEntries);
        True(!provider.GetDirectoryContents("/missing").Exists, "A missing directory exists.");
        True(!provider.GetFileInfo("/assets").Exists, "A directory is a file.");
        True(!provider.GetFileInfo("/assets/../index.html").Exists && !provider.GetDirectoryContents("/../assets").Exists,
            "A traversal path exists.");
        True(provider.GetFileInfo("/assets/nested/data.json").PhysicalPath is null, "An asset has a physical path.");
        True(provider.Watch("**/*") is NullChangeToken, "An archive source has a change token that can fire.");
    }

    [Test, DisplayName("RunicAssetFileProvider.Watch fires when a development source changes")]
    public async Task FileProviderWatchFiresOnChange()
    {
        string directory = Path.Combine(Path.GetTempPath(), "runic-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "index.html"), "<p>one</p>");
            using var source = new DevelopmentDirectoryAssetSource(directory, "index.html");
            var provider = new RunicAssetFileProvider(source);
            IChangeToken token = provider.Watch("index.html");
            var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using IDisposable registration = token.RegisterChangeCallback(_ => fired.TrySetResult(), null);
            True(token.ActiveChangeCallbacks && !token.HasChanged, "The token fired before a change.");

            File.WriteAllText(Path.Combine(directory, "app.js"), "1");
            await source.RefreshAsync().ConfigureAwait(false);
            await fired.Task.WaitScaledAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            True(token.HasChanged, "The token did not report the change.");
            True(provider.GetFileInfo("app.js").Exists, "The provider did not list the new file.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test, DisplayName("RunicAssetResults.Asset serves an asset from a minimal API")]
    public async Task ResultsServeAnAsset()
    {
        AssetArchiveSource source = await ArchiveAsync().ConfigureAwait(false);
        var exact = new AssetRoutingOptions { ServeEntryPointAtRoot = false, EnableSinglePageApplicationFallback = false, ContentSecurityPolicy = Policy };
        await using WebApplication app = await StartAsync(app =>
        {
            app.MapGet("/app/{**path}", (string? path) => RunicAssetResults.Asset(source, path ?? ""));
            app.MapGet("/exact/{**path}", (string? path) => RunicAssetResults.Asset(source, path ?? "", exact));
        }).ConfigureAwait(false);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        AssetDescriptor css = source.Manifest.Assets.Single(static asset => asset.RelativePath == "assets/site.css");

        using (HttpResponseMessage response = await client.GetAsync("/app/assets/site.css").ConfigureAwait(false))
        {
            Equal(HttpStatusCode.OK, response.StatusCode);
            Equal(css.EntityTag, response.Headers.ETag?.ToString());
            Equal("text/css; charset=utf-8", response.Content.Headers.ContentType?.ToString());
            True(!response.Headers.Contains("Content-Security-Policy"), "CSS carried a policy.");
        }

        using (var conditional = new HttpRequestMessage(HttpMethod.Get, "/app/assets/site.css"))
        {
            conditional.Headers.TryAddWithoutValidation("If-None-Match", css.EntityTag);
            using HttpResponseMessage response = await client.SendAsync(conditional).ConfigureAwait(false);
            Equal(HttpStatusCode.NotModified, response.StatusCode);
        }

        using (var unsatisfiable = new HttpRequestMessage(HttpMethod.Get, "/app/assets/site.css"))
        {
            unsatisfiable.Headers.Range = new RangeHeaderValue(999, null);
            using HttpResponseMessage response = await client.SendAsync(unsatisfiable).ConfigureAwait(false);
            Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        }

        // The default routing serves the entry point at the root and for extensionless paths.
        Equal("<!doctype html><p>entry</p>", await client.GetStringAsync("/app/").ConfigureAwait(false));
        Equal("<!doctype html><p>entry</p>", await client.GetStringAsync("/app/settings").ConfigureAwait(false));
        using (HttpResponseMessage undeclared = await client.GetAsync("/app/missing.js").ConfigureAwait(false))
            Equal(HttpStatusCode.NotFound, undeclared.StatusCode);
        using (HttpResponseMessage noFallback = await client.GetAsync("/exact/settings").ConfigureAwait(false))
            Equal(HttpStatusCode.NotFound, noFallback.StatusCode);

        using (HttpResponseMessage html = await client.GetAsync("/exact/index.html").ConfigureAwait(false))
            Equal(Policy, html.Headers.GetValues("Content-Security-Policy").Single());
        using (HttpResponseMessage css2 = await client.GetAsync("/exact/assets/site.css").ConfigureAwait(false))
            True(!css2.Headers.Contains("Content-Security-Policy"), "CSS carried a policy.");
        using (HttpResponseMessage html = await client.GetAsync("/app/index.html").ConfigureAwait(false))
            True(!html.Headers.Contains("Content-Security-Policy"), "HTML carried a policy that was not set.");
    }

    [Test, DisplayName("MapRunicAssetSource sends the routing policy with HTML only")]
    public async Task EndpointSendsThePolicyWithHtmlOnly()
    {
        AssetArchiveSource source = await ArchiveAsync().ConfigureAwait(false);
        await using WebApplication app = await StartAsync(app =>
            app.MapRunicAssetSource(source, routing: new AssetRoutingOptions { ContentSecurityPolicy = Policy })).ConfigureAwait(false);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        foreach (string path in new[] { "/", "/index.html", "/client/route" })
        {
            using HttpResponseMessage response = await client.GetAsync(path).ConfigureAwait(false);
            Equal(Policy, response.Headers.GetValues("Content-Security-Policy").Single());
        }

        using HttpResponseMessage css = await client.GetAsync("/assets/site.css").ConfigureAwait(false);
        True(!css.Headers.Contains("Content-Security-Policy"), "CSS carried a policy.");
        using HttpResponseMessage json = await client.GetAsync("/assets/nested/data.json").ConfigureAwait(false);
        True(!json.Headers.Contains("Content-Security-Policy"), "JSON carried a policy.");

        foreach (string invalid in new[] { "", " ", "default-src 'self'\r\nX-Injected: 1", "a\nb", "a\0b", "café" })
            Throws<ArgumentException>(() => _ = new AssetRoutingOptions { ContentSecurityPolicy = invalid });
        Equal(null, new AssetRoutingOptions { ContentSecurityPolicy = null }.ContentSecurityPolicy);
    }

    private static async Task<AssetArchiveSource> ArchiveAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "runic-adapter-" + Guid.NewGuid().ToString("N"));
        try
        {
            Write(directory, "index.html", "<!doctype html><p>entry</p>");
            Write(directory, "assets/site.css", "body { color: rebeccapurple; }");
            Write(directory, "assets/nested/data.json", "{\"value\":1}");
            using var archive = new MemoryStream();
            await AssetArchive.WriteTrustedDirectoryAsync(directory, archive).ConfigureAwait(false);
            archive.Position = 0;
            return await AssetArchive.ReadAsync(archive).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static void Write(string root, string relativePath, string content)
        {
            string path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplication> configure)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDirectoryBrowser();
        WebApplication app = builder.Build();
        configure(app);
        await app.StartAsync().ConfigureAwait(false);
        return app;
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}' but found '{actual}'.");
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        T[] expectedItems = [.. expected];
        T[] actualItems = [.. actual];
        if (!expectedItems.SequenceEqual(actualItems))
            throw new InvalidOperationException($"Expected [{string.Join(", ", expectedItems)}] but found [{string.Join(", ", actualItems)}].");
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
