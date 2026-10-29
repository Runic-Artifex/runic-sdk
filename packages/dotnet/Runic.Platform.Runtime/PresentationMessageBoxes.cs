using System.Collections.Immutable;

namespace Runic.Platform.Runtime;

/// <summary>Tracks the message boxes of one presentation: closes them and reports the owner closed during shutdown.</summary>
/// <remarks>
/// A presentation without a native owner, or the application itself, may have <c>unowned</c> boxes:
/// <see cref="OwnerPolicy.PreferOwner"/> shows those while the lifetime has no owner and no owned backend, and
/// <see cref="OwnerPolicy.RequireOwner"/> reports the owner unavailable. The status describes a PreferOwner box.
/// </remarks>
internal sealed class PresentationMessageBoxes : IDesktopMessageBoxes, IPlatformCapabilities, IDesktopServiceStatus
{
    private readonly PresentationLifetime lifetime;
    private readonly IDesktopMessageBoxes? backend;
    private readonly IDesktopMessageBoxes? unowned;

    /// <summary>Creates a facade over the provider's boxes; <see langword="null"/> when the provider has none.</summary>
    public PresentationMessageBoxes(PresentationLifetime lifetime, IDesktopMessageBoxes? backend = null, IDesktopMessageBoxes? unowned = null)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        this.lifetime = lifetime;
        this.backend = backend;
        this.unowned = unowned;
    }

    // Unowned boxes serve a presentation that has none of its own: no owner now, and no owned backend.
    private bool UsesUnowned => unowned is not null && backend is null && !lifetime.HasOwner;

    private PlatformUnavailableReason? Reason(OwnerPolicy policy)
    {
        if (lifetime.IsClosing) return PlatformUnavailableReason.OwnerClosed;
        if (UsesUnowned)
        {
            if (policy != OwnerPolicy.PreferOwner) return PlatformUnavailableReason.OwnerUnavailable;
            return DesktopServiceStatus.Get(unowned!) is CapabilityStatus.Unavailable(var unownedReason) ? unownedReason : null;
        }
        return backend is null ? PlatformUnavailableReason.ProviderNotConfigured
            : DesktopServiceStatus.Get(backend) is CapabilityStatus.Unavailable(var unavailable) ? unavailable
            : !lifetime.HasOwner ? PlatformUnavailableReason.OwnerUnavailable : null;
    }

    /// <inheritdoc />
    public CapabilityStatus Status => Reason(OwnerPolicy.PreferOwner) is { } reason
        ? new CapabilityStatus.Unavailable(reason) : new CapabilityStatus.Available();

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
        if (Reason(messageBox.OwnerPolicy) is { } reason) return new PlatformResult<DesktopMessageBoxButton?>.Unavailable(reason);
        var shown = UsesUnowned ? unowned! : backend!;
        // Shutdown closes the box as the caller's cancellation does.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Shutdown);
        try
        {
            var result = await shown.ShowAsync(messageBox, linked.Token).ConfigureAwait(false);
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
