using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Runic.Assets.AspNetCore;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace Runic.Assets.Tests;

internal static class Program
{
    private static readonly TestCase[] Tests =
    [
        new("paths reject traversal and ambiguous syntax", SafePaths),
        new("manifest metadata and ordering are deterministic", DeterministicManifest),
        new("embedded assets validate and open offline", EmbeddedAssets),
        new("portable archives round-trip deterministic metadata", ArchiveRoundTrip),
        new("archive writes reject content mutation after validation", ArchiveWriteMutation),
        new("directory compiler uses the canonical archive authority", DirectoryArchiveAuthority),
        new("archive inspection and schema-1 migration reports are deterministic", ArchiveInspectionAndMigration),
        new("archive manifest parsing is decompression bounded", ArchiveManifestBound),
        new("ASP.NET Core integration preserves response metadata", AspNetCoreIntegration),
        new("ASP.NET Core delivery binds metadata and bytes from one source snapshot", AspNetCoreSnapshotIntegration),
        new("development directory refresh preserves immutable snapshots", DirectoryRefresh),
        new("development directory snapshots remain digest-verified while files change", DirectorySnapshot),
        new("development directory publishes owned immutable change snapshots", DirectoryChangeNotifications),
        new("development directory watcher coalesces one owned refresh", DirectoryWatch),
        new("development watcher follows the pinned root after path replacement", DirectoryPinnedWatch),
        new("concurrent watcher disposal waits for the active refresh", DirectoryWatchDispose),
        new("development notifications serialize reentrant and unsubscribed handlers", DirectoryNotificationDispatch),
        new("development disposal closes callback admission before returning", DirectoryDisposeDispatch),
        new("development directory detects content drift", DirectoryDrift),
        new("development directory rejects symbolic links", DirectoryLinks),
        new("development reads reject symlink replacement races", DirectoryLinkSwap),
        new("development source pins its root across root and ancestor swaps", DirectoryRootPinning),
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
        Equal("sha256-" + Convert.ToBase64String(Convert.FromHexString(first.Sha256)), first.SubresourceIntegrity);
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

    private static async Task DirectoryArchiveAuthority()
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
        Equal(AssetCacheMode.Immutable, source.Manifest.Assets[0].CacheMode);
        True(!source.Manifest.TryGetAsset("excluded.txt", out _));
    }

