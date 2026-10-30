using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Runic.Assets.AspNetCore;
using TUnit.Core;

namespace Runic.Assets.Tests;

// The development-directory source on every operating system in scope. CI runs this class on Linux in the managed
// job and on Windows and macOS in the native job: FileSystemWatcher uses inotify, ReadDirectoryChangesW and FSEvents,
// and the root is a pinned handle on Linux and macOS but a checked path on Windows.
internal sealed class DevelopmentDirectoryTests
{
    [Test, DisplayName("the development-directory source is stable, cross-platform API with one stop path")]
    public void PublicShape()
    {
        Type source = typeof(DevelopmentDirectoryAssetSource);
        True(source.GetCustomAttributes(typeof(System.Runtime.Versioning.SupportedOSPlatformAttribute), false).Length == 0);
        True(typeof(IAssetSource).Assembly.GetType("Runic.Assets.IAssetWatch") is null);
        True(source.GetMethod("Refresh") is null);
        Equal(typeof(ValueTask<AssetManifest>), source.GetMethod("RefreshAsync")!.ReturnType);
        var startWatching = source.GetMethods().Where(static method => method.Name == "StartWatching").ToArray();
        Equal(1, startWatching.Length);
        Equal(typeof(void), startWatching[0].ReturnType);
        SequenceEqual(new[] { typeof(AssetWatchOptions) }, startWatching[0].GetParameters().Select(static parameter => parameter.ParameterType));
    }

