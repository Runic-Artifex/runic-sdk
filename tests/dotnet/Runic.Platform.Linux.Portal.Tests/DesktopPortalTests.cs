using Microsoft.Win32.SafeHandles;
using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Tmds.DBus.Protocol;

internal static class DesktopPortalTests
{
    internal static async Task RunAsync()
    {
        using var connection = new DBusConnection(DBusAddress.Session!);
        await connection.ConnectAsync();
        var service = new DesktopPortalService(connection);
        connection.AddMethodHandler(service);
        await using var settings = new PortalDesktopSettings(destination: connection.UniqueName!);
        var result = await settings.ReadAsync();
        Check(result is PlatformResult<DesktopAppearance>.Success { Value.ColorScheme: DesktopColorScheme.Dark, Value.HighContrast: true, Value.ReducedMotion: null, Value.AccentColor.Red: 0.25 }, "native preference variants and unknown optional values");
        await using var notifications = new PortalNotifications(destination: connection.UniqueName!);
        var activated = new TaskCompletionSource<DesktopNotificationActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        notifications.Activated += (_, activation) => activated.TrySetResult(activation);
        Check(await notifications.RequestPermissionAsync() is PlatformResult<Unit>.Success, "notification backend capability");
        Check(await notifications.ShowAsync(new("saved", "Saved", "Body") { Actions = [new("open", "Open result")] }) is PlatformResult<Unit>.Success, "notification submission");
        Check((await activated.Task.WaitAsync(TimeSpan.FromSeconds(3))).ActionId == "open", "action arriving before method reply is routed");
        Check(await notifications.RemoveAsync("saved") is PlatformResult<Unit>.Success && service.Removed == "saved", "notification removal");
        service.Deny = true;
        Check(await notifications.ShowAsync(new("denied", "Title", "Body")) is PlatformResult<Unit>.Failed { Code: FailureCode.PermissionDenied }, "portal permission denial remains distinct");
        service.Deny = false;
        var path = System.IO.Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "file descriptor content");
            using var handle = File.OpenHandle(path);
            service.Version = 2;
            try
            {
                await new PortalTransport(destination: connection.UniqueName!, file: handle, ask: true).RequestAsync("x11:1234", "OpenFile", "", default);
                throw new InvalidOperationException("An old portal silently ignored application choice.");
            }
            catch (Runic.Platform.Runtime.NativeBackendUnavailableException) { }
            service.Version = 3;
            foreach (var method in new[] { "OpenFile", "OpenDirectory" })
            {
                var transport = new PortalTransport(destination: connection.UniqueName!, file: handle, ask: true);
                var handoff = await transport.RequestAsync("x11:1234", method, "", default);
                Check(handoff.Code == 0 && service.Ask && service.Parent == "x11:1234" && service.FileContents == "file descriptor content", "owned FD handoff and application chooser option");
            }
        }
        finally { File.Delete(path); }
        const string appId = "org.runic.PortalTests";
        await using var installed = new PortalNotifications(destination: connection.UniqueName!, applicationId: appId);
        var cold = new TaskCompletionSource<DesktopNotificationActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        installed.Activated += (_, activation) => cold.TrySetResult(activation);
        Check(await installed.RequestPermissionAsync() is PlatformResult<Unit>.Success, "installed application name acquired");
        await connection.CallMethodAsync(Activation(connection, appId));
        Check((await cold.Task.WaitAsync(TimeSpan.FromSeconds(3))) is { NotificationId: "previous-process", ActionId: "open" }, "activation without in-memory notification history");
        Console.WriteLine("PASS desktop portals: settings, notification permission/actions, installed activation and owned file descriptors.");
    }
    private static MessageBuffer Activation(DBusConnection connection, string destination)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: "/org/runic/PortalTests", @interface: "org.freedesktop.Application", member: "ActivateAction", signature: "sava{sv}");
        writer.WriteString("runic-notification");
        writer.WriteArray(new[] { VariantValue.String("previous-process\nopen\nrunic-test://result/1") });
        var data = writer.WriteDictionaryStart(); writer.WriteDictionaryEnd(data);
        return writer.CreateMessage();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
internal sealed class DesktopPortalService(DBusConnection connection) : IPathMethodHandler
{
    public string Path => "/org/freedesktop/portal/desktop";
    public bool HandlesChildPaths => true;
    internal string? Removed, Parent, FileContents;
    internal bool Ask, Deny;
    internal uint Version = 3;
    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        if (context.Request.MemberAsString == "ReadAll")
        {
            using var writer = context.CreateReplyWriter("a{sa{sv}}");
            var namespaces = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart(); writer.WriteString("org.freedesktop.appearance");
            var values = writer.WriteDictionaryStart();
            foreach (var (key, value) in new[] { ("color-scheme", VariantValue.UInt32(1)), ("contrast", VariantValue.UInt32(1)),
                ("reduced-motion", VariantValue.UInt32(999)), ("accent-color", VariantValue.Struct(VariantValue.Double(0.25), VariantValue.Double(0.5), VariantValue.Double(1))) })
            { writer.WriteDictionaryEntryStart(); writer.WriteString(key); writer.WriteVariant(value); }
            writer.WriteDictionaryEnd(values); writer.WriteDictionaryEnd(namespaces); context.Reply(writer.CreateMessage());
        }
        else if (context.Request.MemberAsString == "Get")
        { using var writer = context.CreateReplyWriter("v"); writer.WriteVariant(VariantValue.UInt32(Version)); context.Reply(writer.CreateMessage()); }
        else if (context.Request.MemberAsString == "AddNotification")
        {
            if (Deny) { context.ReplyError("org.freedesktop.portal.Error.NotAllowed", "Disabled by user"); return ValueTask.CompletedTask; }
            string id = reader.ReadString();
            using var signal = connection.GetMessageWriter();
            signal.WriteSignalHeader(path: Path, @interface: "org.freedesktop.portal.Notification", member: "ActionInvoked", signature: "ssav");
            signal.WriteString(id); signal.WriteString("open"); signal.WriteArray(System.Array.Empty<VariantValue>());
            connection.TrySendMessage(signal.CreateMessage());
            using var writer = context.CreateReplyWriter(null); context.Reply(writer.CreateMessage());
        }
        else if (context.Request.MemberAsString == "RemoveNotification")
        { Removed = reader.ReadString(); using var writer = context.CreateReplyWriter(null); context.Reply(writer.CreateMessage()); }
        else
        {
            Parent = reader.ReadString();
            using var handle = reader.ReadHandle<SafeFileHandle>();
            var bytes = new byte[64]; var length = RandomAccess.Read(handle, bytes, 0);
            FileContents = System.Text.Encoding.UTF8.GetString(bytes, 0, length);
            string token = ""; Ask = false;
            var options = reader.ReadDictionaryStart();
            while (reader.HasNext(options))
            { var key = reader.ReadString(); var value = reader.ReadVariantValue(); if (key == "handle_token") token = value.GetString(); if (key == "ask") Ask = value.GetBool(); }
            var requestPath = Path + "/request/" + context.Request.SenderAsString![1..].Replace('.', '_') + "/" + token;
            using var signal = connection.GetMessageWriter();
            signal.WriteSignalHeader(path: requestPath, @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
            signal.WriteUInt32(0); var data = signal.WriteDictionaryStart(); signal.WriteDictionaryEnd(data); connection.TrySendMessage(signal.CreateMessage());
            using var reply = context.CreateReplyWriter("o"); reply.WriteObjectPath(new ObjectPath(requestPath)); context.Reply(reply.CreateMessage());
        }
        return ValueTask.CompletedTask;
    }
}
