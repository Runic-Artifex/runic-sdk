namespace Runic.Platform.Prototype;

// The feature can release early; owner shutdown joins the same release. No raw
// lease escapes the facade, including when shutdown races selection delivery.
internal abstract class PresentationLease(PresentationLifetime lifetime, IAsyncDisposable resource) : IAsyncDisposable
{
    private TaskCompletionSource? _closed;
    protected void RequireOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) is not null || lifetime.IsClosing, this);

    public ValueTask DisposeAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _closed, completion, null) is { } existing) return new(existing.Task);
        _ = ReleaseAsync(completion);
        return new(completion.Task);
    }
    private async Task ReleaseAsync(TaskCompletionSource completion)
    {
        try { await resource.DisposeAsync().ConfigureAwait(false); completion.SetResult(); }
        catch (Exception error) { lifetime.RecordCleanupFailure(error); completion.SetException(error); }
        finally { lifetime.Forget(this); }
    }
}

internal sealed class PresentationReadLease(PresentationLifetime lifetime, IReadFileLease lease)
    : PresentationLease(lifetime, lease), IReadFileLease
{
    public string DisplayName => lease.DisplayName;
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    { RequireOpen(); return lease.OpenReadAsync(cancellationToken); }
}

internal sealed class PresentationSaveLease(PresentationLifetime lifetime, ISaveFileLease lease)
    : PresentationLease(lifetime, lease), ISaveFileLease
{
    public string DisplayName => lease.DisplayName;
    public ValueTask<PlatformResult<IFileWriteTransaction>> BeginWriteAsync(FileWritePolicy policy, CancellationToken cancellationToken = default)
    { RequireOpen(); return lease.BeginWriteAsync(policy, cancellationToken); }
}
