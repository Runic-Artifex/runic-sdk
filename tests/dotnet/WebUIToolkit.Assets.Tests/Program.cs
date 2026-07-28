using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.Assets.Tests;

internal static class Program
{
    private static readonly TestCase[] Tests =
    [
        new("paths reject traversal and ambiguous syntax", SafePaths),
        new("manifest metadata and ordering are deterministic", DeterministicManifest),
        new("embedded assets validate and open offline", EmbeddedAssets),
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
            name.StartsWith("WebUIToolkit.", StringComparison.Ordinal)
            || name.Contains("CsWebUi", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase)));
        return Task.CompletedTask;
    }

    private static EmbeddedAssetSource NewEmbeddedSource() =>
        new(
            typeof(Program).Assembly,
            [
                new("index.html", "WebUIToolkit.Assets.Tests.index.html", IsEntryPoint: true),
                new(
                    "assets/app.css",
                    "WebUIToolkit.Assets.Tests.app.css",
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
