using System.Collections.Immutable;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

// One portal call's view of the shared connection: the connection, the unique name of the service that answers, and a
// token cancelled when that owner goes away. Binding every call to the owner prevents a restart between registration
// and submission from sending an anonymous request to the replacement process.
internal sealed class PortalConnection
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private PortalConnection(DBusConnection connection, string destination, CancellationToken ownerChanged)
    { Connection = connection; Destination = destination; OwnerChanged = ownerChanged; }

    internal DBusConnection Connection { get; }
    internal string Destination { get; }
    internal CancellationToken OwnerChanged { get; }

    // The desktop ID registered with the host portal: none inside Flatpak or Snap, where the sandbox identifies the application.
    internal static string? RegisteredIdentity(PortalApplication? application) =>
        application?.ApplicationId is { } id && !File.Exists("/.flatpak-info") && Environment.GetEnvironmentVariable("SNAP") is null ? id : null;

    internal static async Task<PortalConnection> OpenAsync(string? address, string destination,
        PortalApplication? application, CancellationToken cancellationToken)
    {
        string? identity = RegisteredIdentity(application);
        var link = await PortalBus.Session(address, identity).ConnectAsync(cancellationToken).ConfigureAwait(false);
        var connection = link.Connection;
        // The watch follows the name from before this query, so a change after it is never missed; the query itself is
        // current even when the watch has not read the latest change yet.
        var watch = link.Watch(destination);
        await watch.StartedAsync(cancellationToken).WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
        string? owner = await NameOwnerAsync(connection, destination, cancellationToken).ConfigureAwait(false);
        if (owner is null && !destination.StartsWith(':'))
        {
            await BusAsync(connection, "StartServiceByName", [DBusValue.From(destination), DBusValue.From(0u)], cancellationToken).ConfigureAwait(false);
            owner = await NameOwnerAsync(connection, destination, cancellationToken).ConfigureAwait(false)
                ?? await watch.WaitForOwnerAsync(cancellationToken).WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
        }
        // A unique name that is gone stays the destination: its calls fail as the bus answers them.
        owner ??= destination;
        var ownerChanged = await watch.OwnerChangedAsync(owner, cancellationToken).WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
        if (identity is not null)
        {
            try { await link.RegisterAsync(owner, identity, application!).WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (DBusException error) when (error.ErrorName != DBusException.Disconnected)
            {
                application!.Diagnose("portal-identity-registration-failed", "The portal rejected the application's desktop identity.",
                    $"Install a matching {identity}.desktop entry where the desktop session finds desktop entries, such as ~/.local/share/applications. " +
                    "For development, create the services with a null application ID, for example new PortalApplication() " +
                    "or AddRunicPlatformServices() without an ID, so the portal infers the identity.");
                throw;
            }
        }
        return new PortalConnection(connection, owner, ownerChanged);
    }

    private static async Task<string?> NameOwnerAsync(DBusConnection connection, string name, CancellationToken cancellationToken)
    {
        try { return (await BusAsync(connection, "GetNameOwner", [DBusValue.From(name)], cancellationToken).ConfigureAwait(false)) is [{ Signature.Value: "s" } owner] ? owner.GetString() : null; }
        catch (DBusException error) when (error.ErrorName == "org.freedesktop.DBus.Error.NameHasNoOwner") { return null; }
    }

    private static Task<ImmutableArray<DBusValue>> BusAsync(DBusConnection connection, string member, ImmutableArray<DBusValue> arguments, CancellationToken cancellationToken) =>
        connection.CallMethodAsync(new DBusMethodCall("org.freedesktop.DBus", new DBusObjectPath("/org/freedesktop/DBus"), "org.freedesktop.DBus", member)
        { Arguments = arguments }, cancellationToken).AsTask().WaitAsync(Timeout, cancellationToken);
}

