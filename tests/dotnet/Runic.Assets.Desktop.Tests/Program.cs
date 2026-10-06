using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Runic.Assets.AspNetCore;
using Runic.Desktop;
using Runic.Assets.Desktop;

namespace Runic.Assets.Desktop.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        int failures = 0;
        foreach ((string name, Func<Task> body) in new (string, Func<Task>)[]
        {
            ("Desktop delivery preserves asset streams, cache metadata, ranges, and HEAD", DesktopDelivery),
            ("Desktop and ASP.NET Core route one manifest identically", RoutingParity),
        })
        {
            try
            {
                await body().ConfigureAwait(false);
                Console.WriteLine($"ok - {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"not ok - {name}\n{exception}");
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static async Task DesktopDelivery()
    {
        byte[] content = "hello"u8.ToArray();
        var source = new MemorySource(content);
        await using var host = await DesktopHost.StartAsync().ConfigureAwait(false);
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            ContentHandler = source.ToDesktopContentHandler(),
        }).ConfigureAwait(false);
        using var client = new HttpClient();

        using HttpResponseMessage full = await client.GetAsync(surface.Url).ConfigureAwait(false);
        Equal(HttpStatusCode.OK, full.StatusCode);
        Equal("hello", await full.Content.ReadAsStringAsync().ConfigureAwait(false));
        Equal(source.Manifest.EntryPoint.EntityTag, full.Headers.ETag!.Tag);
        Equal("no-cache", full.Headers.CacheControl!.ToString());

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, surface.Url);
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 3);
        using HttpResponseMessage range = await client.SendAsync(rangeRequest).ConfigureAwait(false);
        Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Equal("ell", await range.Content.ReadAsStringAsync().ConfigureAwait(false));
        Equal("bytes 1-3/5", range.Content.Headers.ContentRange!.ToString());

        using var cachedRequest = new HttpRequestMessage(HttpMethod.Get, surface.Url);
        cachedRequest.Headers.TryAddWithoutValidation("If-None-Match", source.Manifest.EntryPoint.EntityTag);
        using HttpResponseMessage cached = await client.SendAsync(cachedRequest).ConfigureAwait(false);
        Equal(HttpStatusCode.NotModified, cached.StatusCode);

        using var head = new HttpRequestMessage(HttpMethod.Head, surface.Url);
        using HttpResponseMessage headResponse = await client.SendAsync(head).ConfigureAwait(false);
        Equal(HttpStatusCode.OK, headResponse.StatusCode);
        Equal(5L, headResponse.Content.Headers.ContentLength);

        // Only bodies that Desktop opens and owns acquire a snapshot.
        Equal(2, source.Opened);
        Equal(source.Opened, source.Disposed);

        source.ReplaceContent = true;
        using HttpResponseMessage replaced = await client.GetAsync(surface.Url).ConfigureAwait(false);
        Equal(HttpStatusCode.InternalServerError, replaced.StatusCode);
        Equal(source.Opened, source.Disposed);
    }

    private static async Task RoutingParity()
    {
        var source = new RoutingSource(
            ("index.html", "entry", true),
            ("app.js", "script", false),
            ("assets/logo.svg", "logo", false),
            ("docs/guide", "guide", false));
        var exact = new AssetRoutingOptions
        {
            ServeEntryPointAtRoot = false,
            EnableSinglePageApplicationFallback = false,
        };
        var noFallback = new AssetRoutingOptions { EnableSinglePageApplicationFallback = false };

        // Expected body, or null for 404, per routing policy: default, no fallback, exact.
        (string Path, string? Default, string? NoFallback, string? Exact)[] cases =
        [
            ("/", "entry", "entry", null),
            ("/index.html", "entry", "entry", "entry"),
            ("/app.js", "script", "script", "script"),
            ("/assets/logo.svg", "logo", "logo", "logo"),
            ("/docs/guide", "guide", "guide", "guide"),
            ("/settings", "entry", null, null),
            ("/settings/profile", "entry", null, null),
            ("/missing.js", null, null, null),
            ("/assets/missing.css", null, null, null),
            ("/Index.html", null, null, null),
            ("/settings/", null, null, null),
            ("/bad:name", null, null, null),
        ];

        foreach ((AssetRoutingOptions? routing, int column) in new (AssetRoutingOptions?, int)[]
        {
            (null, 0), (noFallback, 1), (exact, 2),
        })
        {
            await using var desktopHost = await DesktopHost.StartAsync().ConfigureAwait(false);
            await using var surface = await desktopHost.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                ContentHandler = source.ToDesktopContentHandler(routing),
            }).ConfigureAwait(false);

            foreach (string prefix in new[] { "", "ui" })
            {
                await using WebApplication web = await StartAspNetCoreAsync(source, prefix, routing).ConfigureAwait(false);
                var aspNetBase = new Uri(web.Urls.Single() + "/" + (prefix.Length == 0 ? "" : prefix + "/"));
                using var client = new HttpClient();
                foreach (var testCase in cases)
                {
                    string? expected = column switch { 0 => testCase.Default, 1 => testCase.NoFallback, _ => testCase.Exact };
                    (HttpStatusCode Status, string Body) desktop = await GetAsync(client, surface.Url, testCase.Path).ConfigureAwait(false);
                    (HttpStatusCode Status, string Body) aspNet = await GetAsync(client, aspNetBase, testCase.Path).ConfigureAwait(false);
                    string label = $"{testCase.Path} (policy {column}, prefix '{prefix}')";
                    Equal(expected is null ? HttpStatusCode.NotFound : HttpStatusCode.OK, desktop.Status, $"Desktop {label}");
                    Equal(desktop.Status, aspNet.Status, $"ASP.NET Core {label}");
                    if (expected is not null)
                    {
                        Equal(expected, desktop.Body, $"Desktop {label}");
                        Equal(expected, aspNet.Body, $"ASP.NET Core {label}");
                    }
                }
            }
        }
    }

    private static async Task<WebApplication> StartAspNetCoreAsync(
        IAssetSnapshotSource source,
        string prefix,
        AssetRoutingOptions? routing)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        app.MapRunicAssetSource(source, prefix, routing);
        await app.StartAsync().ConfigureAwait(false);
        return app;
    }

    private static async Task<(HttpStatusCode, string)> GetAsync(HttpClient client, Uri baseUrl, string path)
    {
        var url = new Uri(baseUrl.AbsoluteUri + path.TrimStart('/'));
        using HttpResponseMessage response = await client.GetAsync(url).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static void Equal<T>(T expected, T actual, string? context = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}{(context is null ? "" : ": ")}Expected {expected}, received {actual}.");
    }
}

