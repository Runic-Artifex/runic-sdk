using System.Security.Cryptography;

namespace Runic.Platform.Prototype;

// One acquired stream, never a path that is reopened after selection. The lease
// owns it even if the caller forgets to dispose the returned stream.
internal sealed class ReadFileLease(string displayName, Stream stream, IAsyncDisposable? access = null) : IReadFileLease
{
    private readonly object _gate = new();
    private bool _opened;
    private TaskCompletionSource? _closed;
    public string DisplayName { get; } = displayName;

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed is not null, this);
            if (_opened) throw new InvalidOperationException("A read lease can be consumed once.");
            _opened = true;
            return ValueTask.FromResult(stream);
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_closed is not null) return new(_closed.Task);
            completion = _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = ReleaseAsync(completion);
        return new(completion.Task);
    }

    private async Task ReleaseAsync(TaskCompletionSource completion)
    {
        List<Exception> failures = [];
        try { await stream.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { if (access is not null) await access.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        FileLeaseCleanup.Complete(completion, failures);
    }
}

internal interface IAtomicFileReplacement
{
    bool IsSupported { get; }
    // No cancellation here: once submitted, the actual outcome must be observed.
    ValueTask<FileCommitResult> ReplaceAsync(string stagingPath, string targetPath, bool existed);
}

// Only compose for a provider that grants same-directory create and local rename.
// A picked URI/document-portal target alone does not imply these permissions.
internal sealed class LocalAtomicFileReplacement : IAtomicFileReplacement
{
    public bool IsSupported => true;
    public ValueTask<FileCommitResult> ReplaceAsync(string stagingPath, string targetPath, bool existed)
    {
        try
        {
            File.Move(stagingPath, targetPath, overwrite: existed);
            return ValueTask.FromResult<FileCommitResult>(new FileCommitResult.Committed());
        }
        catch (UnauthorizedAccessException) { return ValueTask.FromResult<FileCommitResult>(new FileCommitResult.NotCommitted(FailureCode.PermissionDenied)); }
        // An IO error can be reported after the filesystem accepted a rename.
        // Never delete the target or automatically retry an uncertain outcome.
        catch (IOException) { return ValueTask.FromResult<FileCommitResult>(new FileCommitResult.CommitUnknown(FailureCode.IoError)); }
    }
}

internal sealed class SaveFileLease : ISaveFileLease
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly byte[]? _original;
    private readonly IAtomicFileReplacement _replacement;
    private readonly IAsyncDisposable? _access;
    private FileWriteTransaction? _transaction;
    private TaskCompletionSource? _closed;

    private SaveFileLease(string path, byte[]? original, IAtomicFileReplacement replacement, IAsyncDisposable? access)
    { _path = path; _original = original; _replacement = replacement; _access = access; }
    public string DisplayName => Path.GetFileName(_path);

    // Ownership of access transfers even when acquisition fails.
    internal static async ValueTask<SaveFileLease> CreateAsync(string path, IAtomicFileReplacement replacement,
        IAsyncDisposable? access = null, CancellationToken cancellationToken = default)
    {
        try
        {
            path = Path.GetFullPath(path);
            var original = await FingerprintAsync(path, cancellationToken).ConfigureAwait(false);
            return new(path, original, replacement, access);
        }
        catch
        {
            if (access is not null) await access.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task<byte[]?> FingerprintAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // File.Exists hides access failures; actually open and distinguish absence.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return null; }
    }

    public ValueTask<PlatformResult<IFileWriteTransaction>> BeginWriteAsync(FileWritePolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed is not null, this);
            if (!_replacement.IsSupported) return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(
                new PlatformResult<IFileWriteTransaction>.Unavailable(UnavailableReason.AtomicReplaceUnavailable));
            if (_transaction is not null) return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(
                new PlatformResult<IFileWriteTransaction>.Failed(FailureCode.ResourceBusy));
            try
            {
                string staging = Path.Combine(Path.GetDirectoryName(_path)!, $".runic-{Guid.NewGuid():N}.tmp");
                var content = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                _transaction = new(staging, _path, _original, content, _replacement);
                return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(new PlatformResult<IFileWriteTransaction>.Success(_transaction));
            }
            catch (UnauthorizedAccessException) { return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(new PlatformResult<IFileWriteTransaction>.Failed(FailureCode.PermissionDenied)); }
            catch (IOException) { return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(new PlatformResult<IFileWriteTransaction>.Failed(FailureCode.IoError)); }
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_closed is not null) return new(_closed.Task);
            completion = _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = ReleaseAsync(completion);
        return new(completion.Task);
    }

    private async Task ReleaseAsync(TaskCompletionSource completion)
    {
        List<Exception> failures = [];
        try { if (_transaction is not null) await _transaction.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { if (_access is not null) await _access.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        FileLeaseCleanup.Complete(completion, failures);
    }
}

internal sealed class FileWriteTransaction(string staging, string target, byte[]? original,
    FileStream content, IAtomicFileReplacement replacement) : IFileWriteTransaction
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _abort = new();
    private TaskCompletionSource<FileCommitResult>? _commit;
    private TaskCompletionSource? _closed;
    public Stream Content => content;

    public ValueTask<FileCommitResult> CommitAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<FileCommitResult> completion;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed is not null, this);
            if (_commit is not null) throw new InvalidOperationException("Commit can be attempted exactly once; an uncertain outcome must not be retried.");
            completion = _commit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = CommitCoreAsync(completion, cancellationToken);
        return new(completion.Task);
    }

    private async Task CommitCoreAsync(TaskCompletionSource<FileCommitResult> completion, CancellationToken caller)
    {
        bool submitted = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _abort.Token);
            var token = linked.Token;
            token.ThrowIfCancellationRequested();
            await content.FlushAsync(token).ConfigureAwait(false);
            await content.DisposeAsync().ConfigureAwait(false);
            var current = await SaveFileLease.FingerprintAsync(target, token).ConfigureAwait(false);
            if ((original is null) != (current is null) || (original is not null && !original.AsSpan().SequenceEqual(current)))
            {
                completion.SetResult(new FileCommitResult.NotCommitted(FailureCode.Conflict));
                return;
            }
            // Best-effort conflict check, not filesystem CAS. No cancellable wait
            // after this boundary and no copy/delete fallback on rename failure.
            token.ThrowIfCancellationRequested();
            submitted = true;
            var outcome = await replacement.ReplaceAsync(staging, target, original is not null).ConfigureAwait(false);
            completion.SetResult(outcome);
        }
        catch (OperationCanceledException error) when (!submitted) { completion.SetCanceled(error.CancellationToken); }
        catch (UnauthorizedAccessException) { completion.SetResult(submitted ? new FileCommitResult.CommitUnknown(FailureCode.PermissionDenied) : new FileCommitResult.NotCommitted(FailureCode.PermissionDenied)); }
        catch (IOException) { completion.SetResult(submitted ? new FileCommitResult.CommitUnknown(FailureCode.IoError) : new FileCommitResult.NotCommitted(FailureCode.IoError)); }
        catch (Exception error) { completion.SetException(error); }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task<FileCommitResult>? commit;
        lock (_gate)
        {
            if (_closed is not null) return new(_closed.Task);
            completion = _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            commit = _commit?.Task;
        }
        _ = ReleaseAsync(completion, commit);
        return new(completion.Task);
    }

    private async Task ReleaseAsync(TaskCompletionSource completion, Task<FileCommitResult>? commit)
    {
        List<Exception> failures = [];
        try { await _abort.CancelAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        // The caller observes commit errors; disposal still releases resources.
        if (commit is not null) { try { await commit.ConfigureAwait(false); } catch { } }
        try { await content.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { File.Delete(staging); } catch (Exception error) { failures.Add(error); }
        _abort.Dispose();
        FileLeaseCleanup.Complete(completion, failures);
    }
}

internal static class FileLeaseCleanup
{
    internal static void Complete(TaskCompletionSource completion, List<Exception> failures)
    {
        if (failures.Count == 0) completion.SetResult();
        else completion.SetException(failures.Count == 1 ? failures[0] : new AggregateException(failures));
    }
}
