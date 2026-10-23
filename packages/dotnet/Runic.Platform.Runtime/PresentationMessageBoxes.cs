using System.Collections.Immutable;

namespace Runic.Platform.Runtime;

/// <summary>Tracks the message boxes of one presentation: closes them and reports the owner closed during shutdown.</summary>
internal sealed class PresentationMessageBoxes : IDesktopMessageBoxes, IPlatformCapabilities, IDesktopServiceStatus
{
    private readonly PresentationLifetime lifetime;
    private readonly IDesktopMessageBoxes? backend;

    /// <summary>Creates a facade over the provider's boxes; <see langword="null"/> when the provider has none.</summary>
    public PresentationMessageBoxes(PresentationLifetime lifetime, IDesktopMessageBoxes? backend = null)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        this.lifetime = lifetime;
        this.backend = backend;
    }

    private PlatformUnavailableReason? Reason => lifetime.IsClosing ? PlatformUnavailableReason.OwnerClosed
        : backend is null ? PlatformUnavailableReason.ProviderNotConfigured
        : DesktopServiceStatus.Get(backend) is CapabilityStatus.Unavailable(var unavailable) ? unavailable
        : !lifetime.HasOwner ? PlatformUnavailableReason.OwnerUnavailable : null;

    /// <inheritdoc />
    public CapabilityStatus Status => Reason is { } reason ? new CapabilityStatus.Unavailable(reason) : new CapabilityStatus.Available();

    /// <inheritdoc />
    public CapabilitySnapshot GetSnapshot() => new(lifetime.Generation,
        ImmutableDictionary<string, CapabilityStatus>.Empty.Add(PlatformCapabilityIds.MessageBoxes, Status));

    /// <inheritdoc />
    public async ValueTask<PlatformResult<DesktopMessageBoxButton?>> ShowAsync(DesktopMessageBox messageBox, CancellationToken cancellationToken = default)
    {
        DesktopMessageBoxRules.Check(messageBox);
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = lifetime.TryBeginOperation();
        if (operation is null) return new PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        if (Reason is { } reason) return new PlatformResult<DesktopMessageBoxButton?>.Unavailable(reason);
        // Shutdown closes the box as the caller's cancellation does.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Shutdown);
        try
        {
            var result = await backend!.ShowAsync(messageBox, linked.Token).ConfigureAwait(false);
            if (result is PlatformResult<DesktopMessageBoxButton?>.Success { Value: { } button } && !messageBox.Buttons.Contains(button))
                throw new InvalidOperationException("The message box provider returned a button that is not in the box.");
            return result;
        }
        catch (OperationCanceledException) when (lifetime.IsClosing && !cancellationToken.IsCancellationRequested)
        {
            return new PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        }
        catch (OwnerClosedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        }
    }
}
