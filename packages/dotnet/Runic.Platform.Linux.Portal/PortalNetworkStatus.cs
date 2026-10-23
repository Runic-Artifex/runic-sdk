using System.Threading.Channels;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

// Network status from the NetworkMonitor portal, which xdg-desktop-portal implements itself for sandboxed and unsandboxed
// callers, or from NetworkManager on the system bus where no portal answers (D039.3). Each read opens the source that
// answers first; a watch keeps one open, re-reads on its change signal, and reports the source gone when its owner leaves.
internal sealed class PortalNetworkStatus(PortalApplication? application, string? sessionAddress = null, string? systemAddress = null,
    string portal = PortalNetworkStatus.PortalName, string networkManager = PortalNetworkStatus.ManagerName) : INetworkStateSource
{
    internal const string PortalName = "org.freedesktop.portal.Desktop";
    internal const string ManagerName = "org.freedesktop.NetworkManager";
    private const string MonitorInterface = "org.freedesktop.portal.NetworkMonitor";
    private const string ManagerInterface = "org.freedesktop.NetworkManager";
    private static readonly DBusObjectPath PortalPath = new("/org/freedesktop/portal/desktop");
    private static readonly DBusObjectPath ManagerPath = new("/org/freedesktop/NetworkManager");
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    // How long a watch waits before it looks for a source again after losing one.
    internal TimeSpan Retry { get; init; } = TimeSpan.FromSeconds(5);

    public async ValueTask<PlatformResult<DesktopNetworkState>> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var source = await OpenAsync(subscribe: false, cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
                return new PlatformResult<DesktopNetworkState>.Success(await source.ReadAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error) when (IsUnavailable(error, cancellationToken))
        {
            return new PlatformResult<DesktopNetworkState>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
    }

    public ValueTask<IAsyncDisposable> SubscribeAsync(Action<PlatformResult<DesktopNetworkState>> report, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stop = new CancellationTokenSource();
        var running = Task.Run(() => FollowAsync(report, stop.Token), CancellationToken.None);
        return ValueTask.FromResult<IAsyncDisposable>(new Subscription(stop, running));
    }

    private async Task FollowAsync(Action<PlatformResult<DesktopNetworkState>> report, CancellationToken stop)
    {
        bool unavailable = false;
        while (!stop.IsCancellationRequested)
        {
            Source? source = null;
            try
            {
                source = await OpenAsync(subscribe: true, stop).ConfigureAwait(false);
                using var lost = CancellationTokenSource.CreateLinkedTokenSource(stop, source.Lost);
                // Subscribed before the read, so a change between them is not missed.
                report(new PlatformResult<DesktopNetworkState>.Success(await source.ReadAsync(lost.Token).ConfigureAwait(false)));
                unavailable = false;
                while (true)
                {
                    await source.Changed.ReadAsync(lost.Token).ConfigureAwait(false);
                    report(new PlatformResult<DesktopNetworkState>.Success(await source.ReadAsync(lost.Token).ConfigureAwait(false)));
                }
            }
            catch (Exception error) when (!stop.IsCancellationRequested && (IsUnavailable(error, stop) || error is OperationCanceledException or ChannelClosedException))
            {
                if (!unavailable) report(new PlatformResult<DesktopNetworkState>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
                unavailable = true;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            finally { if (source is not null) await source.DisposeAsync().ConfigureAwait(false); }
            try { await Task.Delay(Retry, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    // The portal, else NetworkManager; neither answering is reported once as network-monitor-unavailable.
    private async Task<Source> OpenAsync(bool subscribe, CancellationToken cancellationToken)
    {
        try { return await PortalSource.OpenAsync(sessionAddress, portal, application, subscribe, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (IsUnavailable(error, cancellationToken)) { }
        try { return await ManagerSource.OpenAsync(systemAddress, networkManager, subscribe, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (IsUnavailable(error, cancellationToken))
        {
            application?.Diagnose("network-monitor-unavailable", "Neither the NetworkMonitor portal nor NetworkManager answers.",
                "Run xdg-desktop-portal in the desktop session, or NetworkManager on the system bus; inside a sandbox, allow the portal.");
            throw new NativeBackendUnavailableException("No network status source answers.");
        }
    }

    private static bool IsUnavailable(Exception error, CancellationToken cancellationToken) =>
        error is DBusException or TimeoutException or NativeBackendUnavailableException or IOException
        || error is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    // The portal's connectivity: 1 local, 2 limited, 3 captive portal, 4 full.
    internal static DesktopNetworkState PortalState(bool available, bool? metered, uint? connectivity) => new(available)
    {
        Connectivity = !available ? DesktopNetworkConnectivity.None : connectivity switch
        {
            1 => DesktopNetworkConnectivity.Local,
            2 => DesktopNetworkConnectivity.Limited,
            3 => DesktopNetworkConnectivity.CaptivePortal,
            4 => DesktopNetworkConnectivity.Internet,
            _ => DesktopNetworkConnectivity.Unknown,
        },
        IsMetered = metered,
    };

    // NetworkManager's State (NM_STATE: 50 connected local, 60 site, 70 global), Connectivity (NM_CONNECTIVITY: 1 none,
    // 2 portal, 3 limited, 4 full, 0 unknown, as when the check is off) and Metered (NM_METERED: 1 and 3 yes, 2 and 4 no).
    internal static DesktopNetworkState ManagerState(uint? state, uint? connectivity, uint? metered)
    {
        bool available = state is { } known and not 0 ? known >= 50 : connectivity is >= 2;
        return new DesktopNetworkState(available)
        {
            Connectivity = !available ? DesktopNetworkConnectivity.None : connectivity switch
            {
                2 => DesktopNetworkConnectivity.CaptivePortal,
                3 => DesktopNetworkConnectivity.Limited,
                4 => DesktopNetworkConnectivity.Internet,
                // Connected without a default route, or without a check, by the state alone.
                _ => state is 50 or 60 ? DesktopNetworkConnectivity.Local : DesktopNetworkConnectivity.Unknown,
            },
            IsMetered = metered switch { 1 or 3 => true, 2 or 4 => false, _ => null },
        };
    }

    // One open source: the connection bound to the service's owner, its change signal and its reads.
    private abstract class Source(PortalConnection session, DBusSignalSubscription? subscription) : IAsyncDisposable
    {
        private readonly Channel<bool> _changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private readonly CancellationTokenSource _lost = CancellationTokenSource.CreateLinkedTokenSource(session.OwnerChanged);

        protected PortalConnection Session { get; } = session;
        internal CancellationToken Lost => _lost.Token;
        internal ChannelReader<bool> Changed => _changed.Reader;

        protected void Start()
        {
            if (subscription is null) return;
            _ = PortalValues.PumpAsync(subscription, Signal, _ => { try { _lost.Cancel(); } catch (ObjectDisposedException) { } });
        }

        // Every signal of the subscription is a change worth a read.
        protected virtual void Signal(DBusSignal signal) => _changed.Writer.TryWrite(true);

        internal abstract Task<DesktopNetworkState> ReadAsync(CancellationToken cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (subscription is not null) await subscription.DisposeAsync().ConfigureAwait(false);
            _lost.Dispose();
        }
    }

    // org.freedesktop.portal.NetworkMonitor: GetStatus from version 3, the three getters in version 2, properties in version 1.
    private sealed class PortalSource : Source
    {
        private readonly DBusProxy _proxy;
        private readonly uint _version;

        private PortalSource(PortalConnection session, DBusSignalSubscription? subscription, uint version) : base(session, subscription)
        {
            _proxy = new DBusProxy(session.Connection, session.Destination, PortalPath) { Timeout = CallTimeout };
            _version = version;
            Start();
        }

        internal static async Task<Source> OpenAsync(string? address, string destination, PortalApplication? application, bool subscribe,
            CancellationToken cancellationToken)
        {
            var session = await PortalConnection.OpenAsync(address, destination, application, cancellationToken).ConfigureAwait(false);
            var proxy = new DBusProxy(session.Connection, session.Destination, PortalPath) { Timeout = CallTimeout };
            DBusSignalSubscription? subscription = subscribe
                ? await proxy.SubscribeSignalAsync(MonitorInterface, "changed", cancellationToken).AsTask().WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false)
                : null;
            try
            {
                // A portal without the interface fails here, and NetworkManager answers instead.
                var version = await proxy.GetPropertyAsync(MonitorInterface, "version", cancellationToken).ConfigureAwait(false);
                return new PortalSource(session, subscription, version.Signature.Value == "u" ? version.GetUInt32() : 1);
            }
            catch
            {
                if (subscription is not null) await subscription.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        internal override async Task<DesktopNetworkState> ReadAsync(CancellationToken cancellationToken)
        {
            if (_version >= 3)
            {
                var reply = await _proxy.CallAsync(MonitorInterface, "GetStatus", cancellationToken: cancellationToken).ConfigureAwait(false);
                if (reply is not [{ Signature.Value: "a{sv}" } status]) throw new DBusException(DBusException.InvalidArgs, "GetStatus replied with another type.");
                var values = PortalValues.Variants(status);
                return PortalState(Bool(values, "available") ?? false, Bool(values, "metered"),
                    values.TryGetValue("connectivity", out var level) && level.Signature.Value == "u" ? level.GetUInt32() : null);
            }
            if (_version == 2)
            {
                bool? available = One(await _proxy.CallAsync(MonitorInterface, "GetAvailable", cancellationToken: cancellationToken).ConfigureAwait(false), "b")?.GetBoolean();
                bool? metered = One(await _proxy.CallAsync(MonitorInterface, "GetMetered", cancellationToken: cancellationToken).ConfigureAwait(false), "b")?.GetBoolean();
                uint? connectivity = One(await _proxy.CallAsync(MonitorInterface, "GetConnectivity", cancellationToken: cancellationToken).ConfigureAwait(false), "u")?.GetUInt32();
                return PortalState(available ?? false, metered, connectivity);
            }
            var properties = await _proxy.GetAllPropertiesAsync(MonitorInterface, cancellationToken).ConfigureAwait(false);
            return PortalState(Bool(properties, "available") ?? false, Bool(properties, "metered"),
                properties.TryGetValue("connectivity", out var value) && value.Signature.Value == "u" ? value.GetUInt32() : null);
        }

        private static bool? Bool(IReadOnlyDictionary<string, DBusValue> values, string key) =>
            values.TryGetValue(key, out var value) && value.Signature.Value == "b" ? value.GetBoolean() : null;

        private static DBusValue? One(System.Collections.Immutable.ImmutableArray<DBusValue> reply, string signature) =>
            reply is [var value] && value.Signature.Value == signature ? value : null;
    }

    // NetworkManager's manager object on the system bus: State, Connectivity and Metered, and their PropertiesChanged.
    private sealed class ManagerSource : Source
    {
        private static readonly HashSet<string> Watched = ["State", "Connectivity", "Metered"];
        private readonly DBusProxy _proxy;

        private ManagerSource(PortalConnection session, DBusSignalSubscription? subscription) : base(session, subscription)
        {
            _proxy = new DBusProxy(session.Connection, session.Destination, ManagerPath) { Timeout = CallTimeout };
            Start();
        }

        internal static async Task<Source> OpenAsync(string? address, string destination, bool subscribe, CancellationToken cancellationToken)
        {
            var session = await PortalConnection.OpenSystemAsync(address, destination, cancellationToken).ConfigureAwait(false);
            var proxy = new DBusProxy(session.Connection, session.Destination, ManagerPath) { Timeout = CallTimeout };
            DBusSignalSubscription? subscription = subscribe
                ? await proxy.SubscribePropertiesAsync(ManagerInterface, cancellationToken).AsTask().WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false)
                : null;
            return new ManagerSource(session, subscription);
        }

        protected override void Signal(DBusSignal signal)
        {
            // NetworkManager announces many properties; only these change the state.
            if (DBusProxy.ReadPropertyChanges(signal) is not { } changes
                || changes.Changed.Keys.Any(Watched.Contains) || changes.Invalidated.Any(Watched.Contains))
                base.Signal(signal);
        }

        internal override async Task<DesktopNetworkState> ReadAsync(CancellationToken cancellationToken)
        {
            uint? state = await Number("State").ConfigureAwait(false);
            uint? connectivity = await Number("Connectivity").ConfigureAwait(false);
            uint? metered = await Number("Metered").ConfigureAwait(false);
            return ManagerState(state, connectivity, metered);

            async Task<uint?> Number(string name)
            {
                var value = await _proxy.GetPropertyAsync(ManagerInterface, name, cancellationToken).ConfigureAwait(false);
                return value.Signature.Value == "u" ? value.GetUInt32() : null;
            }
        }
    }

    private sealed class Subscription(CancellationTokenSource stop, Task running) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try { await running.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            stop.Dispose();
        }
    }
}
