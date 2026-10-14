using System.Diagnostics;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Real file-system cases. The native CI job also runs this class on Windows and macOS, whose watchers differ from
// inotify; see "Watching files" in the Runic.Application.Views README.
[Category("FileSystem")]
internal sealed class PathWatcherTests
{
    // FSEvents can deliver an event some time after the change; give each platform time to report a burst.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(OperatingSystem.IsMacOS() ? 1000 : 300);

    [Test]
    public async Task SymlinkRepointedByRenameIsReported()
    {
        using var root = new TempDirectory();
        var a = root.File("a", "a");
        var b = root.File("b", "b");
        var link = root.Path("current");
        CreateLink(link, a);
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var target = RunicWatchTarget.Link(link);
        var inContext = false;
        using var watcher = RunicPathWatcher.Start(context, [target], _ => inContext = context.IsExecuting, options: tester.Options);

        // Replace the link atomically, as `ln -sfn` and nix-env do.
        ReplaceLink(link, b);
        var change = await tester.NextChangeAsync();
        Require(change.Contains(target) && change.Targets.Count == 1, $"Expected the link, got {Describe(change)}.");
        Require(inContext, "The change callback did not run in the model context.");

        // A replacement that leads to the same file is not a change.
        ReplaceLink(link, b);
        await Task.Delay(Settle);
        var after = await tester.DrainAsync();
        Require(after.Count == 0, $"Re-pointing the link to the same target reported {after.Count} change(s).");
    }

    [Test]
    public async Task ProfileLinkChainIsFollowed()
    {
        using var root = new TempDirectory();
        var store1 = root.File("store-1", "1");
        var store2 = root.File("store-2", "2");
        var profiles = root.Directory("profiles");
        // profiles/home-manager -> home-manager-1-link -> store-1, as Nix lays out a profile.
        CreateLink(Path.Combine(profiles, "home-manager-1-link"), store1);
        CreateLink(Path.Combine(profiles, "home-manager"), "home-manager-1-link");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var profile = RunicWatchTarget.Link(Path.Combine(profiles, "home-manager"));
        var generations = RunicWatchTarget.Directory(profiles, "home-manager-*-link");
        using var watcher = RunicPathWatcher.Start(context, [profile, generations], _ => { }, options: tester.Options);

        CreateLink(Path.Combine(profiles, "home-manager-2-link"), store2);
        await Task.Delay(Settle);
        ReplaceLink(Path.Combine(profiles, "home-manager"), "home-manager-2-link");
        var change = await tester.NextChangeAsync();
        var rest = await tester.DrainAsync();
        var all = rest.Prepend(change).SelectMany(item => item.Targets).ToHashSet();
        Require(all.Contains(profile) && all.Contains(generations), $"Expected the profile and its generations, got {string.Join(", ", all)}.");
    }

    [Test]
    public async Task DirectoryLinkReplacedByLnIsReported()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("`ln -sfn` is the Unix way to re-point /run/current-system.");
        using var root = new TempDirectory();
        var first = root.Directory("system-1");
        var second = root.Directory("system-2");
        var current = root.Path("current-system");
        Run(root.Root, "ln", "-sfn", first, current);
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var target = RunicWatchTarget.Link(current);
        using var watcher = RunicPathWatcher.Start(context, [target], _ => { }, options: tester.Options);

