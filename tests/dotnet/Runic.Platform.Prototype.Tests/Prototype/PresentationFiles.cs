using System.Collections.Immutable;

namespace Runic.Platform.Prototype;

internal sealed class PresentationFiles(PresentationLifetime lifetime, IPickerBackend? backend = null)
    : IFileDialogs, IPlatformCapabilities
{
    public CapabilitySnapshot GetSnapshot()
    {
        CapabilityStatus files = Reason(OwnerPolicy.RequireOwner) is { } reason
            ? new CapabilityStatus.Unavailable(reason) : new CapabilityStatus.Available();
        return new(lifetime.Generation, ImmutableDictionary<string, CapabilityStatus>.Empty
            .Add("platform.files.open", files).Add("platform.files.save", files)
            .Add("platform.dialogs.owned", files));
    }

    private UnavailableReason? Reason(OwnerPolicy policy)
    {
        if (lifetime.IsClosing) return UnavailableReason.OwnerClosed;
        if (backend is null) return UnavailableReason.ProviderNotConfigured;
        if (!backend.IsAvailable) return UnavailableReason.BackendUnavailable;
        if (policy == OwnerPolicy.RequireOwner && !lifetime.HasOwner) return UnavailableReason.OwnerUnavailable;
        return null;
    }

    public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PickAsync(options.OwnerPolicy, token => backend!.OpenFileAsync(options, token), cancellationToken);
    }

    public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SuggestedName);
        if (options.SuggestedName.IndexOfAny(['/', '\\', '\0']) >= 0 || options.SuggestedName is "." or "..")
            throw new ArgumentException("The suggestion must be a filename, not a path.", nameof(options));
        return PickAsync(options.OwnerPolicy, token => backend!.SaveFileAsync(options, token), cancellationToken);
    }

    private async ValueTask<PickerResult<T>> PickAsync<T>(OwnerPolicy policy,
        Func<CancellationToken, ValueTask<PickerResult<T>>> invoke, CancellationToken caller) where T : IAsyncDisposable
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        caller.ThrowIfCancellationRequested();
        using var operation = lifetime.TryBeginOperation();
        if (operation is null) return new PickerResult<T>.Unavailable(UnavailableReason.OwnerClosed);
        if (Reason(policy) is { } reason) return new PickerResult<T>.Unavailable(reason);
        using var picker = lifetime.TryBeginPicker();
        if (picker is null)
            return new PickerResult<T>.Failed(FailureCode.ResourceBusy);
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
                return new PickerResult<T>.Unavailable(UnavailableReason.OwnerClosed);
            }
            return result;
        }
        catch (OperationCanceledException) when (lifetime.IsClosing && !caller.IsCancellationRequested)
        {
            return new PickerResult<T>.Unavailable(UnavailableReason.OwnerClosed);
        }
    }
}
