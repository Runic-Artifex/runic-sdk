using System.Runtime.CompilerServices;
using Runic.Platform;

namespace Runic.Platform.Runtime;

/// <summary>Bounded, non-overlapping preference observation, including backend recovery.</summary>
public abstract class DesktopSettingsSource : IDesktopSettings
{
    private readonly CancellationTokenSource _closed = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _readers;
    private bool _disposed;
    private TaskCompletionSource? _disposal;
    /// <inheritdoc />
    public async ValueTask<PlatformResult<DesktopAppearance>> ReadAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource linked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            _readers++;
        }
        try { return await ReadCoreAsync(linked.Token).ConfigureAwait(false); }
        finally
        {
            linked.Dispose();
            lock (_gate) if (--_readers == 0 && _disposed) _drained.TrySetResult();
        }
    }
    /// <summary>Reads one native snapshot and drains any submitted native callback before returning.</summary>
    protected abstract ValueTask<PlatformResult<DesktopAppearance>> ReadCoreAsync(CancellationToken cancellationToken);
    /// <inheritdoc />
    public async IAsyncEnumerable<PlatformResult<DesktopAppearance>> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CancellationTokenSource linked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        }
        using (linked)
        {
            PlatformResult<DesktopAppearance>? previous = null;
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                PlatformResult<DesktopAppearance> current;
                try { current = await ReadAsync(linked.Token).ConfigureAwait(false); }
                catch (ObjectDisposedException) when (linked.IsCancellationRequested)
                { throw new OperationCanceledException(linked.Token); }
                if (current != previous) { previous = current; yield return current; }
                await Task.Delay(TimeSpan.FromSeconds(1), linked.Token).ConfigureAwait(false);
            }
        }
    }
    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal.Task);
            completion = _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposed = true;
            if (_readers == 0) _drained.TrySetResult();
        }
        _ = CloseAsync(completion);
        GC.SuppressFinalize(this);
        return new(completion.Task);
    }
    private async Task CloseAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try { await _closed.CancelAsync().ConfigureAwait(false); } catch (Exception error) { failure = error; }
        await _drained.Task.ConfigureAwait(false);
        _closed.Dispose();
        if (failure is null) completion.TrySetResult();
        else completion.TrySetException(failure);
    }
}

/// <summary>Shared input rules applied before native side effects.</summary>
public static class DesktopServiceValidation
{
    /// <summary>Validates and normalizes a trusted absolute local path.</summary>
    public static string FilePath(string path, DesktopFileOperation operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (path.Contains('\0') || !Path.IsPathFullyQualified(path)) throw new ArgumentException("Supply a fully qualified local path.", nameof(path));
        return Path.GetFullPath(path);
    }
    /// <summary>Validates bounded notification content and routing identifiers.</summary>
    public static void Notification(DesktopNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Identifier(notification.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Title);
        ArgumentNullException.ThrowIfNull(notification.Body);
        if (notification.Title.Length > 256 || notification.Body.Length > 4096 || notification.Title.Contains('\0') || notification.Body.Contains('\0') || notification.Actions.IsDefault || notification.Actions.Length > 4)
            throw new ArgumentException("Notification content exceeds its bounds.", nameof(notification));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in notification.Actions)
        {
            ArgumentNullException.ThrowIfNull(action);
            Identifier(action.Id);
            if (action.Id == "default" || !ids.Add(action.Id)) throw new ArgumentException("Notification action IDs must be unique and non-default.");
            ArgumentException.ThrowIfNullOrWhiteSpace(action.Label);
            if (action.Label.Length > 80 || action.Label.Contains('\0')) throw new ArgumentException("Notification action label is too long.");
        }
        if (notification.ActivationUri is { } uri && (!uri.IsAbsoluteUri || uri.IsFile || uri.AbsoluteUri.Length > 2048))
            throw new ArgumentException("Notification activation requires a bounded absolute non-file URI.");
    }
    /// <summary>Validates an application-owned routing identifier.</summary>
    public static void Identifier(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("Use an identifier of at most 64 ASCII letters, digits, dots, underscores or hyphens.", nameof(id));
    }
}