        Run(root.Root, "ln", "-sfn", second, current);
        var change = await tester.NextChangeAsync();
        Require(change.Contains(target), $"Expected current-system, got {Describe(change)}.");
    }

    [Test]
    public async Task FileWrittenInPlaceIsReported()
    {
        using var root = new TempDirectory();
        var file = root.File("settings.json", "{\"a\":1}");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var target = RunicWatchTarget.File(file);
        using var watcher = RunicPathWatcher.Start(context, [target], _ => { }, options: tester.Options);

        // Same length, written through the existing inode.
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            stream.Write("{\"a\":2}"u8);
        var change = await tester.NextChangeAsync();
        Require(change.Contains(target), $"Expected the file, got {Describe(change)}.");
    }

    [Test]
    public async Task DirectoryEntriesAddedAndRemovedAreReported()
    {
        using var root = new TempDirectory();
        var profiles = root.Directory("profiles");
        root.File(Path.Combine("profiles", "system-1-link"), "1");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var target = RunicWatchTarget.Directory(profiles, "system-*-link");
        using var watcher = RunicPathWatcher.Start(context, [target], _ => { }, options: tester.Options);

        File.WriteAllText(Path.Combine(profiles, "system-2-link"), "2");
        var added = await tester.NextChangeAsync();
        Require(added.Contains(target), $"Expected the added entry, got {Describe(added)}.");

        // Entries that don't match the pattern, and writes to matching entries, are not reported.
        File.WriteAllText(Path.Combine(profiles, "unrelated"), "x");
        File.WriteAllText(Path.Combine(profiles, "system-2-link"), "22");
        await Task.Delay(Settle);
        var ignored = await tester.DrainAsync();
        Require(ignored.Count == 0, $"Unrelated changes reported {ignored.Count} change(s).");

        File.Delete(Path.Combine(profiles, "system-1-link"));
        var removed = await tester.NextChangeAsync();
        Require(removed.Contains(target), $"Expected the removed entry, got {Describe(removed)}.");
    }

    [Test]
    public async Task WatchedDirectoryDeletedAndRecreatedIsReported()
    {
        using var root = new TempDirectory();
        var directory = root.Directory("state");
        var file = root.File(Path.Combine("state", "current"), "1");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var errors = new List<RunicPathWatchErrorKind>();
        var target = RunicWatchTarget.File(file);
        using var watcher = RunicPathWatcher.Start(context, [target], _ => { }, error => errors.Add(error.Kind), tester.Options);

        Directory.Delete(directory, recursive: true);
        // FSEvents reports nothing when the watched directory itself is deleted; the 30-second backstop read does.
        await Task.Delay(Settle);
        tester.Time.Advance(TimeSpan.FromSeconds(30));
        Require((await NextOrDescribeAsync(tester, watcher)).Contains(target), "Deleting the directory was not reported.");
        await Task.Delay(Settle);
        await tester.DrainAsync();
        Require(errors.Contains(RunicPathWatchErrorKind.Missing), $"Expected a Missing error, got [{string.Join(", ", errors)}].");

        Directory.CreateDirectory(directory);
        File.WriteAllText(file, "2");
        Require((await NextOrDescribeAsync(tester, watcher)).Contains(target), "Recreating the directory was not reported.");

        // The watch is back: a write is reported without waiting for a poll.
        await Task.Delay(Settle);
        await tester.DrainAsync();
        File.WriteAllText(file, "33");
        await Task.Delay(Settle);
        tester.Time.Advance(tester.Options.Debounce);
        var change = await NextOrDescribeAsync(tester, watcher, TimeSpan.FromSeconds(2));
        Require(change.Contains(target), "A write after the directory returned was not reported.");
    }

    private static async Task<RunicPathChange> NextOrDescribeAsync(RunicPathWatchTester tester, RunicPathWatcher watcher, TimeSpan? timeout = null)
    {
        try { return await tester.NextChangeAsync(timeout); }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"{exception.Message} Watcher: {watcher.Describe()}", exception);
        }
    }

    [Test]
    public async Task DebounceCoalescesABurst()
    {
        using var root = new TempDirectory();
        var file = root.File("log", "");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var calls = 0;
        var target = RunicWatchTarget.File(file);
        using var watcher = RunicPathWatcher.Start(context, [target], _ => calls++, options: tester.Options);

        for (var i = 0; i < 20; i++) File.AppendAllText(file, $"line {i}\n");
        // Let every event arrive while the test clock stands still, so the debounce has not elapsed.
        await Task.Delay(Settle);
        await tester.NextChangeAsync();
        var more = await tester.DrainAsync();
        Require(more.Count == 0 && calls == 1, $"A burst of 20 writes was reported {calls} times.");
    }

    [Test]
    public async Task GitHeadMovesAreReported()
    {
        using var root = new TempDirectory();
        var repository = root.Directory("checkout");
        if (!Git(repository, "init", "--quiet", "--initial-branch=main"))
            Skip.Test("git is not installed.");
        Commit(repository, "one");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var target = RunicWatchTarget.GitHead(repository);
        using var watcher = RunicPathWatcher.Start(context, [target], _ => { }, options: tester.Options);

        Commit(repository, "two");
        Require((await tester.NextChangeAsync()).Contains(target), "A commit was not reported.");

        // Packing refs moves nothing.
        Require(Git(repository, "pack-refs", "--all"), "git pack-refs failed.");
        await Task.Delay(Settle);
        var packed = await tester.DrainAsync();
        Require(packed.Count == 0, $"Packing refs reported {packed.Count} change(s).");

        // The branch is now in packed-refs; a commit writes a loose ref again.
        Commit(repository, "three");
        Require((await tester.NextChangeAsync()).Contains(target), "A commit after pack-refs was not reported.");

        Require(Git(repository, "switch", "--quiet", "-c", "feature/x"), "git switch failed.");
        Require((await tester.NextChangeAsync()).Contains(target), "Switching branch was not reported.");

        Require(Git(repository, "reset", "--quiet", "--hard", "HEAD~1"), "git reset failed.");
        Require((await tester.NextChangeAsync()).Contains(target), "A reset was not reported.");
    }

    [Test]
    public async Task GitWorktreeHeadMovesAreReported()
    {
        using var root = new TempDirectory();
        var repository = root.Directory("checkout");
        if (!Git(repository, "init", "--quiet", "--initial-branch=main"))
            Skip.Test("git is not installed.");
        Commit(repository, "one");
        var worktree = root.Path("worktree");
        Require(Git(repository, "worktree", "add", "--quiet", "-b", "side", worktree), "git worktree add failed.");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var target = RunicWatchTarget.GitHead(worktree);
        using var watcher = RunicPathWatcher.Start(context, [target], _ => { }, options: tester.Options);

        Commit(repository, "on main");
        await Task.Delay(Settle);
        var other = await tester.DrainAsync();
        Require(other.Count == 0, $"A commit on another branch reported {other.Count} change(s).");

        Commit(worktree, "on side");
        Require((await tester.NextChangeAsync()).Contains(target), "A commit in the worktree was not reported.");
    }

    [Test]
    public async Task DisposalStopsCallbacks()
    {
        using var root = new TempDirectory();
        var file = root.File("state", "1");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var calls = 0;
        var watcher = RunicPathWatcher.Start(context, [RunicWatchTarget.File(file)], _ => calls++, options: tester.Options);
        Require(tester.ActiveWatchers == 1, "The tester did not see the watcher start.");

        File.WriteAllText(file, "2");
        await Task.Delay(Settle);
        watcher.Dispose();
        File.WriteAllText(file, "3");
        await Task.Delay(Settle);
        var after = await tester.DrainAsync();
        Require(after.Count == 0 && calls == 0, $"A disposed watcher reported {calls} change(s).");
        Require(tester.ActiveWatchers == 0, "The tester still counts the disposed watcher.");

        // Closing the model context stops a watcher too.
        var second = RunicPathWatcher.Start(context, [RunicWatchTarget.File(file)], _ => calls++, options: tester.Options);
        await context.DisposeAsync();
        Require(tester.ActiveWatchers == 0, "Closing the model context did not stop the watcher.");
        second.Dispose();
        using var late = RunicPathWatcher.Start(context, [RunicWatchTarget.File(file)], _ => calls++, options: tester.Options);
        Require(tester.ActiveWatchers == 0, "A watcher started on a closed model context kept running.");
    }

    [Test]
    public async Task CallbacksRunInTheModelContextWithRealTime()
    {
        using var root = new TempDirectory();
        var missing = root.Path("later");
        var file = Path.Combine(missing, "file");
        await using var context = new RunicModelContext();
        var error = new TaskCompletionSource<(RunicPathWatchError Error, bool InContext)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = RunicWatchTarget.File(file);
        using var watcher = RunicPathWatcher.Start(
            context,
            [target],
            change => changed.TrySetResult(context.IsExecuting && change.Contains(target)),
            failure => error.TrySetResult((failure, context.IsExecuting)),
            new RunicPathWatcherOptions { Debounce = TimeSpan.FromMilliseconds(50), PollingInterval = TimeSpan.FromMilliseconds(200) });

        var (reported, errorInContext) = await error.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(reported.Kind == RunicPathWatchErrorKind.Missing && errorInContext,
            $"Expected a Missing error in the model context, got {reported.Kind} (in context: {errorInContext}).");

        // The watcher polls until the directory appears.
        Directory.CreateDirectory(missing);
        File.WriteAllText(file, "x");
        Require(await changed.Task.WaitAsync(TimeSpan.FromSeconds(10)), "The change callback did not run in the model context.");
    }

    [Test]
    public async Task PollingReportsChanges()
    {
        using var root = new TempDirectory();
        var file = root.File("state", "1");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var options = new RunicPathWatcherOptions { UsePolling = true, TimeProvider = tester.Time };
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = RunicPathWatcher.Start(context, [RunicWatchTarget.File(file)], _ => changed.TrySetResult(), options: options);

        File.WriteAllText(file, "22");
        tester.Time.Advance(options.PollingInterval);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task TriggerReportsAPathTheTestCannotChange()
    {
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var system = RunicWatchTarget.Link("/run/current-system");
        var refreshed = 0;
        using var watcher = RunicPathWatcher.Start(context, [system], _ => refreshed++, _ => { }, tester.Options);

        var change = await tester.TriggerAsync("/run/current-system");
        Require(change.Contains(system) && refreshed == 1, "The triggered change did not reach the ViewModel.");
        try
        {
            await tester.TriggerAsync("/nowhere");
            throw new InvalidOperationException("Triggering an unwatched path did not fail.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("No active watcher", StringComparison.Ordinal)) { }
    }

    private static void CreateLink(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
        {
            Skip.Test("Creating symbolic links needs Developer Mode or elevation on Windows.");
        }
    }

    // Writes the new link next to the old one and renames it over, which replaces the link in one step.
    private static void ReplaceLink(string link, string target)
    {
        var temporary = link + ".tmp";
        File.CreateSymbolicLink(temporary, target);
        File.Move(temporary, link, overwrite: true);
    }

    private static void Commit(string repository, string message)
    {
        File.WriteAllText(Path.Combine(repository, "file.txt"), message);
        Require(Git(repository, "add", "file.txt") && Git(repository, "commit", "--quiet", "-m", message), $"git commit {message} failed.");
    }

    private static bool Git(string repository, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = repository, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var option in new[] { "-c", "user.name=Runic", "-c", "user.email=runic@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" })
            info.ArgumentList.Add(option);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        try
        {
            using var process = Process.Start(info)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void Run(string directory, string command, params string[] arguments)
    {
        var info = new ProcessStartInfo(command) { WorkingDirectory = directory };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Require(process.ExitCode == 0, $"{command} {string.Join(' ', arguments)} failed.");
    }

    private static string Describe(RunicPathChange change) => string.Join(", ", change.Targets);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => Root = System.IO.Directory.CreateTempSubdirectory("runic-watch-").FullName;

        public string Root { get; }

        public string Path(string name) => System.IO.Path.Combine(Root, name);

        public string File(string name, string content)
        {
            var path = Path(name);
            System.IO.File.WriteAllText(path, content);
            return path;
        }

        public string Directory(string name) => System.IO.Directory.CreateDirectory(Path(name)).FullName;

        public void Dispose()
        {
            try
            {
                // Git marks its objects read-only, which stops Directory.Delete on Windows.
                foreach (var file in System.IO.Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    System.IO.File.SetAttributes(file, FileAttributes.Normal);
                System.IO.Directory.Delete(Root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
