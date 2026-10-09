namespace Runic.Platform.Runtime;

internal sealed class DirectoryLease(string path, IAsyncDisposable? access) : IDirectoryLease
{
    private TaskCompletionSource? _closed;
    public string LocalPath { get { RequireOpen(); return path; } }
    public string DisplayName { get { RequireOpen(); return Path.GetFileName(Path.TrimEndingDirectorySeparator(path)); } }

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
