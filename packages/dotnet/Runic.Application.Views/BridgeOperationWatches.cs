namespace Runic.Application.Views;

// The operation watches of one window (specs/application/README.md, "Watching and the other requests"). A watch is a
// connection's subscription to one operation: after `watch` answered the current status, each status change is
// pushed to the connection as {"kind":"operation",…} until the operation is terminal, the connection sends `unwatch`
// or it closes. A changed progress is pushed at once when the watch's previous push is at least 100 ms old, and
// otherwise the latest progress 100 ms after that push; a terminal status is pushed at once and replaces a pending
// progress push. A connection holds at most 64 watches.
internal sealed class BridgeOperationWatches : IDisposable
{
    internal const int MaximumWatchesPerConnection = 64;
    internal static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly Lock _gate = new();
    private readonly IBridgeTransport _transport;
    private readonly TimeProvider _timeProvider;
    // The watches of each operation, by registry key.
    private readonly Dictionary<string, List<Watch>> _byOperation = new(StringComparer.Ordinal);
    // The watches of each connection.
    private readonly Dictionary<string, List<Watch>> _byConnection = new(StringComparer.Ordinal);
    private BridgeTargetedPushes? _pushes;
    private bool _disposed;

    internal BridgeOperationWatches(IBridgeTransport transport, TimeProvider timeProvider)
    {
        _transport = transport;
        _timeProvider = timeProvider;
    }

    private BridgeTargetedPushes Pushes => _pushes ??= BridgeTargetedPushes.For(_transport);

    // Adds a watch of a running operation and returns the status the `watch` reply carries, or null when the
    // connection already holds MaximumWatchesPerConnection other watches. A watch of an operation that is not running
    // adds nothing; watching an operation the connection already watches does not add a second watch.
    internal BridgeOperationStatus? Add(BridgeOperationIdentity identity, string member, string connectionKey,
        Func<BridgeOperationStatus> lookup)
    {
        lock (_gate)
        {
            // The status is read under the watches' lock: a change the registry reports after it reaches the watch.
            var status = lookup();
            if (_disposed || status.Kind is not BridgeOperationStatusKind.Running) return status;
            var key = identity.RegistryKey;
            _byConnection.TryGetValue(connectionKey, out var watches);
            if (watches?.Any(watch => watch.Key == key && watch.Member == member) == true) return status;
            if (watches?.Count >= MaximumWatchesPerConnection) return null;
            var added = new Watch(key, identity, member, connectionKey) { SentProgress = status.Progress };
            Add(_byConnection, connectionKey, added);
            Add(_byOperation, key, added);
            return status;
        }
    }

    // Ends the connection's watch of the operation, if it has one.
    internal void Remove(BridgeOperationIdentity identity, string member, string connectionKey)
    {
        lock (_gate)
        {
            if (!_byConnection.TryGetValue(connectionKey, out var watches)) return;
            var key = identity.RegistryKey;
            foreach (var watch in watches.Where(watch => watch.Key == key && watch.Member == member).ToArray()) RemoveCore(watch);
        }
    }

    // Ends every watch of a connection that closed.
    internal void ReleaseConnection(string connectionKey)
    {
        lock (_gate)
        {
            if (!_byConnection.TryGetValue(connectionKey, out var watches)) return;
            foreach (var watch in watches.ToArray()) RemoveCore(watch);
        }
    }

    // BridgeOperationRegistry.StatusChanged: a running operation's progress changed, or the operation is terminal.
    internal void OnStatusChanged(string registryKey, BridgeOperationStatus status)
    {
        lock (_gate)
        {
            if (_disposed || !_byOperation.TryGetValue(registryKey, out var watches)) return;
            foreach (var watch in watches.ToArray())
            {
                if (status.Member is { } member && member != watch.Member) continue;
                if (status.Kind is BridgeOperationStatusKind.Running) Progress(watch, status);
                else
                {
                    // The last push of the watch.
                    RemoveCore(watch);
                    Send(watch, status);
                }
            }
        }
    }

    // The gate is held.
    private void Progress(Watch watch, BridgeOperationStatus status)
    {
        watch.Pending = status;
        if (watch.Timer is not null) return;
        var now = _timeProvider.GetTimestamp();
        var elapsed = watch.LastPush is { } last ? _timeProvider.GetElapsedTime(last, now) : TimeSpan.MaxValue;
        if (elapsed >= ProgressInterval)
        {
            SendPending(watch, now);
            return;
        }
        watch.Timer = _timeProvider.CreateTimer(static state =>
        {
            var (owner, watch) = ((BridgeOperationWatches, Watch))state!;
            lock (owner._gate)
            {
                watch.Timer?.Dispose();
                watch.Timer = null;
                if (!watch.Active) return;
                owner.SendPending(watch, owner._timeProvider.GetTimestamp());
            }
        }, (this, watch), ProgressInterval - elapsed, Timeout.InfiniteTimeSpan);
    }

    // Pushes the latest progress of a watch, unless it is the one the watch pushed last. The gate is held.
    private void SendPending(Watch watch, long now)
    {
        if (watch.Pending is not { } status) return;
        watch.Pending = null;
        if (status.Progress == watch.SentProgress) return;
        watch.SentProgress = status.Progress;
        watch.LastPush = now;
        Send(watch, status);
    }

    // The gate is held.
    private void Send(Watch watch, BridgeOperationStatus status) =>
        Pushes.Send(watch.ConnectionKey, BridgeOperationRouter.EncodeOperationPush(watch.Identity, watch.Member, status));

    // The gate is held.
    private void RemoveCore(Watch watch)
    {
        watch.Active = false;
        watch.Timer?.Dispose();
        watch.Timer = null;
        watch.Pending = null;
        Remove(_byConnection, watch.ConnectionKey, watch);
        Remove(_byOperation, watch.Key, watch);
    }

    private static void Add(Dictionary<string, List<Watch>> index, string key, Watch watch)
    {
        if (!index.TryGetValue(key, out var watches)) index.Add(key, watches = []);
        watches.Add(watch);
    }

    private static void Remove(Dictionary<string, List<Watch>> index, string key, Watch watch)
    {
        if (!index.TryGetValue(key, out var watches)) return;
        watches.Remove(watch);
        if (watches.Count == 0) index.Remove(key);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var watch in _byConnection.Values.SelectMany(static watches => watches).ToArray()) RemoveCore(watch);
        }
    }

    private sealed class Watch(string key, BridgeOperationIdentity identity, string member, string connectionKey)
    {
        public string Key { get; } = key;
        public BridgeOperationIdentity Identity { get; } = identity;
        public string Member { get; } = member;
        public string ConnectionKey { get; } = connectionKey;
        public bool Active { get; set; } = true;
        // When the watch pushed last (TimeProvider timestamp), and the progress it pushed.
        public long? LastPush { get; set; }
        public double? SentProgress { get; set; }
        // The latest running status not yet pushed, and the timer that pushes it.
        public BridgeOperationStatus? Pending { get; set; }
        public ITimer? Timer { get; set; }
    }
}