    [Test, DisplayName("ASP.NET Core delivery binds metadata and bytes from one source snapshot")]
    public async Task AspNetCoreSnapshotIntegration()
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
        await source.RefreshAsync().ConfigureAwait(false);
        await RunicAssetEndpointExtensions.WriteAssetAsync(context, source, stale).ConfigureAwait(false);
        Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Equal(source.Manifest.EntryPoint.EntityTag, context.Response.Headers.ETag.ToString());
        Equal("two", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));

        // Development snapshots are seekable, so a range starts without reading the prefix.
        directory.Write("index.html", "0123456789");
        await source.RefreshAsync().ConfigureAwait(false);
        var range = new DefaultHttpContext();
        range.Response.Body = new MemoryStream();
        range.Request.Headers.Range = "bytes=6-8";
        await RunicAssetEndpointExtensions.WriteAssetAsync(range, source, source.Manifest.EntryPoint).ConfigureAwait(false);
        Equal(StatusCodes.Status206PartialContent, range.Response.StatusCode);
        Equal("678", Encoding.UTF8.GetString(((MemoryStream)range.Response.Body).ToArray()));
    }

    [Test, DisplayName("development directory skips names that are not asset paths")]
    public async Task DirectorySkipsInvalidNames()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "entry");
        directory.Write("assets/app.js", "app");
        // Windows file names cannot contain ':', '?' or a backslash.
        string[] odd = OperatingSystem.IsWindows()
            ? ["%2e%2e.txt", "draft#1.txt"]
            : ["%2e%2e.txt", "back\\slash.txt", "draft#1.txt", "notes:draft.txt", "what?.txt"];
        foreach (string name in odd)
        {
            await File.WriteAllTextAsync(Path.Combine(directory.Path, name), "odd").ConfigureAwait(false);
        }

        using var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        SequenceEqual(new[] { "assets/app.js", "index.html" }, source.Manifest.Assets.Select(static asset => asset.RelativePath));
        SequenceEqual(odd, source.SkippedEntries.Order(StringComparer.Ordinal));

        File.Delete(Path.Combine(directory.Path, "draft#1.txt"));
        await source.RefreshAsync().ConfigureAwait(false);
        Equal(odd.Length - 1, source.SkippedEntries.Count);
    }

    [Test, DisplayName("development directory refresh preserves immutable snapshots")]
    public async Task DirectoryRefresh()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        directory.Write("assets/app.js", "first");
        using var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        AssetManifest first = source.Manifest;
        Equal(AssetCacheMode.NoStore, first.EntryPoint.CacheMode);
        SequenceEqual(
            new[] { "assets/app.js", "index.html" },
            first.Assets.Select(static asset => asset.RelativePath));

        directory.Write("index.html", "two");
        directory.Write("assets/extra.css", "body{}");
        AssetManifest second = await source.RefreshAsync().ConfigureAwait(false);
        True(!ReferenceEquals(first, second));
        True(ReferenceEquals(second, source.Manifest));
        Equal(2, first.Assets.Count);
        Equal(3, second.Assets.Count);
        await source.ValidateAsync().ConfigureAwait(false);
        Equal("two", await ReadTextAsync(source, "index.html").ConfigureAwait(false));
        // An unchanged directory keeps the published manifest.
        True(ReferenceEquals(second, await source.RefreshAsync().ConfigureAwait(false)));
    }

    [Test, DisplayName("development directory snapshots remain digest-verified while files change")]
    public async Task DirectorySnapshot()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        using var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        await using Stream snapshot = (await source.OpenAsync("index.html").ConfigureAwait(false)).Content;
        directory.Write("index.html", "two");
        using (var reader = new StreamReader(snapshot, leaveOpen: true))
        {
            Equal("one", await reader.ReadToEndAsync().ConfigureAwait(false));
        }

        await ThrowsAsync<InvalidDataException>(
            async () => await source.OpenAsync("index.html").ConfigureAwait(false)).ConfigureAwait(false);
        await source.RefreshAsync().ConfigureAwait(false);
        Equal("two", await ReadTextAsync(source, "index.html").ConfigureAwait(false));
    }

    [Test, DisplayName("development directory watcher coalesces one owned refresh")]
    public async Task DirectoryWatch()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        directory.Write("assets/app.css", "body{color:black}");
        var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        string expected = Sha256("two");
        var observed = new TaskCompletionSource<AssetSourceChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        int notifications = 0;
        ((IAssetSourceChangeNotifier)source).Changed += (_, change) =>
        {
            Interlocked.Increment(ref notifications);
            // A writer can be seen half way, for example after Windows reports the truncation; the next change follows.
            if (change.Current.EntryPoint.Sha256 == expected) observed.TrySetResult(change);
        };

        source.StartWatching(new AssetWatchOptions { DebounceDelay = TimeSpan.FromMilliseconds(100) });
        Throws<InvalidOperationException>(() => source.StartWatching());
        directory.Write("index.html", "two");
        directory.Write("assets/app.css", "body{color:green}");
        AssetSourceChangedEventArgs change = await observed.Task.WaitScaledAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Equal(2, change.Current.Assets.Count);
        if (OperatingSystem.IsLinux())
        {
            // inotify reports both writes inside one debounce window.
            Equal(1, Volatile.Read(ref notifications));
        }

        await WaitUntilAsync(async () =>
        {
            try { return await ReadTextAsync(source, "index.html").ConfigureAwait(false) == "two"; }
            catch (InvalidDataException) { return false; }
        }).ConfigureAwait(false);

        // Disposing the source is the only way to stop the watch.
        source.Dispose();
        AssetManifest stopped = source.Manifest;
        directory.Write("index.html", "three");
        await Task.Delay(300).ConfigureAwait(false);
        True(ReferenceEquals(stopped, source.Manifest));
        Throws<ObjectDisposedException>(() => source.StartWatching());
    }

    [Test, DisplayName("the watcher retries a directory that is missing its entry for a moment")]
    public async Task DirectoryWatchRetry()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        using var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        string expected = Sha256("two");
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((IAssetSourceChangeNotifier)source).Changed += (_, change) =>
        {
            if (change.Current.EntryPoint.Sha256 == expected) observed.TrySetResult();
        };
        source.StartWatching(new AssetWatchOptions
        {
            DebounceDelay = TimeSpan.FromMilliseconds(25),
            RetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryAttempts = 10,
        });

        // A frontend build that empties its output directory first.
        File.Delete(Path.Combine(directory.Path, "index.html"));
        await Task.Delay(100).ConfigureAwait(false);
        directory.Write("index.html", "two");
        await observed.Task.WaitScaledAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Equal(expected, source.Manifest.EntryPoint.Sha256);
    }

    [Test, DisplayName("concurrent source disposal waits for the active watch refresh")]
    public async Task DirectoryWatchDispose()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        ((IAssetSourceChangeNotifier)source).Changed += (_, _) =>
        {
            callbackEntered.Set();
            releaseCallback.Wait();
        };

        source.StartWatching(new AssetWatchOptions { DebounceDelay = TimeSpan.FromMilliseconds(25) });
        directory.Write("index.html", "two");
        True(callbackEntered.Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(10))));
        Task first = Task.Run(source.Dispose);
        Task second = Task.Run(source.Dispose);
        await Task.Delay(100).ConfigureAwait(false);
        True(!first.IsCompleted);
        True(!second.IsCompleted);
        releaseCallback.Set();
        await first.ConfigureAwait(false);
        await second.ConfigureAwait(false);
        await ThrowsAsync<ObjectDisposedException>(async () => await source.RefreshAsync().ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Test, DisplayName("a watch subscriber can dispose the source from its callback")]
    public async Task DirectoryWatchDisposeFromCallback()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((IAssetSourceChangeNotifier)source).Changed += (_, _) =>
        {
            source.Dispose();
            disposed.TrySetResult();
        };
        source.StartWatching(new AssetWatchOptions { DebounceDelay = TimeSpan.FromMilliseconds(25) });
        directory.Write("index.html", "two");
        await disposed.Task.WaitScaledAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        // An external disposal after it returns too.
        await Task.Run(source.Dispose).WaitScaledAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    [Test, DisplayName("development watcher follows the pinned root after path replacement")]
    public async Task DirectoryPinnedWatch()
    {
        // The watcher follows the root handle through /proc/self/fd on Linux; macOS and Windows watch a path.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        string root = Path.Combine(directory.Path, "assets");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "one").ConfigureAwait(false);
        using var source = new DevelopmentDirectoryAssetSource(root, "index.html");
        var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ((IAssetSourceChangeNotifier)source).Changed += (_, _) => changed.TrySetResult(true);
        source.StartWatching(new AssetWatchOptions { DebounceDelay = TimeSpan.FromMilliseconds(25) });

        string moved = Path.Combine(directory.Path, "moved-assets");
        Directory.Move(root, moved);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "replacement").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(moved, "index.html"), "two").ConfigureAwait(false);
        await changed.Task.WaitScaledAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Equal("two", await ReadTextAsync(source, "index.html").ConfigureAwait(false));
    }

    [Test, DisplayName("development directory publishes owned immutable change snapshots")]
    public async Task DirectoryChangeNotifications()
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
        AssetManifest second = await source.RefreshAsync().ConfigureAwait(false);
        Equal(1, notifications);
        True(observed is not null);
        True(ReferenceEquals(first, observed!.Previous));
        True(ReferenceEquals(second, observed.Current));
        True(!StringComparer.Ordinal.Equals(first.EntryPoint.Sha256, second.EntryPoint.Sha256));
        Throws<NotSupportedException>(() => ((IList<AssetDescriptor>)second.Assets).Add(second.EntryPoint));

        await source.RefreshAsync().ConfigureAwait(false);
        Equal(1, notifications);
        File.Delete(Path.Combine(directory.Path, "index.html"));
        await ThrowsAsync<ArgumentException>(async () => await source.RefreshAsync().ConfigureAwait(false)).ConfigureAwait(false);
        Equal(1, notifications);
        True(ReferenceEquals(second, source.Manifest));
        ((IAssetSourceChangeNotifier)source).Changed -= throwing;
        source.Dispose();
        await ThrowsAsync<ObjectDisposedException>(async () => await source.RefreshAsync().ConfigureAwait(false)).ConfigureAwait(false);
        Throws<ObjectDisposedException>(() => ((IAssetSourceChangeNotifier)source).Changed += (_, _) => { });
    }

    [Test, DisplayName("refreshes honor cancellation and run one at a time")]
    public async Task DirectoryRefreshCancellation()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        using var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(
            async () => await source.RefreshAsync(cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        await ThrowsAsync<OperationCanceledException>(
            async () => await source.OpenAsync("index.html", cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        await ThrowsAsync<OperationCanceledException>(
            async () => await source.ValidateAsync(cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);

        for (int index = 0; index < 20; index++)
        {
            directory.Write($"assets/file-{index}.js", new string('x', 64 * 1024));
        }

        AssetManifest[] results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => source.RefreshAsync().AsTask())).ConfigureAwait(false);
        // The first refresh publishes; the others find the same assets and return the same manifest.
        True(results.All(result => ReferenceEquals(result, source.Manifest)));
        Equal(21, source.Manifest.Assets.Count);
    }

    [Test, DisplayName("development notifications serialize reentrant and unsubscribed handlers")]
    public async Task DirectoryNotificationDispatch()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        using var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        int calls = 0;
        bool concurrentRefreshCompleted = false;
        EventHandler<AssetSourceChangedEventArgs>? reentrant = null;
        reentrant = (_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                Task<AssetManifest> concurrentRefresh = Task.Run(() => source.RefreshAsync().AsTask());
                concurrentRefreshCompleted = concurrentRefresh.Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(2)));
                if (concurrentRefreshCompleted)
                {
                    _ = concurrentRefresh.GetAwaiter().GetResult();
                }
                directory.Write("index.html", "three");
                source.RefreshAsync().AsTask().GetAwaiter().GetResult();
            }
        };
        ((IAssetSourceChangeNotifier)source).Changed += reentrant;

        directory.Write("index.html", "two");
        await source.RefreshAsync().ConfigureAwait(false);
        Equal(2, calls);
        True(concurrentRefreshCompleted);
        Equal("three", await ReadTextAsync(source, "index.html").ConfigureAwait(false));

        int removedCalls = 0;
        EventHandler<AssetSourceChangedEventArgs> removed = (_, _) => removedCalls++;
        ((IAssetSourceChangeNotifier)source).Changed += removed;
        ((IAssetSourceChangeNotifier)source).Changed -= removed;
        directory.Write("index.html", "four");
        await source.RefreshAsync().ConfigureAwait(false);
        Equal(0, removedCalls);
        Equal(3, calls);
    }

    [Test, DisplayName("development disposal closes callback admission before returning")]
    public async Task DirectoryDisposeDispatch()
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
        Task<AssetManifest> refresh = Task.Run(() => source.RefreshAsync().AsTask());
        True(callbackEntered.Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(2))));
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
        await ThrowsAsync<ObjectDisposedException>(async () => await source.RefreshAsync().ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Test, DisplayName("development directory detects content drift")]
    public async Task DirectoryDrift()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "one");
        using var source = new DevelopmentDirectoryAssetSource(directory.Path, "index.html");
        directory.Write("index.html", "changed");
        await ThrowsAsync<InvalidDataException>(
            async () => await source.ValidateAsync().ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Test, DisplayName("development directory rejects symbolic links")]
    public Task DirectoryLinks()
    {
        // Creating a symbolic link on Windows needs Developer Mode or elevation; DirectoryJunctions covers Windows.
        if (OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        using var directory = new TemporaryDirectory();
        directory.Write("site/index.html", "one");
        string target = directory.Write("site/target.css", "body{}");
        string site = Path.Combine(directory.Path, "site");
        File.CreateSymbolicLink(Path.Combine(site, "linked.css"), target);
        Throws<InvalidDataException>(() => _ = new DevelopmentDirectoryAssetSource(site, "index.html"));
        File.Delete(Path.Combine(site, "linked.css"));

        Directory.CreateSymbolicLink(Path.Combine(site, "linked"), Path.Combine(directory.Path, "outside"));
        Directory.CreateDirectory(Path.Combine(directory.Path, "outside"));
        Throws<InvalidDataException>(() => _ = new DevelopmentDirectoryAssetSource(site, "index.html"));
        Directory.Delete(Path.Combine(site, "linked"));

        // The root itself must not be a link either.
        string rootLink = Path.Combine(directory.Path, "site-link");
        Directory.CreateSymbolicLink(rootLink, site);
        Throws<InvalidDataException>(() => _ = new DevelopmentDirectoryAssetSource(rootLink, "index.html"));
        return Task.CompletedTask;
    }

    [Test, DisplayName("development directory rejects junctions on Windows")]
    public async Task DirectoryJunctions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        directory.Write("site/index.html", "one");
        directory.Write("outside/secret.txt", "outside");
        string site = Path.Combine(directory.Path, "site");
        // A junction needs no privilege, unlike a symbolic link.
        await CreateJunctionAsync(Path.Combine(site, "linked"), Path.Combine(directory.Path, "outside")).ConfigureAwait(false);
        Throws<InvalidDataException>(() => _ = new DevelopmentDirectoryAssetSource(site, "index.html"));
        Directory.Delete(Path.Combine(site, "linked"));

        using var source = new DevelopmentDirectoryAssetSource(site, "index.html");
        await CreateJunctionAsync(Path.Combine(site, "later"), Path.Combine(directory.Path, "outside")).ConfigureAwait(false);
        await ThrowsAsync<InvalidDataException>(async () => await source.RefreshAsync().ConfigureAwait(false)).ConfigureAwait(false);
        Directory.Delete(Path.Combine(site, "later"));

        string rootLink = Path.Combine(directory.Path, "site-link");
        await CreateJunctionAsync(rootLink, site).ConfigureAwait(false);
        Throws<InvalidDataException>(() => _ = new DevelopmentDirectoryAssetSource(rootLink, "index.html"));
        Directory.Delete(rootLink);
    }

    [Test, DisplayName("development reads reject symlink replacement races")]
    public async Task DirectoryLinkSwap()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        string index = directory.Write("site/index.html", "safe");
        string outside = directory.Write("outside.html", "outside");
        using var source = new DevelopmentDirectoryAssetSource(Path.Combine(directory.Path, "site"), "index.html");
        for (int attempt = 0; attempt != 20; attempt++)
        {
            string replacement = Path.Combine(directory.Path, "site", "replacement.html");
            File.WriteAllText(replacement, "safe");
            File.Move(replacement, index, overwrite: true);
            Equal("safe", await ReadTextAsync(source, "index.html").ConfigureAwait(false));

            File.Delete(index);
            File.CreateSymbolicLink(index, outside);
            await ThrowsAsync<InvalidDataException>(
                async () => await source.OpenAsync("index.html").ConfigureAwait(false)).ConfigureAwait(false);
            File.Delete(index);
            File.WriteAllText(index, "safe");
        }
    }

    [Test, DisplayName("development source pins its root across root and ancestor swaps")]
    public async Task DirectoryRootPinning()
    {
        // Linux and macOS pin the root handle; Windows checks paths and does not (see the README).
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        string ancestor = Path.Combine(directory.Path, "ancestor");
        string root = Path.Combine(ancestor, "assets");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "pinned").ConfigureAwait(false);
        using var source = new DevelopmentDirectoryAssetSource(root, "index.html");

        string movedAncestor = Path.Combine(directory.Path, "moved-ancestor");
        Directory.Move(ancestor, movedAncestor);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "replacement").ConfigureAwait(false);

        Equal("pinned", await ReadTextAsync(source, "index.html").ConfigureAwait(false));
        await source.RefreshAsync().ConfigureAwait(false);
        Equal("pinned", await ReadTextAsync(source, "index.html").ConfigureAwait(false));

        string movedRoot = Path.Combine(directory.Path, "moved-root");
        Directory.Move(Path.Combine(movedAncestor, "assets"), movedRoot);
        Directory.CreateDirectory(Path.Combine(movedAncestor, "assets"));
        await File.WriteAllTextAsync(
            Path.Combine(movedAncestor, "assets", "index.html"),
            "replacement-two").ConfigureAwait(false);

        Equal("pinned", await ReadTextAsync(source, "index.html").ConfigureAwait(false));
        // A new file in the pinned directory is found by the next scan, wherever the directory moved.
        await File.WriteAllTextAsync(Path.Combine(movedRoot, "added.js"), "added").ConfigureAwait(false);
        await source.RefreshAsync().ConfigureAwait(false);
        Equal("pinned", await ReadTextAsync(source, "index.html").ConfigureAwait(false));
        Equal("added", await ReadTextAsync(source, "added.js").ConfigureAwait(false));
    }

    private static async Task CreateJunctionAsync(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException($"mklink /J failed: {error}");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        DateTime deadline = DateTime.UtcNow + TestTimeouts.Scale(TimeSpan.FromSeconds(10));
        while (!await condition().ConfigureAwait(false))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not hold within 10 seconds.");
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static async Task<string> ReadTextAsync(DevelopmentDirectoryAssetSource source, string path)
    {
        await using Stream content = (await source.OpenAsync(path).ConfigureAwait(false)).Content;
        using var reader = new StreamReader(content);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

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
        T[] expectedItems = expected.ToArray();
        T[] actualItems = actual.ToArray();
        if (!expectedItems.SequenceEqual(actualItems))
        {
            throw new InvalidOperationException(
                $"Sequences differ: expected [{string.Join(", ", expectedItems)}] but found [{string.Join(", ", actualItems)}].");
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
