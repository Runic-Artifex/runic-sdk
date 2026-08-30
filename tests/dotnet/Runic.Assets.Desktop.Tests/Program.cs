using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Runic.Desktop;
using Runic.Assets.Desktop;

namespace Runic.Assets.Desktop.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
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

            Console.WriteLine("ok - Desktop delivery preserves asset streams, cache metadata, ranges, and HEAD");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"not ok - Runic Assets Desktop integration\n{exception}");
            return 1;
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, received {actual}.");
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
    public ValueTask ValidateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<Stream>(new MemoryStream(_content, writable: false));
    public ValueTask<AssetReadSnapshot> OpenSnapshotAsync(
        string relativePath,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new AssetReadSnapshot(Manifest.EntryPoint, new MemoryStream(_content, writable: false)));
}
