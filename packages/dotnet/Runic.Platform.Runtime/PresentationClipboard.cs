using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Runic.Platform.Runtime;

/// <summary>Tracks clipboard work for one presentation and drains it during shutdown.</summary>
/// <remarks>
/// The facade is an <see cref="IDesktopClipboard"/> for every provider. A provider whose clipboard is text-only reports
/// the data operations and the watch <see cref="PlatformUnavailableReason.BackendUnavailable"/>.
/// </remarks>
internal sealed class PresentationClipboard : IDesktopClipboard, IPlatformCapabilities
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

    private PlatformUnavailableReason? Reason => lifetime.IsClosing ? PlatformUnavailableReason.OwnerClosed
        : backend is null ? PlatformUnavailableReason.ProviderNotConfigured
        : !lifetime.HasOwner ? PlatformUnavailableReason.OwnerUnavailable : null;

    // The data operations need a provider clipboard with more than text.
    private PlatformUnavailableReason? DataReason => Reason ?? (backend is IDesktopClipboard ? null : PlatformUnavailableReason.BackendUnavailable);

    /// <inheritdoc />
    public CapabilitySnapshot GetSnapshot()
    {
        CapabilityStatus status = Reason is { } reason
            ? new CapabilityStatus.Unavailable(reason) : new CapabilityStatus.Available();
        CapabilityStatus data = DataReason is { } dataReason
            ? new CapabilityStatus.Unavailable(dataReason) : new CapabilityStatus.Available();
        return new(lifetime.Generation, ImmutableDictionary<string, CapabilityStatus>.Empty
            .Add(PlatformCapabilityIds.ClipboardReadText, status).Add(PlatformCapabilityIds.ClipboardWriteText, status)
            .Add(PlatformCapabilityIds.ClipboardReadData, data).Add(PlatformCapabilityIds.ClipboardWriteData, data)
            .Add(PlatformCapabilityIds.ClipboardWatch, data));
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        return ReadAsync(Reason, (clipboard, token) => clipboard.ReadTextAsync(maximumCharacters, token),
            static (text, maximum) => text is { } value && value.Length > maximum, maximumCharacters, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<PlatformResult<PlatformUnit>> WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = lifetime.TryBeginOperation();
        if (operation is null) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        if (Reason is { } reason) return new PlatformResult<PlatformUnit>.Unavailable(reason);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Shutdown);
        try
        {
            // Provider cancellation only prevents queued work. Once a native write
            // begins its actual result must be returned, including during shutdown.
            return await backend!.WriteTextAsync(text, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsClosing && !cancellationToken.IsCancellationRequested)
        {
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        }
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<DesktopClipboardFormats>> GetFormatsAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(DataReason, static (clipboard, token) => ((IDesktopClipboard)clipboard).GetFormatsAsync(token),
            static (_, _) => false, 0, cancellationToken);

    /// <inheritdoc />
    public ValueTask<PlatformResult<string?>> ReadHtmlAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        return ReadAsync(DataReason, (clipboard, token) => ((IDesktopClipboard)clipboard).ReadHtmlAsync(maximumCharacters, token),
            static (html, maximum) => html is { } value && value.Length > maximum, maximumCharacters, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<string?>> ReadRtfAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        return ReadAsync(DataReason, (clipboard, token) => ((IDesktopClipboard)clipboard).ReadRtfAsync(maximumCharacters, token),
            static (rtf, maximum) => rtf is { } value && value.Length > maximum, maximumCharacters, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<byte[]?>> ReadImageAsync(int maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        return ReadAsync(DataReason, (clipboard, token) => ((IDesktopClipboard)clipboard).ReadImageAsync(maximumBytes, token),
            static (image, maximum) => image is { } value && value.Length > maximum, maximumBytes, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<ImmutableArray<string>>> ReadFilesAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);
        return ReadAsync(DataReason, (clipboard, token) => ((IDesktopClipboard)clipboard).ReadFilesAsync(maximumCount, token),
            static (files, maximum) => !files.IsDefault && files.Length > maximum, maximumCount, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<PlatformResult<PlatformUnit>> WriteAsync(DesktopClipboardContent content, CancellationToken cancellationToken = default)
    {
        DesktopClipboardRules.Check(content);
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = lifetime.TryBeginOperation();
        if (operation is null) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        if (DataReason is { } reason) return new PlatformResult<PlatformUnit>.Unavailable(reason);
        if (DesktopClipboardRules.Validate(content) is { } invalid) return new PlatformResult<PlatformUnit>.Failed(invalid);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Shutdown);
        try
        {
            // As WriteTextAsync: an initiated native write returns its actual result.
            return await ((IDesktopClipboard)backend!).WriteAsync(content, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsClosing && !cancellationToken.IsCancellationRequested)
        {
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PlatformResult<DesktopClipboardFormats>> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DataReason is { } reason)
        {
            yield return new PlatformResult<DesktopClipboardFormats>.Unavailable(reason);
            yield break;
        }
        // The watch is not an operation that shutdown drains: shutdown ends it instead.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Shutdown);
        await using var watch = ((IDesktopClipboard)backend!).WatchAsync(linked.Token).GetAsyncEnumerator(linked.Token);
        while (true)
        {
            PlatformResult<DesktopClipboardFormats> next;
            try
            {
                if (!await watch.MoveNextAsync().ConfigureAwait(false)) yield break;
                next = lifetime.IsClosing ? new PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.OwnerClosed) : watch.Current;
            }
            catch (OperationCanceledException) when (lifetime.IsClosing && !cancellationToken.IsCancellationRequested)
            {
                next = new PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.OwnerClosed);
            }
            yield return next;
            if (next is PlatformResult<DesktopClipboardFormats>.Unavailable) yield break;
        }
    }

    // One read through the provider, with the facade's bound check: a provider that returns more than the bound
    // reports TooLarge.
    private async ValueTask<PlatformResult<T>> ReadAsync<T>(PlatformUnavailableReason? unavailable,
        Func<ITextClipboard, CancellationToken, ValueTask<PlatformResult<T>>> read, Func<T, int, bool> exceeds, int maximum,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = lifetime.TryBeginOperation();
        if (operation is null) return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        if (unavailable is { } reason) return new PlatformResult<T>.Unavailable(reason);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Shutdown);
        try
        {
            var result = await read(backend!, linked.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (lifetime.IsClosing) return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.OwnerClosed);
            return result is PlatformResult<T>.Success { Value: var value } && exceeds(value, maximum)
                ? new PlatformResult<T>.Failed(PlatformFailureCode.TooLarge) : result;
        }
        catch (OperationCanceledException) when (lifetime.IsClosing && !cancellationToken.IsCancellationRequested)
        {
            return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        }
    }
}
