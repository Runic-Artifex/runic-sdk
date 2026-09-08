using Runic.Platform;
using Runic.Platform.Runtime;

internal static class FileLeaseTests
{
    internal static async Task RunAsync()
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
                Check(await write.CommitAsync() is FileCommitResult.NotCommitted { Code: FailureCode.Conflict }, "External edit was overwritten.");
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
            replacement.Result.SetResult(new FileCommitResult.CommitUnknown(FailureCode.IoError));
            Check(await commit is FileCommitResult.CommitUnknown, "The actual uncertain outcome was lost.");
            await closed; await pendingSave.DisposeAsync();
            Check(grant.Releases == 1 && Directory.GetFiles(directory).Length == 1, "Access or staging leaked after unknown commit.");

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
