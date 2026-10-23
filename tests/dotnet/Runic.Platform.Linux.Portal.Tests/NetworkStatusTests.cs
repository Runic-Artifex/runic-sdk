using System.Collections.Immutable;
using Runic.Platform;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Linux.Portal;
using Runic.Platform.Runtime;
using TUnit.Core;

// Network status against a fake NetworkMonitor portal and a fake NetworkManager on the private session bus of
// dbus-run-session, each under a name of its own; NetworkManager's stands in for the system bus.
internal sealed class NetworkStatusTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Test, DisplayName("network: portal and NetworkManager values as states")]
    public void Mapping()
    {
        Check(PortalNetworkStatus.PortalState(true, false, 4) == new DesktopNetworkState(true) { Connectivity = DesktopNetworkConnectivity.Internet, IsMetered = false }, "full");
        Check(PortalNetworkStatus.PortalState(true, true, 3).Connectivity == DesktopNetworkConnectivity.CaptivePortal, "captive portal");
        Check(PortalNetworkStatus.PortalState(true, null, 2).Connectivity == DesktopNetworkConnectivity.Limited, "limited");
        Check(PortalNetworkStatus.PortalState(true, null, 1).Connectivity == DesktopNetworkConnectivity.Local, "local");
        Check(PortalNetworkStatus.PortalState(true, null, null).Connectivity == DesktopNetworkConnectivity.Unknown, "a version without connectivity");
        Check(PortalNetworkStatus.PortalState(false, false, 4) == new DesktopNetworkState(false) { Connectivity = DesktopNetworkConnectivity.None, IsMetered = false },
            "no network has no connectivity");

        Check(PortalNetworkStatus.ManagerState(70, 4, 4) == new DesktopNetworkState(true) { Connectivity = DesktopNetworkConnectivity.Internet, IsMetered = false },
            "connected globally, full, guessed not metered");
        Check(PortalNetworkStatus.ManagerState(70, 2, 1) == new DesktopNetworkState(true) { Connectivity = DesktopNetworkConnectivity.CaptivePortal, IsMetered = true },
            "a portal on a metered connection");
        Check(PortalNetworkStatus.ManagerState(70, 3, 3) is { Connectivity: DesktopNetworkConnectivity.Limited, IsMetered: true }, "limited, guessed metered");
        Check(PortalNetworkStatus.ManagerState(50, 1, 2) is { IsAvailable: true, Connectivity: DesktopNetworkConnectivity.Local, IsMetered: false }, "local only");
        Check(PortalNetworkStatus.ManagerState(70, 0, 0) is { IsAvailable: true, Connectivity: DesktopNetworkConnectivity.Unknown, IsMetered: null },
            "no connectivity check");
        Check(PortalNetworkStatus.ManagerState(20, 1, 0) == new DesktopNetworkState(false) { Connectivity = DesktopNetworkConnectivity.None }, "disconnected");
        Check(PortalNetworkStatus.ManagerState(null, 4, null).IsAvailable && !PortalNetworkStatus.ManagerState(0, 1, null).IsAvailable,
            "without a state, connectivity decides");
    }

    [Test, Explicit, Category("DBus"), DisplayName("network: the NetworkMonitor portal, version 3 and 2, its changed signal and a restart")]
    public async Task PortalAsync()
    {
        foreach (uint version in new uint[] { 3, 2 })
        {
            string name = UniqueName("Portal");
            await using var portal = await FakePortal.StartAsync(name, version);
            var source = new PortalNetworkStatus(null, Address(), Address(), portal: name, networkManager: UniqueName("Absent")) { Retry = TimeSpan.FromMilliseconds(200) };
            await using var status = new NetworkStatusService(source);
            var online = new DesktopNetworkState(true) { Connectivity = DesktopNetworkConnectivity.Internet, IsMetered = false };
            var first = await status.ReadAsync();
            Check(first == Success(online), $"version {version} reads: {first}");

            using var cancel = new CancellationTokenSource(Wait * 3);
            await using var watch = status.WatchAsync(cancel.Token).GetAsyncEnumerator(cancel.Token);
            Check(await Next(watch) == Success(online), "the initial state");
            portal.Set(true, true, 3);
            Check(await Next(watch) == Success(new DesktopNetworkState(true) { Connectivity = DesktopNetworkConnectivity.CaptivePortal, IsMetered = true }),
                "the changed signal reports the captive portal");
            await portal.StopAsync();
            Check(await Next(watch) is PlatformResult<DesktopNetworkState>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "the portal left");
            portal.Set(false, false, 1);
            await portal.RestartAsync();
            Check(await Next(watch) == Success(new DesktopNetworkState(false) { Connectivity = DesktopNetworkConnectivity.None, IsMetered = false }),
                "a fresh state from the restarted portal");
            Console.WriteLine($"PASS NetworkMonitor version {version}");
        }
    }

    [Test, Explicit, Category("DBus"), DisplayName("network: NetworkManager answers where no portal does; neither is reported")]
    public async Task NetworkManagerAsync()
    {
        string name = UniqueName("NetworkManager");
        await using var manager = await FakeNetworkManager.StartAsync(name);
        var source = new PortalNetworkStatus(null, Address(), Address(), portal: UniqueName("Absent"), networkManager: name) { Retry = TimeSpan.FromMilliseconds(200) };
        await using var status = new NetworkStatusService(source);
        Check(await status.ReadAsync() == Success(new DesktopNetworkState(true) { Connectivity = DesktopNetworkConnectivity.Internet, IsMetered = false }),
            "NetworkManager's state");
        using var cancel = new CancellationTokenSource(Wait * 3);
        await using var watch = status.WatchAsync(cancel.Token).GetAsyncEnumerator(cancel.Token);
        Check(await Next(watch) is PlatformResult<DesktopNetworkState>.Success { Value.Connectivity: DesktopNetworkConnectivity.Internet }, "the initial state");
        manager.Change(("Connectivity", 2u), ("Metered", 1u));
        Check(await Next(watch) == Success(new DesktopNetworkState(true) { Connectivity = DesktopNetworkConnectivity.CaptivePortal, IsMetered = true }),
            "PropertiesChanged reports the captive portal");
        manager.Change(("State", 20u), ("Connectivity", 1u));
        Check(await Next(watch) is PlatformResult<DesktopNetworkState>.Success { Value: { IsAvailable: false, Connectivity: DesktopNetworkConnectivity.None } },
            "disconnected");

        var diagnostics = new List<PortalDiagnostic>();
        var nothing = new PortalNetworkStatus(new PortalApplication(null, diagnostic => { lock (diagnostics) diagnostics.Add(diagnostic); }), Address(), Address(),
            portal: UniqueName("Absent"), networkManager: UniqueName("Absent"));
        Check(await nothing.ReadAsync(CancellationToken.None) is PlatformResult<DesktopNetworkState>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "neither answers");
        lock (diagnostics) Check(diagnostics.Any(static d => d.Code == "network-monitor-unavailable" && d.Severity == PortalDiagnosticSeverity.Warning),
            "the diagnostic says so");
    }

    private static PlatformResult<DesktopNetworkState> Success(DesktopNetworkState state) => new PlatformResult<DesktopNetworkState>.Success(state);

    private static async Task<PlatformResult<DesktopNetworkState>> Next(IAsyncEnumerator<PlatformResult<DesktopNetworkState>> watch) =>
        await watch.MoveNextAsync().AsTask().WaitAsync(Wait) ? watch.Current : throw new InvalidOperationException("The watch ended.");

    private static string UniqueName(string what) => $"org.runic.test.{what}{Guid.NewGuid():N}";

    private static string Address() => Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")
        ?? throw new InvalidOperationException("Run the bus cases under dbus-run-session.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // org.freedesktop.portal.NetworkMonitor at /org/freedesktop/portal/desktop, with GetStatus from version 3.
    private sealed class FakePortal : DBusObject, IAsyncDisposable
    {
        private readonly string _name;
        private readonly uint _version;
        private DBusConnection? _connection;
        private IAsyncDisposable? _export;
        private (bool Available, bool Metered, uint Connectivity) _state = (true, false, 4);

        private FakePortal(string name, uint version) { _name = name; _version = version; }

        internal static async Task<FakePortal> StartAsync(string name, uint version)
        {
            var portal = new FakePortal(name, version);
            await portal.RestartAsync();
            return portal;
        }

        internal async Task RestartAsync()
        {
            _connection = ((PlatformResult<DBusConnection>.Success)await DBusConnection.ConnectToAddressAsync(Address())).Value;
            _export = _connection.Export("/org/freedesktop/portal/desktop", this);
            if (await _connection.RequestNameAsync(_name, DBusRequestNameOptions.DoNotQueue) != DBusRequestNameReply.PrimaryOwner)
                throw new InvalidOperationException("The fake portal's name is taken.");
        }

        internal async Task StopAsync()
        {
            if (_export is not null) await _export.DisposeAsync();
            if (_connection is not null) await _connection.DisposeAsync();
            _export = null;
            _connection = null;
        }

        internal void Set(bool available, bool metered, uint connectivity)
        {
            _state = (available, metered, connectivity);
            if (_connection is not null) EmitSignal("org.freedesktop.portal.NetworkMonitor", "changed");
        }

        protected override ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context) => (context.Member, _version) switch
        {
            ("GetStatus", >= 3) => Reply(DBusValue.VariantDictionary(ImmutableDictionary<string, DBusValue>.Empty
                .Add("available", DBusValue.From(_state.Available)).Add("metered", DBusValue.From(_state.Metered))
                .Add("connectivity", DBusValue.From(_state.Connectivity)))),
            ("GetAvailable", >= 2) => Reply(DBusValue.From(_state.Available)),
            ("GetMetered", >= 2) => Reply(DBusValue.From(_state.Metered)),
            ("GetConnectivity", >= 2) => Reply(DBusValue.From(_state.Connectivity)),
            _ => throw new DBusException(DBusException.UnknownMethod, context.Member),
        };

        private static ValueTask<ImmutableArray<DBusValue>> Reply(DBusValue value) => ValueTask.FromResult<ImmutableArray<DBusValue>>([value]);

        protected override ValueTask<ImmutableDictionary<string, DBusValue>> GetAllPropertiesAsync(string interfaceName, DBusMethodContext context) =>
            ValueTask.FromResult(interfaceName == "org.freedesktop.portal.NetworkMonitor"
                ? ImmutableDictionary<string, DBusValue>.Empty.Add("version", DBusValue.From(_version))
                : ImmutableDictionary<string, DBusValue>.Empty);

        protected override async ValueTask<DBusValue> GetPropertyAsync(string interfaceName, string name, DBusMethodContext context) =>
            (await GetAllPropertiesAsync(interfaceName, context)).TryGetValue(name, out var value) ? value
            : throw new DBusException("org.freedesktop.DBus.Error.UnknownProperty", name);

        public ValueTask DisposeAsync() => new(StopAsync());
    }

    // NetworkManager's manager object with State, Connectivity and Metered.
    private sealed class FakeNetworkManager : DBusObject, IAsyncDisposable
    {
        private DBusConnection _connection = null!;
        private IAsyncDisposable _export = null!;
        private ImmutableDictionary<string, DBusValue> _properties = ImmutableDictionary<string, DBusValue>.Empty
            .Add("State", DBusValue.From(70u)).Add("Connectivity", DBusValue.From(4u)).Add("Metered", DBusValue.From(4u))
            .Add("Version", DBusValue.From("1.52.0"));

        internal static async Task<FakeNetworkManager> StartAsync(string name)
        {
            var manager = new FakeNetworkManager();
            manager._connection = ((PlatformResult<DBusConnection>.Success)await DBusConnection.ConnectToAddressAsync(Address())).Value;
            manager._export = manager._connection.Export("/org/freedesktop/NetworkManager", manager);
            if (await manager._connection.RequestNameAsync(name, DBusRequestNameOptions.DoNotQueue) != DBusRequestNameReply.PrimaryOwner)
                throw new InvalidOperationException("The fake NetworkManager's name is taken.");
            return manager;
        }

        internal void Change(params (string Name, uint Value)[] values)
        {
            var changed = values.ToDictionary(static v => v.Name, static v => DBusValue.From(v.Value));
            _properties = _properties.SetItems(changed);
            EmitPropertiesChanged("org.freedesktop.NetworkManager", changed);
        }

        protected override ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context) =>
            throw new DBusException(DBusException.UnknownMethod, context.Member);

        protected override ValueTask<ImmutableDictionary<string, DBusValue>> GetAllPropertiesAsync(string interfaceName, DBusMethodContext context) =>
            ValueTask.FromResult(interfaceName == "org.freedesktop.NetworkManager" ? _properties : ImmutableDictionary<string, DBusValue>.Empty);

        protected override async ValueTask<DBusValue> GetPropertyAsync(string interfaceName, string name, DBusMethodContext context) =>
            (await GetAllPropertiesAsync(interfaceName, context)).TryGetValue(name, out var value) ? value
            : throw new DBusException("org.freedesktop.DBus.Error.UnknownProperty", name);

        public async ValueTask DisposeAsync()
        {
            await _export.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
