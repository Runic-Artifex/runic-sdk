using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

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

// The leases of one multiple selection. Disposing the list releases each lease once.
internal sealed class ReadFileLeaseList(ImmutableArray<IReadFileLease> leases) : IReadFileLeaseList
{
    private Task? _closed;
    public IReadFileLease this[int index] => leases[index];
    public int Count => leases.Length;
    public IEnumerator<IReadFileLease> GetEnumerator() => ((IEnumerable<IReadFileLease>)leases).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public ValueTask DisposeAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _closed, completion.Task, null) is { } existing) return new(existing);
        _ = ReleaseAsync(completion);
        return new(completion.Task);
    }

    private async Task ReleaseAsync(TaskCompletionSource completion)
    {
        List<Exception> failures = [];
        foreach (var lease in leases)
        {
            try { await lease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        FileLeaseCleanup.Complete(completion, failures);
    }
}

internal interface IAtomicFileReplacement
{
    bool IsSupported { get; }
    NativeFileLocation Location => NativeFileLocation.Host;
    // No cancellation here: once submitted, the actual outcome must be observed.
    ValueTask<FileCommitResult> ReplaceAsync(string stagingPath, string targetPath, bool existed);
}

// Only compose for a provider that grants same-directory create and local rename:
// an ordinary host path, or a document in the xdg-document-portal FUSE view. That
// view turns a new sibling name into a private host tempfile and renames it onto
// the document with the host's renameat2. The rename seam returns an errno.
internal sealed partial class LocalAtomicFileReplacement(NativeFileLocation location = NativeFileLocation.Host,
    Func<string, string, uint, int>? rename = null) : IAtomicFileReplacement
{
    public bool IsSupported => true;
    public NativeFileLocation Location => location;
    public ValueTask<FileCommitResult> ReplaceAsync(string stagingPath, string targetPath, bool existed)
    {
        if (OperatingSystem.IsLinux()) return ValueTask.FromResult(ReplaceOnLinux(stagingPath, targetPath, existed));
        try
        {
            // ReplaceFile keeps the destination's ACL, attributes and alternate streams.
            if (existed && OperatingSystem.IsWindows()) File.Replace(stagingPath, targetPath, null);
            else File.Move(stagingPath, targetPath, overwrite: existed);
        }
        catch (UnauthorizedAccessException error) { return ValueTask.FromResult<FileCommitResult>(new FileCommitResult.NotCommitted(PlatformFailureCode.PermissionDenied, FileErrors.Diagnose(error))); }
        // A no-overwrite move refuses a destination created since selection.
        catch (IOException error) when (!existed && IsAlreadyExists(error))
        { return ValueTask.FromResult<FileCommitResult>(new FileCommitResult.NotCommitted(PlatformFailureCode.Conflict, FileErrors.Diagnose(error))); }
        // Another program holds the target open without delete sharing: ReplaceFile either fails to open it (a sharing
        // violation, before it changes anything) or cannot remove it (both files keep their names). Nothing was replaced.
        catch (IOException error) when (existed && OperatingSystem.IsWindows() && error.HResult is UnableToRemoveReplaced or SharingViolation)
        { return ValueTask.FromResult<FileCommitResult>(new FileCommitResult.NotCommitted(PlatformFailureCode.ResourceBusy, FileErrors.Diagnose(error))); }
        // An IO error can be reported after the filesystem accepted a rename.
        // Never delete the target or automatically retry an uncertain outcome.
        catch (IOException error) { return ValueTask.FromResult<FileCommitResult>(new FileCommitResult.CommitUnknown(PlatformFailureCode.IOError, FileErrors.Diagnose(error))); }
        SyncDirectory(Path.GetDirectoryName(targetPath)!);
        return ValueTask.FromResult<FileCommitResult>(new FileCommitResult.Committed());
    }

    private const int UnableToRemoveReplaced = unchecked((int)0x80070497); // ERROR_UNABLE_TO_REMOVE_REPLACED (1175)
    private const int SharingViolation = unchecked((int)0x80070020); // ERROR_SHARING_VIOLATION (32)

    private static bool IsAlreadyExists(IOException error) => OperatingSystem.IsWindows()
        ? error.HResult is unchecked((int)0x80070050) or unchecked((int)0x800700B7) // ERROR_FILE_EXISTS, ERROR_ALREADY_EXISTS
        : error.HResult == 17; // EEXIST

    private const int EPERM = 1, EIO = 5, EACCES = 13, EBUSY = 16, EEXIST = 17, EXDEV = 18, EISDIR = 21, EINVAL = 22, EROFS = 30, ENOSYS = 38, ENOTEMPTY = 39;
    private const uint RenameNoReplace = 1; // RENAME_NOREPLACE

    // Rename directly instead of File.Move: it copies and deletes on EXDEV (for
    // example a bind-mounted file), which is a non-atomic overwrite.
    private FileCommitResult ReplaceOnLinux(string staging, string target, bool existed)
    {
        // A new file never replaces a name that appeared since the conflict check,
        // including a host symlink that a document view shows as absent.
        int error = Rename(staging, target, existed ? 0 : RenameNoReplace);
        if (!existed && error is EINVAL or ENOSYS)
        {
            // Document portals before 1.14, old kernels and some filesystems reject
            // the flag. Re-check and rename; a name created between both steps is replaced.
            if (Exists(target) is not false) return new FileCommitResult.NotCommitted(PlatformFailureCode.Conflict);
            error = Rename(staging, target, 0);
        }
        switch (error)
        {
            case 0:
                // A document view ignores directory sync; its rename is durable once the host filesystem commits it.
                SyncDirectory(Path.GetDirectoryName(target)!);
                return new FileCommitResult.Committed();
            case EEXIST or ENOTEMPTY or EISDIR: return new FileCommitResult.NotCommitted(PlatformFailureCode.Conflict, PlatformDiagnostic.FromErrno(error));
            // A mount point or another filesystem cannot be replaced atomically. Never copy.
            case EXDEV or EBUSY: return new FileCommitResult.NotCommitted(PlatformFailureCode.ResourceBusy, PlatformDiagnostic.FromErrno(error));
            case EACCES or EPERM or EROFS: return new FileCommitResult.NotCommitted(PlatformFailureCode.PermissionDenied, PlatformDiagnostic.FromErrno(error));
            // A network or FUSE filesystem can report an error after it applied the rename.
            case EIO: return new FileCommitResult.CommitUnknown(PlatformFailureCode.IOError, PlatformDiagnostic.FromErrno(error));
            default: return new FileCommitResult.CommitUnknown(PlatformFailureCode.Other, PlatformDiagnostic.FromErrno(error));
        }
    }

    private int Rename(string staging, string target, uint flags)
    {
        if (rename is not null) return rename(staging, target, flags);
        try { return RenameAt2(AtCurrentDirectory, staging, AtCurrentDirectory, target, flags) == 0 ? 0 : Marshal.GetLastPInvokeError(); }
        catch (EntryPointNotFoundException) when (flags != 0) { return ENOSYS; } // A C library without renameat2.
        catch (EntryPointNotFoundException) { return RenamePlain(staging, target) == 0 ? 0 : Marshal.GetLastPInvokeError(); }
    }

    // GetAttributes reports a dangling symlink as present. Unknown is not absent.
    private static bool? Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private const int AtCurrentDirectory = -100; // AT_FDCWD
    [LibraryImport("libc", EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameAt2(int sourceDirectory, string source, int targetDirectory, string target, uint flags);
    [LibraryImport("libc", EntryPoint = "rename", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenamePlain(string source, string target);

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

// A save destination that writes as often as the application saves. Each commit compares the file with the content
// the application last saw (the baseline): the selection, then each committed write, and for a document each read.
internal class SaveFileLease : ISaveFileLease, ILaunchableFileLease
{
    private protected readonly object _gate = new();
    private protected readonly string _path;
    private readonly IAtomicFileReplacement _replacement;
    private readonly IAsyncDisposable? _access;
    private readonly IDesktopFileBookmarks? _bookmarks;
    // Fingerprints of the content the application last saw; null is an absent file. More than one only after an
    // uncertain commit, when both the previous content and the staged bytes are the application's own.
    private ImmutableArray<byte[]?> _baseline;
    // The transaction whose commit has not finished; every undisposed one is released with the lease.
    private FileWriteTransaction? _open;
    private readonly HashSet<FileWriteTransaction> _transactions = [];
    private int _handoffs;
    private readonly TaskCompletionSource _handoffsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private protected TaskCompletionSource? _closed;

    private protected SaveFileLease(string path, byte[]? original, IAtomicFileReplacement replacement, IAsyncDisposable? access,
        IDesktopFileBookmarks? bookmarks)
    { _path = path; _baseline = [original]; _replacement = replacement; _access = access; _bookmarks = bookmarks; }
    public string DisplayName => Path.GetFileName(_path);

    // Ownership of access transfers even when acquisition fails.
    internal static async ValueTask<SaveFileLease> CreateAsync(string path, IAtomicFileReplacement replacement,
        IAsyncDisposable? access = null, IDesktopFileBookmarks? bookmarks = null, CancellationToken cancellationToken = default)
    {
        try
        {
            path = Path.GetFullPath(path);
            var original = await FingerprintAsync(path, cancellationToken).ConfigureAwait(false);
            return new(path, original, replacement, access, bookmarks);
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

    public async ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(IDesktopFileLauncher launcher,
        DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        BeginHandoff();
        try { return await launcher.LaunchAsync(_path, operation, cancellationToken).ConfigureAwait(false); }
        finally { EndHandoff(); }
    }

    public async ValueTask<PlatformResult<ImmutableArray<byte>>> CreateBookmarkAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_bookmarks is null)
        {
            lock (_gate) ObjectDisposedException.ThrowIf(_closed is not null, this);
            return new PlatformResult<ImmutableArray<byte>>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        BeginHandoff();
        try { return await _bookmarks.CreateAsync(_path, cancellationToken).ConfigureAwait(false); }
        finally { EndHandoff(); }
    }

    // The path stays accessible while it is handed to the launcher or the bookmarks; disposal waits for them.
    private void BeginHandoff()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed is not null, this);
            _handoffs++;
        }
    }
    private void EndHandoff() { lock (_gate) if (--_handoffs == 0 && _closed is not null) _handoffsDrained.TrySetResult(); }

    public ValueTask<PlatformResult<IFileWriteTransaction>> BeginWriteAsync(FileWritePolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed is not null, this);
            if (!_replacement.IsSupported) return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(
                new PlatformResult<IFileWriteTransaction>.Unavailable(PlatformUnavailableReason.AtomicReplaceUnavailable));
            if (_open is not null) return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(
                new PlatformResult<IFileWriteTransaction>.Failed(PlatformFailureCode.ResourceBusy));
            bool document = _replacement.Location == NativeFileLocation.DocumentPortal;
            try
            {
                // Replace a symlink's final target rather than the link itself, staging
                // beside that target so the rename stays within one directory. A
                // document view exposes only the granted name: stage beside exactly it.
                string target = document ? _path : ResolveTarget(_path);
                string staging = Path.Combine(Path.GetDirectoryName(target)!, $".runic-{Guid.NewGuid():N}.tmp");
                var content = CreateStaging(staging, target);
                var transaction = new FileWriteTransaction(staging, target, content, _replacement, this);
                _open = transaction;
                _transactions.Add(transaction);
                return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(new PlatformResult<IFileWriteTransaction>.Success(transaction));
            }
            // A read-only or revoked document grant refuses the staging file; the document is untouched.
            catch (UnauthorizedAccessException) when (document) { return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(
                new PlatformResult<IFileWriteTransaction>.Unavailable(PlatformUnavailableReason.AtomicReplaceUnavailable)); }
            catch (UnauthorizedAccessException error) { return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(new PlatformResult<IFileWriteTransaction>.Failed(PlatformFailureCode.PermissionDenied, FileErrors.Diagnose(error))); }
            catch (IOException error) { return ValueTask.FromResult<PlatformResult<IFileWriteTransaction>>(new PlatformResult<IFileWriteTransaction>.Failed(PlatformFailureCode.IOError, FileErrors.Diagnose(error))); }
        }
    }

    // Whether the file's current content (null when absent) is content the application last saw.
    internal bool IsBaseline(byte[]? current)
    {
        lock (_gate)
        {
            foreach (var seen in _baseline)
                if (seen is null ? current is null : current is not null && seen.AsSpan().SequenceEqual(current)) return true;
            return false;
        }
    }

    // A finished commit lets the next write begin. Committed bytes become the baseline; after an uncertain outcome
    // the staged bytes join it, since the file may hold either. A refused or cancelled commit changes nothing.
    internal void Finished(FileWriteTransaction transaction, FileCommitResult? outcome, byte[]? staged)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_open, transaction)) _open = null;
            if (staged is null) return;
            if (outcome is FileCommitResult.Committed) _baseline = [staged];
            else if (outcome is FileCommitResult.CommitUnknown && !IsBaseline(staged)) _baseline = _baseline.Add(staged);
        }
    }

    internal void Closed(FileWriteTransaction transaction)
    {
        lock (_gate)
        {
            _transactions.Remove(transaction);
            if (ReferenceEquals(_open, transaction)) _open = null;
        }
    }

    // A document read makes the content read, or the file's absence, the baseline.
    private protected void Observe(byte[]? content) { lock (_gate) _baseline = [content]; }

    // A new file has no link to resolve; ResolveLinkTarget throws for an absent name.
    private static string ResolveTarget(string path)
    {
        try { return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path; }
        catch (FileNotFoundException) { return path; }
    }

    // The staging file takes the replaced file's permissions, so it is never readable by more users than the file it
    // replaces while it is written, and the rename neither widens nor narrows access. On Unix that is the mode; set-ID
    // bits are not carried to new content. On Windows it is the security descriptor's DACL.
    private static FileStream CreateStaging(string staging, string target)
    {
        if (OperatingSystem.IsWindows()) return CreateStagingOnWindows(staging, target);
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

    // Without a descriptor the staging file would take the folder's inheritable ACL until File.Replace applies the
    // target's, so a private file in a shared folder would be readable there meanwhile (SEC-28). The target's DACL,
    // including whether it is protected from inheritance, is applied when the file is created; Windows recomputes its
    // inherited entries from the same folder. A new target has no descriptor and keeps the folder's, as it will after
    // the move.
    [SupportedOSPlatform("windows")]
    internal static FileStream CreateStagingOnWindows(string staging, string target)
    {
        FileSecurity? security = null;
        try
        {
            byte[] descriptor = new FileInfo(target).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
            security = new FileSecurity();
            security.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        return new FileInfo(staging).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.Asynchronous, security);
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
        FileWriteTransaction[] transactions;
        // No transaction begins once the lease is closed.
        lock (_gate) transactions = [.. _transactions];
        foreach (var transaction in transactions)
        {
            try { await transaction.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        }
        await ReleaseReadsAsync(failures).ConfigureAwait(false);
        try { if (_access is not null) await _access.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        FileLeaseCleanup.Complete(completion, failures);
    }

    private protected virtual ValueTask ReleaseReadsAsync(List<Exception> failures) => ValueTask.CompletedTask;
}

// A document the application reads and saves back to. Each read opens the committed file again, never staging, and
// makes the content read the baseline of the next commit.
internal sealed class DocumentFileLease : SaveFileLease, IDocumentFileLease
{
    private readonly List<FileStream> _reads = [];

    private DocumentFileLease(string path, byte[]? original, IAtomicFileReplacement replacement, IAsyncDisposable? access,
        IDesktopFileBookmarks? bookmarks) : base(path, original, replacement, access, bookmarks) { }

    // Ownership of access transfers even when acquisition fails. An opened document must be a readable regular file;
    // a save destination may not exist yet.
    internal static async ValueTask<DocumentFileLease> CreateAsync(string path, IAtomicFileReplacement replacement, IAsyncDisposable? access,
        IDesktopFileBookmarks? bookmarks, bool existing, CancellationToken cancellationToken = default)
    {
        try
        {
            path = Path.GetFullPath(path);
            byte[]? original;
            try
            {
                await using var stream = RegularFiles.OpenRead(path);
                original = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!existing && error is FileNotFoundException) { original = null; }
            return new(path, original, replacement, access, bookmarks);
        }
        catch
        {
            if (access is not null) await access.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public string LocalPath
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed is not null, this);
                return _path;
            }
        }
    }

    public async ValueTask<PlatformResult<Stream>> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) ObjectDisposedException.ThrowIf(_closed is not null, this);
        FileStream stream;
        byte[] content;
        try { stream = RegularFiles.OpenRead(_path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            Observe(null);
            return new PlatformResult<Stream>.Failed(PlatformFailureCode.NotFound, FileErrors.Diagnose(error));
        }
        catch (UnauthorizedAccessException error) { return new PlatformResult<Stream>.Failed(PlatformFailureCode.PermissionDenied, FileErrors.Diagnose(error)); }
        catch (IOException error) { return new PlatformResult<Stream>.Failed(PlatformFailureCode.IOError, FileErrors.Diagnose(error)); }
        try
        {
            // The fingerprint comes from the same open file the caller reads.
            content = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
        }
        catch (Exception error)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            if (error is IOException) return new PlatformResult<Stream>.Failed(PlatformFailureCode.IOError, FileErrors.Diagnose(error));
            throw;
        }
        lock (_gate)
        {
            if (_closed is null)
            {
                // The caller owns each stream; keep only the ones still open for disposal.
                _reads.RemoveAll(static read => !read.CanRead);
                _reads.Add(stream);
                Observe(content);
                return new PlatformResult<Stream>.Success(stream);
            }
        }
        await stream.DisposeAsync().ConfigureAwait(false);
        throw new ObjectDisposedException(GetType().FullName);
    }

    private protected override async ValueTask ReleaseReadsAsync(List<Exception> failures)
    {
        FileStream[] reads;
        lock (_gate) reads = [.. _reads];
        foreach (var read in reads)
        {
            try { await read.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        }
    }
}

