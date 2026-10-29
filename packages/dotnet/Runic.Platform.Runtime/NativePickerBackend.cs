using System.Collections.Immutable;

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
    // How a lease of this selection stages and replaces the file, when the provider chooses it, such as macOS's item
    // replacement in the App Sandbox; null derives it from AllowsSiblingReplacement and Location.
    internal IAtomicFileReplacement? Replacement { get; init; }
    // The bookmarks a lease of this selection creates, when the provider has its own; null uses the path's.
    internal IDesktopFileBookmarks? Bookmarks { get; init; }
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
    /// <summary>
    /// Whether directory selection can work now. A picker that checks its backend in advance, such as an XDG desktop
    /// portal that may be missing from the session bus, returns <see langword="false"/> once it knows the backend is
    /// missing, so the <c>platform.directories.open</c> capability reports it before a pick. The default is
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>A picker still receives selection calls while it returns <see langword="false"/>, so it can check again.</remarks>
    bool IsDirectorySelectionAvailable => true;
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
public sealed class NativePickerBackend(INativeWindowOwner owner, INativeFilePicker picker) : IPickerBackend
{
    private readonly Guid _generation = owner.Generation;

    /// <inheritdoc />
    public bool IsAvailable => owner.IsAvailable && owner.Generation == _generation;

    /// <inheritdoc />
    public bool SupportsAtomicReplace => picker.SupportsAtomicReplace;

    /// <inheritdoc />
    public bool SupportsDirectorySelection => picker is INativeDirectoryPicker { IsDirectorySelectionAvailable: true };

    /// <inheritdoc />
    public bool SupportsMultipleSelection => picker.SupportsMultipleSelection;

    /// <inheritdoc />
    public bool SupportsMultipleDirectorySelection =>
        picker is INativeDirectoryPicker { SupportsMultipleDirectorySelection: true, IsDirectorySelectionAvailable: true };

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
        // The picker decides when its backend is missing (IsDirectorySelectionAvailable), and may check it again.
        if (picker is not INativeDirectoryPicker { SupportsMultipleDirectorySelection: true })
            return new PickerResult<IDirectoryLeaseList>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
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
                var stream = RegularFiles.OpenRead(selection.Path);
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
    public bool SupportsDocumentAccess => true;

    // Windows and Linux keep access through the path. macOS selections bring NSURL bookmarks (NativeFileSelection.Bookmarks);
    // a macOS picker without them reports bookmarks unavailable.
    private static readonly IDesktopFileBookmarks? PathBookmarks = OperatingSystem.IsMacOS() ? null : new PathFileBookmarks();
    private static IDesktopFileBookmarks? Bookmarks(NativeFileSelection selection) => selection.Bookmarks ?? PathBookmarks;

