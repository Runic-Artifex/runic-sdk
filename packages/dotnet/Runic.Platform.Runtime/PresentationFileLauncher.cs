using System.Collections.Immutable;

namespace Runic.Platform.Runtime;

/// <summary>Drains file handoffs before the native presentation owner is destroyed.</summary>
public sealed class PresentationFileLauncher(PresentationLifetime lifetime, IDesktopFileLauncher? backend = null) : IDesktopFileLauncher, IPlatformCapabilities
{
    /// <inheritdoc />
    public async ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default)
    {
        path = DesktopServiceValidation.FilePath(path, operation);
        cancellationToken.ThrowIfCancellationRequested();
        using var admitted = lifetime.TryBeginOperation();
        if (admitted is null) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        if (backend is null) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        if (!lifetime.HasOwner) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Shutdown);
        try { return await backend.LaunchAsync(path, operation, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsClosing && !cancellationToken.IsCancellationRequested)
        { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
    }
    /// <inheritdoc />
    public CapabilitySnapshot GetSnapshot()
    {
        CapabilityStatus status = backend is null ? new CapabilityStatus.Unavailable(PlatformUnavailableReason.ProviderNotConfigured)
            : lifetime.IsClosing ? new CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerClosed)
            : !lifetime.HasOwner ? new CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerUnavailable) : new CapabilityStatus.Available();
        return new(lifetime.Generation, ImmutableDictionary<string, CapabilityStatus>.Empty.Add("platform.files.launch", status));
    }
}
