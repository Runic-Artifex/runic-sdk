using System.Collections.Immutable;

namespace Runic.Platform.Runtime;

/// <summary>Drains file handoffs before the native presentation owner is destroyed.</summary>
public sealed class PresentationFileLauncher(PresentationLifetime lifetime, IDesktopFileLauncher? backend = null) : IDesktopFileLauncher, IPlatformCapabilities
{
    /// <inheritdoc />
    public async ValueTask<PlatformResult<Unit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default)
    {
        path = DesktopServiceValidation.FilePath(path, operation);
        cancellationToken.ThrowIfCancellationRequested();
        using var admitted = lifetime.TryBeginOperation();
        if (admitted is null) return new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerClosed);
        if (backend is null) return new PlatformResult<Unit>.Unavailable(UnavailableReason.ProviderNotConfigured);
        if (!lifetime.HasOwner) return new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerUnavailable);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Shutdown);
        try { return await backend.LaunchAsync(path, operation, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsClosing && !cancellationToken.IsCancellationRequested)
        { return new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerClosed); }
    }
    /// <inheritdoc />
    public CapabilitySnapshot GetSnapshot()
    {
        CapabilityStatus status = backend is null ? new CapabilityStatus.Unavailable(UnavailableReason.ProviderNotConfigured)
            : lifetime.IsClosing ? new CapabilityStatus.Unavailable(UnavailableReason.OwnerClosed)
            : !lifetime.HasOwner ? new CapabilityStatus.Unavailable(UnavailableReason.OwnerUnavailable) : new CapabilityStatus.Available();
        return new(lifetime.Generation, ImmutableDictionary<string, CapabilityStatus>.Empty.Add("platform.files.launch", status));
    }
}
