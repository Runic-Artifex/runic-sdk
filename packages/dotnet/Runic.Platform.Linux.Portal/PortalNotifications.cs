using System.Collections.Concurrent;
using Runic.Platform.Runtime;
using Tmds.DBus.Protocol;

namespace Runic.Platform.Linux.Portal;

internal sealed class PortalNotifications(string? address = null, string destination = "org.freedesktop.portal.Desktop", string? applicationId = null,
    Action<PortalDiagnostic>? diagnosticSink = null) : IDesktopNotifications
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, DesktopNotification> _sent = new(StringComparer.Ordinal);
    private DBusConnection? _connection;
    private IDisposable? _actions;
    private volatile bool _disposed;
    public event EventHandler<DesktopNotificationActivation>? Activated;

    public ValueTask<PlatformResult<Unit>> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(null, cancellationToken); // The portal owns permission policy; no separate authorization method exists.
    public ValueTask<PlatformResult<Unit>> ShowAsync(DesktopNotification notification, CancellationToken cancellationToken = default)
    {
        DesktopServiceValidation.Notification(notification);
        return ExecuteAsync(proxy => proxy.AddNotificationAsync(notification.Id, NotificationOptions(notification)), cancellationToken, notification);
    }
    public ValueTask<PlatformResult<Unit>> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        DesktopServiceValidation.Identifier(id);
        return ExecuteAsync(proxy => proxy.RemoveNotificationAsync(id), cancellationToken, remove: id);
    }
    private async ValueTask<PlatformResult<Unit>> ExecuteAsync(Func<Protocol.Notification, Task>? request,
        CancellationToken cancellationToken, DesktopNotification? notification = null, string? remove = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is null)
            {
                var connection = new DBusConnection(address ?? DBusAddress.Session ?? throw new NativeBackendUnavailableException());
                try
                {
                    await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    // Owning a well-known bus name alone does not associate this connection
                    // with a desktop application in xdg-desktop-portal.
                    if (applicationId is not null && !File.Exists("/.flatpak-info") && Environment.GetEnvironmentVariable("SNAP") is null)
                    {
                        await new Protocol.Registry(connection, destination, "/org/freedesktop/portal/desktop").RegisterAsync(applicationId, new())
                            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                        Diagnose("portal-notification-identity-registered", "The notification connection registered its desktop identity.", "The matching desktop entry controls application attribution.");
                    }
                    else if (applicationId is null)
                        Diagnose("portal-notification-identity-unspecified", "No desktop application ID was supplied for notifications.", "Supply an installed reverse-DNS desktop ID; an unidentified host application may lose notification actions or history.");
                    if (applicationId is not null)
                    {
                        connection.AddMethodHandler(new ActivationHandler(this, applicationId));
                        if (!await connection.TryRequestNameAsync(applicationId, RequestNameOptions.None).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false))
                            throw new NotificationCapacityException();
                    }
                    _actions = await new Protocol.Notification(connection, destination, "/org/freedesktop/portal/desktop")
                        .WatchActionInvokedAsync(value => OnAction(value.Id, value.Action), emitOnCapturedContext: false)
                        .AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    _connection = connection;
                }
                catch { connection.Dispose(); throw; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (request is not null)
            {
                // Observe the actual reply after a native submission; cancellation only prevents queued work.
                if (notification is not null && _sent.Count >= 64 && !_sent.ContainsKey(notification.Id)) throw new NotificationCapacityException();
                DesktopNotification? previous = null;
                if (notification is not null) { _sent.TryGetValue(notification.Id, out previous); _sent[notification.Id] = notification; }
                try { await request(new Protocol.Notification(_connection, destination, "/org/freedesktop/portal/desktop")).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch
                {
                    if (notification is not null)
                    {
                        if (previous is not null) _sent[notification.Id] = previous;
                        else _sent.TryRemove(notification.Id, out _);
                    }
                    throw;
                }
                if (remove is not null) _sent.TryRemove(remove, out _);
                Diagnose("portal-notification-request-accepted", "The portal accepted the notification request.", "Acceptance does not confirm popup delivery, history retention or action activation.");
            }
            else
            {
                var version = await new Protocol.Notification(_connection, destination, "/org/freedesktop/portal/desktop").GetVersionAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                if (version < 1) return new PlatformResult<Unit>.Unavailable(UnavailableReason.BackendUnavailable);
            }
            return new PlatformResult<Unit>.Success(new Unit());
        }
        catch (DBusErrorReplyException error) when (error.ErrorName is "org.freedesktop.portal.Error.NotAllowed" or "org.freedesktop.DBus.Error.AccessDenied")
        { Diagnose("portal-notification-permission-denied", error.ErrorName, "Check application identity and desktop notification permissions."); return new PlatformResult<Unit>.Failed(FailureCode.PermissionDenied); }
        catch (NotificationCapacityException) { return new PlatformResult<Unit>.Failed(FailureCode.ResourceBusy); }
        catch (Exception error) when (error is DBusExceptionBase or TimeoutException or NativeBackendUnavailableException)
        {
            Diagnose("portal-notification-backend-unavailable", error is DBusErrorReplyException reply ? reply.ErrorName : error.GetType().Name,
                "Check session portal services and the installed desktop entry for the supplied application ID.");
            _actions?.Dispose(); _actions = null; _connection?.Dispose(); _connection = null;
            return new PlatformResult<Unit>.Unavailable(UnavailableReason.BackendUnavailable);
        }
        finally { _gate.Release(); }
    }
    private void OnAction(string id, string action)
    {
        if (!_sent.TryGetValue(id, out var notification) || action != "default" && !notification.Actions.Any(a => a.Id == action)) return;
        Raise(new DesktopNotificationActivation(id, action, notification.ActivationUri));
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
    private void Diagnose(string code, string message, string remedy)
    {
        try { diagnosticSink?.Invoke(new PortalDiagnostic(code, message, remedy)); }
        catch { /* Diagnostic observers cannot change platform outcomes. */ }
    }
    // The XML describes this extensible dictionary only as a{sv}. Keep its
    // notification semantics handwritten; the generator owns the wire envelope.
    private Dictionary<string, VariantValue> NotificationOptions(DesktopNotification notification)
    {
        var options = new Dictionary<string, VariantValue>
        {
            ["title"] = VariantValue.String(notification.Title),
            ["body"] = VariantValue.String(notification.Body),
            ["default-action"] = VariantValue.String(applicationId is null ? "default" : "app.runic-notification"),
        };
        if (applicationId is not null)
            options["default-action-target"] = VariantValue.String(Target(notification, "default"));
        if (!notification.Actions.IsEmpty)
        {
            var buttons = new Array<Dict<string, VariantValue>>();
            foreach (var action in notification.Actions)
            {
                var button = new Dict<string, VariantValue> { { "label", VariantValue.String(action.Label) },
                    { "action", VariantValue.String(applicationId is null ? action.Id : "app.runic-notification") } };
                if (applicationId is not null) button.Add("target", VariantValue.String(Target(notification, action.Id)));
                buttons.Add(button);
            }
            options["buttons"] = buttons;
        }
        return options;
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; _actions?.Dispose(); _connection?.Dispose(); _sent.Clear(); Activated = null; }
        finally { _gate.Release(); }
    }
    private static string Target(DesktopNotification notification, string action) =>
        notification.Id + "\n" + action + "\n" + notification.ActivationUri?.AbsoluteUri;
    private sealed class ActivationHandler(PortalNotifications owner, string id) : IPathMethodHandler
    {
        public string Path => "/" + id.Replace('.', '/').Replace('-', '_');
        public bool HandlesChildPaths => false;
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request.InterfaceAsString != "org.freedesktop.Application" || context.Request.MemberAsString != "ActivateAction")
            { context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", "Only notification actions are exported here."); return ValueTask.CompletedTask; }
            try
            {
                var reader = context.Request.GetBodyReader();
                var action = reader.ReadString();
                var parameters = reader.ReadArrayOfVariantValue();
                if (action != "runic-notification" || parameters.Length != 1 || parameters[0].Type != VariantValueType.String)
                    throw new ArgumentException("Unknown notification action.");
                var target = parameters[0].GetString();
                if (target.Length > 2200) throw new ArgumentException("Notification target is too long.");
                var parts = target.Split('\n');
                if (parts.Length != 3) throw new ArgumentException("Invalid notification target.");
                DesktopServiceValidation.Identifier(parts[0]); DesktopServiceValidation.Identifier(parts[1]);
                Uri? uri = parts[2].Length == 0 ? null : new Uri(parts[2], UriKind.Absolute);
                if (uri is { IsFile: true }) throw new ArgumentException("File activation targets are not accepted.");
                // Installed activation can arrive after restart. Consumers validate the IDs against their durable state.
                owner.Raise(new(parts[0], parts[1], uri));
                using var reply = context.CreateReplyWriter(null); context.Reply(reply.CreateMessage());
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException)
            { context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "Invalid notification activation."); }
            return ValueTask.CompletedTask;
        }
    }
    private sealed class NotificationCapacityException : Exception;
}
