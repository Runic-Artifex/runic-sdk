namespace Runic.Platform;

/// <summary>Desktop notifications for hosts and platforms that cannot provide them.</summary>
public static class DesktopNotifications
{
    /// <summary>Creates notifications that report every request unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>
    /// <see cref="IDesktopNotifications.RequestPermissionAsync"/>, <see cref="IDesktopNotifications.ShowAsync"/> and
    /// <see cref="IDesktopNotifications.RemoveAsync"/> return <see cref="PlatformResult{T}.Unavailable"/>, and
    /// <see cref="IDesktopNotifications.Activated"/> is never raised. Nothing throws except for a missing argument,
    /// caller cancellation and use after disposal.
    /// </remarks>
    public static IDesktopNotifications Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableDesktopNotifications(reason);
    }

    private sealed class UnavailableDesktopNotifications(PlatformUnavailableReason reason) : IDesktopNotifications
    {
        private readonly PlatformResult<PlatformUnit> _result = new PlatformResult<PlatformUnit>.Unavailable(reason);
        private volatile bool _disposed;

        public event EventHandler<DesktopNotificationActivation>? Activated { add { } remove { } }

        public ValueTask<PlatformResult<PlatformUnit>> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
            Answer(cancellationToken);

        public ValueTask<PlatformResult<PlatformUnit>> ShowAsync(DesktopNotification notification, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(notification);
            return Answer(cancellationToken);
        }

        public ValueTask<PlatformResult<PlatformUnit>> RemoveAsync(string id, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(id);
            return Answer(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }

        private ValueTask<PlatformResult<PlatformUnit>> Answer(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_result);
        }
    }
}
