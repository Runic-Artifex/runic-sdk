using System.Collections.Immutable;

namespace Runic.Platform.Runtime;

/// <summary>Serializes owned selection and attaches every acquired lease to its presentation.</summary>
internal sealed class PresentationFiles(PresentationLifetime lifetime, IPickerBackend? backend = null)
    : IFileDialogs, IPlatformCapabilities
{
    /// <inheritdoc />
    public CapabilitySnapshot GetSnapshot()
    {
        CapabilityStatus files = Reason(OwnerPolicy.RequireOwner) is { } reason
            ? new CapabilityStatus.Unavailable(reason) : new CapabilityStatus.Available();
        // A destination that can never be written atomically is not a usable save.
        CapabilityStatus save = files is CapabilityStatus.Available && backend?.SupportsAtomicReplace == false
            ? new CapabilityStatus.Unavailable(PlatformUnavailableReason.AtomicReplaceUnavailable) : files;
        CapabilityStatus directories = files is CapabilityStatus.Available && backend?.SupportsDirectorySelection != true
            ? new CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable) : files;
        CapabilityStatus multiple = files is CapabilityStatus.Available && backend?.SupportsMultipleSelection != true
            ? new CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable) : files;
        CapabilityStatus multipleDirectories = files is CapabilityStatus.Available && backend?.SupportsMultipleDirectorySelection != true
            ? new CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable) : files;
        CapabilityStatus documents = files is CapabilityStatus.Available && backend?.SupportsDocumentAccess != true
            ? new CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable) : files;
        return new(lifetime.Generation, ImmutableDictionary<string, CapabilityStatus>.Empty
            .Add(PlatformCapabilityIds.FilesOpen, files).Add(PlatformCapabilityIds.FilesSave, save)
            .Add(PlatformCapabilityIds.DirectoriesOpen, directories)
            .Add(PlatformCapabilityIds.DialogsOwned, files)
            .Add(PlatformCapabilityIds.FilesOpenMultiple, multiple)
            .Add(PlatformCapabilityIds.DirectoriesOpenMultiple, multipleDirectories)
            .Add(PlatformCapabilityIds.FilesOpenDocument, documents));
    }

    /// <inheritdoc />
    public ValueTask<PickerResult<IDirectoryLease>> OpenDirectoryAsync(OpenDirectoryOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PickAsync(options.OwnerPolicy, token => OpenBackendDirectoryAsync(backend!, options, token),
            value => new PresentationDirectoryLease(lifetime, value), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PickerResult<IDirectoryLeaseList>> OpenDirectoriesAsync(OpenDirectoryOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PickAsync(options.OwnerPolicy, token => backend!.SupportsMultipleDirectorySelection ? backend.OpenDirectoriesAsync(options, token)
                : ValueTask.FromResult<PickerResult<IDirectoryLeaseList>>(new PickerResult<IDirectoryLeaseList>.Unavailable(PlatformUnavailableReason.BackendUnavailable)),
            value => new PresentationDirectoryLeaseList(lifetime, value), cancellationToken);
    }

    // The backend's OpenDirectoryAsync is the only selection behavior; its
    // SupportsDirectorySelection only reports that behavior before a call. A result
    // other than Unavailable from a backend declaring no support means the backend
    // implements the method but would be reported unavailable: fail visibly.
    private static async ValueTask<PickerResult<IDirectoryLease>> OpenBackendDirectoryAsync(IPickerBackend backend,
        OpenDirectoryOptions options, CancellationToken cancellationToken)
    {
        var result = await backend.OpenDirectoryAsync(options, cancellationToken).ConfigureAwait(false);
        if (result is PickerResult<IDirectoryLease>.Unavailable || backend.SupportsDirectorySelection) return result;
        if (result is PickerResult<IDirectoryLease>.Selected selected) await selected.Value.DisposeAsync().ConfigureAwait(false);
        throw new InvalidOperationException(
            $"{backend.GetType().Name} implements {nameof(IFileDialogs.OpenDirectoryAsync)} but reports {nameof(IPickerBackend.SupportsDirectorySelection)} as false. " +
            "Return true from SupportsDirectorySelection so the platform.directories.open capability reports directory selection.");
    }

    private PlatformUnavailableReason? Reason(OwnerPolicy policy)
    {
        if (lifetime.IsClosing) return PlatformUnavailableReason.OwnerClosed;
        if (backend is null) return PlatformUnavailableReason.ProviderNotConfigured;
        if (!backend.IsAvailable) return PlatformUnavailableReason.BackendUnavailable;
        if (policy == OwnerPolicy.RequireOwner && !lifetime.HasOwner) return PlatformUnavailableReason.OwnerUnavailable;
        return null;
    }

    /// <inheritdoc />
    public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PickAsync(options.OwnerPolicy, token => backend!.OpenFileAsync(options, token), value => new PresentationReadLease(lifetime, value), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PickerResult<IReadFileLeaseList>> OpenFilesAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PickAsync(options.OwnerPolicy, token => backend!.SupportsMultipleSelection ? backend.OpenFilesAsync(options, token)
                : ValueTask.FromResult<PickerResult<IReadFileLeaseList>>(new PickerResult<IReadFileLeaseList>.Unavailable(PlatformUnavailableReason.BackendUnavailable)),
            value => new PresentationReadLeaseList(lifetime, value), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SuggestedName);
        if (options.SuggestedName.IndexOfAny(['/', '\\', '\0']) >= 0 || options.SuggestedName is "." or "..")
            throw new ArgumentException("The suggestion must be a filename, not a path.", nameof(options));
        return PickAsync(options.OwnerPolicy, token => backend!.SaveFileAsync(options, token), value => new PresentationSaveLease(lifetime, value), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PickerResult<IDocumentFileLease>> OpenDocumentAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PickAsync(options.OwnerPolicy, token => backend!.SupportsDocumentAccess ? backend.OpenDocumentAsync(options, token) : NoDocuments(),
            value => new PresentationDocumentLease(lifetime, value), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PickerResult<IDocumentFileLease>> SaveDocumentAsync(SaveFileOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SuggestedName);
        if (options.SuggestedName.IndexOfAny(['/', '\\', '\0']) >= 0 || options.SuggestedName is "." or "..")
            throw new ArgumentException("The suggestion must be a filename, not a path.", nameof(options));
        return PickAsync(options.OwnerPolicy, token => backend!.SupportsDocumentAccess ? backend.SaveDocumentAsync(options, token) : NoDocuments(),
            value => new PresentationDocumentLease(lifetime, value), cancellationToken);
    }

    private static ValueTask<PickerResult<IDocumentFileLease>> NoDocuments() =>
        ValueTask.FromResult<PickerResult<IDocumentFileLease>>(new PickerResult<IDocumentFileLease>.Unavailable(PlatformUnavailableReason.BackendUnavailable));

    private async ValueTask<PickerResult<T>> PickAsync<T>(OwnerPolicy policy,
        Func<CancellationToken, ValueTask<PickerResult<T>>> invoke, Func<T, PresentationLease> wrap, CancellationToken caller) where T : IAsyncDisposable
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        caller.ThrowIfCancellationRequested();
        using var operation = lifetime.TryBeginOperation();
        if (operation is null) return new PickerResult<T>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        if (Reason(policy) is { } reason) return new PickerResult<T>.Unavailable(reason);
        using var picker = lifetime.TryBeginPicker();
        if (picker is null)
            return new PickerResult<T>.Failed(PlatformFailureCode.ResourceBusy);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime.Shutdown);
            cancellation.Token.ThrowIfCancellationRequested();
            PickerResult<T> result = await invoke(cancellation.Token).ConfigureAwait(false);
            if (caller.IsCancellationRequested || lifetime.IsClosing)
            {
                if (result is PickerResult<T>.Selected selected)
                {
                    try { await selected.Value.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) { lifetime.RecordCleanupFailure(error); throw; }
                }
                caller.ThrowIfCancellationRequested();
                return new PickerResult<T>.Unavailable(PlatformUnavailableReason.OwnerClosed);
            }
            if (result is PickerResult<T>.Selected selectedResult)
            {
                var owned = wrap(selectedResult.Value);
                if (!lifetime.Own(owned))
                {
                    await owned.DisposeAsync().ConfigureAwait(false);
                    return new PickerResult<T>.Unavailable(PlatformUnavailableReason.OwnerClosed);
                }
                return new PickerResult<T>.Selected((T)(IAsyncDisposable)owned) { SelectedFilter = selectedResult.SelectedFilter };
            }
            return result;
        }
        catch (OperationCanceledException) when (lifetime.IsClosing && !caller.IsCancellationRequested)
        {
            return new PickerResult<T>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        }
    }
}
