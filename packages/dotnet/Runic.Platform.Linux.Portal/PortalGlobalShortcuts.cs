using System.Collections.Immutable;
using System.Threading.Channels;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

// org.freedesktop.portal.GlobalShortcuts. The portal binds a set of shortcuts once per session, so each BindAsync closes
// the previous session and binds the new set in a new one; the desktop keeps the user's keys per application and action
// ID across sessions. Activated and ShortcutsChanged arrive on the portal object for every session of the connection and
// are matched to the current session. A replaced portal ends the session: the set is bound again in a new session.
internal sealed class PortalGlobalShortcuts(PortalApplication application, string? address = null,
    string destination = "org.freedesktop.portal.Desktop") : IDesktopGlobalShortcuts
{
    private const string Root = "/org/freedesktop/portal/desktop";
    private const int EventQueueLimit = 64;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);
    private static readonly PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>> Gone =
        new PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Unavailable(PlatformUnavailableReason.BackendUnavailable);

    private readonly SemaphoreSlim _gate = new(1, 1);
    // Bus handlers read the bindings on the connection's thread; the gate's holder replaces them under this lock.
    private readonly Lock _lock = new();
    // Activations and binding changes reach the application one at a time, in order, and never after disposal. The queue
    // is bounded, so a peer that signals faster than the handlers run cannot grow it.
    private readonly Channel<object> _events = Channel.CreateBounded<object>(new BoundedChannelOptions(EventQueueLimit)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait,
    });
    private int _dropping;
    private Task? _pump;
    // The requested set; default before the first BindAsync. Under the gate.
    private ImmutableArray<DesktopGlobalShortcut> _shortcuts;
    // The bindings of the current set, in its order. Under _lock.
    private ImmutableArray<DesktopGlobalShortcutBinding> _bindings = [];
    private volatile Session? _session;
    private volatile bool _disposed;

    public event EventHandler<DesktopGlobalShortcutActivation>? Activated;
    public event EventHandler<DesktopGlobalShortcutsChange>? BindingsChanged;

    // For the tests: the current session's handle.
    internal string? SessionHandle => _session?.Handle;

    public async ValueTask<PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>> BindAsync(
        ImmutableArray<DesktopGlobalShortcut> shortcuts, CancellationToken cancellationToken = default)
    {
        DesktopGlobalShortcutRules.Validate(shortcuts);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pump ??= Task.Run(PumpAsync, CancellationToken.None);
            await CloseSessionAsync().ConfigureAwait(false);
            _shortcuts = shortcuts;
            SetBindings([.. shortcuts.Select(static shortcut => Unbound(shortcut.Id))]);
            if (shortcuts.IsEmpty) return Success([]);
            return await OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_shortcuts.IsDefaultOrEmpty) return Success([]);
            // A session that ended, with its portal or by the portal's choice, is bound again.
            if (_session is not { Ended: false } session) return await OpenAsync(cancellationToken).ConfigureAwait(false);
            var (code, results) = await PortalRequests.ExchangeAsync(session.Portal, token => PortalOptions.RequestAsync(
                Proxy(session.Portal), Protocol.GlobalShortcuts.InterfaceName, "ListShortcuts",
                [DBusValue.From(new DBusObjectPath(session.Handle!)), new PortalOptions { ["handle_token"] = DBusValue.From(token) }.ToValue()]),
                cancellationToken).ConfigureAwait(false);
            if (code != 0) return new PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Failed(PlatformFailureCode.Other, PortalErrors.Response(code));
            var bindings = Map(Ids(_shortcuts), Reply(results), keepMissing: false);
            SetBindings(bindings);
            return Success(bindings);
        }
        catch (DBusException error) when (error.ErrorName is "org.freedesktop.portal.Error.NotAllowed" or "org.freedesktop.DBus.Error.AccessDenied")
        { return new PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Failed(PlatformFailureCode.PermissionDenied, PortalErrors.Reply(error)); }
        catch (Exception error) when (error is DBusException or TimeoutException or NativeBackendUnavailableException or IOException) { return Gone; }
        finally { _gate.Release(); }
    }

    public async ValueTask<PlatformResult<PlatformUnit>> ConfigureAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var unavailable = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            // The dialog configures a session's shortcuts: there is nothing to configure before a set is bound.
            if (_shortcuts.IsDefaultOrEmpty) return unavailable;
            if (_session is not { Ended: false })
            {
                await OpenAsync(cancellationToken).ConfigureAwait(false);
                if (_session is null) return unavailable;
            }
            var session = _session!;
            var proxy = Proxy(session.Portal);
            // ConfigureShortcuts is version 2 of the interface.
            if (await proxy.GetVersionAsync(cancellationToken).AsTask().WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false) < 2)
                return unavailable;
            await proxy.CallAsync(Protocol.GlobalShortcuts.InterfaceName, "ConfigureShortcuts",
                [DBusValue.From(new DBusObjectPath(session.Handle!)), DBusValue.From(""), new PortalOptions().ToValue()],
                cancellationToken: cancellationToken).AsTask().WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
            return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        }
        catch (DBusException error) when (error.ErrorName is "org.freedesktop.portal.Error.NotAllowed" or "org.freedesktop.DBus.Error.AccessDenied")
        { return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied, PortalErrors.Reply(error)); }
        catch (Exception error) when (error is DBusException or TimeoutException or NativeBackendUnavailableException or IOException)
        { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _events.Writer.TryComplete();
            await CloseSessionAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // Opens a session and binds the current set in it. Under the gate.
    private async Task<PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>> OpenAsync(CancellationToken cancellationToken)
    {
        var shortcuts = _shortcuts;
        PortalConnection? portal = null;
        Session? session = null;
        try
        {
            portal = await PortalConnection.OpenAsync(address, destination, application, cancellationToken).ConfigureAwait(false);
            var proxy = Proxy(portal);
            // A portal without a GlobalShortcuts backend does not have the interface, and its version read fails.
            try { await proxy.GetVersionAsync(cancellationToken).AsTask().WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false); }
            catch (DBusException error) when (error.ErrorName != DBusException.Disconnected)
            {
                application.Diagnose("global-shortcuts-unavailable", error.ErrorName,
                    "The desktop has no GlobalShortcuts portal backend; KDE Plasma 5.27, GNOME 48, Hyprland and later provide one.");
                return Gone;
            }
            // Subscribe before the session exists: the portal may signal as soon as it binds.
            var signals = await portal.Connection.SubscribeAsync(new DBusSignalFilter
            {
                Sender = portal.Destination, Path = new DBusObjectPath(Root), Interface = Protocol.GlobalShortcuts.InterfaceName,
            }, cancellationToken).AsTask().WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
            session = new Session(portal, signals);
            _ = PortalValues.PumpAsync(signals, signal => OnSignal(session, signal), static _ => { });
            string sessionToken = PortalSessions.Token();
            var (created, createdResults) = await PortalRequests.ExchangeAsync(portal, token => PortalOptions.RequestAsync(proxy,
                Protocol.GlobalShortcuts.InterfaceName, "CreateSession",
                [new PortalOptions { ["handle_token"] = DBusValue.From(token), ["session_handle_token"] = DBusValue.From(sessionToken) }.ToValue()]),
                cancellationToken).ConfigureAwait(false);
            if (created != 0)
            {
                await session.Signals.DisposeAsync().ConfigureAwait(false);
                return new PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Failed(PlatformFailureCode.Other, PortalErrors.Response(created));
            }
            session.Handle = PortalSessions.Handle(portal, createdResults) ?? throw new IOException("The portal returned an invalid session handle.");
            _session = session;
            session.OwnerGone = portal.OwnerChanged.Register(static state => ((PortalGlobalShortcuts)state!).OnOwnerGone(), this);
            var request = shortcuts.Select(static shortcut =>
            {
                var options = new PortalOptions { ["description"] = DBusValue.From(shortcut.Description) };
                if (DesktopGlobalShortcutRules.Requested(shortcut) is { } preferred && DesktopShortcutKeys.XdgTrigger(preferred) is { } trigger)
                    options["preferred_trigger"] = DBusValue.From(trigger);
                return DBusValue.Struct(DBusValue.From(shortcut.Id), options.ToValue());
            });
            var (bound, boundResults) = await PortalRequests.ExchangeAsync(portal, token => PortalOptions.RequestAsync(proxy,
                Protocol.GlobalShortcuts.InterfaceName, "BindShortcuts",
                [DBusValue.From(new DBusObjectPath(session.Handle!)), DBusValue.Array("(sa{sv})", request), DBusValue.From(""),
                    new PortalOptions { ["handle_token"] = DBusValue.From(token) }.ToValue()]),
                cancellationToken).ConfigureAwait(false);
            // The user dismissed the portal's dialog (1): every action stays unassigned.
            if (bound == 2) return new PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Failed(PlatformFailureCode.Other, PortalErrors.Response(bound));
            var bindings = Map(Ids(shortcuts), bound == 0 ? Reply(boundResults) : [], keepMissing: false);
            SetBindings(bindings);
            return Success(bindings);
        }
        catch (DBusException error) when (error.ErrorName is "org.freedesktop.portal.Error.NotAllowed" or "org.freedesktop.DBus.Error.AccessDenied")
        {
            // xdg-desktop-portal refuses CreateSession to a caller without an application ID.
            application.Diagnose("global-shortcuts-unavailable", error.ErrorName,
                "The portal binds global shortcuts only for an identified application: pass its installed desktop ID to PortalApplication or AddRunicPlatformServices.");
            await DropAsync(session).ConfigureAwait(false);
            return new PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Failed(PlatformFailureCode.PermissionDenied, PortalErrors.Reply(error));
        }
        catch (Exception error) when (error is DBusException or TimeoutException or NativeBackendUnavailableException or IOException)
        {
            await DropAsync(session).ConfigureAwait(false);
            return Gone;
        }
        catch
        {
            await DropAsync(session).ConfigureAwait(false);
            throw;
        }
    }

    // Ends a session that did not finish binding.
    private async Task DropAsync(Session? session)
    {
        if (session is null) return;
        if (ReferenceEquals(_session, session)) _session = null;
        session.Ended = true;
        session.OwnerGone.Dispose();
        await session.Signals.DisposeAsync().ConfigureAwait(false);
        if (session.Handle is { } handle) await PortalSessions.CloseAsync(session.Portal, handle).ConfigureAwait(false);
    }

    // Closes the current session. Under the gate.
    private async Task CloseSessionAsync()
    {
        if (_session is not { } session) return;
        _session = null;
        session.Ended = true;
        session.OwnerGone.Dispose();
        await session.Signals.DisposeAsync().ConfigureAwait(false);
        await PortalSessions.CloseAsync(session.Portal, session.Handle!).ConfigureAwait(false);
    }

    // The portal was replaced or left the bus, which ended the session: bind the set again in a new session.
    private void OnOwnerGone() => _ = Task.Run(async () =>
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || _session is not { } session || !session.Portal.OwnerChanged.IsCancellationRequested) return;
                session.Ended = true;
                _session = null;
                await session.Signals.DisposeAsync().ConfigureAwait(false);
                if (_shortcuts.IsDefaultOrEmpty) return;
                // Without a portal the actions are unassigned until the next request binds them again. The new portal may
                // have assigned other keys, so the result is reported either way.
                if (await OpenAsync(CancellationToken.None).ConfigureAwait(false) is not PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success)
                    SetBindings([.. _shortcuts.Select(static shortcut => Unbound(shortcut.Id))]);
                Queue(new DesktopGlobalShortcutsChange(Current));
            }
            finally { _gate.Release(); }
        }
        catch (ObjectDisposedException) { }
    });

    private void OnSignal(Session session, DBusSignal signal)
    {
        if (session.Ended || session.Handle is not { } handle) return;
        switch (signal.Member)
        {
            // Activated(o session_handle, s shortcut_id, t timestamp, a{sv} options); Deactivated, the release, is not reported.
            case "Activated" when signal.Arguments is [{ Signature.Value: "o" } path, { Signature.Value: "s" } id, { Signature.Value: "t" }, { Signature.Value: "a{sv}" } options]
                && path.GetObjectPath().Value == handle:
                string shortcutId = id.GetString();
                if (!Current.Any(binding => binding.Id == shortcutId)) return;
                DesktopActivationContext? context = null;
                if (PortalValues.Variants(options).TryGetValue("activation_token", out var token) && token.Signature.Value == "s")
                {
                    try { context = new DesktopActivationContext(activationToken: token.GetString()); }
                    catch (ArgumentException) { /* An unusable token cannot raise a window; activate without it. */ }
                }
                Queue(new DesktopGlobalShortcutActivation(shortcutId) { PlatformContext = context });
                break;
            // ShortcutsChanged(o session_handle, a(sa{sv}) shortcuts): the user or the desktop changed some of them.
            case "ShortcutsChanged" when signal.Arguments is [{ Signature.Value: "o" } path, { Signature.Value: "a(sa{sv})" } shortcuts]
                && path.GetObjectPath().Value == handle:
                ImmutableArray<DesktopGlobalShortcutBinding> changed;
                lock (_lock)
                {
                    var before = _bindings;
                    var after = Map([.. before.Select(static binding => binding.Id)], Read(shortcuts), keepMissing: true, before);
                    if (after.SequenceEqual(before)) return;
                    _bindings = changed = after;
                }
                Queue(new DesktopGlobalShortcutsChange(changed));
                break;
        }
    }

    private void Queue(object change)
    {
        // TryWrite fails when the queue is full, or after disposal; the newest event is the one dropped.
        if (!_events.Writer.TryWrite(change) && !_disposed && Interlocked.Exchange(ref _dropping, 1) == 0)
            application.Diagnose("global-shortcuts-events-dropped",
                $"More than {EventQueueLimit} global shortcut events waited for the event handlers; later ones were dropped.",
                "Return from the Activated and BindingsChanged handlers quickly and move longer work off them.");
    }

    private async Task PumpAsync()
    {
        await foreach (var change in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_disposed) return;
            if (!_events.Reader.TryPeek(out _)) Volatile.Write(ref _dropping, 0);
            try
            {
                if (change is DesktopGlobalShortcutActivation activation) Activated?.Invoke(this, activation);
                else BindingsChanged?.Invoke(this, (DesktopGlobalShortcutsChange)change);
            }
            catch (Exception error)
            {
                // As with an event raised on the thread pool, a throwing handler fails the process; later events still arrive until then.
                var capture = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
                ThreadPool.QueueUserWorkItem(static state => ((System.Runtime.ExceptionServices.ExceptionDispatchInfo)state!).Throw(), capture);
            }
        }
    }

    private ImmutableArray<DesktopGlobalShortcutBinding> Current { get { lock (_lock) return _bindings; } }

    private void SetBindings(ImmutableArray<DesktopGlobalShortcutBinding> bindings) { lock (_lock) _bindings = bindings; }

    private static Protocol.GlobalShortcuts Proxy(PortalConnection portal) => new(portal.Connection, portal.Destination, new DBusObjectPath(Root));

    private static PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>> Success(ImmutableArray<DesktopGlobalShortcutBinding> bindings) =>
        new PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success(bindings);

    private static ImmutableArray<string> Ids(ImmutableArray<DesktopGlobalShortcut> shortcuts) => [.. shortcuts.Select(static shortcut => shortcut.Id)];

    private static DesktopGlobalShortcutBinding Unbound(string id) => new(id) { Status = DesktopGlobalShortcutStatus.Unbound };

    // The shortcuts of a BindShortcuts or ListShortcuts response.
    private static Dictionary<string, string?> Reply(Dictionary<string, DBusValue> results) =>
        results.TryGetValue("shortcuts", out var value) && value.Signature.Value == "a(sa{sv})" ? Read(value) : [];

    // Each shortcut's trigger_description, null when it has none. A peer controls the list, so a repeated id keeps its last entry.
    internal static Dictionary<string, string?> Read(DBusValue shortcuts)
    {
        var read = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var item in shortcuts.GetItems())
        {
            var fields = item.GetFields();
            read[fields[0].GetString()] = PortalValues.Variants(fields[1]).TryGetValue("trigger_description", out var trigger)
                && trigger.Signature.Value == "s" && trigger.GetString().Trim() is { Length: > 0 } text ? text : null;
        }
        return read;
    }

    // The bindings of the requested set from the portal's shortcuts. An action the portal does not list is unbound, or keeps
    // its earlier binding for a change signal, which lists only the shortcuts that changed.
    internal static ImmutableArray<DesktopGlobalShortcutBinding> Map(ImmutableArray<string> ids,
        Dictionary<string, string?> reply, bool keepMissing, ImmutableArray<DesktopGlobalShortcutBinding> before = default)
    {
        var bindings = ImmutableArray.CreateBuilder<DesktopGlobalShortcutBinding>(ids.Length);
        for (int index = 0; index < ids.Length; index++)
        {
            string id = ids[index];
            if (!reply.TryGetValue(id, out var trigger))
            {
                bindings.Add(keepMissing && !before.IsDefault ? before[index] : Unbound(id));
                continue;
            }
            bindings.Add(trigger is null ? Unbound(id) : new DesktopGlobalShortcutBinding(id)
            {
                Shortcut = DesktopShortcutKeys.ParseTrigger(trigger),
                TriggerDescription = trigger,
                Status = DesktopGlobalShortcutStatus.Bound,
            });
        }
        return bindings.MoveToImmutable();
    }

    // One portal session and the subscription to its portal's signals.
    private sealed class Session(PortalConnection portal, DBusSignalSubscription signals)
    {
        internal PortalConnection Portal { get; } = portal;
        internal DBusSignalSubscription Signals { get; } = signals;
        internal string? Handle { get; set; }
        internal CancellationTokenRegistration OwnerGone { get; set; }
        internal volatile bool Ended;
    }
}
