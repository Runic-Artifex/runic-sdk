using System.Collections.Immutable;

namespace Runic.Platform.Runtime;

// A provider's badge and progress for one window: the Windows taskbar button, the macOS Dock tile, the Linux launcher
// entry. Disposal, when the window's presentation stops, removes what the window showed.
internal interface IWindowTaskbar : IAsyncDisposable
{
    // Unavailable when the desktop has none at all, for the capability snapshot.
    CapabilityStatus Status => new CapabilityStatus.Available();
    ValueTask<PlatformResult<PlatformUnit>> SetBadgeAsync(DesktopBadge? badge, CancellationToken cancellationToken);
    ValueTask<PlatformResult<PlatformUnit>> SetProgressAsync(DesktopProgress? progress, CancellationToken cancellationToken);
}

/// <summary>Tracks the badge and progress of one presentation and removes them during shutdown.</summary>
internal sealed class PresentationTaskbar : IPlatformCapabilities
{
    private readonly PresentationLifetime lifetime;
    private readonly IWindowTaskbar? backend;

    /// <summary>Creates a facade and transfers ownership of the provider's taskbar to the lifetime.</summary>
    public PresentationTaskbar(PresentationLifetime lifetime, IWindowTaskbar? backend = null)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        this.lifetime = lifetime;
        this.backend = backend;
        if (backend is not null && !lifetime.Own(new TaskbarResource(lifetime, backend)))
            throw new ObjectDisposedException(nameof(lifetime), "Register providers before shutting down the presentation.");
    }

    private sealed class TaskbarResource(PresentationLifetime lifetime, IAsyncDisposable resource) : PresentationLease(lifetime, resource);

    private PlatformUnavailableReason? Reason => lifetime.IsClosing ? PlatformUnavailableReason.OwnerClosed
        : backend is null ? PlatformUnavailableReason.ProviderNotConfigured
        : backend.Status is CapabilityStatus.Unavailable(var unavailable) ? unavailable
        : !lifetime.HasOwner ? PlatformUnavailableReason.OwnerUnavailable : null;

    /// <inheritdoc />
    public CapabilitySnapshot GetSnapshot()
    {
        CapabilityStatus status = Reason is { } reason ? new CapabilityStatus.Unavailable(reason) : new CapabilityStatus.Available();
        return new(lifetime.Generation, ImmutableDictionary<string, CapabilityStatus>.Empty
            .Add(PlatformCapabilityIds.TaskbarBadge, status).Add(PlatformCapabilityIds.TaskbarProgress, status));
    }

    public ValueTask<PlatformResult<PlatformUnit>> SetBadgeAsync(DesktopBadge? badge, CancellationToken cancellationToken = default)
    {
        DesktopTaskbarRules.Check(badge, cancellationToken);
        return InvokeAsync((taskbar, token) => taskbar.SetBadgeAsync(badge, token), cancellationToken);
    }

    public ValueTask<PlatformResult<PlatformUnit>> SetProgressAsync(DesktopProgress? progress, CancellationToken cancellationToken = default)
    {
        DesktopTaskbarRules.Check(progress, cancellationToken);
        return InvokeAsync((taskbar, token) => taskbar.SetProgressAsync(progress, token), cancellationToken);
    }

    private async ValueTask<PlatformResult<PlatformUnit>> InvokeAsync(
        Func<IWindowTaskbar, CancellationToken, ValueTask<PlatformResult<PlatformUnit>>> action, CancellationToken cancellationToken)
    {
        using var operation = lifetime.TryBeginOperation();
        if (operation is null) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        if (Reason is { } reason) return new PlatformResult<PlatformUnit>.Unavailable(reason);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Shutdown);
        try
        {
            return await action(backend!, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsClosing && !cancellationToken.IsCancellationRequested)
        {
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        }
        catch (OwnerClosedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        }
    }
}