// The process's connections. Every Portal service shares one connection per bus and desktop identity: the host portal
// takes the identity once per connection, so an application that supplies an ID and windows whose services infer it use
// two. A connection that closed is replaced on the next request. Shared connections stay open for the process.
internal sealed class PortalBus
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly Lock s_gate = new();
    private static readonly Dictionary<(DBusBus Bus, string? Address, string? Identity), PortalBus> s_buses = [];

    private readonly DBusBus _bus;
    private readonly string? _address;
    private readonly Lock _gate = new();
    private Task<PortalLink>? _connecting;

    private PortalBus(DBusBus bus, string? address) { _bus = bus; _address = address; }

    // The session bus, or the bus at an address (tests), for one registered identity.
    internal static PortalBus Session(string? address, string? identity) => Get(DBusBus.Session, address, identity);
    internal static PortalBus System(string? address) => Get(DBusBus.System, address, null);

    private static PortalBus Get(DBusBus bus, string? address, string? identity)
    {
        lock (s_gate)
        {
            if (!s_buses.TryGetValue((bus, address, identity), out var shared)) s_buses[(bus, address, identity)] = shared = new PortalBus(bus, address);
            return shared;
        }
    }

    // The open connection, connecting first when there is none. One attempt serves every caller, and a caller's
    // cancellation stops only its own wait.
    /// <exception cref="NativeBackendUnavailableException">There is no bus, or it refused the connection.</exception>
    /// <exception cref="TimeoutException">The bus did not accept the connection in time.</exception>
    internal Task<PortalLink> ConnectAsync(CancellationToken cancellationToken)
    {
        Task<PortalLink> connecting;
        lock (_gate)
        {
            if (_connecting is not { } current || current.IsFaulted || current.IsCanceled
                || current is { IsCompletedSuccessfully: true, Result.Connection.Disconnected.IsCancellationRequested: true })
            {
                if (_connecting is { IsCompletedSuccessfully: true } closed) _ = closed.Result.Connection.DisposeAsync().AsTask();
                _connecting = ConnectCoreAsync();
            }
            connecting = _connecting;
        }
        return connecting.WaitAsync(cancellationToken);
    }

    private async Task<PortalLink> ConnectCoreAsync()
    {
        using var deadline = new CancellationTokenSource(Timeout);
        PlatformResult<DBusConnection> result;
        try
        {
            result = _address is null ? await DBusConnection.ConnectAsync(_bus, deadline.Token).ConfigureAwait(false)
                : await DBusConnection.ConnectToAddressAsync(_address, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw new TimeoutException("The D-Bus connection timed out."); }
        return result is PlatformResult<DBusConnection>.Success(var connection) ? new PortalLink(connection) : throw new NativeBackendUnavailableException();
    }

    // The open connection as a result, for the services that report results rather than exceptions.
    internal async ValueTask<PlatformResult<DBusConnection>> TryConnectAsync(CancellationToken cancellationToken)
    {
        try { return new PlatformResult<DBusConnection>.Success((await ConnectAsync(cancellationToken).ConfigureAwait(false)).Connection); }
        catch (Exception error) when (error is NativeBackendUnavailableException or TimeoutException)
        { return new PlatformResult<DBusConnection>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
    }
}

// One connection and what the services learned about its peers: the owners of the names they call, and the portal
// owners that took the connection's desktop identity.
internal sealed class PortalLink(DBusConnection connection)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, NameOwnerWatch> _watches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _registrations = new(StringComparer.Ordinal);

    internal DBusConnection Connection { get; } = connection;

    internal NameOwnerWatch Watch(string name)
    {
        lock (_gate)
        {
            if (!_watches.TryGetValue(name, out var watch) || watch.Ended)
            {
                // Watches of unique names end with their peer; forget them.
                if (_watches.Count >= 32)
                    foreach (var ended in _watches.Where(static entry => entry.Value.Ended).Select(static entry => entry.Key).ToArray()) _watches.Remove(ended);
                _watches[name] = watch = new NameOwnerWatch(Connection, name);
            }
            return watch;
        }
    }

    // Registers the identity with one portal owner once. The registration is not tied to one caller's cancellation, since
    // the portal accepts only one per connection; a failed one is tried again by the next caller.
    internal Task RegisterAsync(string owner, string identity, PortalApplication application)
    {
        lock (_gate)
        {
            if (!_registrations.TryGetValue(owner, out var registration) || registration.IsFaulted || registration.IsCanceled)
                _registrations[owner] = registration = RegisterCoreAsync(owner, identity, application);
            return registration;
        }
    }

    private async Task RegisterCoreAsync(string owner, string identity, PortalApplication application)
    {
        await new Protocol.Registry(Connection, owner, "/org/freedesktop/portal/desktop")
            .RegisterAsync(identity, ImmutableDictionary<string, DBusValue>.Empty).AsTask().WaitAsync(Timeout).ConfigureAwait(false);
        application.Diagnose("portal-identity-registered", "The portal connection registered its desktop identity.", "All services created by this application use the same desktop ID.");
    }
}

