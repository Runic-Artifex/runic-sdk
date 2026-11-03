using System.Threading.Channels;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

// B4: power and session events on the system bus, with either toolkit and without GTK.
// - logind (org.freedesktop.login1): the manager's PrepareForSleep(true/false) is Suspending and Resumed; the process's
//   session (GetSessionByPID, else the user's display session) gives Lock, Unlock and the LockedHint property that
//   lock screens set.
// - UPower's OnBattery, and the power-profiles daemon's ActiveProfile ("power-saver" is low power), under its current
//   name org.freedesktop.UPower.PowerProfiles or the earlier net.hadess.PowerProfiles.
// Each source is optional; the state leaves out what does not answer, and the watch is unavailable only while none
// answers. A source whose owner leaves restarts the watch after Retry: the watch reports Unavailable for the gap, in
// which sleeps, wakes and lock changes are missed, and then the current state.
internal sealed partial class PortalPowerEvents(PortalApplication? application = null, string? systemAddress = null, string login = PortalPowerEvents.LoginName,
    string upower = PortalPowerEvents.UPowerName, string profiles = PortalPowerEvents.ProfilesName,
    string legacyProfiles = PortalPowerEvents.LegacyProfilesName) : IPowerEventSource
{
    internal const string LoginName = "org.freedesktop.login1";
    internal const string UPowerName = "org.freedesktop.UPower";
    internal const string ProfilesName = "org.freedesktop.UPower.PowerProfiles";
    internal const string LegacyProfilesName = "net.hadess.PowerProfiles";
    private const string ManagerInterface = "org.freedesktop.login1.Manager";
    private const string SessionInterface = "org.freedesktop.login1.Session";
    private const string UserInterface = "org.freedesktop.login1.User";
    private static readonly DBusObjectPath LoginPath = new("/org/freedesktop/login1");
    private static readonly DBusObjectPath UPowerPath = new("/org/freedesktop/UPower");
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private string? SystemAddress => systemAddress;
    private string Login => login;
    private string UPower => upower;
    private string Profiles => profiles;
    private string LegacyProfiles => legacyProfiles;

    // How long a watch waits before it opens its sources again after losing one.
    internal TimeSpan Retry { get; init; } = TimeSpan.FromSeconds(5);

    // The process and user whose session counts; the current ones outside tests.
    internal uint ProcessId { get; init; } = (uint)Environment.ProcessId;
    internal uint? UserId { get; init; }

    public async ValueTask<PlatformResult<DesktopPowerState>> ReadAsync(CancellationToken cancellationToken)
    {
        var sources = await Sources.OpenAsync(this, subscribe: false, cancellationToken).ConfigureAwait(false);
        await using (sources.ConfigureAwait(false))
        {
            if (!sources.Any)
            {
                Diagnose();
                return new PlatformResult<DesktopPowerState>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            }
            return new PlatformResult<DesktopPowerState>.Success(await sources.ReadAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    public ValueTask<IAsyncDisposable> SubscribeAsync(Action<PlatformResult<DesktopPowerEvent>> report, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stop = new CancellationTokenSource();
        var running = Task.Run(() => FollowAsync(report, stop.Token), CancellationToken.None);
        return ValueTask.FromResult<IAsyncDisposable>(new Subscription(stop, running));
    }

    private async Task FollowAsync(Action<PlatformResult<DesktopPowerEvent>> report, CancellationToken stop)
    {
        var unavailable = false;
        while (!stop.IsCancellationRequested)
        {
            Sources? sources = null;
            try
            {
                sources = await Sources.OpenAsync(this, subscribe: true, stop).ConfigureAwait(false);
                if (!sources.Any)
                {
                    if (!unavailable)
                    {
                        Diagnose();
                        report(new PlatformResult<DesktopPowerEvent>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
                    }
                    unavailable = true;
                }
                else
                {
                    unavailable = false;
                    using var lost = CancellationTokenSource.CreateLinkedTokenSource(stop, sources.Lost);
                    var tracker = new PowerStateTracker(report);
                    // Subscribed before the read, so a change between them is not missed.
                    tracker.Start(await sources.ReadAsync(lost.Token).ConfigureAwait(false));
                    while (true)
                    {
                        var signal = await sources.Signals.ReadAsync(lost.Token).ConfigureAwait(false);
                        await ApplyAsync(signal, tracker, sources, lost.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error) when (IsUnavailable(error, stop) || error is OperationCanceledException or ChannelClosedException)
            {
                // A source went away: events until the sources are open again are missed, so the watch says so, and the
                // reopened sources report the current state again, which the watch then shows even when it is unchanged.
                if (!unavailable) report(new PlatformResult<DesktopPowerEvent>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
                unavailable = true;
            }
            finally { if (sources is not null) await sources.DisposeAsync().ConfigureAwait(false); }
            try { await Task.Delay(Retry, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    // One signal of the sources, on the watch's loop.
    internal static async Task ApplyAsync(DBusSignal signal, PowerStateTracker tracker, Sources sources, CancellationToken cancellationToken)
    {
        switch (signal.Interface, signal.Member)
        {
            case (ManagerInterface, "PrepareForSleep") when signal.Arguments is [{ Signature.Value: "b" } start]:
                if (start.GetBoolean())
                {
                    tracker.Suspending();
                }
                else
                {
                    tracker.Resumed();
                    // The power source may have changed during the sleep.
                    tracker.Update(await sources.ReadAsync(cancellationToken).ConfigureAwait(false) with { IsLocked = tracker.State.IsLocked });
                }
                break;
            case (SessionInterface, "Lock"):
                tracker.Locked(true);
                break;
            case (SessionInterface, "Unlock"):
                tracker.Locked(false);
                break;
            default:
                if (DBusProxy.ReadPropertyChanges(signal) is not { } changes) break;
                if (changes.Interface == SessionInterface && changes.Changed.TryGetValue("LockedHint", out var hint) && hint.Signature.Value == "b")
                    tracker.Locked(hint.GetBoolean());
                else if (changes.Interface == UPowerName && changes.Changed.TryGetValue("OnBattery", out var battery) && battery.Signature.Value == "b")
                    tracker.Update(tracker.State with { IsOnBattery = battery.GetBoolean() });
                else if (changes.Changed.TryGetValue("ActiveProfile", out var profile) && profile.Signature.Value == "s")
                    tracker.Update(tracker.State with { IsLowPower = profile.GetString() == "power-saver" });
                break;
        }
    }

    private void Diagnose() => application?.Diagnose("power-events-unavailable", "Neither logind, UPower nor a power-profiles daemon answers on the system bus.",
        "Run systemd-logind or elogind and UPower; inside Flatpak, allow --system-talk-name=org.freedesktop.login1 and org.freedesktop.UPower.");

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUserId();

    private static bool IsUnavailable(Exception error, CancellationToken cancellationToken) =>
        error is DBusException or TimeoutException or NativeBackendUnavailableException or IOException
        || error is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    // The sources that answered, their signals in arrival order, and a token cancelled when one's owner leaves.
    internal sealed class Sources : IAsyncDisposable
    {
        private readonly Channel<DBusSignal> _signals = Channel.CreateUnbounded<DBusSignal>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _lost = new();
        private readonly List<DBusSignalSubscription> _subscriptions = [];
        private readonly List<CancellationTokenRegistration> _owners = [];
        private static int s_ownerRegistrations;

        // The registrations on the sources' owners that are not disposed yet, in the process; for tests.
        internal static int OwnerRegistrations => Volatile.Read(ref s_ownerRegistrations);
        private DBusProxy? _session;
        private DBusProxy? _upower;
        private (DBusProxy Proxy, string Interface)? _profiles;

        internal bool Any => _session is not null || _upower is not null || _profiles is not null;
        internal CancellationToken Lost => _lost.Token;
        internal ChannelReader<DBusSignal> Signals => _signals.Reader;

        internal static async Task<Sources> OpenAsync(PortalPowerEvents owner, bool subscribe, CancellationToken cancellationToken)
        {
            var sources = new Sources();
            try
            {
                await sources.OpenLoginAsync(owner, subscribe, cancellationToken).ConfigureAwait(false);
                await sources.OpenUPowerAsync(owner, subscribe, cancellationToken).ConfigureAwait(false);
                await sources.OpenProfilesAsync(owner, subscribe, cancellationToken).ConfigureAwait(false);
                return sources;
            }
            catch
            {
                await sources.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async Task<PortalConnection?> ConnectAsync(PortalPowerEvents owner, string destination, CancellationToken cancellationToken)
        {
            try
            {
                var connection = await PortalConnection.OpenSystemAsync(owner.SystemAddress, destination, cancellationToken)
                    .WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
                _owners.Add(connection.OwnerChanged.Register(static state => ((Sources)state!).Lose(), this));
                Interlocked.Increment(ref s_ownerRegistrations);
                return connection;
            }
            catch (Exception error) when (IsUnavailable(error, cancellationToken)) { return null; }
        }

        private void Lose()
        {
            try { _lost.Cancel(); } catch (ObjectDisposedException) { }
        }

        private async Task SubscribeAsync(Func<ValueTask<DBusSignalSubscription>> subscribe, CancellationToken cancellationToken)
        {
            var subscription = await subscribe().AsTask().WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
            _subscriptions.Add(subscription);
            _ = PortalValues.PumpAsync(subscription, signal => _signals.Writer.TryWrite(signal), _ => Lose());
        }

        private async Task OpenLoginAsync(PortalPowerEvents owner, bool subscribe, CancellationToken cancellationToken)
        {
            if (await ConnectAsync(owner, owner.Login, cancellationToken).ConfigureAwait(false) is not { } login) return;
            try
            {
                var manager = new DBusProxy(login.Connection, login.Destination, LoginPath) { Timeout = CallTimeout };
                var path = await SessionPathAsync(manager, owner, cancellationToken).ConfigureAwait(false);
                if (subscribe) await SubscribeAsync(() => manager.SubscribeSignalAsync(ManagerInterface, "PrepareForSleep", cancellationToken), cancellationToken).ConfigureAwait(false);
                if (path is not { } sessionPath) return;
                var session = new DBusProxy(login.Connection, login.Destination, sessionPath) { Timeout = CallTimeout };
                if (subscribe)
                {
                    await SubscribeAsync(() => session.SubscribeSignalAsync(SessionInterface, "Lock", cancellationToken), cancellationToken).ConfigureAwait(false);
                    await SubscribeAsync(() => session.SubscribeSignalAsync(SessionInterface, "Unlock", cancellationToken), cancellationToken).ConfigureAwait(false);
                    await SubscribeAsync(() => session.SubscribePropertiesAsync(SessionInterface, cancellationToken), cancellationToken).ConfigureAwait(false);
                }
                _session = session;
            }
            catch (Exception error) when (IsUnavailable(error, cancellationToken)) { }
        }

        // The process's session, else the user's display session; null when neither exists, such as for a system service.
        private static async Task<DBusObjectPath?> SessionPathAsync(DBusProxy manager, PortalPowerEvents owner, CancellationToken cancellationToken)
        {
            try
            {
                if (await manager.CallAsync(ManagerInterface, "GetSessionByPID", [DBusValue.From(owner.ProcessId)], cancellationToken: cancellationToken).ConfigureAwait(false)
                    is [{ Signature.Value: "o" } session])
                    return session.GetObjectPath();
            }
            catch (DBusException) { }
            try
            {
                var uid = owner.UserId ?? GetUserId();
                if (await manager.CallAsync(ManagerInterface, "GetUser", [DBusValue.From(uid)], cancellationToken: cancellationToken).ConfigureAwait(false)
                    is not [{ Signature.Value: "o" } user]) return null;
                var display = await new DBusProxy(manager.Connection, manager.Destination, user.GetObjectPath()) { Timeout = CallTimeout }
                    .GetPropertyAsync(UserInterface, "Display", cancellationToken).ConfigureAwait(false);
                return display.Signature.Value == "(so)" && display.GetFields() is [_, { Signature.Value: "o" } path]
                    && path.GetObjectPath().Value != "/" ? path.GetObjectPath() : (DBusObjectPath?)null;
            }
            catch (DBusException) { return null; }
        }

        private async Task OpenUPowerAsync(PortalPowerEvents owner, bool subscribe, CancellationToken cancellationToken)
        {
            if (await ConnectAsync(owner, owner.UPower, cancellationToken).ConfigureAwait(false) is not { } connection) return;
            try
            {
                var proxy = new DBusProxy(connection.Connection, connection.Destination, UPowerPath) { Timeout = CallTimeout };
                // A daemon without the property fails here.
                _ = await proxy.GetPropertyAsync(UPowerName, "OnBattery", cancellationToken).ConfigureAwait(false);
                if (subscribe) await SubscribeAsync(() => proxy.SubscribePropertiesAsync(UPowerName, cancellationToken), cancellationToken).ConfigureAwait(false);
                _upower = proxy;
            }
            catch (Exception error) when (IsUnavailable(error, cancellationToken)) { }
        }

        private async Task OpenProfilesAsync(PortalPowerEvents owner, bool subscribe, CancellationToken cancellationToken)
        {
            foreach (var name in (string[])[owner.Profiles, owner.LegacyProfiles])
            {
                if (await ConnectAsync(owner, name, cancellationToken).ConfigureAwait(false) is not { } connection) continue;
                try
                {
                    var proxy = new DBusProxy(connection.Connection, connection.Destination, new DBusObjectPath("/" + name.Replace('.', '/')))
                    { Timeout = CallTimeout };
                    _ = await proxy.GetPropertyAsync(name, "ActiveProfile", cancellationToken).ConfigureAwait(false);
                    if (subscribe) await SubscribeAsync(() => proxy.SubscribePropertiesAsync(name, cancellationToken), cancellationToken).ConfigureAwait(false);
                    _profiles = (proxy, name);
                    return;
                }
                catch (Exception error) when (IsUnavailable(error, cancellationToken)) { }
            }
        }

        internal async Task<DesktopPowerState> ReadAsync(CancellationToken cancellationToken) => new()
        {
            IsLocked = _session is { } session ? await BooleanAsync(session, SessionInterface, "LockedHint", cancellationToken).ConfigureAwait(false) : null,
            IsOnBattery = _upower is { } upower ? await BooleanAsync(upower, UPowerName, "OnBattery", cancellationToken).ConfigureAwait(false) : null,
            IsLowPower = _profiles is { } profiles ? await ProfileAsync(profiles.Proxy, profiles.Interface, cancellationToken).ConfigureAwait(false) : null,
        };

        private static async Task<bool?> BooleanAsync(DBusProxy proxy, string interfaceName, string name, CancellationToken cancellationToken)
        {
            try
            {
                var value = await proxy.GetPropertyAsync(interfaceName, name, cancellationToken).ConfigureAwait(false);
                return value.Signature.Value == "b" ? value.GetBoolean() : null;
            }
            catch (DBusException) { return null; }
        }

        private static async Task<bool?> ProfileAsync(DBusProxy proxy, string interfaceName, CancellationToken cancellationToken)
        {
            try
            {
                var value = await proxy.GetPropertyAsync(interfaceName, "ActiveProfile", cancellationToken).ConfigureAwait(false);
                return value.Signature.Value == "s" ? value.GetString() == "power-saver" : null;
            }
            catch (DBusException) { return null; }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var subscription in _subscriptions) await subscription.DisposeAsync().ConfigureAwait(false);
            _signals.Writer.TryComplete();
            foreach (var owner in _owners)
            {
                await owner.DisposeAsync().ConfigureAwait(false);
                Interlocked.Decrement(ref s_ownerRegistrations);
            }
            _owners.Clear();
            _lost.Dispose();
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
