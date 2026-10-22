namespace Runic.Platform.Runtime;

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

// The list owns its leases' lifetime registration; each item is checked against the list and the presentation.
internal sealed class PresentationReadLeaseList : PresentationLease, IReadFileLeaseList
{
    private readonly IReadFileLease[] _items;
    public PresentationReadLeaseList(PresentationLifetime lifetime, IReadFileLeaseList leases) : base(lifetime, leases) =>
        _items = leases.Select(lease => (IReadFileLease)new ListedLease(this, lease)).ToArray();
    public IReadFileLease this[int index] => _items[index];
    public int Count => _items.Length;
    public IEnumerator<IReadFileLease> GetEnumerator() => ((IEnumerable<IReadFileLease>)_items).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class ListedLease(PresentationReadLeaseList list, IReadFileLease lease) : IReadFileLease
    {
        public string DisplayName => lease.DisplayName;
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        { list.RequireOpen(); return lease.OpenReadAsync(cancellationToken); }
        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }
}

internal sealed class PresentationDirectoryLease(PresentationLifetime lifetime, IDirectoryLease lease)
    : PresentationLease(lifetime, lease), IDirectoryLease
{
    public string DisplayName { get { RequireOpen(); return lease.DisplayName; } }
    public string LocalPath { get { RequireOpen(); return lease.LocalPath; } }
}

// As PresentationReadLeaseList: each directory is checked against the list and the presentation.
internal sealed class PresentationDirectoryLeaseList : PresentationLease, IDirectoryLeaseList
{
    private readonly IDirectoryLease[] _items;
    public PresentationDirectoryLeaseList(PresentationLifetime lifetime, IDirectoryLeaseList leases) : base(lifetime, leases) =>
        _items = leases.Select(lease => (IDirectoryLease)new ListedLease(this, lease)).ToArray();
    public IDirectoryLease this[int index] => _items[index];
    public int Count => _items.Length;
    public IEnumerator<IDirectoryLease> GetEnumerator() => ((IEnumerable<IDirectoryLease>)_items).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class ListedLease(PresentationDirectoryLeaseList list, IDirectoryLease lease) : IDirectoryLease
    {
        public string DisplayName { get { list.RequireOpen(); return lease.DisplayName; } }
        public string LocalPath { get { list.RequireOpen(); return lease.LocalPath; } }
        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }
}

internal sealed class PresentationSaveLease(PresentationLifetime lifetime, ISaveFileLease lease)
    : PresentationLease(lifetime, lease), ISaveFileLease, ILaunchableFileLease
{
    public string DisplayName => lease.DisplayName;
    public ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(IDesktopFileLauncher launcher,
        DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return lease is ILaunchableFileLease file ? file.LaunchAsync(launcher, operation, cancellationToken)
            : ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
    }
    public ValueTask<PlatformResult<IFileWriteTransaction>> BeginWriteAsync(FileWritePolicy policy, CancellationToken cancellationToken = default)
    { RequireOpen(); return lease.BeginWriteAsync(policy, cancellationToken); }
}
