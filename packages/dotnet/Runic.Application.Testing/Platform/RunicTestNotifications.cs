using Runic.Platform;

namespace Runic.Application.Testing;

/// <summary>
/// Recording <see cref="IDesktopNotifications"/> for tests: shown and removed notifications are kept, and
/// <see cref="Activate"/> raises <see cref="Activated"/> as a click on a notification or one of its actions would.
/// </summary>
/// <remarks>
/// Notifications belong to the application, not to a window, so <c>AddRunicTestPlatform</c> registers one instance for
/// the service provider. Set <see cref="UnavailableReason"/> or <see cref="PermissionResult"/> to test an application
/// without notifications. The type is safe to use from any thread.
/// </remarks>
public sealed class RunicTestNotifications : IDesktopNotifications, IDesktopServiceStatus
{
    private readonly object _gate = new();
    private readonly List<DesktopNotification> _shown = [];
    private readonly List<string> _removed = [];
    private readonly Dictionary<string, DesktopNotification> _current = new(StringComparer.Ordinal);
    private int _permissionRequests;
    private bool _disposed;

    internal Action<string, (string, string?)[]>? Recorder { get; set; }

    /// <summary>Raised by <see cref="Activate"/>, on the calling thread.</summary>
    public event EventHandler<DesktopNotificationActivation>? Activated;

    /// <summary>When set, every call reports <see cref="PlatformResult{T}.Unavailable"/> with this reason.</summary>
    public PlatformUnavailableReason? UnavailableReason { get; set; }

    /// <summary>What <see cref="RequestPermissionAsync"/> reports when the notifications are available; success by default.</summary>
    public PlatformResult<PlatformUnit> PermissionResult { get; set; } = new PlatformResult<PlatformUnit>.Success(default);

    /// <inheritdoc />
    public CapabilityStatus Status => UnavailableReason is { } reason
        ? new CapabilityStatus.Unavailable(reason)
        : new CapabilityStatus.Available();

    /// <summary>Every notification shown, oldest first, including ones that replaced another with the same id.</summary>
    public IReadOnlyList<DesktopNotification> Shown
    {
        get { lock (_gate) return [.. _shown]; }
    }

    /// <summary>The notifications shown and not removed, one per id.</summary>
    public IReadOnlyList<DesktopNotification> Current
    {
        get { lock (_gate) return [.. _current.Values]; }
    }

    /// <summary>The ids the application removed, oldest first.</summary>
    public IReadOnlyList<string> Removed
    {
        get { lock (_gate) return [.. _removed]; }
    }

    /// <summary>How often the application requested permission.</summary>
    public int PermissionRequests
    {
        get { lock (_gate) return _permissionRequests; }
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> RequestPermissionAsync(CancellationToken cancellationToken = default)
    {
        Begin(cancellationToken);
        lock (_gate) _permissionRequests++;
        return Answer(UnavailableReason is null ? PermissionResult : null);
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> ShowAsync(DesktopNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Begin(cancellationToken);
        if (UnavailableReason is not null) return Answer(null);
        lock (_gate)
        {
            _shown.Add(notification);
            _current[notification.Id] = notification;
        }
        Recorder?.Invoke("show", [("id", notification.Id), ("title", notification.Title), ("body", notification.Body),
            ("actions", notification.Actions.IsDefaultOrEmpty ? null : string.Join('\n', notification.Actions.Select(static action => action.Id)))]);
        return Answer(new PlatformResult<PlatformUnit>.Success(default));
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Begin(cancellationToken);
        if (UnavailableReason is not null) return Answer(null);
        lock (_gate)
        {
            _removed.Add(id);
            _current.Remove(id);
        }
        Recorder?.Invoke("remove", [("id", id)]);
        return Answer(new PlatformResult<PlatformUnit>.Success(default));
    }

    /// <summary>Raises <see cref="Activated"/> as the user clicking a notification or one of its actions would.</summary>
    /// <param name="notificationId">The notification's id.</param>
    /// <param name="actionId">The action's id, or <see langword="null"/> for a click on the notification itself.</param>
    /// <param name="trusted">The activation's <see cref="DesktopNotificationActivation.IsTrusted"/>; <see langword="true"/> by default.</param>
    /// <returns>The activation raised.</returns>
    /// <remarks>
    /// The action id of a click on the notification itself is empty. A trusted activation names a notification shown
    /// and not removed, and one of its actions, and carries its <see cref="DesktopNotification.ActivationUri"/>; pass
    /// <paramref name="trusted"/> <see langword="false"/> for an activation the provider could not match.
    /// </remarks>
    public DesktopNotificationActivation Activate(string notificationId, string? actionId = null, bool trusted = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(notificationId);
        Uri? uri = null;
        if (trusted)
        {
            DesktopNotification? notification;
            lock (_gate) _current.TryGetValue(notificationId, out notification);
            if (notification is null)
                throw new InvalidOperationException($"No notification with the id '{notificationId}' is shown; pass trusted: false for an unmatched activation.");
            if (actionId is not null && !notification.Actions.Any(action => action.Id == actionId))
                throw new InvalidOperationException($"The notification '{notificationId}' has no action '{actionId}'.");
            uri = notification.ActivationUri;
        }
        var activation = new DesktopNotificationActivation(notificationId, actionId ?? "", uri) { IsTrusted = trusted };
        Activated?.Invoke(this, activation);
        return activation;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        return ValueTask.CompletedTask;
    }

    private void Begin(CancellationToken cancellationToken)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private ValueTask<PlatformResult<PlatformUnit>> Answer(PlatformResult<PlatformUnit>? result) =>
        ValueTask.FromResult(result ?? new PlatformResult<PlatformUnit>.Unavailable(UnavailableReason ?? PlatformUnavailableReason.BackendUnavailable));
}