    private static async Task ArchiveWriteMutation()
    {
        var source = new MutationAfterValidationSource("first", "other");
        using var archive = new MemoryStream();
        await ThrowsAsync<InvalidDataException>(
            async () => await AssetArchive.WriteAsync(source, archive).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    private static async Task ArchiveInspectionAndMigration()
    {
        using var archive = new MemoryStream();
        await AssetArchive.WriteAsync(NewEmbeddedSource(), archive).ConfigureAwait(false);
        byte[] original = archive.ToArray();

        archive.Position = 0;
        AssetArchiveInspection first = AssetArchive.Inspect(archive);
        archive.Position = 0;
        AssetArchiveCompatibilityReport report = AssetArchive.GetCompatibilityReport(archive);
        Equal(AssetArchive.CurrentVersion, first.ArchiveVersion);
        Equal(first.Manifest.EntryPoint.Sha256, report.Inspection.Manifest.EntryPoint.Sha256);
        True(report.IsCompatible);
        True(!report.RequiresMigration);
        Contains("migrationAction=No migration required", report.ToDeterministicReport());
        Contains("asset=assets/app.css|text/css|", first.ToDeterministicReport());

        archive.Position = 0;
        using var migrated = new MemoryStream();
        AssetArchiveCompatibilityReport migration = await AssetArchive
            .MigrateAsync(archive, migrated)
            .ConfigureAwait(false);
        SequenceEqual(original, migrated.ToArray());
        Equal(report.MigrationAction, migration.MigrationAction);
        Equal(0L, archive.Position);
        Equal(0L, migrated.Position);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        archive.Position = 0;
        await ThrowsAsync<OperationCanceledException>(
            async () => await AssetArchive.MigrateAsync(
                archive,
                migrated,
                cancellationToken: cancelled.Token).ConfigureAwait(false)).ConfigureAwait(false);
        Equal(0L, archive.Position);
        Equal(0L, migrated.Position);
        await ThrowsAsync<ArgumentException>(
            async () => await AssetArchive.MigrateAsync(archive, archive).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    private static async Task ArchiveManifestBound()
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
        archive.Position = 0;
        Throws<InvalidDataException>(() => AssetArchive.GetCompatibilityReport(archive, options));
        archive.Position = 0;
        using var migrated = new MemoryStream();
        await ThrowsAsync<InvalidDataException>(
            async () => await AssetArchive.MigrateAsync(archive, migrated, options).ConfigureAwait(false))
            .ConfigureAwait(false);
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

        var unsafeSource = new InMemoryAssetSource(
            ("index.html", "unowned", "text/html", true, AssetCacheMode.Revalidate));
        Throws<ArgumentException>(() => RunicAssetEndpointExtensions
            .WriteAssetAsync(new DefaultHttpContext(), unsafeSource, unsafeSource.Manifest.EntryPoint)
            .GetAwaiter()
            .GetResult());

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

    private static async Task AspNetCoreSnapshotIntegration()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        using var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        AssetDescriptor stale = source.Manifest.EntryPoint;
        directory.Write("index.html", "two");

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await ThrowsAsync<InvalidDataException>(
            async () => await RunicAssetEndpointExtensions
                .WriteAssetAsync(context, source, stale)
                .ConfigureAwait(false)).ConfigureAwait(false);
        source.Refresh();
        await RunicAssetEndpointExtensions.WriteAssetAsync(context, source, stale).ConfigureAwait(false);
        Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Equal(source.Manifest.EntryPoint.EntityTag, context.Response.Headers.ETag.ToString());
        Equal("two", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
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

    private static async Task DirectorySnapshot()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        using var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        await using Stream snapshot = await source.OpenReadAsync("index.html").ConfigureAwait(false);
        directory.Write("index.html", "two");
        using (var reader = new StreamReader(snapshot, leaveOpen: true))
        {
            Equal("one", await reader.ReadToEndAsync().ConfigureAwait(false));
        }

        await ThrowsAsync<InvalidDataException>(
            async () => await source.OpenReadAsync("index.html").ConfigureAwait(false)).ConfigureAwait(false);
        source.Refresh();
        await using Stream refreshed = await source.OpenReadAsync("index.html").ConfigureAwait(false);
        using var refreshedReader = new StreamReader(refreshed);
        Equal("two", await refreshedReader.ReadToEndAsync().ConfigureAwait(false));
    }

    private static async Task DirectoryWatch()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        directory.Write("assets/app.css", "body{color:black}");
        using var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        var observed = new TaskCompletionSource<AssetSourceChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        int notifications = 0;
        ((IAssetSourceChangeNotifier)source).Changed += (_, change) =>
        {
            Interlocked.Increment(ref notifications);
            observed.TrySetResult(change);
        };

        using var cancellation = new CancellationTokenSource();
        IAssetWatch watch = source.StartWatching(
            new AssetWatchOptions { DebounceDelay = TimeSpan.FromMilliseconds(100) },
            cancellation.Token);
        True(watch.IsWatching);
        Throws<InvalidOperationException>(() => source.StartWatching());
        directory.Write("index.html", "two");
        directory.Write("assets/app.css", "body{color:green}");
        AssetSourceChangedEventArgs change = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Equal("two", await ReadTextAsync(source, "index.html").ConfigureAwait(false));
        Equal(2, change.Current.Assets.Count);
        Equal(1, notifications);

        cancellation.Cancel();
        True(!watch.IsWatching);
        AssetManifest stopped = source.Manifest;
        directory.Write("index.html", "three");
        await Task.Delay(150).ConfigureAwait(false);
        True(ReferenceEquals(stopped, source.Manifest));
    }

    private static async Task DirectoryWatchDispose()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        using var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        ((IAssetSourceChangeNotifier)source).Changed += (_, _) =>
        {
            callbackEntered.Set();
            releaseCallback.Wait();
        };

        IAssetWatch watch = source.StartWatching(
            new AssetWatchOptions { DebounceDelay = TimeSpan.FromMilliseconds(25) });
        directory.Write("index.html", "two");
        True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));
        Task first = Task.Run(watch.Dispose);
        Task second = Task.Run(watch.Dispose);
        await Task.Delay(100).ConfigureAwait(false);
        True(!first.IsCompleted);
        True(!second.IsCompleted);
        releaseCallback.Set();
        await first.ConfigureAwait(false);
        await second.ConfigureAwait(false);
        True(!watch.IsWatching);
    }