internal sealed class FileWriteTransaction(string staging, string target, FileStream content,
    IAtomicFileReplacement replacement, SaveFileLease lease) : IFileWriteTransaction
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

    // The lease learns every outcome before the caller does, so the caller can begin its next write at once.
    private async Task CommitCoreAsync(TaskCompletionSource<FileCommitResult> completion, CancellationToken caller)
    {
        bool submitted = false;
        byte[]? staged = null;
        FileCommitResult outcome;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _abort.Token);
            var token = linked.Token;
            token.ThrowIfCancellationRequested();
            await content.FlushAsync(token).ConfigureAwait(false);
            // Make the staged bytes durable before the rename can publish them.
            content.Flush(flushToDisk: true);
            await content.DisposeAsync().ConfigureAwait(false);
            // The staged bytes become the baseline of the next commit once they are published.
            staged = await SaveFileLease.FingerprintAsync(staging, token).ConfigureAwait(false)
                ?? throw new IOException("The staged content no longer exists.");
            var current = await SaveFileLease.FingerprintAsync(target, token).ConfigureAwait(false);
            if (!lease.IsBaseline(current)) outcome = new FileCommitResult.NotCommitted(PlatformFailureCode.Conflict);
            else
            {
                // Best-effort conflict check, not filesystem CAS. No cancellable wait
                // after this boundary and no copy/delete fallback on rename failure.
                token.ThrowIfCancellationRequested();
                submitted = true;
                outcome = await replacement.ReplaceAsync(staging, target, current is not null).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (!submitted)
        {
            lease.Finished(this, null, null);
            completion.SetCanceled(error.CancellationToken);
            return;
        }
        catch (UnauthorizedAccessException error)
        {
            var diagnostic = FileErrors.Diagnose(error);
            outcome = submitted ? new FileCommitResult.CommitUnknown(PlatformFailureCode.PermissionDenied, diagnostic) : new FileCommitResult.NotCommitted(PlatformFailureCode.PermissionDenied, diagnostic);
        }
        catch (IOException error)
        {
            var diagnostic = FileErrors.Diagnose(error);
            outcome = submitted ? new FileCommitResult.CommitUnknown(PlatformFailureCode.IOError, diagnostic) : new FileCommitResult.NotCommitted(PlatformFailureCode.IOError, diagnostic);
        }
        catch (Exception error)
        {
            // An unexpected failure after submission leaves the outcome open: the staged bytes may be the file's.
            lease.Finished(this, submitted ? new FileCommitResult.CommitUnknown(PlatformFailureCode.Other) : null, staged);
            completion.SetException(error);
            return;
        }
        lease.Finished(this, outcome, staged);
        completion.SetResult(outcome);
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
        // Only once the commit settled and staging is gone may the lease begin another write.
        lease.Closed(this);
        FileLeaseCleanup.Complete(completion, failures);
    }
}

