using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Runic.Platform.Runtime;

/// <summary>An acquired native selection, confined to C# provider infrastructure.</summary>
/// <param name="Path">The acquired local path, never a bridge payload.</param>
/// <param name="Access">Optional access grant transferred to the resulting lease.</param>
/// <param name="AllowsSiblingReplacement">Whether access permits same-directory staging and replacement.</param>
public sealed record NativeFileSelection(string Path, IAsyncDisposable? Access, bool AllowsSiblingReplacement)
{
    /// <summary>The filesystem view that holds the path. It selects how a save stages and replaces the file.</summary>
    public NativeFileLocation Location { get; init; }
    /// <summary>The request's filter that was active when the user confirmed the dialog, or null when the dialog does
    /// not report it. The first selection of a dialog carries it; a filter that is not one of the request's is ignored.</summary>
    public FileDialogFilter? SelectedFilter { get; init; }
}
/// <summary>The filesystem view through which a native selection is reached.</summary>
public enum NativeFileLocation
{
    /// <summary>An ordinary host path. A save replaces the final target of a symbolic link.</summary>
    Host,
    /// <summary>A document in the xdg-document-portal FUSE view. A save stages beside the exact document name and never resolves links.</summary>
    DocumentPortal,
}
/// <summary>An acquired local directory, confined to C# provider infrastructure.</summary>
/// <param name="Path">The exact local path, never a bridge payload.</param>
/// <param name="Access">Optional native access grant transferred to the resulting lease.</param>
public sealed record NativeDirectorySelection(string Path, IAsyncDisposable? Access);
/// <summary>What one native file dialog asks for, confined to C# provider infrastructure.</summary>
/// <param name="Save">Whether the dialog selects a save destination rather than existing files.</param>
/// <param name="SuggestedName">The suggested filename of a save dialog.</param>
public sealed record NativeFileDialogRequest(bool Save, string? SuggestedName)
{
    /// <summary>The file types to offer, the first selected initially; empty offers every file. A hint, never enforced.</summary>
    public ImmutableArray<FileDialogFilter> Filters { get; init; } = [];
    /// <summary>The fully qualified directory to open in, or null for the platform's choice.</summary>
    public string? InitialDirectory { get; init; }
    /// <summary>Whether an open dialog lets the user select more than one file. Never set for a save dialog.</summary>
    public bool AllowMultiple { get; init; }
    /// <summary>Whether the dialog should offer a choice between <see cref="Filters"/> where it does not by itself, as
    /// <see cref="OpenFileOptions.ShowFilterChoice"/> asks.</summary>
    public bool ShowFilterChoice { get; init; }
}
/// <summary>Optional directory-selection support for a native file picker.</summary>
public interface INativeDirectoryPicker
{
    /// <summary>Shows one owned directory dialog that opens in <paramref name="initialDirectory"/> where supported;
    /// null means dismissal. Acquired access transfers to the caller.</summary>
    ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(string? initialDirectory, CancellationToken cancellationToken);
    /// <summary>Shows one owned directory dialog that lets the user select several directories; null or an empty list
    /// means dismissal. Acquired access transfers to the caller.</summary>
    /// <remarks>The default throws <see cref="NativeBackendUnavailableException"/>. A picker that implements it also
    /// returns <see langword="true"/> from <see cref="SupportsMultipleDirectorySelection"/>.</remarks>
    ValueTask<IReadOnlyList<NativeDirectorySelection>?> SelectDirectoriesAsync(string? initialDirectory, CancellationToken cancellationToken) =>
        throw new NativeBackendUnavailableException();
    /// <summary>Whether <see cref="SelectDirectoriesAsync"/> is implemented.</summary>
    bool SupportsMultipleDirectorySelection => false;
}
/// <summary>A statically selected native picker which returns acquired access.</summary>
public interface INativeFilePicker
{
    /// <summary>Shows one owned dialog for <paramref name="request"/>; null or an empty list means dismissal.</summary>
    /// <remarks>
    /// Cancellation must release acquired access. A picker that returns more than one selection must also return
    /// <see langword="true"/> from <see cref="SupportsMultipleSelection"/>; one that cannot show a dialog throws
    /// <see cref="NativeBackendUnavailableException"/>.
    /// </remarks>
    ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken);
    /// <summary>Whether this environment's save selections permit sibling staging and replacement.</summary>
    bool SupportsAtomicReplace => true;
    /// <summary>Whether <see cref="SelectAsync(NativeFileDialogRequest, CancellationToken)"/> implements multiple selection.</summary>
    bool SupportsMultipleSelection => false;
}

