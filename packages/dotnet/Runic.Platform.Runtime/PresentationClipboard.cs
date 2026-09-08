using System.Collections.Immutable;

namespace Runic.Platform.Runtime;

/// <summary>Tracks clipboard work for one presentation and drains it during shutdown.</summary>
public sealed class PresentationClipboard : ITextClipboard, IPlatformCapabilities
{
    private readonly PresentationLifetime lifetime;
    private readonly ITextClipboard? backend;

    /// <summary>Creates a facade and transfers ownership of an asynchronously disposable provider to the lifetime.</summary>
    public PresentationClipboard(PresentationLifetime lifetime, ITextClipboard? backend = null)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        this.lifetime = lifetime;
        this.backend = backend;
        if (backend is IAsyncDisposable disposable)
        {
            var resource = new ClipboardResource(lifetime, disposable);
            if (!lifetime.Own(resource))
                throw new ObjectDisposedException(nameof(lifetime), "Register providers before shutting down the presentation.");
        }
    }

    private sealed class ClipboardResource(PresentationLifetime lifetime, IAsyncDisposable resource)
        : PresentationLease(lifetime, resource);

    private UnavailableReason? Reason => lifetime.IsClosing ? UnavailableReason.OwnerClosed
        : backend is null ? UnavailableReason.ProviderNotConfigured
        : !lifetime.HasOwner ? UnavailableReason.OwnerUnavailable : null;

    /// <inheritdoc />
    public CapabilitySnapshot GetSnapshot()
    {
        CapabilityStatus status = Reason is { } reason
            ? new CapabilityStatus.Unavailable(reason) : new CapabilityStatus.Available();
        return new(lifetime.Generation, ImmutableDictionary<string, CapabilityStatus>.Empty
            .Add("platform.clipboard.readText", status).Add("platform.clipboard.writeText", status));
    }

    /// <inheritdoc />
    public async ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = lifetime.TryBeginOperation();
        if (operation is null) return new PlatformResult<string?>.Unavailable(UnavailableReason.OwnerClosed);
        if (Reason is { } reason) return new PlatformResult<string?>.Unavailable(reason);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Shutdown);
        try
        {
            var result = await backend!.ReadTextAsync(maximumCharacters, linked.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (lifetime.IsClosing) return new PlatformResult<string?>.Unavailable(UnavailableReason.OwnerClosed);
            return result is PlatformResult<string?>.Success { Value: { } text } && text.Length > maximumCharacters
                ? new PlatformResult<string?>.Failed(FailureCode.TooLarge) : result;
        }
        catch (OperationCanceledException) when (lifetime.IsClosing && !cancellationToken.IsCancellationRequested)
        {
            return new PlatformResult<string?>.Unavailable(UnavailableReason.OwnerClosed);
        }
    }

    /// <inheritdoc />
    public async ValueTask<PlatformResult<Unit>> WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = lifetime.TryBeginOperation();
        if (operation is null) return new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerClosed);
        if (Reason is { } reason) return new PlatformResult<Unit>.Unavailable(reason);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Shutdown);
        try
        {
            // Provider cancellation only prevents queued work. Once a native write
            // begins its actual result must be returned, including during shutdown.
            return await backend!.WriteTextAsync(text, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsClosing && !cancellationToken.IsCancellationRequested)
        {
            return new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerClosed);
        }
    }
}
