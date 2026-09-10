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
        return ExecuteAsync(connection =>
        {
            if (_sent.Count >= 64 && !_sent.ContainsKey(notification.Id)) throw new NotificationCapacityException();
            return Add(connection, notification);
        }, cancellationToken, notification);
    }
    public ValueTask<PlatformResult<Unit>> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        DesktopServiceValidation.Identifier(id);
        return ExecuteAsync(connection => Remove(connection, id), cancellationToken, remove: id);
    }
    private async ValueTask<PlatformResult<Unit>> ExecuteAsync(Func<DBusConnection, MessageBuffer>? request,
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
                        await connection.CallMethodAsync(Register(connection, applicationId))
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
                    _actions = await connection.AddMatchAsync(new MatchRule
                    {
                        Type = MessageType.Signal,
                        Sender = destination,
                        Path = "/org/freedesktop/portal/desktop",
                        Interface = "org.freedesktop.portal.Notification",
                        Member = "ActionInvoked"
                    },
                        static (message, _) => { var r = message.GetBodyReader(); return (r.ReadString(), r.ReadString()); },
                        value => { if (value.HasValue) OnAction(value.Value.Item1, value.Value.Item2); },
                        emitOnCapturedContext: false).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    _connection = connection;
                }
                catch { connection.Dispose(); throw; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (request is not null)
            {
                // Observe the actual reply after a native submission; cancellation only prevents queued work.
                var message = request(_connection);
                DesktopNotification? previous = null;
                if (notification is not null) { _sent.TryGetValue(notification.Id, out previous); _sent[notification.Id] = notification; }
                try { await _connection.CallMethodAsync(message).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
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
                var version = await _connection.CallMethodAsync(PermissionRequest(_connection), static (m, _) => m.GetBodyReader().ReadVariantValue().GetUInt32())
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
    private MessageBuffer Register(DBusConnection connection, string id)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: "/org/freedesktop/portal/desktop",
            @interface: "org.freedesktop.host.portal.Registry", member: "Register", signature: "sa{sv}");
        writer.WriteString(id);
        var options = writer.WriteDictionaryStart(); writer.WriteDictionaryEnd(options);
        return writer.CreateMessage();
    }
    private MessageBuffer Add(DBusConnection connection, DesktopNotification notification)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: "/org/freedesktop/portal/desktop",
            @interface: "org.freedesktop.portal.Notification", member: "AddNotification", signature: "sa{sv}");
        writer.WriteString(notification.Id);
        var options = writer.WriteDictionaryStart();
        foreach (var (name, value) in new[] { ("title", notification.Title), ("body", notification.Body), ("default-action", applicationId is null ? "default" : "app.runic-notification") })
        { writer.WriteDictionaryEntryStart(); writer.WriteString(name); writer.WriteVariant(VariantValue.String(value)); }
        if (applicationId is not null)
        { writer.WriteDictionaryEntryStart(); writer.WriteString("default-action-target"); writer.WriteVariant(VariantValue.String(Target(notification, "default"))); }
        if (!notification.Actions.IsEmpty)
        {
            writer.WriteDictionaryEntryStart(); writer.WriteString("buttons");
            var buttons = new Array<Dict<string, VariantValue>>();
            foreach (var action in notification.Actions)
            {
                var button = new Dict<string, VariantValue> { { "label", VariantValue.String(action.Label) },
                    { "action", VariantValue.String(applicationId is null ? action.Id : "app.runic-notification") } };
                if (applicationId is not null) button.Add("target", VariantValue.String(Target(notification, action.Id)));
                buttons.Add(button);
            }
            writer.WriteVariant(buttons);
        }
        writer.WriteDictionaryEnd(options);
        return writer.CreateMessage();
    }
    private MessageBuffer Remove(DBusConnection connection, string id)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: "/org/freedesktop/portal/desktop",
            @interface: "org.freedesktop.portal.Notification", member: "RemoveNotification", signature: "s");
        writer.WriteString(id); return writer.CreateMessage();
    }
    private MessageBuffer PermissionRequest(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: "/org/freedesktop/portal/desktop",
            @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss");
        writer.WriteString("org.freedesktop.portal.Notification"); writer.WriteString("version");
        return writer.CreateMessage();
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
