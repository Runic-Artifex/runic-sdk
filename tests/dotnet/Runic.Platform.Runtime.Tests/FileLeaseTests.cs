using Runic.Platform;
using Runic.Platform.Runtime;
using TUnit.Core;

internal sealed class FileLeaseTests
{
    [Test, DisplayName("FILE: concrete stream access, staging, conflict and commit cancellation")]
    public async Task FileLeasesAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"runic-leases-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "document.txt");
            await File.WriteAllTextAsync(path, "original");
            var access = new Access();
            var read = new ReadFileLease("document.txt", new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), access);
            var stream = await read.OpenReadAsync();
            await read.DisposeAsync(); await read.DisposeAsync();
            Check(access.Releases == 1 && !stream.CanRead, "Read access and the outstanding stream must close once.");
            await Throws<ObjectDisposedException>(() => read.OpenReadAsync().AsTask());

            // Selection and disposal without commit must leave the target untouched.
            await using (var save = await SaveFileLease.CreateAsync(path, new LocalAtomicFileReplacement()))
            {
                var write = await Begin(save);
                await write.Content.WriteAsync("discard"u8.ToArray());
                Check(await File.ReadAllTextAsync(path) == "original", "Staging changed the target.");
            }
            Check(Directory.GetFiles(directory).Length == 1, "Uncommitted staging leaked.");

            await using (var save = await SaveFileLease.CreateAsync(path, new LocalAtomicFileReplacement()))
            {
                var write = await Begin(save);
                await write.Content.WriteAsync("changed"u8.ToArray());
                Check(await write.CommitAsync() is FileCommitResult.Committed, "Commit failed.");
                await Throws<InvalidOperationException>(() => write.CommitAsync().AsTask());
            }
            Check(await File.ReadAllTextAsync(path) == "changed", "Commit did not replace the target.");

            await using (var save = await SaveFileLease.CreateAsync(path, new LocalAtomicFileReplacement()))
            {
                var write = await Begin(save);
                await File.WriteAllTextAsync(path, "external edit");
                Check(await write.CommitAsync() is FileCommitResult.NotCommitted { Code: PlatformFailureCode.Conflict }, "External edit was overwritten.");
            }
            Check(await File.ReadAllTextAsync(path) == "external edit", "Conflict changed the target.");

            await using (var save = await SaveFileLease.CreateAsync(path, new LocalAtomicFileReplacement()))
            {
                var write = await Begin(save);
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                await Throws<OperationCanceledException>(() => write.CommitAsync(cancellation.Token).AsTask());
            }
            Check(Directory.GetFiles(directory).Length == 1, "Cancelled staging leaked.");

            var replacement = new Replacement();
            var grant = new Access();
            var pendingSave = await SaveFileLease.CreateAsync(path, replacement, grant);
            var pendingWrite = await Begin(pendingSave);
            using var cancelled = new CancellationTokenSource();
            var commit = pendingWrite.CommitAsync(cancelled.Token).AsTask();
            await replacement.Entered.Task;
            cancelled.Cancel();
            var closed = pendingSave.DisposeAsync().AsTask();
            Check(!closed.IsCompleted && !commit.IsCompleted && grant.Releases == 0, "Cancellation abandoned a submitted replacement.");
            replacement.Result.SetResult(new FileCommitResult.CommitUnknown(PlatformFailureCode.IoError));
            Check(await commit is FileCommitResult.CommitUnknown, "The actual uncertain outcome was lost.");
            await closed; await pendingSave.DisposeAsync();
            Check(grant.Releases == 1 && Directory.GetFiles(directory).Length == 1, "Access or staging leaked after unknown commit.");

            // A target created after the conflict check is refused, not reported as uncertain.
            string appeared = Path.Combine(directory, "appeared.txt");
            string staged = Path.Combine(directory, ".staged.tmp");
            await File.WriteAllTextAsync(staged, "staged");
            await File.WriteAllTextAsync(appeared, "concurrent");
            Check(await new LocalAtomicFileReplacement().ReplaceAsync(staged, appeared, existed: false) is FileCommitResult.NotCommitted { Code: PlatformFailureCode.Conflict },
                "A newly appeared target must be a conflict.");
            Check(await File.ReadAllTextAsync(appeared) == "concurrent", "A conflicting new target was replaced.");
            File.Delete(staged); File.Delete(appeared);

            if (!OperatingSystem.IsWindows())
            {
                // Group write must survive the process umask; set-ID bits are not carried over.
                var shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
                File.SetUnixFileMode(path, shared | UnixFileMode.SetGroup);
                await Commit(path, "shared");
                Check(File.GetUnixFileMode(path) == shared && await File.ReadAllTextAsync(path) == "shared", "Replacement changed the target's permissions.");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                string link = Path.Combine(directory, "link.txt");
                File.CreateSymbolicLink(link, path);
                await Commit(link, "through link");
                Check(new FileInfo(link).LinkTarget == path && await File.ReadAllTextAsync(path) == "through link",
                    "Replacement must update the link's target and keep the link.");
                File.Delete(link);
            }
            Check(Directory.GetFiles(directory).Length == 1, "Replacement checks leaked files.");
            if (OperatingSystem.IsLinux()) await LinuxRenameAsync(directory, path);

            replacement = new Replacement { IsSupported = false };
            await using (var unsupported = await SaveFileLease.CreateAsync(path, replacement))
                Check(await unsupported.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace) is PlatformResult<IFileWriteTransaction>.Unavailable,
                    "Unsupported atomic replacement must be refused before staging.");
            var failingRead = new ReadFileLease("unknown length", new BrokenStream(), new Access { Fail = true });
            await Throws<AggregateException>(() => failingRead.DisposeAsync().AsTask());
            await Throws<AggregateException>(() => failingRead.DisposeAsync().AsTask());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // The Linux commit is one renameat2: plain over an existing file, no-replace for
    // a new one, and never a copy. The seam returns an errno instead of renaming.
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task LinuxRenameAsync(string directory, string path)
    {
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(path, mode);
        long inode = Inode(path);
        await Commit(path, "renamed");
        Check(Inode(path) != inode && File.GetUnixFileMode(path) == mode && await File.ReadAllTextAsync(path) == "renamed",
            "An overwrite must publish a new inode with the original mode.");

        string fresh = Path.Combine(directory, "fresh.txt");
        await Commit(fresh, "new file");
        Check(await File.ReadAllTextAsync(fresh) == "new file", "A new file must be created.");
        File.Delete(fresh);

        uint? flags = null;
        var observed = new LocalAtomicFileReplacement(rename: (_, _, value) => { flags = value; return 17; }); // EEXIST
        Check(await CommitWith(fresh, observed) is FileCommitResult.NotCommitted { Code: PlatformFailureCode.Conflict } && flags == 1 && !File.Exists(fresh),
            "A new file must use RENAME_NOREPLACE and report a conflict.");

        foreach (int error in new[] { 18, 16 }) // EXDEV, EBUSY
        {
            string before = await File.ReadAllTextAsync(path);
            var refused = new LocalAtomicFileReplacement(rename: (_, _, value) => { flags = value; return error; });
            Check(await CommitWith(path, refused) is FileCommitResult.NotCommitted { Code: PlatformFailureCode.ResourceBusy } && flags == 0,
                $"errno {error} must leave the overwrite uncommitted.");
            Check(await File.ReadAllTextAsync(path) == before && Directory.GetFiles(directory).Length == 1,
                $"errno {error} must not copy over the target and must remove staging.");
        }
        Check(await CommitWith(path, new LocalAtomicFileReplacement(rename: (_, _, _) => 13)) is FileCommitResult.NotCommitted { Code: PlatformFailureCode.PermissionDenied },
            "EACCES is a refused rename.");
        Check(await CommitWith(path, new LocalAtomicFileReplacement(rename: (_, _, _) => 5)) is FileCommitResult.CommitUnknown { Code: PlatformFailureCode.IoError },
            "EIO after submission is an uncertain outcome.");

        // A filesystem or older document portal that rejects RENAME_NOREPLACE re-checks the name.
        var calls = new List<uint>();
        int Fallback(string staging, string target, uint value)
        {
            calls.Add(value);
            if (value != 0) return 22; // EINVAL
            File.Move(staging, target);
            return 0;
        }
        Check(await CommitWith(fresh, new LocalAtomicFileReplacement(rename: Fallback)) is FileCommitResult.Committed
            && calls.SequenceEqual([1u, 0u]) && await File.ReadAllTextAsync(fresh) == "replacement", "An absent name must still be created.");
        File.Delete(fresh);
        calls.Clear();
        string dangling = Path.Combine(directory, "dangling.txt");
        File.CreateSymbolicLink(dangling, Path.Combine(directory, "missing.txt"));
        Check(await CommitWith(dangling, new LocalAtomicFileReplacement(NativeFileLocation.DocumentPortal, Fallback)) is FileCommitResult.NotCommitted { Code: PlatformFailureCode.Conflict }
            && calls.SequenceEqual([1u]) && new FileInfo(dangling).LinkTarget is not null, "A present name, even a dangling link, must not be replaced.");
        File.Delete(dangling);

        // A document view resolves no links and stages beside the exact name.
        string target = Path.Combine(directory, "target.txt");
        string link = Path.Combine(directory, "view.txt");
        await File.WriteAllTextAsync(target, "target");
        File.CreateSymbolicLink(link, target);
        await using (var save = await SaveFileLease.CreateAsync(link, new LocalAtomicFileReplacement(NativeFileLocation.DocumentPortal)))
        {
            var write = await Begin(save);
            Check(Directory.GetFiles(directory, ".runic-*.tmp").Single() is { } staged && Path.GetDirectoryName(staged) == directory, "Staging must be a sibling of the exact name.");
            await write.Content.WriteAsync("document"u8.ToArray());
            Check(await write.CommitAsync() is FileCommitResult.Committed, "Document commit failed.");
        }
        Check(new FileInfo(link).LinkTarget is null && await File.ReadAllTextAsync(link) == "document" && await File.ReadAllTextAsync(target) == "target",
            "A document view must replace the exact name, never a resolved host path.");
        File.Delete(link); File.Delete(target);

        // A read-only or revoked document grant refuses staging: atomic replacement is unavailable.
        if (Environment.UserName != "root")
        {
            string locked = Path.Combine(directory, "locked");
            Directory.CreateDirectory(locked);
            string document = Path.Combine(locked, "document.txt");
            await File.WriteAllTextAsync(document, "kept");
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                await using var save = await SaveFileLease.CreateAsync(document, new LocalAtomicFileReplacement(NativeFileLocation.DocumentPortal));
                Check(await save.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace) is PlatformResult<IFileWriteTransaction>.Unavailable { Reason: PlatformUnavailableReason.AtomicReplaceUnavailable },
                    "A refused document staging file must report AtomicReplaceUnavailable.");
                await using var host = await SaveFileLease.CreateAsync(document, new LocalAtomicFileReplacement());
                Check(await host.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace) is PlatformResult<IFileWriteTransaction>.Failed { Code: PlatformFailureCode.PermissionDenied },
                    "A host directory without write access still reports permission denial.");
                Check(await File.ReadAllTextAsync(document) == "kept" && Directory.GetFiles(locked).Length == 1, "Refused staging changed the directory.");
            }
            finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); Directory.Delete(locked, true); }
        }
        else Console.WriteLine("SKIP document staging refusal: root bypasses directory permissions.");
    }

    private static async Task<FileCommitResult> CommitWith(string path, LocalAtomicFileReplacement replacement)
    {
        await using var save = await SaveFileLease.CreateAsync(path, replacement);
        var write = await Begin(save);
        await write.Content.WriteAsync("replacement"u8.ToArray());
        return await write.CommitAsync();
    }

    private static long Inode(string path)
    {
        var output = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("stat", ["-c", "%i", path]) { RedirectStandardOutput = true })!;
        string text = output.StandardOutput.ReadToEnd();
        output.WaitForExit();
        return long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task Commit(string path, string text)
    {
        await using var save = await SaveFileLease.CreateAsync(path, new LocalAtomicFileReplacement());
        var write = await Begin(save);
        await write.Content.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text));
        Check(await write.CommitAsync() is FileCommitResult.Committed, "Commit failed.");
    }
    private static async Task<IFileWriteTransaction> Begin(SaveFileLease save) =>
        (await save.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace) as PlatformResult<IFileWriteTransaction>.Success)?.Value
        ?? throw new InvalidOperationException("Staging unavailable.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private sealed class Access : IAsyncDisposable
    {
        internal int Releases;
        internal bool Fail;
        public ValueTask DisposeAsync() { Releases++; return Fail ? ValueTask.FromException(new IOException("Access release failed.")) : ValueTask.CompletedTask; }
    }
    private sealed class BrokenStream : MemoryStream
    {
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); throw new IOException("Stream close failed."); }
    }
    private sealed class Replacement : IAtomicFileReplacement
    {
        public bool IsSupported { get; init; } = true;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<FileCommitResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<FileCommitResult> ReplaceAsync(string stagingPath, string targetPath, bool existed)
        { Entered.SetResult(); return new(Result.Task); }
    }
}
