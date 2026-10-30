using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Runic.Platform;
using Runic.Platform.Runtime;
using TUnit.Core;

// Real file-system cases in temporary directories. The native CI job also runs this class on Windows and macOS.
internal sealed partial class FileTransferTests
{
    private static readonly DateTime Earlier = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);

    // A small block makes a file of a few kilobytes take several blocks, as a large one does.
    private static FileTransferEngine Engine() => new(blockSize: 4096);

    [Test, DisplayName("transfers: a copy keeps content, structure, times and permissions, and reports progress to the end")]
    public async Task CopyTreeAsync()
    {
        using var scratch = new Scratch();
        string source = scratch.Directory("source/tree");
        scratch.File("source/tree/a.txt", "alpha");
        scratch.File("source/tree/sub/b.bin", new string('b', 20_000));
        scratch.Directory("source/tree/empty");
        File.SetLastWriteTimeUtc(Path.Combine(source, "a.txt"), Earlier);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(source, "a.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            File.SetUnixFileMode(Path.Combine(source, "sub"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            File.CreateSymbolicLink(Path.Combine(source, "link"), "a.txt");
        }
        Directory.SetLastWriteTimeUtc(source, Earlier);
        string destination = scratch.Directory("destination");
        var progress = new Recorder();

        var result = await Engine().CopyAsync(new FileTransferRequest([source], destination) { Progress = progress });
        int items = OperatingSystem.IsWindows() ? 5 : 6; // tree, a.txt, sub, b.bin, empty and the link
        Check(result is PlatformResult<FileTransferSummary>.Success { Value: var summary } && summary.Transferred == items && summary.Skipped == 0
            && summary.Bytes == 20_005, $"summary: {result}");
        string copy = Path.Combine(destination, "tree");
        Check(File.ReadAllText(Path.Combine(copy, "a.txt")) == "alpha" && File.ReadAllText(Path.Combine(copy, "sub", "b.bin")).Length == 20_000
            && Directory.Exists(Path.Combine(copy, "empty")), "content and structure");
        Check(File.GetLastWriteTimeUtc(Path.Combine(copy, "a.txt")) == Earlier && Directory.GetLastWriteTimeUtc(copy) == Earlier, "times");
        Check(File.Exists(Path.Combine(source, "a.txt")), "a copy keeps the source");
        if (!OperatingSystem.IsWindows())
        {
            Check(File.GetUnixFileMode(Path.Combine(copy, "a.txt")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead)
                && File.GetUnixFileMode(Path.Combine(copy, "sub")) == File.GetUnixFileMode(Path.Combine(source, "sub")), "permissions");
            Check(new FileInfo(Path.Combine(copy, "link")).LinkTarget == "a.txt", "a link is copied as a link");
        }
        Check(Directory.EnumerateFileSystemEntries(destination, ".*runic-*", SearchOption.AllDirectories).Any() is false, "no staging entry is left");
        var last = progress.Reports[^1];
        Check(last.CompletedItems == items && last.TotalItems == items && last.CompletedBytes == 20_005 && last.TotalBytes == 20_005 && last.CurrentPath is null,
            $"final progress: {last}");
        Check(progress.Reports[0].CompletedItems == 0 && progress.Reports[0].TotalBytes == 20_005, "the first report has the totals");
        Check(progress.Reports.Zip(progress.Reports.Skip(1)).All(pair => pair.Second.CompletedBytes >= pair.First.CompletedBytes), "progress never goes back");
    }

    [Test, DisplayName("transfers: a move renames within a file system")]
    public async Task MoveAsync()
    {
        using var scratch = new Scratch();
        string a = scratch.File("source/a.txt", "a");
        string tree = scratch.Directory("source/tree");
        scratch.File("source/tree/b.txt", "b");
        string destination = scratch.Directory("destination");
        var result = await Engine().MoveAsync(new FileTransferRequest([a, tree], destination));
        Check(result is PlatformResult<FileTransferSummary>.Success { Value: { Transferred: 3, Skipped: 0, Bytes: 2 } }, $"summary: {result}");
        Check(!File.Exists(a) && !Directory.Exists(tree) && File.ReadAllText(Path.Combine(destination, "a.txt")) == "a"
            && File.ReadAllText(Path.Combine(destination, "tree", "b.txt")) == "b", "moved");

        // Moving an item into the directory it is in does nothing.
        string again = Path.Combine(destination, "a.txt");
        Check(await Engine().MoveAsync(new FileTransferRequest([again], destination)) is PlatformResult<FileTransferSummary>.Success { Value.Skipped: 1 }
            && File.Exists(again), "a move onto itself is skipped");
        // Copying it there makes a second one.
        Check(await Engine().CopyAsync(new FileTransferRequest([again], destination)) is PlatformResult<FileTransferSummary>.Success { Value.Transferred: 1 }
            && File.ReadAllText(Path.Combine(destination, "a (2).txt")) == "a", "a copy onto itself keeps both");
    }

    // A second file system: /dev/shm is a tmpfs on Linux distributions and CI runners.
    [Test, DisplayName("transfers: a move to another file system copies, then removes each source once its item is complete")]
    public async Task MoveAcrossFileSystemsAsync()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm")) Skip.Test("Needs /dev/shm on Linux.");
        using var scratch = new Scratch();
        using var other = new Scratch("/dev/shm");
        string tree = other.Directory("tree");
        other.File("tree/a.txt", "a");
        other.File("tree/sub/b.txt", new string('b', 9000));
        string destination = scratch.Directory("destination");
        scratch.File("destination/tree/sub/b.txt", "existing");
        File.SetLastWriteTimeUtc(Path.Combine(tree, "a.txt"), Earlier);

        // Merge the directories and skip the one file that exists: the skipped source and its directories stay.
        FileConflictHandler handler = (conflict, _) =>
            ValueTask.FromResult(new FileConflictResolution(conflict.CanMerge ? FileConflictAction.Merge : FileConflictAction.Skip));
        var result = await Engine().MoveAsync(new FileTransferRequest([tree], destination) { ResolveConflict = handler });
        Check(result is PlatformResult<FileTransferSummary>.Success { Value: { Transferred: 3, Skipped: 1 } }, $"summary: {result}");
        Check(File.ReadAllText(Path.Combine(destination, "tree", "a.txt")) == "a" && File.GetLastWriteTimeUtc(Path.Combine(destination, "tree", "a.txt")) == Earlier
            && File.ReadAllText(Path.Combine(destination, "tree", "sub", "b.txt")) == "existing", "copied and kept");
        Check(!File.Exists(Path.Combine(tree, "a.txt")) && File.Exists(Path.Combine(tree, "sub", "b.txt")), "the moved source is gone, the skipped one stays");

        File.Delete(Path.Combine(tree, "sub", "b.txt"));
        Check(await Engine().MoveAsync(new FileTransferRequest([tree], scratch.Directory("second"))) is PlatformResult<FileTransferSummary>.Success
            && !Directory.Exists(tree) && Directory.Exists(Path.Combine(scratch.Root, "second", "tree", "sub")), "a whole tree moves and its source goes");
    }

    [Test, DisplayName("transfers: conflicts stop without a handler, and skip, replace, keep both and apply to all")]
    public async Task ConflictsAsync()
    {
        using var scratch = new Scratch();
        string source = scratch.File("source/a.txt", "new");
        string destination = scratch.Directory("destination");
        string existing = scratch.File("destination/a.txt", "old");
        File.SetLastWriteTimeUtc(existing, Earlier);

        Check(await Engine().CopyAsync(new FileTransferRequest([source], destination)) is PlatformResult<FileTransferSummary>.Failed { Code: PlatformFailureCode.Conflict }
            && File.ReadAllText(existing) == "old", "no handler: nothing is replaced");

        var asked = new List<FileTransferConflict>();
        FileConflictHandler Answer(FileConflictAction action, bool all = false) => (conflict, _) =>
        {
            asked.Add(conflict);
            return ValueTask.FromResult(new FileConflictResolution(action) { ApplyToAll = all });
        };
        Check(await Engine().CopyAsync(new FileTransferRequest([source], destination) { ResolveConflict = Answer(FileConflictAction.Skip) })
            is PlatformResult<FileTransferSummary>.Success { Value: { Transferred: 0, Skipped: 1 } } && File.ReadAllText(existing) == "old", "skip");
        var conflict = asked.Single();
        Check(conflict.SourcePath == source && conflict.DestinationPath == existing && conflict.Destination is { IsDirectory: false, Length: 3 }
            && conflict.Destination.LastWriteTime == new DateTimeOffset(Earlier) && conflict.Source.Length == 3 && !conflict.CanMerge, $"the conflict: {conflict}");

        Check(await Engine().CopyAsync(new FileTransferRequest([source], destination) { ResolveConflict = Answer(FileConflictAction.KeepBoth) })
            is PlatformResult<FileTransferSummary>.Success && File.ReadAllText(Path.Combine(destination, "a (2).txt")) == "new", "keep both");
        Check(await Engine().CopyAsync(new FileTransferRequest([source], destination) { ResolveConflict = Answer(FileConflictAction.KeepBoth) })
            is PlatformResult<FileTransferSummary>.Success && File.Exists(Path.Combine(destination, "a (3).txt")), "keep both again");
        Check(await Engine().CopyAsync(new FileTransferRequest([source], destination) { ResolveConflict = Answer(FileConflictAction.Replace) })
            is PlatformResult<FileTransferSummary>.Success && File.ReadAllText(existing) == "new", "replace");

        // A directory replaces a file, and a file a directory with its contents.
        string folder = scratch.Directory("source/folder");
        scratch.File("source/folder/inside.txt", "inside");
        scratch.File("destination/folder", "a file");
        Check(await Engine().CopyAsync(new FileTransferRequest([folder], destination) { ResolveConflict = Answer(FileConflictAction.Replace) })
            is PlatformResult<FileTransferSummary>.Success && File.ReadAllText(Path.Combine(destination, "folder", "inside.txt")) == "inside", "a directory replaces a file");
        string flat = scratch.File("other/folder", "flat");
        Check(await Engine().MoveAsync(new FileTransferRequest([flat], destination) { ResolveConflict = Answer(FileConflictAction.Replace) })
            is PlatformResult<FileTransferSummary>.Success && File.ReadAllText(Path.Combine(destination, "folder")) == "flat" && !File.Exists(flat),
            "a file replaces a directory");
        Check(Directory.EnumerateFileSystemEntries(destination, ".*runic-*").Any() is false, "nothing replaced is left aside");

        // Apply to all: one answer for every file conflict.
        asked.Clear();
        string many = scratch.Directory("many");
        foreach (string name in new[] { "x.txt", "y.txt", "z.txt" }) { scratch.File("many/" + name, "1"); scratch.File("destination/" + name, "0"); }
        var sources = Directory.GetFiles(many).Order(StringComparer.Ordinal).ToImmutableArray();
        Check(await Engine().CopyAsync(new FileTransferRequest(sources, destination) { ResolveConflict = Answer(FileConflictAction.Replace, all: true) })
            is PlatformResult<FileTransferSummary>.Success { Value.Transferred: 3 } && asked.Count == 1
            && File.ReadAllText(Path.Combine(destination, "z.txt")) == "1", "asked once for all");

        // An answer that does not fit the conflict is a programming error.
        Check(await Catch(() => Engine().CopyAsync(new FileTransferRequest([source], destination) { ResolveConflict = Answer(FileConflictAction.Merge) }).AsTask())
            is InvalidOperationException && File.ReadAllText(existing) == "new", "merge of a file");
    }

    [Test, DisplayName("transfers: a merge asks inside, and cancelling it puts back what the item did")]
    public async Task MergeAndCancelAsync()
    {
        using var scratch = new Scratch();
        string tree = scratch.Directory("source/tree");
        scratch.File("source/tree/one.txt", "1");
        scratch.File("source/tree/sub/two.txt", "2");
        scratch.File("source/tree/sub/three.txt", "3");
        string destination = scratch.Directory("destination");
        scratch.File("destination/tree/sub/two.txt", "old");
        string before = Listing(destination);

        // Merge both directories, then cancel at the file: one.txt was already moved, and comes back.
        var asked = new List<string>();
        FileConflictHandler cancel = (conflict, _) =>
        {
            asked.Add(Path.GetFileName(conflict.DestinationPath));
            return ValueTask.FromResult(new FileConflictResolution(conflict.CanMerge ? FileConflictAction.Merge : FileConflictAction.Cancel));
        };
        Check(await Engine().MoveAsync(new FileTransferRequest([tree], destination) { ResolveConflict = cancel })
            is PlatformResult<FileTransferSummary>.Failed { Code: PlatformFailureCode.UserDismissed }, "cancelled");
        Check(asked.SequenceEqual(["tree", "sub", "two.txt"]), $"asked: {string.Join(", ", asked)}");
        Check(Listing(destination) == before && File.ReadAllText(Path.Combine(destination, "tree", "sub", "two.txt")) == "old", "the destination is as it was");
        Check(File.Exists(Path.Combine(tree, "one.txt")) && File.Exists(Path.Combine(tree, "sub", "three.txt")), "the moved files are back");

        // A replaced directory comes back too when the item fails later.
        FileConflictHandler merge = (conflict, _) =>
            ValueTask.FromResult(new FileConflictResolution(conflict.CanMerge ? FileConflictAction.Merge : FileConflictAction.Replace));
        var result = await Engine().CopyAsync(new FileTransferRequest([tree], destination) { ResolveConflict = merge });
        Check(result is PlatformResult<FileTransferSummary>.Success { Value: { Transferred: 5 } } && File.ReadAllText(Path.Combine(destination, "tree", "sub", "two.txt")) == "2",
            $"merged: {result}");
    }

    [Test, DisplayName("transfers: cancellation removes the partial file and keeps the source")]
    public async Task CancellationAsync()
    {
        using var scratch = new Scratch();
        string big = scratch.File("source/big.bin", new string('x', 200_000));
        scratch.File("source/later.txt", "later");
        string destination = scratch.Directory("destination");
        using var cancel = new CancellationTokenSource();
        var progress = new Recorder(report => { if (report.CompletedBytes > 0) cancel.Cancel(); });
        var request = new FileTransferRequest([big, Path.Combine(scratch.Root, "source", "later.txt")], destination) { Progress = progress };
        Check(await Catch(() => new FileTransferEngine(new SteppingTime(), blockSize: 4096).CopyAsync(request, cancel.Token).AsTask()) is OperationCanceledException,
            "cancellation throws");
        Check(Directory.GetFileSystemEntries(destination).Length == 0, $"nothing is left: {Listing(destination)}");
        Check(new FileInfo(big).Length == 200_000, "the source is intact");

        using var before = new CancellationTokenSource();
        before.Cancel();
        Check(await Catch(() => Engine().CopyAsync(request, before.Token).AsTask()) is OperationCanceledException, "cancelled before it starts");
    }

    [Test, DisplayName("transfers: a failure inside an item removes what it created")]
    public async Task FailureUndoesTheItemAsync()
    {
        if (OperatingSystem.IsWindows() || GetEffectiveUserId() == 0) { Skip.Test("Needs Unix permissions and a user that is not root."); return; }
        using var scratch = new Scratch();
        string tree = scratch.Directory("source/tree");
        scratch.File("source/tree/a.txt", "a");
        string secret = scratch.File("source/tree/z-secret.txt", "s");
        File.SetUnixFileMode(secret, 0);
        string done = scratch.File("source/done.txt", "done");
        string destination = scratch.Directory("destination");
        try
        {
            var result = await Engine().CopyAsync(new FileTransferRequest([done, tree], destination));
            Check(result is PlatformResult<FileTransferSummary>.Failed { Code: PlatformFailureCode.PermissionDenied }, $"refused: {result}");
            Check(File.Exists(Path.Combine(destination, "done.txt")) && !Directory.Exists(Path.Combine(destination, "tree")),
                $"the completed item stays, the failed one is gone: {Listing(destination)}");
        }
        finally { File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    [Test, DisplayName("transfers: special files are skipped without being opened")]
    public async Task SpecialFilesAsync()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("Windows paths reach no named pipes.");
        using var scratch = new Scratch();
        string tree = scratch.Directory("source/tree");
        scratch.File("source/tree/a.txt", "a");
        Check(MakeFifo(Path.Combine(tree, "pipe"), 0x180) == 0, "mkfifo");
        var result = await Engine().CopyAsync(new FileTransferRequest([tree], scratch.Directory("destination"))).AsTask().WaitScaledAsync(TimeSpan.FromSeconds(10));
        Check(result is PlatformResult<FileTransferSummary>.Success { Value: { Transferred: 2, Skipped: 1 } }, $"the pipe is skipped: {result}");
    }

    [Test, DisplayName("transfers: the handler runs on the context the transfer started on")]
    public async Task HandlerContextAsync()
    {
        using var scratch = new Scratch();
        string source = scratch.File("source/a.txt", "a");
        string destination = scratch.Directory("destination");
        scratch.File("destination/a.txt", "b");
        using var context = new QueueContext();
        bool onContext = false;
        FileConflictHandler handler = async (_, cancellationToken) =>
        {
            onContext = SynchronizationContext.Current == context;
            await Task.Yield();
            return new FileConflictResolution(FileConflictAction.Skip);
        };
        var completion = new TaskCompletionSource<PlatformResult<FileTransferSummary>>();
        async Task StartAsync()
        {
            try { completion.SetResult(await Engine().CopyAsync(new FileTransferRequest([source], destination) { ResolveConflict = handler })); }
            catch (Exception error) { completion.SetException(error); }
        }
        context.Post(_ => _ = StartAsync(), null);
        Check(await completion.Task.WaitScaledAsync(TimeSpan.FromSeconds(10)) is PlatformResult<FileTransferSummary>.Success && onContext, "asked on the context");
    }

    [Test, DisplayName("transfers: requests are checked, and missing paths are NotFound")]
    public async Task ValidationAsync()
    {
        using var scratch = new Scratch();
        string tree = scratch.Directory("tree");
        string destination = scratch.Directory("destination");
        var engine = Engine();
        Check(await Catch(() => engine.CopyAsync(new FileTransferRequest([], destination)).AsTask()) is ArgumentException, "no source");
        Check(await Catch(() => engine.CopyAsync(new FileTransferRequest(["relative"], destination)).AsTask()) is ArgumentException, "a relative source");
        Check(await Catch(() => engine.CopyAsync(new FileTransferRequest([tree], "relative")).AsTask()) is ArgumentException, "a relative destination");
        Check(await Catch(() => engine.CopyAsync(new FileTransferRequest([tree], Path.Combine(tree, "inner"))).AsTask()) is ArgumentException, "into itself");
        Check(await Catch(() => engine.MoveAsync(new FileTransferRequest([tree], tree)).AsTask()) is ArgumentException, "onto itself");
        Check(await engine.CopyAsync(new FileTransferRequest([Path.Combine(scratch.Root, "missing")], destination))
            is PlatformResult<FileTransferSummary>.Failed { Code: PlatformFailureCode.NotFound }, "a missing source");
        Check(await engine.CopyAsync(new FileTransferRequest([tree], Path.Combine(scratch.Root, "missing")))
            is PlatformResult<FileTransferSummary>.Failed { Code: PlatformFailureCode.NotFound }, "a missing destination");

        var unavailable = DesktopFileTransfers.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        Check(await unavailable.MoveAsync(new FileTransferRequest([tree], destination)) is PlatformResult<FileTransferSummary>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured },
            "unavailable");
        Check(await Catch(() => unavailable.CopyAsync(new FileTransferRequest([], destination)).AsTask()) is ArgumentException, "the unavailable transfers check the request");
        Check(LocalFileTransfers.Create() is FileTransferEngine, "the factory");
        Check(FileTransferEngine.FreeName(destination, "report.tar.gz", false) == "report.tar (2).gz" && FileTransferEngine.FreeName(destination, ".bashrc", false) == ".bashrc (2)"
            && FileTransferEngine.FreeName(destination, "v1.2", true) == "v1.2 (2)", "free names");
    }

    private static string Listing(string root) => string.Join("\n", Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path)).Order(StringComparer.Ordinal));

    private sealed class Recorder(Action<FileTransferProgress>? onReport = null) : IProgress<FileTransferProgress>
    {
        private readonly ConcurrentQueue<FileTransferProgress> _reports = new();
        internal List<FileTransferProgress> Reports => [.. _reports];
        public void Report(FileTransferProgress value) { _reports.Enqueue(value); onReport?.Invoke(value); }
    }

    // Each reading is a second later, so every block is reported.
    private sealed class SteppingTime : TimeProvider
    {
        private long _ticks;
        public override long GetTimestamp() => Interlocked.Add(ref _ticks, TimestampFrequency);
    }

    // A single-threaded context, as a model context is.
    private sealed class QueueContext : SynchronizationContext, IDisposable
    {
        private readonly System.Threading.Channels.Channel<(SendOrPostCallback, object?)> _queue = System.Threading.Channels.Channel.CreateUnbounded<(SendOrPostCallback, object?)>();
        private readonly Thread _thread;
        internal QueueContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                while (_queue.Reader.WaitToReadAsync().AsTask().Result)
                    while (_queue.Reader.TryRead(out var item)) item.Item1(item.Item2);
            }) { IsBackground = true };
            _thread.Start();
        }
        public override void Post(SendOrPostCallback d, object? state) => _queue.Writer.TryWrite((d, state));
        public void Dispose() => _queue.Writer.TryComplete();
    }

    private sealed class Scratch : IDisposable
    {
        internal Scratch(string? parent = null) =>
            Root = System.IO.Directory.CreateDirectory(Path.Combine(parent ?? Path.GetTempPath(), "runic-transfer-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        internal string Root { get; }
        internal string Directory(string relative) => System.IO.Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;
        internal string File(string relative, string content)
        {
            string path = Path.GetFullPath(Path.Combine(Root, relative)); // Windows reports paths with its own separators.
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
            return path;
        }
        public void Dispose()
        {
            if (!OperatingSystem.IsWindows())
                foreach (string path in System.IO.Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
                    try { if (new FileInfo(path).LinkTarget is null) System.IO.File.SetUnixFileMode(path, (UnixFileMode)0x1C0); } catch (IOException) { }
            System.IO.Directory.Delete(Root, recursive: true);
        }
    }

    private static async Task<Exception?> Catch(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    [LibraryImport("libc", EntryPoint = "geteuid")] private static partial uint GetEffectiveUserId();
    [LibraryImport("libc", EntryPoint = "mkfifo", StringMarshalling = StringMarshalling.Utf8)] private static partial int MakeFifo(string path, int mode);
}
