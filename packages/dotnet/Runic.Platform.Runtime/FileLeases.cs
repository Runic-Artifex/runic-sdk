using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Runic.Platform.Runtime;

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
internal sealed partial class LocalAtomicFileReplacement : IAtomicFileReplacement
{
    public bool IsSupported => true;
    public ValueTask<FileCommitResult> ReplaceAsync(string stagingPath, string targetPath, bool existed)
    {
        try
        {
            // ReplaceFile keeps the destination's ACL, attributes and alternate streams.
            if (existed && OperatingSystem.IsWindows()) File.Replace(stagingPath, targetPath, null);
            else File.Move(stagingPath, targetPath, overwrite: existed);
        }
        catch (UnauthorizedAccessException) { return ValueTask.FromResult<FileCommitResult>(new FileCommitResult.NotCommitted(PlatformFailureCode.PermissionDenied)); }
        // A no-overwrite move refuses a destination created since selection.
        catch (IOException error) when (!existed && IsAlreadyExists(error))
        { return ValueTask.FromResult<FileCommitResult>(new FileCommitResult.NotCommitted(PlatformFailureCode.Conflict)); }
        // An IO error can be reported after the filesystem accepted a rename.
        // Never delete the target or automatically retry an uncertain outcome.
        catch (IOException) { return ValueTask.FromResult<FileCommitResult>(new FileCommitResult.CommitUnknown(PlatformFailureCode.IoError)); }
        SyncDirectory(Path.GetDirectoryName(targetPath)!);
        return ValueTask.FromResult<FileCommitResult>(new FileCommitResult.Committed());
    }

    private static bool IsAlreadyExists(IOException error) => OperatingSystem.IsWindows()
        ? error.HResult is unchecked((int)0x80070050) or unchecked((int)0x800700B7) // ERROR_FILE_EXISTS, ERROR_ALREADY_EXISTS
        : error.HResult == 17; // EEXIST

    // Persist the renamed directory entry. Best effort: the replacement is already
    // visible, and some filesystems do not support synchronizing a directory.
    private static void SyncDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            int descriptor = Open(directory, OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000); // O_RDONLY | O_CLOEXEC
            if (descriptor < 0) return;
            _ = Sync(descriptor);
            _ = Close(descriptor);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)] private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "fsync")] private static partial int Sync(int descriptor);
    [LibraryImport("libc", EntryPoint = "close")] private static partial int Close(int descriptor);
}

internal sealed class SaveFileLease : ISaveFileLease, ILaunchableFileLease
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly byte[]? _original;
    private readonly IAtomicFileReplacement _replacement;
    private readonly IAsyncDisposable? _access;
    private int _handoffs;
    private readonly TaskCompletionSource _handoffsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(IDesktopFileLauncher launcher,
        DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed is not null, this);
            _handoffs++;
        }
        try { return await launcher.LaunchAsync(_path, operation, cancellationToken).ConfigureAwait(false); }
        finally { lock (_gate) if (--_handoffs == 0 && _closed is not null) _handoffsDrained.TrySetResult(); }
    }
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
                new PlatformResult<IFileWriteTransaction>.Unavailable(PlatformUnavailableReason.AtomicReplaceUnavailable));
            if (_transaction is not null) return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(
                new PlatformResult<IFileWriteTransaction>.Failed(PlatformFailureCode.ResourceBusy));
            try
            {
                // Replace a symlink's final target rather than the link itself, staging
                // beside that target so the rename stays within one directory.
                string target = new FileInfo(_path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? _path;
                string staging = Path.Combine(Path.GetDirectoryName(target)!, $".runic-{Guid.NewGuid():N}.tmp");
                var content = CreateStaging(staging, target);
                _transaction = new(staging, target, _original, content, _replacement);
                return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(new PlatformResult<IFileWriteTransaction>.Success(_transaction));
            }
            catch (UnauthorizedAccessException) { return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(new PlatformResult<IFileWriteTransaction>.Failed(PlatformFailureCode.PermissionDenied)); }
            catch (IOException) { return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(new PlatformResult<IFileWriteTransaction>.Failed(PlatformFailureCode.IoError)); }
        }
    }

    // On Unix the staging file takes the replaced file's permissions, so the rename
    // neither widens nor narrows access. Set-ID bits are not carried to new content.
    private static FileStream CreateStaging(string staging, string target)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            BufferSize = 4096, Options = FileOptions.Asynchronous,
        };
        UnixFileMode? mode = null;
        if (!OperatingSystem.IsWindows())
        {
            try { mode = File.GetUnixFileMode(target) & (UnixFileMode)0x1FF; } // rwxrwxrwx
            catch (FileNotFoundException) { }
            if (mode is { } permissions) options.UnixCreateMode = permissions;
        }
        var content = new FileStream(staging, options);
        try
        {
            // Creation applies the umask, which could narrow shared permissions.
            if (mode is { } permissions && !OperatingSystem.IsWindows()) File.SetUnixFileMode(content.SafeFileHandle, permissions);
            return content;
        }
        catch
        {
            content.Dispose();
            File.Delete(staging);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_closed is not null) return new(_closed.Task);
            completion = _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_handoffs == 0) _handoffsDrained.TrySetResult();
        }
        _ = ReleaseAsync(completion);
        return new(completion.Task);
    }

    private async Task ReleaseAsync(TaskCompletionSource completion)
    {
        await _handoffsDrained.Task.ConfigureAwait(false);
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
            // Make the staged bytes durable before the rename can publish them.
            content.Flush(flushToDisk: true);
            await content.DisposeAsync().ConfigureAwait(false);
            var current = await SaveFileLease.FingerprintAsync(target, token).ConfigureAwait(false);
            if ((original is null) != (current is null) || (original is not null && !original.AsSpan().SequenceEqual(current)))
            {
                completion.SetResult(new FileCommitResult.NotCommitted(PlatformFailureCode.Conflict));
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
        catch (UnauthorizedAccessException) { completion.SetResult(submitted ? new FileCommitResult.CommitUnknown(PlatformFailureCode.PermissionDenied) : new FileCommitResult.NotCommitted(PlatformFailureCode.PermissionDenied)); }
        catch (IOException) { completion.SetResult(submitted ? new FileCommitResult.CommitUnknown(PlatformFailureCode.IoError) : new FileCommitResult.NotCommitted(PlatformFailureCode.IoError)); }
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