    /// <inheritdoc />
    public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default) =>
        SaveAsync<ISaveFileLease>(options, async (selection, token) => await SaveFileLease.CreateAsync(selection.Path, Replacement(selection),
            selection.Access, Bookmarks(selection), token).ConfigureAwait(false), cancellationToken);

    /// <inheritdoc />
    public ValueTask<PickerResult<IDocumentFileLease>> SaveDocumentAsync(SaveFileOptions options, CancellationToken cancellationToken = default) =>
        SaveAsync<IDocumentFileLease>(options, async (selection, token) => await DocumentFileLease.CreateAsync(selection.Path, Replacement(selection),
            selection.Access, Bookmarks(selection), existing: false, token).ConfigureAwait(false), cancellationToken);

    /// <inheritdoc />
    public async ValueTask<PickerResult<IDocumentFileLease>> OpenDocumentAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.OwnerPolicy)) throw new ArgumentOutOfRangeException(nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) return new PickerResult<IDocumentFileLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        IReadOnlyList<NativeFileSelection>? selections = null;
        bool transferred = false;
        try
        {
            selections = await picker.SelectAsync(new NativeFileDialogRequest(false, null)
            {
                Filters = options.Filters, InitialDirectory = options.InitialDirectory, ShowFilterChoice = options.ShowFilterChoice,
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAvailable) return new PickerResult<IDocumentFileLease>.Unavailable(PlatformUnavailableReason.OwnerClosed);
            if (selections is null || selections.Count == 0) return new PickerResult<IDocumentFileLease>.Dismissed();
            if (selections.Count != 1) return new PickerResult<IDocumentFileLease>.Failed(PlatformFailureCode.InvalidData);
            var selection = selections[0];
            // CreateAsync takes ownership of acquired access, including failure paths.
            transferred = true;
            var lease = await DocumentFileLease.CreateAsync(selection.Path, Replacement(selection), selection.Access, Bookmarks(selection), existing: true,
                cancellationToken).ConfigureAwait(false);
            return new PickerResult<IDocumentFileLease>.Selected(lease) { SelectedFilter = ChosenFilter(options.Filters, selection.SelectedFilter) };
        }
        catch (OwnerClosedException) { return new PickerResult<IDocumentFileLease>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (DllNotFoundException) { return new PickerResult<IDocumentFileLease>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (EntryPointNotFoundException) { return new PickerResult<IDocumentFileLease>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (NativeBackendUnavailableException) { return new PickerResult<IDocumentFileLease>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (UnauthorizedAccessException error) { return new PickerResult<IDocumentFileLease>.Failed(PlatformFailureCode.PermissionDenied, FileErrors.Diagnose(error)); }
        catch (IOException error) { return new PickerResult<IDocumentFileLease>.Failed(PlatformFailureCode.IOError, FileErrors.Diagnose(error)); }
        finally
        {
            if (!transferred && selections is not null)
                foreach (var selection in selections)
                    if (selection?.Access is { } access) await access.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static IAtomicFileReplacement Replacement(NativeFileSelection selection) => selection.Replacement
        ?? (selection.AllowsSiblingReplacement ? new LocalAtomicFileReplacement(selection.Location) : new NoAtomicReplacement());

    private async ValueTask<PickerResult<T>> SaveAsync<T>(SaveFileOptions options,
        Func<NativeFileSelection, CancellationToken, ValueTask<T>> create, CancellationToken cancellationToken) where T : ISaveFileLease
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.OwnerPolicy)) throw new ArgumentOutOfRangeException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SuggestedName);
        if (options.SuggestedName.IndexOfAny(['/', '\\', '\0']) >= 0 || options.SuggestedName is "." or "..")
            throw new ArgumentException("The suggestion must be a filename, not a path.", nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) return new PickerResult<T>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
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
                return new PickerResult<T>.Failed(PlatformFailureCode.InvalidData);
            }
            var selection = selections is { Count: 1 } ? selections[0] : null;
            if (selection is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new PickerResult<T>.Dismissed();
            }
            if (!IsAvailable)
            {
                if (selection.Access is { } access) await access.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new PickerResult<T>.Unavailable(PlatformUnavailableReason.OwnerClosed);
            }
            // The lease takes ownership of acquired access, including failure paths.
            var lease = await create(selection, cancellationToken).ConfigureAwait(false);
            return new PickerResult<T>.Selected(lease) { SelectedFilter = ChosenFilter(options.Filters, selection.SelectedFilter) };
        }
        catch (OwnerClosedException) { return new PickerResult<T>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (DllNotFoundException) { return new PickerResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (EntryPointNotFoundException) { return new PickerResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (NativeBackendUnavailableException) { return new PickerResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (UnauthorizedAccessException error) { return new PickerResult<T>.Failed(PlatformFailureCode.PermissionDenied, FileErrors.Diagnose(error)); }
        catch (IOException error) { return new PickerResult<T>.Failed(PlatformFailureCode.IOError, FileErrors.Diagnose(error)); }
    }

    // Only one of the request's own filters is reported; a picker cannot invent one.
    private static FileDialogFilter? ChosenFilter(System.Collections.Immutable.ImmutableArray<FileDialogFilter> filters, FileDialogFilter? chosen) =>
        chosen is not null && !filters.IsDefaultOrEmpty && filters.Contains(chosen) ? chosen : null;

    private sealed class NoAtomicReplacement : IAtomicFileReplacement
    {
        public bool IsSupported => false;
        public ValueTask<FileCommitResult> ReplaceAsync(string stagingPath, string targetPath, bool existed) => throw new NotSupportedException();
    }
}