    private static async Task DirectoryPinnedWatch()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        string root = Path.Combine(directory.Path, "assets");
        Directory.CreateDirectory(root);
        string pinnedIndex = Path.Combine(root, "index.html");
        await File.WriteAllTextAsync(pinnedIndex, "one").ConfigureAwait(false);
        using var source = new DevelopmentDirectoryAssetSource(root, "index.html");
        var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ((IAssetSourceChangeNotifier)source).Changed += (_, _) => changed.TrySetResult(true);
        using IAssetWatch watch = source.StartWatching(
            new AssetWatchOptions { DebounceDelay = TimeSpan.FromMilliseconds(25) });

        string moved = Path.Combine(directory.Path, "moved-assets");
        Directory.Move(root, moved);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "replacement").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(moved, "index.html"), "two").ConfigureAwait(false);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Equal("two", await ReadTextAsync(source, "index.html").ConfigureAwait(false));
    }

    private static Task DirectoryChangeNotifications()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        AssetManifest first = source.Manifest;
        AssetSourceChangedEventArgs? observed = null;
        int notifications = 0;
        EventHandler<AssetSourceChangedEventArgs> throwing = (_, _) => throw new InvalidOperationException();
        ((IAssetSourceChangeNotifier)source).Changed += throwing;
        ((IAssetSourceChangeNotifier)source).Changed += (_, change) =>
        {
            notifications++;
            observed = change;
        };

        directory.Write("index.html", "two");
        AssetManifest second = source.Refresh();
        Equal(1, notifications);
        True(observed is not null);
        True(ReferenceEquals(first, observed!.Previous));
        True(ReferenceEquals(second, observed.Current));
        True(!StringComparer.Ordinal.Equals(first.EntryPoint.Sha256, second.EntryPoint.Sha256));
        Throws<NotSupportedException>(() => ((IList<AssetDescriptor>)second.Assets).Add(second.EntryPoint));

        source.Refresh();
        Equal(1, notifications);
        File.Delete(System.IO.Path.Combine(directory.Path, "index.html"));
        Throws<ArgumentException>(() => source.Refresh());
        Equal(1, notifications);
        True(ReferenceEquals(second, source.Manifest));
        ((IAssetSourceChangeNotifier)source).Changed -= throwing;
        source.Dispose();
        Throws<ObjectDisposedException>(() => source.Refresh());
        Throws<ObjectDisposedException>(() => ((IAssetSourceChangeNotifier)source).Changed += (_, _) => { });
        return Task.CompletedTask;
    }

    private static async Task DirectoryNotificationDispatch()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        int calls = 0;
        bool concurrentRefreshCompleted = false;
        EventHandler<AssetSourceChangedEventArgs>? reentrant = null;
        reentrant = (_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                Task<AssetManifest> concurrentRefresh = Task.Run(() => source.Refresh());
                concurrentRefreshCompleted = concurrentRefresh.Wait(TimeSpan.FromSeconds(2));
                if (concurrentRefreshCompleted)
                {
                    _ = concurrentRefresh.GetAwaiter().GetResult();
                }
                directory.Write("index.html", "three");
                source.Refresh();
            }
        };
        ((IAssetSourceChangeNotifier)source).Changed += reentrant;

        directory.Write("index.html", "two");
        source.Refresh();
        Equal(2, calls);
        True(concurrentRefreshCompleted);
        await using Stream content = await source.OpenReadAsync("index.html").ConfigureAwait(false);
        using var reader = new StreamReader(content);
        Equal("three", await reader.ReadToEndAsync().ConfigureAwait(false));

        int removedCalls = 0;
        EventHandler<AssetSourceChangedEventArgs> removed = (_, _) => removedCalls++;
        ((IAssetSourceChangeNotifier)source).Changed += removed;
        ((IAssetSourceChangeNotifier)source).Changed -= removed;
        directory.Write("index.html", "four");
        source.Refresh();
        Equal(0, removedCalls);
        Equal(3, calls);
    }

    private static async Task DirectoryDisposeDispatch()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        using var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        int secondCallbackCalls = 0;
        ((IAssetSourceChangeNotifier)source).Changed += (_, _) =>
        {
            source.Dispose();
            callbackEntered.Set();
            releaseCallback.Wait();
        };
        ((IAssetSourceChangeNotifier)source).Changed += (_, _) => secondCallbackCalls++;

        directory.Write("index.html", "two");
        Task<AssetManifest> refresh = Task.Run(() => source.Refresh());
        True(callbackEntered.Wait(TimeSpan.FromSeconds(2)));
        Task firstDispose = Task.Run(source.Dispose);
        Task secondDispose = Task.Run(source.Dispose);
        await Task.Delay(100).ConfigureAwait(false);
        True(!firstDispose.IsCompleted);
        True(!secondDispose.IsCompleted);
        releaseCallback.Set();
        await firstDispose.ConfigureAwait(false);
        await secondDispose.ConfigureAwait(false);
        await refresh.ConfigureAwait(false);
        Equal(0, secondCallbackCalls);
        Throws<ObjectDisposedException>(() => source.Refresh());
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

    private static async Task DirectoryLinkSwap()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        string index = directory.Write("index.html", "safe");
        string outside = directory.Write("outside.html", "outside");
        using var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        for (int attempt = 0; attempt != 20; attempt++)
        {
            string replacement = Path.Combine(directory.Path, "replacement.html");
            File.WriteAllText(replacement, "safe");
            File.Move(replacement, index, overwrite: true);
            await using (Stream content = await source.OpenReadAsync("index.html").ConfigureAwait(false))
            using (var reader = new StreamReader(content))
            {
                Equal("safe", await reader.ReadToEndAsync().ConfigureAwait(false));
            }

            File.Delete(index);
            File.CreateSymbolicLink(index, outside);
            await ThrowsAsync<InvalidDataException>(
                async () => await source.OpenReadAsync("index.html").ConfigureAwait(false)).ConfigureAwait(false);
            File.Delete(index);
            File.WriteAllText(index, "safe");
        }
    }

    private static async Task DirectoryRootPinning()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        string ancestor = Path.Combine(directory.Path, "ancestor");
        string root = Path.Combine(ancestor, "assets");
        Directory.CreateDirectory(root);
        string index = Path.Combine(root, "index.html");
        await File.WriteAllTextAsync(index, "pinned").ConfigureAwait(false);
        using var source = new DevelopmentDirectoryAssetSource(root, "index.html");

        string movedAncestor = Path.Combine(directory.Path, "moved-ancestor");
        Directory.Move(ancestor, movedAncestor);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "replacement").ConfigureAwait(false);

        Equal("pinned", await ReadTextAsync(source, "index.html").ConfigureAwait(false));
        source.Refresh();
        Equal("pinned", await ReadTextAsync(source, "index.html").ConfigureAwait(false));

        string movedRoot = Path.Combine(directory.Path, "moved-root");
        Directory.Move(Path.Combine(movedAncestor, "assets"), movedRoot);
        Directory.CreateDirectory(Path.Combine(movedAncestor, "assets"));
        await File.WriteAllTextAsync(
            Path.Combine(movedAncestor, "assets", "index.html"),
            "replacement-two").ConfigureAwait(false);

        Equal("pinned", await ReadTextAsync(source, "index.html").ConfigureAwait(false));
        source.Refresh();
        Equal("pinned", await ReadTextAsync(source, "index.html").ConfigureAwait(false));
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

    private static async Task<string> ReadTextAsync(DevelopmentDirectoryAssetSource source, string path)
    {
        await using Stream content = await source.OpenReadAsync(path).ConfigureAwait(false);
        using var reader = new StreamReader(content);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static EmbeddedAssetSource NewEmbeddedSource() =>
        new(
            typeof(Program).Assembly,
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

        public ValueTask<Stream> OpenReadAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Manifest.TryGetAsset(relativePath, out _))
            {
                throw new FileNotFoundException("Missing test asset.", relativePath);
            }

            return ValueTask.FromResult<Stream>(new SwitchingReadStream(_validated, _mutated));
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