internal sealed class RoutingSource : IAssetSnapshotSource
{
    private readonly Dictionary<string, byte[]> _contents = new(StringComparer.Ordinal);

    internal RoutingSource(params (string Path, string Content, bool EntryPoint)[] assets)
    {
        var descriptors = new List<AssetDescriptor>();
        foreach ((string path, string text, bool entryPoint) in assets)
        {
            byte[] content = Encoding.UTF8.GetBytes(text);
            _contents.Add(path, content);
            descriptors.Add(new AssetDescriptor(
                path,
                AssetMediaTypes.Resolve(path),
                content.Length,
                Convert.ToHexStringLower(SHA256.HashData(content)),
                entryPoint));
        }

        Manifest = new AssetManifest(descriptors);
    }

    public AssetManifest Manifest { get; }
    public ValueTask ValidateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<Stream>(new MemoryStream(_contents[relativePath], writable: false));
    public ValueTask<AssetReadSnapshot> OpenSnapshotAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        Manifest.TryGetAsset(relativePath, out AssetDescriptor? descriptor);
        return ValueTask.FromResult(new AssetReadSnapshot(
            descriptor ?? throw new FileNotFoundException(relativePath),
            new MemoryStream(_contents[descriptor.RelativePath], writable: false)));
    }
}

internal sealed class MemorySource : IAssetSnapshotSource
{
    private readonly byte[] _content;

    internal MemorySource(byte[] content)
    {
        _content = content;
        string digest = Convert.ToHexStringLower(SHA256.HashData(content));
        Manifest = new AssetManifest([
            new AssetDescriptor("index.html", "text/html; charset=utf-8", content.Length, digest, isEntryPoint: true),
        ]);
    }

    public AssetManifest Manifest { get; }
    internal int Opened;
    internal int Disposed;
    internal bool ReplaceContent { get; set; }
    public ValueTask ValidateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<Stream>(new MemoryStream(_content, writable: false));
    public ValueTask<AssetReadSnapshot> OpenSnapshotAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Opened);
        byte[] content = ReplaceContent ? "other"u8.ToArray() : _content;
        AssetDescriptor descriptor = ReplaceContent
            ? new AssetDescriptor("index.html", "text/html; charset=utf-8", content.Length,
                Convert.ToHexStringLower(SHA256.HashData(content)), isEntryPoint: true)
            : Manifest.EntryPoint;
        return ValueTask.FromResult(new AssetReadSnapshot(descriptor, new TrackedStream(content, this)));
    }

    private sealed class TrackedStream(byte[] content, MemorySource owner) : MemoryStream(content, writable: false)
    {
        private int _disposed;

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Increment(ref owner.Disposed);
            base.Dispose(disposing);
        }
    }
}
