using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Runic.Platform.Runtime;

/// <summary>The selected native backend cannot serve this environment.</summary>
public sealed class NativeBackendUnavailableException : Exception;

/// <summary>An acquired native selection, confined to C# provider infrastructure.</summary>
/// <param name="Path">The acquired local path, never a bridge payload.</param>
/// <param name="Access">Optional access grant transferred to the resulting lease.</param>
/// <param name="AllowsSiblingReplacement">Whether access permits same-directory staging and replacement.</param>
public sealed record NativeFileSelection(string Path, IAsyncDisposable? Access, bool AllowsSiblingReplacement);
/// <summary>A statically selected native picker which returns acquired access.</summary>
public interface INativeFilePicker
{
    /// <summary>Shows one owned dialog; null means dismissal. Cancellation must release acquired access.</summary>
    ValueTask<NativeFileSelection?> SelectAsync(bool save, string? suggestedName, CancellationToken cancellationToken);
    /// <summary>Whether this environment's save selections permit sibling staging and replacement.</summary>
    bool SupportsAtomicReplace => true;
}

/// <summary>Converts acquired native selections to safe, single-use file leases.</summary>
public sealed partial class NativePickerBackend(INativePickerOwner owner, INativeFilePicker picker) : IPickerBackend
{
    private readonly Guid _generation = owner.Generation;

    /// <inheritdoc />
    public bool IsAvailable => owner.IsAvailable && owner.Generation == _generation;

    /// <inheritdoc />
    public bool SupportsAtomicReplace => picker.SupportsAtomicReplace;

    /// <inheritdoc />
    public async ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.OwnerPolicy)) throw new ArgumentOutOfRangeException(nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) return new PickerResult<IReadFileLease>.Unavailable(UnavailableReason.OwnerUnavailable);
        NativeFileSelection? selection = null;
        try
        {
            selection = await picker.SelectAsync(false, null, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAvailable) return new PickerResult<IReadFileLease>.Unavailable(UnavailableReason.OwnerClosed);
            if (selection is null) return new PickerResult<IReadFileLease>.Dismissed();
            var stream = OpenSelected(selection.Path);
            var lease = new ReadFileLease(Path.GetFileName(selection.Path), stream, selection.Access);
            selection = null;
            return new PickerResult<IReadFileLease>.Selected(lease);
        }
        catch (OwnerClosedException) { return new PickerResult<IReadFileLease>.Unavailable(UnavailableReason.OwnerClosed); }
        catch (DllNotFoundException) { return new PickerResult<IReadFileLease>.Unavailable(UnavailableReason.BackendUnavailable); }
        catch (EntryPointNotFoundException) { return new PickerResult<IReadFileLease>.Unavailable(UnavailableReason.BackendUnavailable); }
        catch (NativeBackendUnavailableException) { return new PickerResult<IReadFileLease>.Unavailable(UnavailableReason.BackendUnavailable); }
        catch (UnauthorizedAccessException) { return new PickerResult<IReadFileLease>.Failed(FailureCode.PermissionDenied); }
        catch (IOException) { return new PickerResult<IReadFileLease>.Failed(FailureCode.IoError); }
        finally { if (selection?.Access is { } access) await access.DisposeAsync().ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public async ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.OwnerPolicy)) throw new ArgumentOutOfRangeException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SuggestedName);
        if (options.SuggestedName.IndexOfAny(['/', '\\', '\0']) >= 0 || options.SuggestedName is "." or "..")
            throw new ArgumentException("The suggestion must be a filename, not a path.", nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) return new PickerResult<ISaveFileLease>.Unavailable(UnavailableReason.OwnerUnavailable);
        try
        {
            var selection = await picker.SelectAsync(true, options.SuggestedName, cancellationToken).ConfigureAwait(false);
            if (selection is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new PickerResult<ISaveFileLease>.Dismissed();
            }
            if (!IsAvailable)
            {
                if (selection.Access is { } access) await access.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new PickerResult<ISaveFileLease>.Unavailable(UnavailableReason.OwnerClosed);
            }
            // CreateAsync takes ownership of acquired access, including failure paths.
            var lease = await SaveFileLease.CreateAsync(selection.Path,
                selection.AllowsSiblingReplacement ? new LocalAtomicFileReplacement() : new NoAtomicReplacement(),
                selection.Access, cancellationToken).ConfigureAwait(false);
            return new PickerResult<ISaveFileLease>.Selected(lease);
        }
        catch (OwnerClosedException) { return new PickerResult<ISaveFileLease>.Unavailable(UnavailableReason.OwnerClosed); }
        catch (DllNotFoundException) { return new PickerResult<ISaveFileLease>.Unavailable(UnavailableReason.BackendUnavailable); }
        catch (EntryPointNotFoundException) { return new PickerResult<ISaveFileLease>.Unavailable(UnavailableReason.BackendUnavailable); }
        catch (NativeBackendUnavailableException) { return new PickerResult<ISaveFileLease>.Unavailable(UnavailableReason.BackendUnavailable); }
        catch (UnauthorizedAccessException) { return new PickerResult<ISaveFileLease>.Failed(FailureCode.PermissionDenied); }
        catch (IOException) { return new PickerResult<ISaveFileLease>.Failed(FailureCode.IoError); }
    }

    // Opening a FIFO blocks until a writer appears, and special files may never end.
    // On Unix, open without blocking and accept only a seekable non-directory file.
    private static FileStream OpenSelected(string path)
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
                _ => new IOException("The selected file cannot be opened."),
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

    private sealed class NoAtomicReplacement : IAtomicFileReplacement
    {
        public bool IsSupported => false;
        public ValueTask<FileCommitResult> ReplaceAsync(string stagingPath, string targetPath, bool existed) => throw new NotSupportedException();
    }
}
