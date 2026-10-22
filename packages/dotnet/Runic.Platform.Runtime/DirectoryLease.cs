namespace Runic.Platform.Runtime;

internal sealed class DirectoryLease(string path, IAsyncDisposable? access) : IDirectoryLease
{
    private TaskCompletionSource? _closed;
    public string LocalPath { get { RequireOpen(); return path; } }
    public string DisplayName { get { RequireOpen(); return GetDisplayName(path); } }

    // A filesystem root has no final name; the root itself identifies it.
    internal static string GetDisplayName(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;

    private void RequireOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) is not null, this);

    public ValueTask DisposeAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _closed, completion, null) is { } existing) return new(existing.Task);
        _ = ReleaseAsync(completion);
        return new(completion.Task);
    }

    private async Task ReleaseAsync(TaskCompletionSource completion)
    {
        try
        {
            if (access is not null) await access.DisposeAsync().ConfigureAwait(false);
            completion.SetResult();
        }
        catch (Exception error) { completion.SetException(error); }
    }
}

// The leases of one multiple directory selection; disposing the list releases each lease once.
internal sealed class DirectoryLeaseList(System.Collections.Immutable.ImmutableArray<IDirectoryLease> leases) : IDirectoryLeaseList
{
    private Task? _closed;
    public IDirectoryLease this[int index] => leases[index];
    public int Count => leases.Length;
    public IEnumerator<IDirectoryLease> GetEnumerator() => ((IEnumerable<IDirectoryLease>)leases).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public ValueTask DisposeAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _closed, completion.Task, null) is { } existing) return new(existing);
        _ = ReleaseAsync(completion);
        return new(completion.Task);
    }

    private async Task ReleaseAsync(TaskCompletionSource completion)
    {
        List<Exception> failures = [];
        foreach (var lease in leases)
        {
            try { await lease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        FileLeaseCleanup.Complete(completion, failures);
    }
}
