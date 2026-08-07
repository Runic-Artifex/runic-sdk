using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using RunicAssets.AspNetCore;
using RunicAssets.CsWebUi;
using RunicAssets.RunicToolkit;

namespace RunicAssets.Tests;

internal static class Program
{
    private static readonly TestCase[] Tests =
    [
        new("paths reject traversal and ambiguous syntax", SafePaths),
        new("manifest metadata and ordering are deterministic", DeterministicManifest),
        new("embedded assets validate and open offline", EmbeddedAssets),
        new("Runic Toolkit integration projects exact frontend assets", RunicToolkitIntegration),
        new("portable archives round-trip deterministic metadata", ArchiveRoundTrip),
        new("CsWebUi integration serves direct metadata-rich responses", CsWebUiIntegration),
        new("CsWebUi integration observes development refresh", CsWebUiDevelopmentRefresh),
        new("ASP.NET Core integration preserves response metadata", AspNetCoreIntegration),
        new("development directory refresh preserves immutable snapshots", DirectoryRefresh),
        new("development directory detects content drift", DirectoryDrift),
        new("development directory rejects symbolic links", DirectoryLinks),
        new("sources honor cancellation", Cancellation),
        new("shipping assembly stays framework neutral", FrameworkNeutral),
    ];

    public static async Task<int> Main()
    {
        int failures = 0;
        foreach (TestCase test in Tests)
        {
            try
            {
                await test.Body().ConfigureAwait(false);
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}");
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine($"RESULT passed={Tests.Length - failures} failed={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static Task SafePaths()
    {
        Equal("assets/app.js", AssetPath.Normalize(@"assets\app.js"));
        foreach (string hostile in new[]
        {
            "", " index.html", "/index.html", @"C:\index.html", "../index.html",
            "assets/../index.html", "./index.html", "assets//app.js", "assets/%2e%2e/app.js",
            "app.js?x=1", "app.js#fragment", "assets/na:me.js", "assets/\0.js",
        })
        {
            Throws<ArgumentException>(() => AssetPath.Normalize(hostile));
        }

        return Task.CompletedTask;
    }

    private static Task DeterministicManifest()
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
        Equal("image/svg+xml", AssetMediaTypes.Resolve("images/logo.svg"));
        Equal("application/octet-stream", AssetMediaTypes.Resolve("data/custom"));
        Throws<ArgumentException>(() => _ = new AssetManifest([entry, entry]));
        return Task.CompletedTask;
    }

    private static async Task EmbeddedAssets()
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

        await using Stream content = await source.OpenReadAsync("index.html").ConfigureAwait(false);
        using var reader = new StreamReader(content, Encoding.UTF8);
        True((await reader.ReadToEndAsync().ConfigureAwait(false)).Contains("Asset boundary", StringComparison.Ordinal));
        await ThrowsAsync<FileNotFoundException>(
            async () => await source.OpenReadAsync("missing.txt").ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task RunicToolkitIntegration()
    {
        EmbeddedAssetSource source = NewEmbeddedSource();
        var boundary = new RunicToolkitAssetBoundary(
            source,
            new Uri("app://runic-assets/application"));

        await boundary.ValidateAsync().ConfigureAwait(false);
        Equal("runic-toolkit.frontend-assets/1", boundary.Manifest.ManifestVersion);
        Equal(source.Manifest.Assets.Count, boundary.Manifest.Assets.Count);
        Equal("app://runic-assets/application/index.html", boundary.EntryPoint.AbsoluteUri);
        True(boundary.Manifest.Assets.Single(static asset => asset.IsEntryPoint).RelativePath == "index.html");
        await using Stream content = await boundary
            .OpenReadAsync("index.html", CancellationToken.None)
            .ConfigureAwait(false);
        using var reader = new StreamReader(content);
        True((await reader.ReadToEndAsync().ConfigureAwait(false))
            .Contains("Asset boundary", StringComparison.Ordinal));
    }

    private static async Task ArchiveRoundTrip()
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

    private static Task CsWebUiIntegration()
    {
        var source = new InMemoryAssetSource(
            ("index.html", "<h1>entry</h1>", "text/html; charset=utf-8", true, AssetCacheMode.NoStore),
            ("assets/app.css", "body{}", "text/css; charset=utf-8", false, AssetCacheMode.Immutable),
            ("assets/hello world.txt", "space", "text/plain; charset=utf-8", false, AssetCacheMode.Revalidate),
            ("日本語.txt", "unicode", "text/plain; charset=utf-8", false, AssetCacheMode.Revalidate));
        global::CsWebUi.WebUiFileHandler handler = source.ToWebUiFileHandler();

        string root = ResponseText(handler("/"));
        StartsWith("HTTP/1.1 200 OK\r\n", root);
        Contains("Content-Type: text/html; charset=utf-8\r\n", root);
        Contains("Content-Length: 14\r\n", root);
        Contains("Cache-Control: no-store\r\n", root);
        Contains($"ETag: {source.Manifest.EntryPoint.EntityTag}\r\n", root);
        Contains("X-Content-Type-Options: nosniff\r\n", root);
        Equal("<h1>entry</h1>", ResponseBody(root));

        string nested = ResponseText(handler("/assets/app.css"));
        Contains("Cache-Control: public, max-age=31536000, immutable\r\n", nested);
        Equal("body{}", ResponseBody(nested));
        Equal("space", ResponseBody(ResponseText(handler("/assets/hello world.txt"))));
        Equal("unicode", ResponseBody(ResponseText(handler("/日本語.txt"))));

        StartsWith("HTTP/1.1 404 Not Found\r\n", ResponseText(handler("/Assets/app.css")));
        StartsWith("HTTP/1.1 404 Not Found\r\n", ResponseText(handler("/../secret.txt")));
        StartsWith("HTTP/1.1 404 Not Found\r\n", ResponseText(handler("/client/route")));

        global::CsWebUi.WebUiFileHandler spaHandler = source.ToWebUiFileHandler(
            new RunicAssetsCsWebUiOptions { EnableSinglePageApplicationFallback = true });
        Equal("<h1>entry</h1>", ResponseBody(ResponseText(spaHandler("/client/route"))));
        StartsWith("HTTP/1.1 404 Not Found\r\n", ResponseText(spaHandler("/missing.js")));

        var failing = new ThrowingAssetSource(source.Manifest);
        StartsWith(
            "HTTP/1.1 500 Internal Server Error\r\n",
            ResponseText(failing.ToWebUiFileHandler()("/")));
        StartsWith(
            "HTTP/1.1 500 Internal Server Error\r\n",
            ResponseText(source.ToWebUiFileHandler(
                new RunicAssetsCsWebUiOptions { MaxResponseBytes = 1 })("/")));
        return Task.CompletedTask;
    }

    private static Task CsWebUiDevelopmentRefresh()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        global::CsWebUi.WebUiFileHandler handler = source.ToWebUiFileHandler();

        Equal("one", ResponseBody(ResponseText(handler("/"))));
        directory.Write("index.html", "two");
        source.Refresh();
        Equal("two", ResponseBody(ResponseText(handler("/"))));
        return Task.CompletedTask;
    }

    private static async Task AspNetCoreIntegration()
    {
        var source = NewEmbeddedSource();
        AssetDescriptor descriptor = source.Manifest.EntryPoint;
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await RunicAssetEndpointExtensions
            .WriteAssetAsync(context, source, descriptor)
            .ConfigureAwait(false);

        Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Equal(descriptor.MediaType, context.Response.ContentType);
        Equal(descriptor.Length, context.Response.ContentLength);
        Equal(descriptor.EntityTag, context.Response.Headers.ETag.ToString());
        Equal(descriptor.CacheControl, context.Response.Headers.CacheControl.ToString());
        Equal(descriptor.Length, context.Response.Body.Length);

        var conditional = new DefaultHttpContext();
        conditional.Request.Headers.IfNoneMatch = descriptor.EntityTag;
        await RunicAssetEndpointExtensions
            .WriteAssetAsync(conditional, source, descriptor)
            .ConfigureAwait(false);
        Equal(StatusCodes.Status304NotModified, conditional.Response.StatusCode);
    }

    private static async Task DirectoryRefresh()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        directory.Write("assets/app.js", "first");
        var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        AssetManifest first = source.Manifest;
        Equal(AssetCacheMode.NoStore, first.EntryPoint.CacheMode);
        SequenceEqual(
            new[] { "assets/app.js", "index.html" },
            first.Assets.Select(static asset => asset.RelativePath));

        directory.Write("index.html", "two");
        directory.Write("assets/extra.css", "body{}");
        AssetManifest second = source.Refresh();
        True(!ReferenceEquals(first, second));
        Equal(2, first.Assets.Count);
        Equal(3, second.Assets.Count);
        await source.ValidateAsync().ConfigureAwait(false);
        await using Stream content = await source.OpenReadAsync("index.html").ConfigureAwait(false);
        using var reader = new StreamReader(content);
        Equal("two", await reader.ReadToEndAsync().ConfigureAwait(false));
    }

    private static async Task DirectoryDrift()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        directory.Write("index.html", "changed");
        await ThrowsAsync<InvalidDataException>(
            async () => await source.ValidateAsync().ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static Task DirectoryLinks()
    {
        if (OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        string target = directory.Write("target.css", "body{}");
        string link = System.IO.Path.Combine(directory.Path, "linked.css");
        File.CreateSymbolicLink(link, target);
        Throws<InvalidDataException>(
            () => _ = new DevelopmentDirectoryAssetSource(directory.Path, "index.html"));
        return Task.CompletedTask;
    }

    private static async Task Cancellation()
    {
        var source = NewEmbeddedSource();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(
            async () => await source.ValidateAsync(cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        await ThrowsAsync<OperationCanceledException>(
            async () => await source.OpenReadAsync("index.html", cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static Task FrameworkNeutral()
    {
        Assembly assembly = typeof(AssetManifest).Assembly;
        string[] references = assembly.GetReferencedAssemblies().Select(static reference => reference.Name ?? "").ToArray();
        True(!references.Any(static name =>
            name.StartsWith("RunicToolkit.", StringComparison.Ordinal)
            || name.Contains("CsWebUi", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase)));
        return Task.CompletedTask;
    }

    private static EmbeddedAssetSource NewEmbeddedSource() =>
        new(
            typeof(Program).Assembly,
            [
                new("index.html", "RunicAssets.Tests.index.html", IsEntryPoint: true),
                new(
                    "assets/app.css",
                    "RunicAssets.Tests.app.css",
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

    private static string ResponseText(global::CsWebUi.WebUiFileHandlerResult result)
    {
        True(result.IsHandled);
        return Encoding.UTF8.GetString(result.Response.Span);
    }

    private static string ResponseBody(string response)
    {
        int separator = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        True(separator >= 0);
        return response[(separator + 4)..];
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

    private sealed record TestCase(string Name, Func<Task> Body);

    private sealed class InMemoryAssetSource : IAssetSource
    {
        private readonly Dictionary<string, byte[]> _contents = new(StringComparer.Ordinal);

        public InMemoryAssetSource(
            params (string Path, string Content, string MediaType, bool EntryPoint, AssetCacheMode CacheMode)[] assets)
        {
            var descriptors = new List<AssetDescriptor>();
            foreach (var asset in assets)
            {
                byte[] content = Encoding.UTF8.GetBytes(asset.Content);
                _contents.Add(asset.Path, content);
                descriptors.Add(new AssetDescriptor(
                    asset.Path,
                    asset.MediaType,
                    content.Length,
                    Convert.ToHexString(SHA256.HashData(content)),
                    asset.EntryPoint,
                    asset.CacheMode));
            }

            Manifest = new AssetManifest(descriptors);
        }

        public AssetManifest Manifest { get; }

        public ValueTask ValidateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<Stream> OpenReadAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = AssetPath.Normalize(relativePath);
            if (!_contents.TryGetValue(normalized, out byte[]? content))
            {
                throw new FileNotFoundException("Missing test asset.", normalized);
            }

            return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
        }
    }

    private sealed class ThrowingAssetSource : IAssetSource
    {
        public ThrowingAssetSource(AssetManifest manifest) => Manifest = manifest;

        public AssetManifest Manifest { get; }

        public ValueTask ValidateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Test failure."));

        public ValueTask<Stream> OpenReadAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<Stream>(new IOException("Test failure."));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "webuitoolkit-assets-" + Guid.NewGuid().ToString("N"));

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
