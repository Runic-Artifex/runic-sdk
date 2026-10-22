using System.Collections.Concurrent;
using System.Collections.Immutable;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

internal sealed class PortalNotifications(string? address = null, string destination = "org.freedesktop.portal.Desktop", string? applicationId = null,
    Action<PortalDiagnostic>? diagnosticSink = null, PortalApplication? application = null) : IDesktopNotifications
{
    private readonly PortalApplication _application = application ?? new PortalApplication(applicationId, diagnosticSink);
    private string? ApplicationId => _application.ApplicationId;
    private PortalConnection? _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, DesktopNotification> _sent = new(StringComparer.Ordinal);
    // Routes actions for the most recently shown notifications; older ones are evicted.
    private readonly List<string> _recent = [];
    private const int RecentCapacity = 64;
    private DBusConnection? _connection;
    private DBusSignalSubscription? _actions;
    // The application's bus name and its org.freedesktop.Application object, on the connection that owns them.
    private DBusConnection? _owner;
    private IAsyncDisposable? _activation;
    private volatile bool _disposed;
    public event EventHandler<DesktopNotificationActivation>? Activated;

    public ValueTask<PlatformResult<PlatformUnit>> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(null, cancellationToken); // The portal owns permission policy; no separate authorization method exists.
    public ValueTask<PlatformResult<PlatformUnit>> ShowAsync(DesktopNotification notification, CancellationToken cancellationToken = default)
    {
        DesktopServiceValidation.Notification(notification);
        return ExecuteAsync(proxy => proxy.CallAsync(Protocol.Notification.InterfaceName, "AddNotification",
            [DBusValue.From(notification.Id), NotificationOptions(notification).ToValue()]).AsTask(), cancellationToken, notification);
    }
    public ValueTask<PlatformResult<PlatformUnit>> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        DesktopServiceValidation.Identifier(id);
        return ExecuteAsync(proxy => proxy.RemoveNotificationAsync(id).AsTask(), cancellationToken, remove: id);
    }
    private async ValueTask<PlatformResult<PlatformUnit>> ExecuteAsync(Func<Protocol.Notification, Task>? request,
        CancellationToken cancellationToken, DesktopNotification? notification = null, string? remove = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session?.OwnerChanged.IsCancellationRequested == true)
            {
                Diagnose("portal-notification-owner-changed", "The portal service was replaced.", "Reconnecting and registering before the next request; submitted notifications are not replayed.");
                await ResetConnectionAsync().ConfigureAwait(false);
            }
            if (_connection is null)
            {
                var session = await PortalConnection.OpenAsync(address, destination, _application, cancellationToken).ConfigureAwait(false);
                var connection = session.Connection;
                if (ApplicationId is null)
                    Diagnose("portal-notification-identity-unspecified", "No desktop application ID was supplied for notifications.", "Supply an installed reverse-DNS desktop ID; an unidentified host application may lose notification actions or history.");
                else if (_owner != connection) await OwnApplicationNameAsync(connection, ApplicationId, cancellationToken).ConfigureAwait(false);
                var actions = await connection.SubscribeAsync(new DBusSignalFilter
                {
                    Sender = session.Destination, Path = new DBusObjectPath("/org/freedesktop/portal/desktop"),
                    Interface = Protocol.Notification.InterfaceName, Member = "ActionInvoked",
                }, cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                _ = PortalValues.PumpAsync(actions, signal =>
                {
                    if (!session.OwnerChanged.IsCancellationRequested && signal.Arguments is [{ Signature.Value: "s" } id, { Signature.Value: "s" } action, ..])
                        OnAction(id.GetString(), action.GetString());
                }, static _ => { });
                _actions = actions;
                _connection = connection;
                _session = session;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (request is not null)
            {
                // Observe the actual reply after a native submission; cancellation only prevents queued work.
                DesktopNotification? previous = null;
                if (notification is not null) { _sent.TryGetValue(notification.Id, out previous); _sent[notification.Id] = notification; }
                try
                {
                    try { await request(new Protocol.Notification(_connection, _session!.Destination, "/org/freedesktop/portal/desktop"))
                        .WaitAsync(TimeSpan.FromSeconds(5), _session.OwnerChanged).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_session!.OwnerChanged.IsCancellationRequested)
                    { throw new NativeBackendUnavailableException(); }
                }
                catch
                {
                    if (notification is not null)
                    {
                        if (previous is not null) _sent[notification.Id] = previous;
                        else _sent.TryRemove(notification.Id, out _);
                    }
                    throw;
                }
                if (notification is not null)
                {
                    _recent.Remove(notification.Id); _recent.Add(notification.Id);
                    while (_recent.Count > RecentCapacity) { _sent.TryRemove(_recent[0], out _); _recent.RemoveAt(0); }
                }
                if (remove is not null) { _sent.TryRemove(remove, out _); _recent.Remove(remove); }
                Diagnose("portal-notification-request-accepted", "The portal accepted the notification request.", "Acceptance does not confirm popup delivery, history retention or action activation.");
            }
            else
            {
                var version = await new Protocol.Notification(_connection, _session!.Destination, "/org/freedesktop/portal/desktop").GetVersionAsync(cancellationToken)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                if (_session!.OwnerChanged.IsCancellationRequested) throw new NativeBackendUnavailableException();
                if (version < 1) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            }
            return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        }
        catch (DBusException error) when (error.ErrorName is "org.freedesktop.portal.Error.NotAllowed" or "org.freedesktop.DBus.Error.AccessDenied")
        { Diagnose("portal-notification-permission-denied", error.ErrorName, "Check application identity and desktop notification permissions."); return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied, PortalErrors.Reply(error)); }
        catch (NotificationCapacityException) { return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.ResourceBusy); }
        catch (Exception error) when (error is DBusException or TimeoutException or NativeBackendUnavailableException)
        {
            Diagnose("portal-notification-backend-unavailable", error is DBusException reply ? reply.ErrorName : error.GetType().Name,
                "Check session portal services and the installed desktop entry for the supplied application ID.");
            await ResetConnectionAsync().ConfigureAwait(false);
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        finally { _gate.Release(); }
    }
    private void OnAction(string id, string action)
    {
        if (!_sent.TryGetValue(id, out var notification) || action != "default" && !notification.Actions.Any(a => a.Id == action)) return;
        Raise(new DesktopNotificationActivation(id, action, notification.ActivationUri) { IsTrusted = true });
    }
    // org.freedesktop.Application.ActivateAction from the installed desktop entry. A notification this process showed,
    // with one of its actions, is trusted and carries the link the application gave it, whatever the target says. Any
    // other activation can come after a restart or from any process on the session bus (SEC-09): it is untrusted, and its
    // link is kept only where LaunchUriAsync could open it on Linux (http, https or mailto without attachments).
    private void OnInstalledAction(string id, string action, Uri? uri, DesktopActivationContext context)
    {
        if (_sent.TryGetValue(id, out var notification) && (action == "default" || notification.Actions.Any(a => a.Id == action)))
        {
            Raise(new DesktopNotificationActivation(id, action, notification.ActivationUri) { PlatformContext = context, IsTrusted = true });
            return;
        }
        Diagnose("portal-notification-activation-unmatched", "The desktop invoked an action of a notification this process did not show.",
            "The activation is untrusted: check its identifiers against the application's own state.");
        Raise(new DesktopNotificationActivation(id, action, UntrustedLink(uri)) { PlatformContext = context });
    }
    internal static Uri? UntrustedLink(Uri? uri)
    {
        if (uri is null) return null;
        try { DesktopUriPolicy.Require(uri); }
        catch (ArgumentException) { return null; }
        return DesktopUriPolicy.IsWindowsOnly(uri) ? null : uri;
    }
    private void Raise(DesktopNotificationActivation activation)
    {
        Diagnose("portal-notification-action-received", "The desktop invoked a notification action.", "Application observers are dispatched on a worker thread.");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (_disposed) return;
            foreach (EventHandler<DesktopNotificationActivation> observer in Activated?.GetInvocationList() ?? [])
                try { observer(this, activation); } catch { /* Native event delivery must survive an application observer. */ }
        });
    }
    // The application's name and activation object stay with their connection across portal restarts.
    private async Task OwnApplicationNameAsync(DBusConnection connection, string id, CancellationToken cancellationToken)
    {
        await ReleaseApplicationNameAsync().ConfigureAwait(false);
        IAsyncDisposable activation;
        // Another service of this process already answers for the name.
        try { activation = connection.Export(new DBusObjectPath("/" + id.Replace('.', '/').Replace('-', '_')), new ActivationObject(this)); }
        catch (InvalidOperationException) { throw new NotificationCapacityException(); }
        try
        {
            // The export above shows no other service of this process answers for the name, so a name this connection
            // already owns, from a request whose caller stopped waiting, is ours.
            var reply = await connection.RequestNameAsync(id, DBusRequestNameOptions.DoNotQueue, CancellationToken.None).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (reply is not (DBusRequestNameReply.PrimaryOwner or DBusRequestNameReply.AlreadyOwner)) throw new NotificationCapacityException();
        }
        catch
        {
            await activation.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _owner = connection;
        _activation = activation;
    }

    private async Task ReleaseApplicationNameAsync()
    {
        var (owner, activation) = (_owner, _activation);
        _owner = null; _activation = null;
        if (activation is not null) await activation.DisposeAsync().ConfigureAwait(false);
        if (owner is null || ApplicationId is null || owner.Disconnected.IsCancellationRequested) return;
        try { await owner.ReleaseNameAsync(ApplicationId).AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception error) when (error is DBusException or TimeoutException) { /* Closing the connection releases the name too. */ }
    }

    private async ValueTask ResetConnectionAsync()
    {
        var actions = _actions;
        _actions = null; _session = null; _connection = null;
        if (actions is not null) await actions.DisposeAsync().ConfigureAwait(false);
    }
    private void Diagnose(string code, string message, string remedy) => _application.Diagnose(code, message, remedy);
    // The XML describes this extensible dictionary only as a{sv}. Keep its
    // notification semantics handwritten; the generator owns the wire envelope.
    private PortalOptions NotificationOptions(DesktopNotification notification)
    {
        var options = new PortalOptions();
        options["title"] = DBusValue.From(notification.Title);
        options["body"] = DBusValue.From(notification.Body);
        options["default-action"] = DBusValue.From(ApplicationId is null ? "default" : "app.runic-notification");
        if (ApplicationId is not null)
            options["default-action-target"] = DBusValue.From(Target(notification, "default"));
        if (!notification.Actions.IsEmpty)
        {
            options["buttons"] = DBusValue.Array("a{sv}", notification.Actions.Select(action =>
            {
                var button = new List<KeyValuePair<string, DBusValue>>
                {
                    new("label", DBusValue.From(action.Label)),
                    new("action", DBusValue.From(ApplicationId is null ? action.Id : "app.runic-notification")),
                };
                if (ApplicationId is not null) button.Add(new("target", DBusValue.From(Target(notification, action.Id))));
                return DBusValue.VariantDictionary(button);
            }));
        }
        return options;
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await ResetConnectionAsync().ConfigureAwait(false);
            await ReleaseApplicationNameAsync().ConfigureAwait(false);
            _sent.Clear(); _recent.Clear(); Activated = null;
        }
        finally { _gate.Release(); }
    }
    private static string Target(DesktopNotification notification, string action) =>
        notification.Id + "\n" + action + "\n" + notification.ActivationUri?.AbsoluteUri;
    // org.freedesktop.Application at the application's object path; only ActivateAction of a notification is answered.
    private sealed class ActivationObject(PortalNotifications owner) : DBusObject
    {
        protected override ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context)
        {
            if (context.Interface != "org.freedesktop.Application" || context.Member != "ActivateAction")
                throw new DBusException(DBusException.UnknownMethod, "Only notification actions are exported here.");
            try
            {
                if (context.Signature.Value != "sava{sv}") throw new ArgumentException("Unexpected signature.");
                var action = context.Arguments[0].GetString();
                var parameters = context.Arguments[1].GetItems();
                var platformData = PortalValues.Variants(context.Arguments[2]);
                string? ContextString(string key) => platformData.TryGetValue(key, out var value) && value.Signature.Value == "s" ? value.GetString() : null;
                var activationContext = new DesktopActivationContext(ContextString("activation-token"), ContextString("desktop-startup-id"));
                if (action != "runic-notification" || parameters.Length != 1 || parameters[0].GetVariant() is not { Signature.Value: "s" } targetValue)
                    throw new ArgumentException("Unknown notification action.");
                var target = targetValue.GetString();
                if (target.Length > 2200) throw new ArgumentException("Notification target is too long.");
                var parts = target.Split('\n');
                if (parts.Length != 3) throw new ArgumentException("Invalid notification target.");
                DesktopServiceValidation.Identifier(parts[0]); DesktopServiceValidation.Identifier(parts[1]);
                Uri? uri = parts[2].Length == 0 ? null : new Uri(parts[2], UriKind.Absolute);
                if (uri is { IsFile: true }) throw new ArgumentException("File activation targets are not accepted.");
                // Installed activation can arrive after restart. Consumers validate the IDs against their durable state.
                owner.OnInstalledAction(parts[0], parts[1], uri, activationContext);
                return ValueTask.FromResult(ImmutableArray<DBusValue>.Empty);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException)
            { throw new DBusException(DBusException.InvalidArgs, "Invalid notification activation."); }
        }
    }
    private sealed class NotificationCapacityException : Exception;
}