/// <summary>Converts acquired native selections to safe, single-use file leases.</summary>
public sealed partial class NativePickerBackend(INativeWindowOwner owner, INativeFilePicker picker) : IPickerBackend
{
    private readonly Guid _generation = owner.Generation;

    /// <inheritdoc />
    public bool IsAvailable => owner.IsAvailable && owner.Generation == _generation;

    /// <inheritdoc />
    public bool SupportsAtomicReplace => picker.SupportsAtomicReplace;

    /// <inheritdoc />
    public bool SupportsDirectorySelection => picker is INativeDirectoryPicker;

    /// <inheritdoc />
    public bool SupportsMultipleSelection => picker.SupportsMultipleSelection;

    /// <inheritdoc />
    public bool SupportsMultipleDirectorySelection => picker is INativeDirectoryPicker { SupportsMultipleDirectorySelection: true };

    /// <inheritdoc />
    public async ValueTask<PickerResult<IDirectoryLease>> OpenDirectoryAsync(OpenDirectoryOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.OwnerPolicy)) throw new ArgumentOutOfRangeException(nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        return await OpenDirectoriesCoreAsync(options, multiple: false, cancellationToken).ConfigureAwait(false) switch
        {
            PickerResult<DirectoryLeaseList>.Selected(var list) => new PickerResult<IDirectoryLease>.Selected(list[0]),
            PickerResult<DirectoryLeaseList>.Dismissed => new PickerResult<IDirectoryLease>.Dismissed(),
            PickerResult<DirectoryLeaseList>.Unavailable(var reason) => new PickerResult<IDirectoryLease>.Unavailable(reason),
            PickerResult<DirectoryLeaseList>.Failed(var code, var diagnostic) => new PickerResult<IDirectoryLease>.Failed(code, diagnostic),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <inheritdoc />
    public async ValueTask<PickerResult<IDirectoryLeaseList>> OpenDirectoriesAsync(OpenDirectoryOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.OwnerPolicy)) throw new ArgumentOutOfRangeException(nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) return new PickerResult<IDirectoryLeaseList>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        if (!SupportsMultipleDirectorySelection) return new PickerResult<IDirectoryLeaseList>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        return await OpenDirectoriesCoreAsync(options, multiple: true, cancellationToken).ConfigureAwait(false) switch
        {
            PickerResult<DirectoryLeaseList>.Selected(var list) => new PickerResult<IDirectoryLeaseList>.Selected(list),
            PickerResult<DirectoryLeaseList>.Dismissed => new PickerResult<IDirectoryLeaseList>.Dismissed(),
            PickerResult<DirectoryLeaseList>.Unavailable(var reason) => new PickerResult<IDirectoryLeaseList>.Unavailable(reason),
            PickerResult<DirectoryLeaseList>.Failed(var code, var diagnostic) => new PickerResult<IDirectoryLeaseList>.Failed(code, diagnostic),
            _ => throw new InvalidOperationException(),
        };
    }

    // Checks every selected directory before returning any: one that is not a directory releases all of them.
    private async ValueTask<PickerResult<DirectoryLeaseList>> OpenDirectoriesCoreAsync(OpenDirectoryOptions options, bool multiple, CancellationToken cancellationToken)
    {
        if (!IsAvailable) return new PickerResult<DirectoryLeaseList>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        if (picker is not INativeDirectoryPicker directories)
            return new PickerResult<DirectoryLeaseList>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        IReadOnlyList<NativeDirectorySelection?>? selections = null;
        bool transferred = false;
        try
        {
            selections = multiple
                ? await directories.SelectDirectoriesAsync(options.InitialDirectory, cancellationToken).ConfigureAwait(false)
                : await directories.SelectDirectoryAsync(options.InitialDirectory, cancellationToken).ConfigureAwait(false) is { } one ? [one] : null;
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAvailable) return new PickerResult<DirectoryLeaseList>.Unavailable(PlatformUnavailableReason.OwnerClosed);
            if (selections is null || selections.Count == 0) return new PickerResult<DirectoryLeaseList>.Dismissed();
            foreach (var selection in selections)
            {
                if (selection is null || !Path.IsPathFullyQualified(selection.Path) || selection.Path.Contains('\0'))
                    return new PickerResult<DirectoryLeaseList>.Failed(PlatformFailureCode.InvalidData);
                if (!OperatingSystem.IsWindows()) _ = new System.Text.UTF8Encoding(false, true).GetByteCount(selection.Path);
                // GetAttributes preserves operational failures; Directory.Exists would hide
                // permission denial and a directory removed after native selection.
                if ((File.GetAttributes(selection.Path) & FileAttributes.Directory) == 0)
                    return new PickerResult<DirectoryLeaseList>.Failed(PlatformFailureCode.InvalidData);
            }
            var list = new DirectoryLeaseList([.. selections.Select(static selection => (IDirectoryLease)new DirectoryLease(selection!.Path, selection.Access))]);
            transferred = true;
            return new PickerResult<DirectoryLeaseList>.Selected(list);
        }
        catch (OwnerClosedException) { return new PickerResult<DirectoryLeaseList>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (DllNotFoundException) { return new PickerResult<DirectoryLeaseList>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (EntryPointNotFoundException) { return new PickerResult<DirectoryLeaseList>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (NativeBackendUnavailableException) { return new PickerResult<DirectoryLeaseList>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (UnauthorizedAccessException error) { return new PickerResult<DirectoryLeaseList>.Failed(PlatformFailureCode.PermissionDenied, FileErrors.Diagnose(error)); }
        catch (ArgumentException) { return new PickerResult<DirectoryLeaseList>.Failed(PlatformFailureCode.InvalidData); }
        catch (IOException error) { return new PickerResult<DirectoryLeaseList>.Failed(PlatformFailureCode.IOError, FileErrors.Diagnose(error)); }
        finally
        {
            if (!transferred && selections is not null)
                foreach (var selection in selections)
                    if (selection?.Access is { } access) await access.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.OwnerPolicy)) throw new ArgumentOutOfRangeException(nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        return await OpenAsync(options, multiple: false, cancellationToken).ConfigureAwait(false) switch
        {
            PickerResult<ReadFileLeaseList>.Selected { Value: var list } selected => new PickerResult<IReadFileLease>.Selected(list[0]) { SelectedFilter = selected.SelectedFilter },
            PickerResult<ReadFileLeaseList>.Dismissed => new PickerResult<IReadFileLease>.Dismissed(),
            PickerResult<ReadFileLeaseList>.Unavailable(var reason) => new PickerResult<IReadFileLease>.Unavailable(reason),
            PickerResult<ReadFileLeaseList>.Failed(var code, var diagnostic) => new PickerResult<IReadFileLease>.Failed(code, diagnostic),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <inheritdoc />
    public async ValueTask<PickerResult<IReadFileLeaseList>> OpenFilesAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.OwnerPolicy)) throw new ArgumentOutOfRangeException(nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) return new PickerResult<IReadFileLeaseList>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        if (!picker.SupportsMultipleSelection) return new PickerResult<IReadFileLeaseList>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        return await OpenAsync(options, multiple: true, cancellationToken).ConfigureAwait(false) switch
        {
            PickerResult<ReadFileLeaseList>.Selected { Value: var list } selected => new PickerResult<IReadFileLeaseList>.Selected(list) { SelectedFilter = selected.SelectedFilter },
            PickerResult<ReadFileLeaseList>.Dismissed => new PickerResult<IReadFileLeaseList>.Dismissed(),
            PickerResult<ReadFileLeaseList>.Unavailable(var reason) => new PickerResult<IReadFileLeaseList>.Unavailable(reason),
            PickerResult<ReadFileLeaseList>.Failed(var code, var diagnostic) => new PickerResult<IReadFileLeaseList>.Failed(code, diagnostic),
            _ => throw new InvalidOperationException(),
        };
    }

    // Opens every selected file before returning any: a selection that cannot be opened releases all of them.
    private async ValueTask<PickerResult<ReadFileLeaseList>> OpenAsync(OpenFileOptions options, bool multiple, CancellationToken cancellationToken)
    {
        if (!IsAvailable) return new PickerResult<ReadFileLeaseList>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        IReadOnlyList<NativeFileSelection>? selections = null;
        List<ReadFileLease> leases = [];
        bool transferred = false;
        try
        {
            selections = await picker.SelectAsync(new NativeFileDialogRequest(false, null)
            {
                Filters = options.Filters, InitialDirectory = options.InitialDirectory, AllowMultiple = multiple,
                ShowFilterChoice = options.ShowFilterChoice,
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAvailable) return new PickerResult<ReadFileLeaseList>.Unavailable(PlatformUnavailableReason.OwnerClosed);
            if (selections is null || selections.Count == 0) return new PickerResult<ReadFileLeaseList>.Dismissed();
            if (!multiple && selections.Count != 1) return new PickerResult<ReadFileLeaseList>.Failed(PlatformFailureCode.InvalidData);
            foreach (var selection in selections)
            {
                var stream = OpenSelected(selection.Path);
                leases.Add(new ReadFileLease(Path.GetFileName(selection.Path), stream, selection.Access));
            }
            transferred = true;
            return new PickerResult<ReadFileLeaseList>.Selected(new ReadFileLeaseList([.. leases]))
            {
                SelectedFilter = ChosenFilter(options.Filters, selections[0].SelectedFilter),
            };
        }
        catch (OwnerClosedException) { return new PickerResult<ReadFileLeaseList>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (DllNotFoundException) { return new PickerResult<ReadFileLeaseList>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (EntryPointNotFoundException) { return new PickerResult<ReadFileLeaseList>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (NativeBackendUnavailableException) { return new PickerResult<ReadFileLeaseList>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (UnauthorizedAccessException error) { return new PickerResult<ReadFileLeaseList>.Failed(PlatformFailureCode.PermissionDenied, FileErrors.Diagnose(error)); }
        catch (IOException error) { return new PickerResult<ReadFileLeaseList>.Failed(PlatformFailureCode.IOError, FileErrors.Diagnose(error)); }
        finally
        {
            if (!transferred)
            {
                // Leases own the access of the selections they opened; release the rest directly.
                foreach (var lease in leases) await lease.DisposeAsync().ConfigureAwait(false);
                if (selections is not null)
                    for (int index = leases.Count; index < selections.Count; index++)
                        if (selections[index]?.Access is { } access) await access.DisposeAsync().ConfigureAwait(false);
            }
        }
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
        if (!IsAvailable) return new PickerResult<ISaveFileLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        try
        {
            var selections = await picker.SelectAsync(new NativeFileDialogRequest(true, options.SuggestedName)
            {
                Filters = options.Filters, InitialDirectory = options.InitialDirectory, ShowFilterChoice = options.ShowFilterChoice,
            }, cancellationToken).ConfigureAwait(false);
            if (selections is { Count: > 1 })
            {
                foreach (var extra in selections)
                    if (extra?.Access is { } extraAccess) await extraAccess.DisposeAsync().ConfigureAwait(false);
                return new PickerResult<ISaveFileLease>.Failed(PlatformFailureCode.InvalidData);
            }
            var selection = selections is { Count: 1 } ? selections[0] : null;
            if (selection is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new PickerResult<ISaveFileLease>.Dismissed();
            }
            if (!IsAvailable)
            {
                if (selection.Access is { } access) await access.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new PickerResult<ISaveFileLease>.Unavailable(PlatformUnavailableReason.OwnerClosed);
            }
            // CreateAsync takes ownership of acquired access, including failure paths.
            var lease = await SaveFileLease.CreateAsync(selection.Path,
                selection.AllowsSiblingReplacement ? new LocalAtomicFileReplacement(selection.Location) : new NoAtomicReplacement(),
                selection.Access, cancellationToken).ConfigureAwait(false);
            return new PickerResult<ISaveFileLease>.Selected(lease) { SelectedFilter = ChosenFilter(options.Filters, selection.SelectedFilter) };
        }
        catch (OwnerClosedException) { return new PickerResult<ISaveFileLease>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (DllNotFoundException) { return new PickerResult<ISaveFileLease>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (EntryPointNotFoundException) { return new PickerResult<ISaveFileLease>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (NativeBackendUnavailableException) { return new PickerResult<ISaveFileLease>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (UnauthorizedAccessException error) { return new PickerResult<ISaveFileLease>.Failed(PlatformFailureCode.PermissionDenied, FileErrors.Diagnose(error)); }
        catch (IOException error) { return new PickerResult<ISaveFileLease>.Failed(PlatformFailureCode.IOError, FileErrors.Diagnose(error)); }
    }

    // Only one of the request's own filters is reported; a picker cannot invent one.
    private static FileDialogFilter? ChosenFilter(System.Collections.Immutable.ImmutableArray<FileDialogFilter> filters, FileDialogFilter? chosen) =>
        chosen is not null && !filters.IsDefaultOrEmpty && filters.Contains(chosen) ? chosen : null;

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