// Opening a FIFO blocks until a writer appears, and special files may never end.
// On Unix, open without blocking and accept only a seekable non-directory file.
internal static partial class RegularFiles
{
    internal static FileStream OpenRead(string path)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var handle = new SafeFileHandle(Open(path, OperatingSystem.IsMacOS() ? 0x1000004 : 0x80800), ownsHandle: true); // O_RDONLY | O_NONBLOCK | O_CLOEXEC
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw error switch
            {
                1 or 13 => new UnauthorizedAccessException("The selected file cannot be read."), // EPERM, EACCES
                2 => new FileNotFoundException("The selected file no longer exists."), // ENOENT
                20 => new DirectoryNotFoundException("A directory of the selected file no longer exists."), // ENOTDIR
                _ => new IOException("The selected file cannot be opened.", error),
            };
        }
        try
        {
            if (File.GetAttributes(handle).HasFlag(FileAttributes.Directory)) throw new UnauthorizedAccessException("A directory is not a readable file.");
            var stream = new FileStream(handle, FileAccess.Read, 4096);
            if (stream.CanSeek) return stream;
            stream.Dispose();
            throw new IOException("The selection is not a regular file.");
        }
        catch { handle.Dispose(); throw; }
    }
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);
}

internal static class FileLeaseCleanup
{
    internal static void Complete(TaskCompletionSource completion, List<Exception> failures)
    {
        if (failures.Count == 0) completion.SetResult();
        else completion.SetException(failures.Count == 1 ? failures[0] : new AggregateException(failures));
    }
}