// Follows the owner of one bus name on a connection, from the first query on.
internal sealed class NameOwnerWatch
{
    // Owners seen recently, so a caller whose own query is ahead of or behind the watch can tell where it stands.
    private const int SeenLimit = 16;
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationToken _disconnected;
    private readonly Queue<string?> _seen = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource _ownerLifetime;
    private string? _owner;
    private Exception? _failure;
    private volatile bool _ended;

    internal NameOwnerWatch(DBusConnection connection, string name)
    {
        _disconnected = connection.Disconnected;
        _ownerLifetime = CancellationTokenSource.CreateLinkedTokenSource(_disconnected);
        _ = FollowAsync(connection, name);
    }

    internal bool Ended => _ended;

    private async Task FollowAsync(DBusConnection connection, string name)
    {
        try
        {
            await foreach (var owner in connection.WatchNameOwnerAsync(name, _disconnected).ConfigureAwait(false))
            {
                // A unique name never comes back once its peer is gone.
                if (owner is null && name.StartsWith(':')) break;
                CancellationTokenSource previous;
                TaskCompletionSource changed;
                lock (_gate)
                {
                    _owner = owner;
                    _seen.Enqueue(owner);
                    if (_seen.Count > SeenLimit) _seen.Dequeue();
                    previous = _ownerLifetime;
                    _ownerLifetime = CancellationTokenSource.CreateLinkedTokenSource(_disconnected);
                    changed = _changed;
                    _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                _first.TrySetResult();
                previous.Cancel();
                previous.Dispose();
                changed.TrySetResult();
            }
        }
        // The bus's answer, such as AccessDenied inside a sandbox, reaches the callers as it is.
        catch (DBusException error) { _failure = error; }
        catch (OperationCanceledException) { }
        finally
        {
            // The connection closed or the bus refused the watch: nothing is known about the owner any more.
            TaskCompletionSource changed;
            lock (_gate)
            {
                _ended = true;
                _owner = null;
                _ownerLifetime.Cancel();
                changed = _changed;
            }
            _first.TrySetException(_failure ?? new NativeBackendUnavailableException());
            changed.TrySetException(_failure ?? new NativeBackendUnavailableException());
            // Nobody may be waiting; the next caller starts a new watch and sees its own outcome.
            _ = _first.Task.Exception;
            _ = changed.Task.Exception;
        }
    }

    // Completes once the watch follows the name.
    internal Task StartedAsync(CancellationToken cancellationToken) => _first.Task.WaitAsync(cancellationToken);

    internal async Task<string> WaitForOwnerAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_owner is { } owner) return owner;
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // A token cancelled once the name has another owner than this one, which a fresh query reported, or the connection
    // closed. Waits until the watch has caught up with the query; cancelled at once when it has since moved on.
    internal async Task<CancellationToken> OwnerChangedAsync(string owner, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_seen.Contains(owner)) return !_ended && _owner == owner ? _ownerLifetime.Token : new CancellationToken(true);
                // The peer of a unique name left before the watch saw it.
                if (_ended) return new CancellationToken(true);
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
