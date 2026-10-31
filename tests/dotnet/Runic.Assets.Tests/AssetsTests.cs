using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Assets.AspNetCore;
using TUnit.Core;

namespace Runic.Assets.Tests;

// Linux only: the directory archive compiler and the packer need no-follow directory handles. The development-directory
// source runs on every operating system in DevelopmentDirectoryTests.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class AssetsTests
{
    [Test, DisplayName("paths reject traversal and ambiguous syntax")]
    public Task SafePaths()
    {
        Equal("assets/app.js", AssetPath.Normalize(@"assets\app.js"));
        foreach (string hostile in HostilePaths)
        {
            Throws<ArgumentException>(() => AssetPath.Normalize(hostile));
        }

        return Task.CompletedTask;
    }

    [Test, DisplayName("one source interface opens snapshots; no Runic.Assets API is experimental")]
    public void PublicShape()
    {
        Assembly assets = typeof(IAssetSource).Assembly;
        SequenceEqual(
            new[] { "OpenAsync", "ValidateAsync", "get_Manifest" },
            typeof(IAssetSource).GetMethods().Select(static method => method.Name).Order(StringComparer.Ordinal));
        Equal(0, typeof(IAssetSource).GetInterfaces().Length);
        Equal(typeof(System.Threading.Tasks.ValueTask<AssetReadSnapshot>), typeof(IAssetSource).GetMethod("OpenAsync")!.ReturnType);
        True(assets.GetType("Runic.Assets.IAssetSnapshotSource") is null && assets.GetType("Runic.Assets.IAssetManifestProvider") is null);
        foreach (Type source in new[] { typeof(AssetArchiveSource), typeof(EmbeddedAssetSource), typeof(DevelopmentDirectoryAssetSource) })
            True(source.GetMethod("OpenReadAsync") is null && source.GetMethod("OpenSnapshotAsync") is null);
        True(typeof(AssetArchiveInspection).GetMethod("ToDeterministicReport") is null);

        // RUNICASSETS001 graduated in 0.10.0.
        True(assets.GetExportedTypes().All(static type =>
            type.GetCustomAttribute<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>() is null
            && type.GetMembers().All(static member =>
                member.GetCustomAttribute<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>() is null)));
    }

    [Test, DisplayName("lookups return false for invalid paths")]
    public Task InvalidLookups()
    {
        AssetManifest manifest = NewRoutingManifest();
        foreach (string hostile in HostilePaths)
        {
            True(!AssetPath.TryNormalize(hostile, out string? normalized));
            True(normalized is null);
            True(!manifest.TryGetAsset(hostile, out AssetDescriptor? asset));
            True(asset is null);
            True(hostile.Length == 0 || !manifest.TryResolveRequestPath("/" + hostile, null, out _));
        }

        True(!AssetPath.TryNormalize(null, out _));
        True(AssetPath.TryNormalize(@"assets\app.js", out string? canonical));
        Equal("assets/app.js", canonical);
        True(manifest.TryGetAsset(@"assets\app.js", out AssetDescriptor? found));
        Equal("assets/app.js", found!.RelativePath);
        True(!manifest.TryGetAsset("Assets/app.js", out _));
        Throws<ArgumentNullException>(() => manifest.TryGetAsset(null!, out _));
        return Task.CompletedTask;
    }

    [Test, DisplayName("request routing serves the root and SPA fallback by policy")]
    public Task RequestRouting()
    {
        AssetManifest manifest = NewRoutingManifest();
        var exact = new AssetRoutingOptions { ServeEntryPointAtRoot = false, EnableSinglePageApplicationFallback = false };
        string? Resolve(string? path, AssetRoutingOptions? options) =>
            manifest.TryResolveRequestPath(path, options, out AssetDescriptor? asset) ? asset.RelativePath : null;

        foreach (string? root in new[] { null, "", "/" })
        {
            Equal("index.html", Resolve(root, null));
            Equal(null, Resolve(root, exact));
        }

        Equal("assets/app.js", Resolve("/assets/app.js", null));
        Equal("assets/app.js", Resolve("assets/app.js", exact));
        Equal("index.html", Resolve("/settings/profile", null));
        Equal(null, Resolve("/settings/profile", exact));
        Equal(null, Resolve("/settings/profile", new AssetRoutingOptions { EnableSinglePageApplicationFallback = false }));
        Equal(null, Resolve("/missing.js", null));
        Equal(null, Resolve("/settings/", null));
        Equal(null, Resolve("//index.html", null));
        Equal(null, Resolve("/../index.html", null));
        return Task.CompletedTask;
    }

    private static readonly string[] HostilePaths =
    [
        "", " index.html", "/index.html", @"C:\index.html", "../index.html",
        "assets/../index.html", "./index.html", "assets//app.js", "assets/%2e%2e/app.js",
        "app.js?x=1", "app.js#fragment", "assets/na:me.js", "assets/\0.js",
    ];

    private static AssetManifest NewRoutingManifest() => new(
    [
        new AssetDescriptor("index.html", "text/html", 1, new string('a', 64), isEntryPoint: true),
        new AssetDescriptor("assets/app.js", "text/javascript", 1, new string('b', 64)),
    ]);

    [Test, DisplayName("manifest metadata and ordering are deterministic")]
    public Task DeterministicManifest()
    {
        var first = new AssetDescriptor(
            "z.js",
            "text/javascript",
            1,
            new string('A', 64),
            cacheMode: AssetCacheMode.Immutable);
        var entry = new AssetDescriptor(
            "index.html",
            "text/html",
            2,
            new string('b', 64),
            isEntryPoint: true);
        var manifest = new AssetManifest([first, entry]);

        SequenceEqual(new[] { "index.html", "z.js" }, manifest.Assets.Select(static asset => asset.RelativePath));
        Equal("index.html", manifest.EntryPoint.RelativePath);
        Equal("\"sha256-" + new string('a', 64) + "\"", first.EntityTag);
        Equal("public, max-age=31536000, immutable", first.CacheControl);
        Equal("sha256-" + Convert.ToBase64String(Convert.FromHexString(first.Sha256)), first.SubresourceIntegrity);
        Equal("image/svg+xml", AssetMediaTypes.Resolve("images/logo.svg"));
        Equal("application/octet-stream", AssetMediaTypes.Resolve("data/custom"));
        Throws<ArgumentException>(() => _ = new AssetManifest([entry, entry]));
        return Task.CompletedTask;
    }

    [Test, DisplayName("embedded assets validate and open offline")]
    public async Task EmbeddedAssets()
    {
        var source = NewEmbeddedSource();
        var repeatedSource = NewEmbeddedSource();
        await source.ValidateAsync().ConfigureAwait(false);
        SequenceEqual(
            new[] { "assets/app.css", "index.html" },
            source.Manifest.Assets.Select(static asset => asset.RelativePath));
        Equal("text/html", source.Manifest.EntryPoint.MediaType);
        Equal(AssetCacheMode.Revalidate, source.Manifest.EntryPoint.CacheMode);
        SequenceEqual(
            source.Manifest.Assets.Select(static asset => (asset.RelativePath, asset.Sha256)),
            repeatedSource.Manifest.Assets.Select(static asset => (asset.RelativePath, asset.Sha256)));

        await using Stream content = (await source.OpenAsync("index.html").ConfigureAwait(false)).Content;
        using var reader = new StreamReader(content, Encoding.UTF8);
        True((await reader.ReadToEndAsync().ConfigureAwait(false)).Contains("Asset boundary", StringComparison.Ordinal));
        await ThrowsAsync<FileNotFoundException>(
            async () => await source.OpenAsync("missing.txt").ConfigureAwait(false)).ConfigureAwait(false);
    }

    // #730: one folder of an archive with several frontends is served as a frontend of its own.
    [Test, DisplayName("a folder of a source presents itself as its own frontend")]
    public async Task SubdirectoryView()
    {
        var source = NewEmbeddedSource();
        using var stream = new MemoryStream();
        await AssetArchive.WriteAsync(source, stream).ConfigureAwait(false);
        stream.Position = 0;
        AssetArchiveSource archive = AssetArchive.Read(stream);

        IAssetSource view = archive.Subdirectory("assets/", entryPoint: "app.css");
        AssetDescriptor original = archive.Manifest.Assets.Single(static asset => asset.RelativePath == "assets/app.css");
        AssetDescriptor entry = Assert(view.Manifest.Assets);
        Equal("app.css", entry.RelativePath);
        True(entry.IsEntryPoint);
        Equal(original.Sha256, entry.Sha256);
        Equal(original.MediaType, entry.MediaType);
        Equal(AssetCacheMode.Immutable, entry.CacheMode);
        True(view.Manifest.TryResolveRequestPath("/", null, out AssetDescriptor? root) && root.RelativePath == "app.css");
        await view.ValidateAsync().ConfigureAwait(false);

        await using (AssetReadSnapshot snapshot = await view.OpenAsync("app.css").ConfigureAwait(false))
        await using (AssetReadSnapshot full = await archive.OpenAsync("assets/app.css").ConfigureAwait(false))
        {
            Equal(entry, snapshot.Descriptor);
            using var expected = new MemoryStream();
            using var actual = new MemoryStream();
            await full.Content.CopyToAsync(expected).ConfigureAwait(false);
            await snapshot.Content.CopyToAsync(actual).ConfigureAwait(false);
            SequenceEqual(expected.ToArray(), actual.ToArray());
        }

        // The view serves only the folder.
        await ThrowsAsync<FileNotFoundException>(
            async () => await view.OpenAsync("index.html").ConfigureAwait(false)).ConfigureAwait(false);
        await ThrowsAsync<FileNotFoundException>(
            async () => await view.OpenAsync("assets/app.css").ConfigureAwait(false)).ConfigureAwait(false);
        // A folder without its entry point, an empty folder and an unsafe path fail when the view is created.
        Throws<FileNotFoundException>(() => archive.Subdirectory("assets"));
        Throws<DirectoryNotFoundException>(() => archive.Subdirectory("missing"));
        Throws<DirectoryNotFoundException>(() => archive.Subdirectory("index.html"));
        Throws<ArgumentException>(() => archive.Subdirectory("../assets"));
        Throws<ArgumentException>(() => archive.Subdirectory("assets", "../index.html"));
    }

    [Test, DisplayName("a folder view refuses content that changed after it was created")]
    public async Task SubdirectoryViewRefusesChangedContent()
    {
        var manifest = new AssetManifest([
            new AssetDescriptor("index.html", "text/html", 1, new string('0', 64), isEntryPoint: true),
            new AssetDescriptor("settings/index.html", "text/html", 1, new string('1', 64)),
        ]);
        var changing = new ChangingAssetSource(manifest);
        IAssetSource view = changing.Subdirectory("settings");
        await ThrowsAsync<InvalidDataException>(
            async () => await view.OpenAsync("index.html").ConfigureAwait(false)).ConfigureAwait(false);
        True(changing.Disposed);
    }

    // S1 (D053.25): the build packed and verified this archive; ReadEmbedded reads the directory and inflates on open.
    [Test, DisplayName("an embedded archive opens each asset on first use and serves folder views")]
    public async Task EmbeddedArchive()
    {
        AssetArchiveSource archive = AssetArchive.ReadEmbedded(typeof(AssetsTests).Assembly, "Runic.Assets.Tests.Archive");
        SequenceEqual(
            new[] { "index.html", "settings/app.js", "settings/index.html" },
            archive.Manifest.Assets.Select(static asset => asset.RelativePath));
        string root = Path.Combine(AppContext.BaseDirectory, "../../../TestAssets/archive");
        foreach (AssetDescriptor asset in archive.Manifest.Assets)
        {
            SequenceEqual(File.ReadAllBytes(Path.Combine(root, asset.RelativePath)), await ReadAllAsync(archive, asset.RelativePath).ConfigureAwait(false));
        }

        await archive.ValidateAsync().ConfigureAwait(false);
        IAssetSource settings = archive.Subdirectory("settings");
        Equal("index.html", settings.Manifest.EntryPoint.RelativePath);
        SequenceEqual(File.ReadAllBytes(Path.Combine(root, "settings/app.js")), await ReadAllAsync(settings, "app.js").ConfigureAwait(false));
        await settings.ValidateAsync().ConfigureAwait(false);
        Throws<ArgumentException>(() => AssetArchive.ReadEmbedded(typeof(AssetsTests).Assembly, "Runic.Assets.Tests.Missing"));
    }

    [Test, DisplayName("an archive read in place checks digests on demand and damaged entries when opened")]
    public async Task ArchiveReadInPlace()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "<!doctype html><p>entry</p>");
        string image = directory.Write("image.png", "not really a png, so stored rather than deflated");
        using var written = new MemoryStream();
        await AssetArchive.WriteDirectoryAsync(directory.Path, written).ConfigureAwait(false);
        byte[] bytes = written.ToArray();

        // Concurrent first opens inflate once each and agree.
        AssetArchiveSource intact = AssetArchive.ReadInPlace(new MemoryStream(bytes, writable: false), null);
        byte[][] reads = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            Task.Run(() => ReadAllAsync(intact, index % 2 == 0 ? "index.html" : "image.png")))).ConfigureAwait(false);
        for (int index = 0; index < reads.Length; index++)
        {
            SequenceEqual(index % 2 == 0 ? Encoding.UTF8.GetBytes("<!doctype html><p>entry</p>") : File.ReadAllBytes(image), reads[index]);
        }

        // Same length, other content: an archive from outside the assembly fails to load; read in place, the digest
        // is checked by ValidateAsync only.
        byte[] tampered = (byte[])bytes.Clone();
        int stored = tampered.AsSpan().IndexOf("not really"u8);
        tampered[stored] = (byte)'N';
        Throws<InvalidDataException>(() => AssetArchive.Read(new MemoryStream(tampered, writable: false)));
        AssetArchiveSource inPlace = AssetArchive.ReadInPlace(new MemoryStream(tampered, writable: false), null);
        Equal((byte)'N', (await ReadAllAsync(inPlace, "image.png").ConfigureAwait(false))[0]);
        await ThrowsAsync<InvalidDataException>(async () => await inPlace.ValidateAsync().ConfigureAwait(false)).ConfigureAwait(false);

        // A damaged deflate stream loads, and fails when the asset is opened.
        byte[] damaged = (byte[])bytes.Clone();
        int name = damaged.AsSpan().IndexOf("assets/index.html"u8);
        int extra = BitConverter.ToUInt16(damaged, name - 2);
        damaged[name + "assets/index.html".Length + extra] = 0xFF;
        AssetArchiveSource broken = AssetArchive.ReadInPlace(new MemoryStream(damaged, writable: false), null);
        await ThrowsAsync<InvalidDataException>(async () => await ReadAllAsync(broken, "index.html").ConfigureAwait(false)).ConfigureAwait(false);
        SequenceEqual(File.ReadAllBytes(image), await ReadAllAsync(broken, "image.png").ConfigureAwait(false));

        // The directory and the limits are checked when the archive is read.
        Throws<InvalidDataException>(() => AssetArchive.ReadInPlace(new MemoryStream(bytes, writable: false), new AssetArchiveReadOptions { MaxArchiveBytes = 16 }));
        Throws<InvalidDataException>(() => AssetArchive.ReadInPlace(new MemoryStream(bytes.AsSpan(0, bytes.Length - 8).ToArray(), writable: false), null));
    }

    private static async Task<byte[]> ReadAllAsync(IAssetSource source, string path)
    {
        await using AssetReadSnapshot snapshot = await source.OpenAsync(path).ConfigureAwait(false);
        using var copy = new MemoryStream();
        await snapshot.Content.CopyToAsync(copy).ConfigureAwait(false);
        return copy.ToArray();
    }

    [Test, DisplayName("portable archives round-trip deterministic metadata")]
    public async Task ArchiveRoundTrip()
    {
        var source = NewEmbeddedSource();
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        await AssetArchive.WriteAsync(source, first).ConfigureAwait(false);
        await AssetArchive.WriteAsync(source, second).ConfigureAwait(false);
        SequenceEqual(first.ToArray(), second.ToArray());

        first.Position = 0;
        AssetArchiveSource restored = AssetArchive.Read(first);
        await restored.ValidateAsync().ConfigureAwait(false);
        SequenceEqual(
            source.Manifest.Assets.Select(static asset =>
                (asset.RelativePath, asset.MediaType, asset.Length, asset.Sha256, asset.CacheMode)),
            restored.Manifest.Assets.Select(static asset =>
                (asset.RelativePath, asset.MediaType, asset.Length, asset.Sha256, asset.CacheMode)));

        second.Position = 0;
        Throws<InvalidDataException>(() => AssetArchive.Read(
            second,
            new AssetArchiveReadOptions { MaxArchiveBytes = 1 }));
    }

    [Test, DisplayName("directory compiler uses the canonical archive authority")]
    public async Task DirectoryArchiveAuthority()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "entry");
        directory.Write("assets/app.js", "console.log('runic');");
        directory.Write("excluded.txt", "omit");
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        await AssetArchive.WriteDirectoryAsync(
            directory.Path,
            first,
            excludedPaths: ["excluded.txt"]).ConfigureAwait(false);
        await AssetArchive.WriteDirectoryAsync(
            directory.Path,
            second,
            excludedPaths: ["excluded.txt"]).ConfigureAwait(false);
        SequenceEqual(first.ToArray(), second.ToArray());

        first.Position = 0;
        AssetArchiveSource source = AssetArchive.Read(first);
        Equal(2, source.Manifest.Assets.Count);
        Equal(AssetCacheMode.Revalidate, source.Manifest.EntryPoint.CacheMode);
        Equal(AssetCacheMode.Revalidate, source.Manifest.Assets[0].CacheMode);
        True(!source.Manifest.TryGetAsset("excluded.txt", out _));
    }

    [Test, DisplayName("directory archives cache only content-hashed names immutably")]
    public async Task DirectoryArchiveCacheModes()
    {
        (string Path, AssetCacheMode Expected)[] cases =
        [
            ("index.html", AssetCacheMode.Revalidate),
            ("legacy.htm", AssetCacheMode.Revalidate),
            ("favicon.ico", AssetCacheMode.Revalidate),
            ("robots.txt", AssetCacheMode.Revalidate),
            ("manifest.webmanifest", AssetCacheMode.Revalidate),
            ("sw.js", AssetCacheMode.Revalidate),
            ("config.json", AssetCacheMode.Revalidate),
            ("assets/app.js", AssetCacheMode.Revalidate),
            ("fonts/Roboto-SemiBold.woff2", AssetCacheMode.Revalidate),
            ("release-20240101.json", AssetCacheMode.Revalidate),
            ("assets/index-Cf3tzbYH.js", AssetCacheMode.Immutable),
            ("assets/index-B-x_3kQz.css", AssetCacheMode.Immutable),
            ("assets/index-Cf3tzbYH.js.map", AssetCacheMode.Immutable),
            ("chunk-5INURT2O.js", AssetCacheMode.Immutable),
            ("_app/immutable/entry/start.D2kX0q3e.js", AssetCacheMode.Immutable),
            ("main.0123456789abcdef0123.js", AssetCacheMode.Immutable),
            ("hashed-page-Cf3tzbYH.html", AssetCacheMode.Revalidate),
        ];
        using var directory = new TemporaryDirectory();
        foreach ((string path, _) in cases)
        {
            directory.Write(path, path);
        }

        using var archive = new MemoryStream();
        await AssetArchive.WriteDirectoryAsync(directory.Path, archive).ConfigureAwait(false);
        archive.Position = 0;
        AssetArchiveSource source = AssetArchive.Read(archive);
        foreach ((string path, AssetCacheMode expected) in cases)
        {
            True(source.Manifest.TryGetAsset(path, out AssetDescriptor? descriptor));
            Equal((path, expected), (path, descriptor!.CacheMode));
        }
    }

    [Test, DisplayName("archive writes reject content mutation after validation")]
    public async Task ArchiveWriteMutation()
    {
        var source = new MutationAfterValidationSource("first", "other");
        using var archive = new MemoryStream();
        await ThrowsAsync<InvalidDataException>(
            async () => await AssetArchive.WriteAsync(source, archive).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [Test, DisplayName("archive inspection is deterministic")]
    public async Task ArchiveInspection()
    {
        using var archive = new MemoryStream();
        await AssetArchive.WriteAsync(NewEmbeddedSource(), archive).ConfigureAwait(false);

        archive.Position = 0;
        AssetArchiveInspection first = AssetArchive.Inspect(archive);
        Equal(AssetArchive.CurrentVersion, first.ArchiveVersion);
        Contains("asset=assets/app.css|text/css|", first.ToDeterministicReport());
    }

    [Test, DisplayName("archive inspection rejects incompatible schema versions")]
    public async Task ArchiveUnsupportedVersion()
    {
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry manifest = zip.CreateEntry("runic-assets.json", CompressionLevel.SmallestSize);
            await using Stream content = manifest.Open();
            await content.WriteAsync(Encoding.UTF8.GetBytes("{\"version\":\"runic.assets.archive/2\",\"assets\":[]}"))
                .ConfigureAwait(false);
        }

        archive.Position = 0;
        Throws<InvalidDataException>(() => AssetArchive.Inspect(archive));
    }

    [Test, DisplayName("archive manifest parsing is decompression bounded")]
    public async Task ArchiveManifestBound()
    {
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry manifest = zip.CreateEntry("runic-assets.json", CompressionLevel.SmallestSize);
            await using Stream content = manifest.Open();
            byte[] oversized = Encoding.UTF8.GetBytes("{" + new string(' ', 8_192) + "}");
            await content.WriteAsync(oversized).ConfigureAwait(false);
        }

        var options = new AssetArchiveReadOptions { MaxManifestBytes = 32 };
        archive.Position = 0;
        Throws<InvalidDataException>(() => AssetArchive.Inspect(archive, options));
    }

    [Test, DisplayName("ASP.NET Core integration preserves response metadata")]
    public async Task AspNetCoreIntegration()
    {
        var source = NewEmbeddedSource();
        AssetDescriptor descriptor = source.Manifest.EntryPoint;
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await context.WriteAssetAsync(source, descriptor).ConfigureAwait(false);

        Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Equal("text/html; charset=utf-8", context.Response.ContentType);
        Equal(descriptor.Length, context.Response.ContentLength);

        var head = new DefaultHttpContext();
        head.Request.Method = HttpMethods.Head;
        head.Response.Body = new MemoryStream();
        await RunicAssetEndpointExtensions
            .WriteAssetAsync(head, source, descriptor)
            .ConfigureAwait(false);
        Equal(StatusCodes.Status200OK, head.Response.StatusCode);
        Equal(descriptor.Length, head.Response.ContentLength);
        Equal(descriptor.EntityTag, head.Response.Headers.ETag.ToString());
        Equal(0L, head.Response.Body.Length);
        Equal(descriptor.EntityTag, context.Response.Headers.ETag.ToString());
        Equal(descriptor.CacheControl, context.Response.Headers.CacheControl.ToString());
        Equal(descriptor.Length, context.Response.Body.Length);

        var conditional = new DefaultHttpContext();
        conditional.Request.Headers.IfNoneMatch = "W/" + descriptor.EntityTag + ", \"other\"";
        await RunicAssetEndpointExtensions
            .WriteAssetAsync(conditional, source, descriptor)
            .ConfigureAwait(false);
        Equal(StatusCodes.Status304NotModified, conditional.Response.StatusCode);

        var wildcard = new DefaultHttpContext();
        wildcard.Request.Headers.IfNoneMatch = "*";
        await RunicAssetEndpointExtensions
            .WriteAssetAsync(wildcard, source, descriptor)
            .ConfigureAwait(false);
        Equal(StatusCodes.Status304NotModified, wildcard.Response.StatusCode);

        byte[] full = ((MemoryStream)context.Response.Body).ToArray();
        var range = new DefaultHttpContext();
        range.Response.Body = new MemoryStream();
        range.Request.Headers.Range = "bytes=1-3";
        await RunicAssetEndpointExtensions
            .WriteAssetAsync(range, source, descriptor)
            .ConfigureAwait(false);
        Equal(StatusCodes.Status206PartialContent, range.Response.StatusCode);
        Equal("bytes 1-3/" + descriptor.Length, range.Response.Headers.ContentRange.ToString());
        SequenceEqual(full[1..4], ((MemoryStream)range.Response.Body).ToArray());

        var fullRange = new DefaultHttpContext();
        fullRange.Response.Body = new MemoryStream();
        fullRange.Request.Headers.Range = "bytes=0-" + (descriptor.Length - 1);
        await RunicAssetEndpointExtensions
            .WriteAssetAsync(fullRange, source, descriptor)
            .ConfigureAwait(false);
        Equal(StatusCodes.Status206PartialContent, fullRange.Response.StatusCode);
        SequenceEqual(full, ((MemoryStream)fullRange.Response.Body).ToArray());

        var staleIfRange = new DefaultHttpContext();
        staleIfRange.Response.Body = new MemoryStream();
        staleIfRange.Request.Headers.Range = "bytes=1-3";
        staleIfRange.Request.Headers.IfRange = "\"stale\"";
        await RunicAssetEndpointExtensions
            .WriteAssetAsync(staleIfRange, source, descriptor)
            .ConfigureAwait(false);
        Equal(StatusCodes.Status200OK, staleIfRange.Response.StatusCode);
        SequenceEqual(full, ((MemoryStream)staleIfRange.Response.Body).ToArray());

        var unsatisfiable = new DefaultHttpContext();
        unsatisfiable.Request.Headers.Range = "bytes=999-";
        await RunicAssetEndpointExtensions
            .WriteAssetAsync(unsatisfiable, source, descriptor)
            .ConfigureAwait(false);
        Equal(StatusCodes.Status416RangeNotSatisfiable, unsatisfiable.Response.StatusCode);

        var unknownUnit = new DefaultHttpContext();
        unknownUnit.Response.Body = new MemoryStream();
        unknownUnit.Request.Headers.Range = "items=0-1";
        await RunicAssetEndpointExtensions
            .WriteAssetAsync(unknownUnit, source, descriptor)
            .ConfigureAwait(false);
        Equal(StatusCodes.Status200OK, unknownUnit.Response.StatusCode);
        SequenceEqual(full, ((MemoryStream)unknownUnit.Response.Body).ToArray());

        var multiRange = new DefaultHttpContext();
        multiRange.Response.Body = new MemoryStream();
        multiRange.Request.Headers.Range = "bytes=0-0,2-2";
        await RunicAssetEndpointExtensions
            .WriteAssetAsync(multiRange, source, descriptor)
            .ConfigureAwait(false);
        Equal(StatusCodes.Status200OK, multiRange.Response.StatusCode);
        SequenceEqual(full, ((MemoryStream)multiRange.Response.Body).ToArray());
    }

    [Test, DisplayName("ASP.NET Core assets coexist with app endpoints, controllers, static files and fallbacks")]
    public async Task AspNetCoreCoexistence()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("wwwroot/site.css", "static");
        var source = NewEmbeddedSource();
        var exact = new AssetRoutingOptions { ServeEntryPointAtRoot = false, EnableSinglePageApplicationFallback = false };
        foreach (AssetRoutingOptions? routing in new[] { null, exact })
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = directory.Path,
                WebRootPath = System.IO.Path.Combine(directory.Path, "wwwroot"),
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddControllers().AddApplicationPart(typeof(CoexistenceController).Assembly);
            await using WebApplication app = builder.Build();
            app.UseRouting();
            app.UseStaticFiles();
            app.MapRunicAssetSource(source, routing: routing);
            app.MapGet("/api/items", () => "api-get");
            app.MapPost("/api/items", () => "api-post");
            app.MapControllerRoute("conventional", "{controller}/{action}");
            app.MapFallback(() => "fallback");
            await app.StartAsync().ConfigureAwait(false);

            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            async Task<string> Send(HttpMethod method, string path)
            {
                using HttpResponseMessage response = await client
                    .SendAsync(new HttpRequestMessage(method, path))
                    .ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return $"{(int)response.StatusCode} {(body.StartsWith("<!doctype", StringComparison.Ordinal) ? "entry" : body)}";
            }

            Equal("200 api-get", await Send(HttpMethod.Get, "/api/items").ConfigureAwait(false));
            Equal("200 api-post", await Send(HttpMethod.Post, "/api/items").ConfigureAwait(false));
            Equal("200 controller", await Send(HttpMethod.Get, "/coexistence/hello").ConfigureAwait(false));
            Equal("200 static", await Send(HttpMethod.Get, "/site.css").ConfigureAwait(false));
            // MapFallback skips file-like paths, so nothing claims an unknown file.
            Equal("404 ", await Send(HttpMethod.Get, "/missing.txt").ConfigureAwait(false));
            Equal("200 fallback", await Send(HttpMethod.Post, "/settings").ConfigureAwait(false));
            Equal("200 entry", await Send(HttpMethod.Get, "/index.html").ConfigureAwait(false));
            Equal("200 body { color: rebeccapurple; }", (await Send(HttpMethod.Get, "/assets/app.css").ConfigureAwait(false)).Trim());
            string spa = routing is null ? "200 entry" : "200 fallback";
            Equal(spa, await Send(HttpMethod.Get, "/").ConfigureAwait(false));
            Equal(spa, await Send(HttpMethod.Get, "/settings").ConfigureAwait(false));
            await app.StopAsync().ConfigureAwait(false);
        }
    }

    [Test, DisplayName("sources honor cancellation")]
    public async Task Cancellation()
    {
        var source = NewEmbeddedSource();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(
            async () => await source.ValidateAsync(cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        await ThrowsAsync<OperationCanceledException>(
            async () => await source.OpenAsync("index.html", cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Test, DisplayName("shipping assembly stays framework neutral")]
    public Task FrameworkNeutral()
    {
        Assembly assembly = typeof(AssetManifest).Assembly;
        string[] references = assembly.GetReferencedAssemblies().Select(static reference => reference.Name ?? "").ToArray();
        True(!references.Any(static name =>
            name.StartsWith("Runic.Application", StringComparison.Ordinal)
            || name.Contains("CsWebUi", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase)));
        return Task.CompletedTask;
    }

    private static EmbeddedAssetSource NewEmbeddedSource() =>
        new(
            typeof(AssetsTests).Assembly,
            [
                new("index.html", "Runic.Assets.Tests.index.html", IsEntryPoint: true),
                new(
                    "assets/app.css",
                    "Runic.Assets.Tests.app.css",
                    CacheMode: AssetCacheMode.Immutable),
            ]);

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Assertion failed.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}' but found '{actual}'.");
        }
    }

    private static void StartsWith(string expected, string actual)
    {
        if (!actual.StartsWith(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected response to start with '{expected}'.");
        }
    }

    private static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected response to contain '{expected}'.");
        }
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException("Sequences differ.");
        }
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

    private static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }


    private static T Assert<T>(IReadOnlyList<T> items) =>
        items.Count == 1 ? items[0] : throw new InvalidOperationException($"Expected one item, found {items.Count}.");

    // Opens every asset with other content than its manifest declares, as a live source does after a change.
    private sealed class ChangingAssetSource(AssetManifest manifest) : IAssetSource
    {
        public AssetManifest Manifest { get; } = manifest;
        public bool Disposed { get; private set; }

        public ValueTask ValidateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<AssetReadSnapshot> OpenAsync(string relativePath, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AssetReadSnapshot(
                new AssetDescriptor(relativePath, "text/html", 1, new string('2', 64)),
                new TrackingStream(() => Disposed = true)));

        private sealed class TrackingStream(Action disposed) : MemoryStream([0x41], writable: false)
        {
            protected override void Dispose(bool disposing)
            {
                disposed();
                base.Dispose(disposing);
            }
        }
    }

    private sealed class ThrowingAssetSource : IAssetSource
    {
        public ThrowingAssetSource(AssetManifest manifest) => Manifest = manifest;

        public AssetManifest Manifest { get; }

        public ValueTask ValidateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Test failure."));

        public ValueTask<AssetReadSnapshot> OpenAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<AssetReadSnapshot>(new IOException("Test failure."));
    }

    private sealed class MutationAfterValidationSource : IAssetSource
    {
        private readonly byte[] _validated;
        private readonly byte[] _mutated;

        public MutationAfterValidationSource(string validated, string mutated)
        {
            _validated = Encoding.UTF8.GetBytes(validated);
            _mutated = Encoding.UTF8.GetBytes(mutated);
            if (_validated.Length != _mutated.Length)
            {
                throw new ArgumentException("Test inputs must have equal lengths.");
            }

            Manifest = new AssetManifest(
            [
                new AssetDescriptor(
                    "index.html",
                    "text/html",
                    _validated.Length,
                    Convert.ToHexString(SHA256.HashData(_validated)),
                    isEntryPoint: true),
            ]);
        }

        public AssetManifest Manifest { get; }

        public ValueTask ValidateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StringComparer.Ordinal.Equals(
                Manifest.EntryPoint.Sha256,
                Convert.ToHexString(SHA256.HashData(_validated)).ToLowerInvariant()))
            {
                throw new InvalidDataException("The test source failed validation.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<AssetReadSnapshot> OpenAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Manifest.TryGetAsset(relativePath, out AssetDescriptor? descriptor))
            {
                throw new FileNotFoundException("Missing test asset.", relativePath);
            }

            return ValueTask.FromResult(new AssetReadSnapshot(descriptor, new SwitchingReadStream(_validated, _mutated)));
        }
    }

    private sealed class SwitchingReadStream : Stream
    {
        private readonly byte[] _validated;
        private readonly byte[] _mutated;
        private int _position;

        public SwitchingReadStream(byte[] validated, byte[] mutated)
        {
            _validated = validated;
            _mutated = mutated;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _validated.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_position == _validated.Length || buffer.Length == 0)
            {
                return 0;
            }

            buffer[0] = _position == 0 ? _validated[0] : _mutated[_position];
            _position++;
            return 1;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Runs the packer this checkout built, as a separate process, for each
    // documented failure. JSON output keeps the RAS code and the exit code, and
    // its message names no path. Runic.CommandLine redacts a detail value that
    // looks like a home, /tmp or /root path, a drive or UNC path, or exception
    // text, so the source directory is redacted only where the temporary
    // directory looks like one; macOS /var/folders is shown. Human output still
    // names the path.
    [Test, DisplayName("packer JSON failures keep their RAS codes and redact paths")]
    public async Task PackerJsonFaults()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("site/index.html", "<p>x</p>");
        string site = Path.Combine(directory.Path, "site");
        string archive = Path.Combine(directory.Path, "out.runic-assets");
        string missing = Path.Combine(directory.Path, "missing");

        // The Runic tool exit codes: 2 usage or validation, 1 a failed command or a missing resource.
        (int exitCode, JsonElement fault) = await PackJsonAsync(missing, archive).ConfigureAwait(false);
        Equal(1, exitCode);
        Equal("RAS1001", fault.GetProperty("code").GetString());
        Equal("The source directory does not exist.", fault.GetProperty("message").GetString());
        Equal(
            SanitizerRedacts(missing) ? "[redacted]" : missing,
            fault.GetProperty("details").GetProperty("sourceDirectory").GetString());
        (int humanExitCode, string _, string error) = await RunPackerAsync(missing, archive).ConfigureAwait(false);
        Equal(1, humanExitCode);
        string[] errorLines = error.Trim().Split(Environment.NewLine);
        Equal($"Source directory '{missing}' does not exist.", errorLines[0]);
        // The message links to the code's catalog entry at the release tag.
        True(errorLines.Length == 2
            && errorLines[1].StartsWith("Help for RAS1001: https://github.com/Runic-Artifex/runic-sdk/blob/v", StringComparison.Ordinal)
            && errorLines[1].EndsWith("/docs/diagnostics.md#ras1001", StringComparison.Ordinal));

        (exitCode, fault) = await PackJsonAsync(site, archive, "--entry-point", "missing.html", "--trusted-generated-output").ConfigureAwait(false);
        Equal(1, exitCode);
        Equal("RAS1002", fault.GetProperty("code").GetString());
        True(!fault.GetProperty("message").GetString()!.Contains(directory.Path, StringComparison.Ordinal));
        Equal("missing.html", fault.GetProperty("details").GetProperty("entryPoint").GetString());

        // Creating a symbolic link on Windows needs Developer Mode or elevation.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.CreateSymbolicLink(Path.Combine(site, "linked.txt"), Path.Combine(site, "index.html"));
        (exitCode, fault) = await PackJsonAsync(site, archive, "--trusted-generated-output").ConfigureAwait(false);
        Equal(1, exitCode);
        Equal("RAS1003", fault.GetProperty("code").GetString());
        Equal("The archive could not be packed.", fault.GetProperty("message").GetString());
        Equal(
            "Trusted generated asset output cannot contain symbolic links or reparse points.",
            fault.GetProperty("details").GetProperty("reason").GetString());
        True(!File.Exists(archive));
    }

    // The documented success line: a runic.assets.pack-result/1 payload of { "archiveLength": <bytes> }.
    // Runic.CommandLine 0.6.0-preview.3 and later serialize through the JSON context's options,
    // so the payload name must not depend on them.
    [Test, DisplayName("packer JSON success keeps the runic.assets.pack-result/1 payload")]
    public async Task PackerJsonSuccess()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("site/index.html", "<p>x</p>");
        string site = Path.Combine(directory.Path, "site");
        string archive = Path.Combine(directory.Path, "out.runic-assets");

        (int exitCode, string output, string _) = await RunPackerAsync(site, archive, "--format", "json").ConfigureAwait(false);
        Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        True(root.GetProperty("success").GetBoolean());
        Equal("runic.assets.pack-result/1", root.GetProperty("payloadType").GetString());
        JsonElement payload = root.GetProperty("payload");
        Equal("archiveLength", string.Join(",", payload.EnumerateObject().Select(property => property.Name)));
        Equal(new FileInfo(archive).Length, payload.GetProperty("archiveLength").GetInt64());
    }

    // Mirrors the technical-content rule of Runic.CommandLine's fault sanitizer.
    private static bool SanitizerRedacts(string value) =>
        value.Contains("Exception", StringComparison.Ordinal)
        || value.Contains(@"\\", StringComparison.Ordinal)
        || value.Contains("/home/", StringComparison.Ordinal)
        || value.Contains("/Users/", StringComparison.Ordinal)
        || value.Contains("/root/", StringComparison.Ordinal)
        || value.Contains("/tmp/", StringComparison.Ordinal)
        || System.Text.RegularExpressions.Regex.IsMatch(value, "[A-Za-z]:[\\\\/]");

    private static async Task<(int ExitCode, JsonElement Fault)> PackJsonAsync(params string[] arguments)
    {
        (int exitCode, string output, string _) = await RunPackerAsync([.. arguments, "--format", "json"]).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        True(!root.GetProperty("success").GetBoolean());
        Equal(exitCode, root.GetProperty("exitCode").GetInt32());
        return (exitCode, root.GetProperty("fault").Clone());
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunPackerAsync(params string[] arguments)
    {
        // The test runs from <repository>/tests/dotnet/Runic.Assets.Tests/bin/<Configuration>/net10.0/.
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string configuration = output.Parent!.Name;
        string repository = Path.GetFullPath(Path.Combine(output.FullName, "../../../../../.."));
        string packer = Path.Combine(repository, "tools/Runic.Assets.Packer/bin", configuration, "net10.0/Runic.Assets.Packer.dll");
        if (!File.Exists(packer))
        {
            throw new InvalidOperationException(
                $"The packer was not found at '{packer}'. Build tools/Runic.Assets.Packer first ({configuration}).");
        }

        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(packer);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode, await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "runic-assets-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public string Write(string relativePath, string content)
        {
            string fullPath = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
            return fullPath;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

/// <summary>A conventional-route controller for the ASP.NET Core coexistence test.</summary>
public sealed class CoexistenceController : Controller
{
    /// <summary>Returns a marker body.</summary>
    public ContentResult Hello() => Content("controller");
}
