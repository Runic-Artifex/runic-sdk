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
        var change = await EventAsync(tester, watcher);
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
        var change = await EventAsync(tester, watcher);
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
        var change = await EventAsync(tester, watcher);
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
        var change = await EventAsync(tester, watcher);
        Require(change.Contains(target), $"Expected the file, got {Describe(change)}.");
        RequireEntries(change, (RunicPathChangeKind.Changed, file));

        File.Delete(file);
        RequireEntries(await EventAsync(tester, watcher), (RunicPathChangeKind.Deleted, file));
        File.WriteAllText(file, "{}");
        RequireEntries(await EventAsync(tester, watcher), (RunicPathChangeKind.Created, file));
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
        var added = await EventAsync(tester, watcher);
        Require(added.Contains(target), $"Expected the added entry, got {Describe(added)}.");
        RequireEntries(added, (RunicPathChangeKind.Created, Path.Combine(profiles, "system-2-link")));

        // Entries that don't match the pattern, and writes to matching entries, are not reported.
        File.WriteAllText(Path.Combine(profiles, "unrelated"), "x");
        File.WriteAllText(Path.Combine(profiles, "system-2-link"), "22");
        await Task.Delay(Settle);
        var ignored = await tester.DrainAsync();
        Require(ignored.Count == 0, $"Unrelated changes reported {ignored.Count} change(s).");

        File.Delete(Path.Combine(profiles, "system-1-link"));
        var removed = await EventAsync(tester, watcher);
        Require(removed.Contains(target), $"Expected the removed entry, got {Describe(removed)}.");
        RequireEntries(removed, (RunicPathChangeKind.Deleted, Path.Combine(profiles, "system-1-link")));

        // A rename is the old name removed and the new one added, in ordinal order of the names.
        File.Move(Path.Combine(profiles, "system-2-link"), Path.Combine(profiles, "system-3-link"));
        var renamed = await EventAsync(tester, watcher);
        RequireEntries(renamed, (RunicPathChangeKind.Deleted, Path.Combine(profiles, "system-2-link")),
            (RunicPathChangeKind.Created, Path.Combine(profiles, "system-3-link")));
    }

    // W310-003: a file manager watches the directory it shows while a transfer writes into it. The engine writes each
    // file under a hidden temporary name and renames it into place, so a pattern for the real names sees each file
    // once it is complete, and never a partial one.
    [Test]
    public async Task TransferredEntriesAreReported()
    {
        using var root = new TempDirectory();
        var source = root.Directory("source");
        root.File(Path.Combine("source", "report.txt"), new string('r', 50_000));
        var shown = root.Directory("shown");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var target = RunicWatchTarget.Directory(shown, "*.txt");
        using var watcher = RunicPathWatcher.Start(context, [target], _ => { }, options: tester.Options);

        var result = await Runic.Platform.Runtime.LocalFileTransfers.Create().CopyAsync(
            new Runic.Platform.FileTransferRequest([Path.Combine(source, "report.txt")], shown));
        Require(result is Runic.Platform.PlatformResult<Runic.Platform.FileTransferSummary>.Success, $"The copy failed: {result}.");
        var added = await EventAsync(tester, watcher);
        Require(added.Contains(target), $"Expected the copied entry, got {Describe(added)}.");
        Require(new FileInfo(Path.Combine(shown, "report.txt")).Length == 50_000, "The reported entry is incomplete.");
    }

    // #302: a program that updates a record atomically writes a temporary file and renames it over the record. The
    // directory's entries keep their names, so Directory does not report it; Files does.
    [Test]
    public async Task FilesReplacedByRenameAreReported()
    {
        using var root = new TempDirectory();
        var records = root.Directory("operations");
        var record = root.File(Path.Combine("operations", "1.json"), "{\"status\":\"running\"}");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var files = RunicWatchTarget.Files(records, "*.json");
        var names = RunicWatchTarget.Directory(records, "*.json");
        using var watcher = RunicPathWatcher.Start(context, [names, files], _ => { }, options: tester.Options);

        // The same length and write time as the record it replaces: only the event tells them apart.
        Replace(record, "{\"status\":\"stopped\"}", keepWriteTime: true);
        var replaced = await EventAsync(tester, watcher);
        var rest = await tester.DrainAsync();
        var all = rest.Prepend(replaced).SelectMany(change => change.Targets).ToHashSet();
        Require(all.SetEquals([files]), $"Expected only the Files target for a replaced entry, got {string.Join(", ", all)}.");
        // Its size and write time did not change, so the entry is the target itself.
        RequireEntries(replaced, (RunicPathChangeKind.Changed, Path.GetFullPath(records)));

        // A write in place counts too, as for File.
        using (var stream = new FileStream(record, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            stream.Write("{"u8);
        var written = await EventAsync(tester, watcher);
        Require(written.Contains(files) && !written.Contains(names), $"Expected the written entry, got {Describe(written)}.");
        // The entry, or the target itself where the file system kept the write time.
        Require(written.Entries.Count == 1 && written.Entries[0].Kind == RunicPathChangeKind.Changed
                && (written.Entries[0].Path == record || written.Entries[0].Path == Path.GetFullPath(records)),
            $"Expected the written entry changed, got {Describe(written)}.");

        // A new entry is reported by both, and files that don't match the pattern by neither.
        var added = Path.Combine(records, "2.json");
        File.WriteAllText(Path.Combine(records, "2.json.tmp"), "{}");
        await Task.Delay(Settle);
        Require((await tester.DrainAsync()).Count == 0, "A temporary file that does not match the pattern was reported.");
        File.Move(Path.Combine(records, "2.json.tmp"), added);
        var created = await EventAsync(tester, watcher);
        all = (await tester.DrainAsync()).Prepend(created).SelectMany(change => change.Targets).ToHashSet();
        Require(all.SetEquals([names, files]), $"Expected both targets for a new entry, got {string.Join(", ", all)}.");
        var entries = (await tester.DrainAsync()).Prepend(created).SelectMany(change => change.Entries).ToArray();
        Require(entries.Length == 2 && entries.All(entry => entry.Kind == RunicPathChangeKind.Created && entry.Path == added)
                && entries.Select(entry => entry.Target).ToHashSet().SetEquals([names, files]),
            $"Expected the new entry created for both targets, got {string.Join(", ", entries.Select(entry => $"{entry.Target}: {entry}"))}.");

        // Without events, the backstop read compares each entry's size and write time.
        watcher.IgnoreEvents = true;
        Replace(added, "{\"status\":\"running\",\"step\":2}");
        var polled = await NextOrDescribeAsync(tester, watcher, TimeSpan.FromSeconds(30));
        Require(polled.Contains(files) && !polled.Contains(names) && !polled.ByEvent, $"Expected the backstop read to report the replaced entry, got {Describe(polled)}.");
    }

    // Writes a temporary file beside path and renames it over path, as atomic writers do.
    private static void Replace(string path, string text, bool keepWriteTime = false)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        if (keepWriteTime) File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(path));
        File.Move(temporary, path, overwrite: true);
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

        // FSEvents reports nothing for the watched directory itself; the parent's watch reports the delete.
        Directory.Delete(directory, recursive: true);
        Require((await EventAsync(tester, watcher)).Contains(target), "Deleting the directory was not reported.");
        await Task.Delay(Settle);
        await tester.DrainAsync();
        Require(errors.Contains(RunicPathWatchErrorKind.Missing), $"Expected a Missing error, got [{string.Join(", ", errors)}].");

        // While the directory is missing the target is polled; the parent's event reports its return first.
        Directory.CreateDirectory(directory);
        File.WriteAllText(file, "2");
        // Let the event arrive while the test clock stands still, before the next 2-second poll can run.
        await Task.Delay(Settle);
        var returned = await NextOrDescribeAsync(tester, watcher);
        Require(returned.Contains(target), "Recreating the directory was not reported.");
        Require(returned.ByEvent, $"The directory's return was found by polling, not by an event. {watcher.Describe()}");

        // The watch is back: a write is reported by an event.
        await Task.Delay(Settle);
        await tester.DrainAsync();
        File.WriteAllText(file, "33");
        Require((await EventAsync(tester, watcher)).Contains(target), "A write after the directory returned was not reported.");
    }

    [Test]
    public async Task DirectoryReplacedWithoutAReadIsWatchedAgain()
    {
        using var root = new TempDirectory();
        var directory = root.Directory("checkout");
        var file = root.File(Path.Combine("checkout", "current"), "1");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var target = RunicWatchTarget.File(file);
        using var watcher = RunicPathWatcher.Start(context, [target], _ => { }, _ => { }, tester.Options);

        // As `rm -rf checkout && git clone`: the directory is replaced before the watcher reads anything.
        Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        File.WriteAllText(file, "2");
        // A watcher started now must not join the watch of the deleted directory.
        var other = RunicWatchTarget.File(Path.Combine(directory, "other"));
        using var late = RunicPathWatcher.Start(context, [other], _ => { }, _ => { }, tester.Options);
        Require((await EventAsync(tester, watcher)).Contains(target), "Replacing the directory was not reported.");
        await Task.Delay(Settle);
        await tester.DrainAsync();

        File.WriteAllText(file, "33");
        Require((await EventAsync(tester, watcher)).Contains(target), "A write in the replaced directory was not reported by an event.");
        await Task.Delay(Settle);
        await tester.DrainAsync();
        File.WriteAllText(Path.Combine(directory, "other"), "x");
        Require((await EventAsync(tester, late)).Contains(other), "A watcher started after the replacement got no event.");
    }

    [Test]
    public async Task BackstopReadReportsChangesWithoutEvents()
    {
        using var root = new TempDirectory();
        var file = root.File("state", "1");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var target = RunicWatchTarget.File(file);
        using var watcher = RunicPathWatcher.Start(context, [target], _ => { }, options: tester.Options);
        watcher.IgnoreEvents = true;

        File.WriteAllText(file, "22");
        var start = tester.Time.GetUtcNow();
        var change = await NextOrDescribeAsync(tester, watcher, TimeSpan.FromSeconds(30));
        var elapsed = tester.Time.GetUtcNow() - start;
        Require(change.Contains(target) && !change.ByEvent && elapsed >= TimeSpan.FromSeconds(30),
            $"Expected the 30-second backstop read to report the write, got it after {elapsed}.");
    }

    [Test]
    public void DirectoryIdentityMatchesStat()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("Windows identifies a directory by volume serial number and file index; there is no stat command.");
        using var root = new TempDirectory();
        var directory = root.Directory("d");
        var identity = DirectoryIdentity.Read(directory);
        var format = OperatingSystem.IsMacOS() ? new[] { "-f", "%d:%i" } : ["-c", "%d:%i"];
        var expected = Output(root.Root, "stat", [.. format, directory]).Trim();
        Require(identity?.StartsWith(expected + ":", StringComparison.Ordinal) == true, $"DirectoryIdentity read {identity}, stat {expected}.");
        Directory.Delete(directory);
        Directory.CreateDirectory(directory);
        Require(DirectoryIdentity.Read(directory) != identity, "A recreated directory kept its identity.");
    }

    // Awaits a change that an event reported: every target is watched, and an event, not a poll or the backstop
    // read, led to reading the target that changed. Disabling events, or a watch that died, fails it.
    private static async Task<RunicPathChange> EventAsync(RunicPathWatchTester tester, RunicPathWatcher watcher)
    {
        Require(watcher.IsEventDriven, $"The watcher polls instead of watching. {watcher.Describe()}");
        var change = await NextOrDescribeAsync(tester, watcher);
        Require(change.ByEvent, $"A poll, not an event, reported the change. {watcher.Describe()}");
        return change;
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
        await EventAsync(tester, watcher);
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
        Require((await EventAsync(tester, watcher)).Contains(target), "A commit was not reported.");

        // Packing refs moves nothing.
        Require(Git(repository, "pack-refs", "--all"), "git pack-refs failed.");
        await Task.Delay(Settle);
        var packed = await tester.DrainAsync();
        Require(packed.Count == 0, $"Packing refs reported {packed.Count} change(s).");

        // The branch is now in packed-refs; a commit writes a loose ref again.
        Commit(repository, "three");
        Require((await EventAsync(tester, watcher)).Contains(target), "A commit after pack-refs was not reported.");

        Require(Git(repository, "switch", "--quiet", "-c", "feature/x"), "git switch failed.");
        Require((await EventAsync(tester, watcher)).Contains(target), "Switching branch was not reported.");

        Require(Git(repository, "reset", "--quiet", "--hard", "HEAD~1"), "git reset failed.");
        Require((await EventAsync(tester, watcher)).Contains(target), "A reset was not reported.");
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
        Require((await EventAsync(tester, watcher)).Contains(target), "A commit in the worktree was not reported.");
    }

    [Test]
    public async Task GitReftableWorktreeHeadMovesAreReported()
    {
        using var root = new TempDirectory();
        var repository = root.Directory("checkout");
        if (!Git(repository, "init", "--quiet", "--initial-branch=main", "--ref-format=reftable"))
            Skip.Test("This git does not support the reftable ref format (git 2.45 or later).");
        Commit(repository, "one");
        Commit(repository, "two");
        var worktree = root.Path("worktree");
        Require(Git(repository, "worktree", "add", "--quiet", "-b", "side", worktree), "git worktree add failed.");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var target = RunicWatchTarget.GitHead(worktree);
        using var watcher = RunicPathWatcher.Start(context, [target], _ => { }, options: tester.Options);

        Require(Git(worktree, "switch", "--quiet", "-c", "other"), "git switch failed.");
        Require((await EventAsync(tester, watcher)).Contains(target), "Switching branch in a reftable worktree was not reported.");
        Require(Git(worktree, "switch", "--quiet", "--detach", "HEAD~1"), "git switch --detach failed.");
        Require((await EventAsync(tester, watcher)).Contains(target), "Detaching HEAD in a reftable worktree was not reported.");
        Require(Git(worktree, "reset", "--quiet", "--hard", "main"), "git reset failed.");
        Require((await EventAsync(tester, watcher)).Contains(target), "A reset in a reftable worktree was not reported.");
    }

    [Test]
    public async Task GitHeadOfANonRepositoryReportsItAndWaitsForOne()
    {
        using var root = new TempDirectory();
        var directory = root.Directory("not-yet");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var errors = new List<RunicPathWatchError>();
        var target = RunicWatchTarget.GitHead(directory);
        using var watcher = RunicPathWatcher.Start(context, [target], _ => { }, errors.Add, tester.Options);
        await Task.Delay(Settle);
        await tester.DrainAsync();
        Require(errors.Count == 1 && errors[0].Kind == RunicPathWatchErrorKind.NotGitRepository,
            $"Expected one NotGitRepository error, got [{string.Join(", ", errors.Select(error => error.Kind))}].");

        if (!Git(directory, "init", "--quiet", "--initial-branch=main")) Skip.Test("git is not installed.");
        Require((await EventAsync(tester, watcher)).Contains(target), "The new repository was not reported.");
    }

    [Test]
    public async Task GitHeadFollowsSymbolicLooseRefs()
    {
        using var root = new TempDirectory();
        var repository = root.Directory("checkout");
        if (!Git(repository, "init", "--quiet", "--initial-branch=main")) Skip.Test("git is not installed.");
        Commit(repository, "one");
        // HEAD -> refs/heads/alias -> refs/heads/main.
        Require(Git(repository, "symbolic-ref", "refs/heads/alias", "refs/heads/main")
            && Git(repository, "symbolic-ref", "HEAD", "refs/heads/alias"), "git symbolic-ref failed.");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var target = RunicWatchTarget.GitHead(repository);
        using var watcher = RunicPathWatcher.Start(context, [target], _ => { }, options: tester.Options);

        Require(Git(repository, "update-ref", "refs/heads/main", "HEAD", "HEAD"), "git update-ref failed.");
        Commit(repository, "two");
        Require((await EventAsync(tester, watcher)).Contains(target), "A commit through a symbolic ref was not reported.");
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
    public async Task DisposingTheTesterStopsItsWatchers()
    {
        using var root = new TempDirectory();
        var file = root.File("state", "1");
        await using var context = new RunicModelContext();
        var tester = new RunicPathWatchTester();
        var calls = 0;
        using var watcher = RunicPathWatcher.Start(context, [RunicWatchTarget.File(file)], _ => calls++, options: tester.Options);
        Require(tester.ActiveWatchers == 1, "The tester did not see the watcher start.");

        tester.Dispose();
        Require(tester.ActiveWatchers == 0, "Disposing the tester did not stop its watcher.");
        File.WriteAllText(file, "2");
        await Task.Delay(Settle);
        tester.Time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(Settle);
        Require(calls == 0, $"A watcher of a disposed tester reported {calls} change(s).");
        // Detached: a watcher started later with the options is not tracked.
        using var later = RunicPathWatcher.Start(context, [RunicWatchTarget.File(file)], _ => { }, options: tester.Options);
        Require(tester.ActiveWatchers == 0, "A disposed tester tracked a new watcher.");
        try
        {
            await tester.NextChangeAsync();
            throw new InvalidOperationException("NextChangeAsync of a disposed tester did not fail.");
        }
        catch (ObjectDisposedException) { }
        tester.Dispose();
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

    private static string Output(string directory, string command, string[] arguments)
    {
        var info = new ProcessStartInfo(command) { WorkingDirectory = directory, RedirectStandardOutput = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Require(process.ExitCode == 0, $"{command} {string.Join(' ', arguments)} failed.");
        return output;
    }

    private static void Run(string directory, string command, params string[] arguments)
    {
        var info = new ProcessStartInfo(command) { WorkingDirectory = directory };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Require(process.ExitCode == 0, $"{command} {string.Join(' ', arguments)} failed.");
    }

    private static string Describe(RunicPathChange change) =>
        $"{string.Join(", ", change.Targets)} [{string.Join(", ", change.Entries)}]";

    private static void RequireEntries(RunicPathChange change, params (RunicPathChangeKind Kind, string Path)[] expected) =>
        Require(change.Entries.Select(entry => (entry.Kind, entry.Path)).SequenceEqual(expected),
            $"Expected entries [{string.Join(", ", expected.Select(entry => $"{entry.Kind} {entry.Path}"))}], got {Describe(change)}.");

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
