using System.Collections.Immutable;
using Runic.Platform;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Linux.Portal;
using Runic.Platform.Runtime;
using TUnit.Core;

// B4: power and session events against fake logind, UPower and power-profiles services on the private session bus of
// dbus-run-session, each under a name of its own; the session bus stands in for the system bus.
internal sealed class PowerEventsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const string SessionPath = "/org/freedesktop/login1/session/_31";

    [Test, Explicit, Category("DBus"), DisplayName("power: logind sleep and lock, UPower battery and the power-saver profile")]
    public async Task EventsAsync()
    {
        string loginName = UniqueName("Login"), upowerName = UniqueName("UPower"), profilesName = UniqueName("Profiles");
        await using var login = await FakeService.StartAsync(loginName, "/org/freedesktop/login1", SessionPath);
        await using var upower = await FakeService.StartAsync(upowerName, "/org/freedesktop/UPower");
        await using var profiles = await FakeService.StartAsync(profilesName, "/" + profilesName.Replace('.', '/'));
        login.Session!["LockedHint"] = DBusValue.From(false);
        upower.Root["OnBattery"] = DBusValue.From(false);
        profiles.Root["ActiveProfile"] = DBusValue.From("balanced");
        var source = new PortalPowerEvents(null, Address(), login: loginName, upower: upowerName, profiles: profilesName, legacyProfiles: UniqueName("Absent"))
        { Retry = TimeSpan.FromMilliseconds(200) };
        await using var events = new PowerEventsService(source);
        var mains = new DesktopPowerState { IsOnBattery = false, IsLowPower = false, IsLocked = false };
        var read = await events.ReadAsync();
        Check(read == new PlatformResult<DesktopPowerState>.Success(mains), $"the read: {read}");

        using var cancel = new CancellationTokenSource(Wait * 3);
        await using var watch = events.WatchAsync(cancel.Token).GetAsyncEnumerator(cancel.Token);
        Check(await Next(watch) == Event(DesktopPowerEventKind.StateChanged, mains), "the current state first");
        login.Emit("org.freedesktop.login1.Manager", "PrepareForSleep", DBusValue.From(true));
        Check(await Next(watch) == Event(DesktopPowerEventKind.Suspending, mains), "PrepareForSleep(true)");
        upower.Root["OnBattery"] = DBusValue.From(true);
        login.Emit("org.freedesktop.login1.Manager", "PrepareForSleep", DBusValue.From(false));
        Check(await Next(watch) == Event(DesktopPowerEventKind.Resumed, mains), "PrepareForSleep(false)");
        Check(await Next(watch) == Event(DesktopPowerEventKind.StateChanged, mains with { IsOnBattery = true }), "the state is read again after a wake");

        login.EmitSession("Lock");
        var locked = mains with { IsOnBattery = true, IsLocked = true };
        Check(await Next(watch) == Event(DesktopPowerEventKind.Locked, locked), "Lock");
        login.ChangeSession("LockedHint", DBusValue.From(true));
        login.ChangeSession("LockedHint", DBusValue.From(false));
        Check(await Next(watch) == Event(DesktopPowerEventKind.Unlocked, locked with { IsLocked = false }), "LockedHint false; the repeated true is not reported");

        upower.Change("org.freedesktop.UPower", "OnBattery", DBusValue.From(false));
        Check(await Next(watch) == Event(DesktopPowerEventKind.StateChanged, mains), "back on mains");
        profiles.Change(profilesName, "ActiveProfile", DBusValue.From("power-saver"));
        Check(await Next(watch) == Event(DesktopPowerEventKind.StateChanged, mains with { IsLowPower = true }), "the power-saver profile");

        // logind leaves: the watch reports the gap, opens the sources again, which still answer, and reports the
        // current state.
        await login.StopAsync();
        Check(await Next(watch) is PlatformResult<DesktopPowerEvent>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "the gap");
        Check(await Next(watch) == Event(DesktopPowerEventKind.StateChanged, new DesktopPowerState { IsOnBattery = false, IsLowPower = true }),
            "without logind the lock state is unknown");
    }

    [Test, Explicit, Category("DBus"), DisplayName("power: the user's display session, and nothing answering")]
    public async Task FallbacksAsync()
    {
        string loginName = UniqueName("Login");
        await using var login = await FakeService.StartAsync(loginName, "/org/freedesktop/login1", SessionPath, noSessionByPid: true);
        login.Session!["LockedHint"] = DBusValue.From(true);
        var source = new PortalPowerEvents(null, Address(), login: loginName, upower: UniqueName("Absent"), profiles: UniqueName("Absent"),
            legacyProfiles: UniqueName("Absent")) { UserId = 1000 };
        var read = await source.ReadAsync(CancellationToken.None);
        Check(read == new PlatformResult<DesktopPowerState>.Success(new DesktopPowerState { IsLocked = true }), $"the display session's lock: {read}");

        var diagnostics = new List<PortalDiagnostic>();
        var nothing = new PortalPowerEvents(new PortalApplication(null, diagnostic => { lock (diagnostics) diagnostics.Add(diagnostic); }), Address(),
            login: UniqueName("Absent"), upower: UniqueName("Absent"), profiles: UniqueName("Absent"), legacyProfiles: UniqueName("Absent"));
        Check(await nothing.ReadAsync(CancellationToken.None) is PlatformResult<DesktopPowerState>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "nothing answers");
        lock (diagnostics) Check(diagnostics.Any(static d => d.Code == "power-events-unavailable"), "the diagnostic says so");
    }

    [Test, Explicit, Category("DBus"), DisplayName("power: reads release their owner registrations")]
    public async Task ReadsReleaseRegistrationsAsync()
    {
        string loginName = UniqueName("Login"), upowerName = UniqueName("UPower");
        await using var login = await FakeService.StartAsync(loginName, "/org/freedesktop/login1", SessionPath);
        await using var upower = await FakeService.StartAsync(upowerName, "/org/freedesktop/UPower");
        login.Session!["LockedHint"] = DBusValue.From(false);
        upower.Root["OnBattery"] = DBusValue.From(false);
        var source = new PortalPowerEvents(null, Address(), login: loginName, upower: upowerName, profiles: UniqueName("Absent"), legacyProfiles: UniqueName("Absent"));
        var before = PortalPowerEvents.Sources.OwnerRegistrations;
        for (var index = 0; index < 20; index++)
            Check(await source.ReadAsync(CancellationToken.None) is PlatformResult<DesktopPowerState>.Success, "a read");
        Check(PortalPowerEvents.Sources.OwnerRegistrations == before,
            $"twenty reads left {PortalPowerEvents.Sources.OwnerRegistrations - before} owner registrations");
    }

    private static PlatformResult<DesktopPowerEvent> Event(DesktopPowerEventKind kind, DesktopPowerState state) =>
        new PlatformResult<DesktopPowerEvent>.Success(new DesktopPowerEvent(kind, state));

    private static async Task<PlatformResult<DesktopPowerEvent>> Next(IAsyncEnumerator<PlatformResult<DesktopPowerEvent>> watch) =>
        await watch.MoveNextAsync().AsTask().WaitScaledAsync(Wait) ? watch.Current : throw new InvalidOperationException("The watch ended.");

    private static string UniqueName(string what) => $"org.runic.test.{what}{Guid.NewGuid():N}";

    private static string Address() => Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")
        ?? throw new InvalidOperationException("Run the bus cases under dbus-run-session.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // A service with properties on a root object, and for logind a session object, under its own bus name.
    private sealed class FakeService : IAsyncDisposable
    {
        private DBusConnection? _connection;
        private readonly List<IAsyncDisposable> _exports = [];
        private FakeObject _root = null!;
        private FakeObject? _session;

        internal Dictionary<string, DBusValue> Root => _root.Properties;
        internal Dictionary<string, DBusValue>? Session => _session?.Properties;

        internal static async Task<FakeService> StartAsync(string name, string path, string? sessionPath = null, bool noSessionByPid = false)
        {
            var service = new FakeService();
            service._connection = ((PlatformResult<DBusConnection>.Success)await DBusConnection.ConnectToAddressAsync(Address())).Value;
            service._root = new FakeObject(sessionPath, noSessionByPid);
            service._exports.Add(service._connection.Export(path, service._root));
            if (sessionPath is not null)
            {
                service._session = new FakeObject(null, false);
                service._exports.Add(service._connection.Export(sessionPath, service._session));
                service._exports.Add(service._connection.Export("/org/freedesktop/login1/user/_1000", new FakeObject(null, false)
                {
                    Properties = { ["Display"] = DBusValue.Struct(DBusValue.From("31"), DBusValue.From(new DBusObjectPath(sessionPath))) },
                }));
            }
            if (await service._connection.RequestNameAsync(name, DBusRequestNameOptions.DoNotQueue) != DBusRequestNameReply.PrimaryOwner)
                throw new InvalidOperationException("The fake service's name is taken.");
            return service;
        }

        internal void Emit(string interfaceName, string member, DBusValue argument) => _root.Emit(interfaceName, member, argument);

        internal void EmitSession(string member) => _session!.Emit("org.freedesktop.login1.Session", member);

        internal void ChangeSession(string name, DBusValue value) => _session!.Change("org.freedesktop.login1.Session", name, value);

        internal void Change(string interfaceName, string name, DBusValue value) => _root.Change(interfaceName, name, value);

        internal async Task StopAsync()
        {
            foreach (var export in _exports) await export.DisposeAsync();
            _exports.Clear();
            if (_connection is not null) await _connection.DisposeAsync();
            _connection = null;
        }

        public ValueTask DisposeAsync() => new(StopAsync());
    }

    private sealed class FakeObject(string? sessionPath, bool noSessionByPid) : DBusObject
    {
        internal Dictionary<string, DBusValue> Properties { get; init; } = [];

        internal void Emit(string interfaceName, string member, params DBusValue[] arguments) => EmitSignal(interfaceName, member, arguments);

        internal void Change(string interfaceName, string name, DBusValue value)
        {
            Properties[name] = value;
            EmitPropertiesChanged(interfaceName, new Dictionary<string, DBusValue> { [name] = value });
        }

        protected override ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context) => context.Member switch
        {
            "GetSessionByPID" when sessionPath is not null && !noSessionByPid => ValueTask.FromResult<ImmutableArray<DBusValue>>([DBusValue.From(new DBusObjectPath(sessionPath))]),
            "GetUser" when sessionPath is not null => ValueTask.FromResult<ImmutableArray<DBusValue>>([DBusValue.From(new DBusObjectPath("/org/freedesktop/login1/user/_1000"))]),
            _ => throw new DBusException("org.freedesktop.login1.NoSessionForPID", context.Member),
        };

        protected override ValueTask<ImmutableDictionary<string, DBusValue>> GetAllPropertiesAsync(string interfaceName, DBusMethodContext context) =>
            ValueTask.FromResult(Properties.ToImmutableDictionary());

        protected override ValueTask<DBusValue> GetPropertyAsync(string interfaceName, string name, DBusMethodContext context) =>
            Properties.TryGetValue(name, out var value) ? ValueTask.FromResult(value)
            : throw new DBusException("org.freedesktop.DBus.Error.UnknownProperty", name);
    }
}
