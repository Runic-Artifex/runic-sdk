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
        service.Calls.Clear();
        Check(await installed.RequestPermissionAsync() is PlatformResult<Unit>.Success, "installed application name acquired");
        Check(service.RegisteredId == appId && service.Calls is ["Register", "Get"], "host identity registered before notification capability call");
        await connection.CallMethodAsync(Activation(connection, appId));
        var received = await cold.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(received is { NotificationId: "previous-process", ActionId: "open", PlatformContext: { ActivationToken: "test-wayland-token", StartupId: "test-x11-id" } }, "cold activation preserves platform context across worker dispatch");
        Check(!received.ToString().Contains("test-wayland-token", StringComparison.Ordinal), "activation diagnostics do not expose focus tokens");
        foreach (var (error, denied) in new[] { ("org.freedesktop.portal.Error.Failed", false), ("org.freedesktop.portal.Error.NotAllowed", true) })
        {
            service.RegistrationError = error;
            service.Calls.Clear();
            var diagnostics = new List<string>();
            await using var invalidIdentity = new PortalNotifications(destination: connection.UniqueName!, applicationId: "org.runic.MissingIdentity",
                diagnosticSink: diagnostic => { diagnostics.Add(diagnostic.Code); throw new InvalidOperationException("observer failure"); });
            var rejected = await invalidIdentity.ShowAsync(new("rejected", "Title", "Body"));
            Check(denied ? rejected is PlatformResult<Unit>.Failed { Code: FailureCode.PermissionDenied }
                : rejected is PlatformResult<Unit>.Unavailable { Reason: UnavailableReason.BackendUnavailable }, "registration failure remains typed despite diagnostic observer failure");
            Check(service.Calls is ["Register"] && diagnostics.Count == 2 && diagnostics[0] == "portal-identity-registration-failed", "failed identity registration prevents notification submission and emits identity and operation diagnostics");
        }
        service.Calls.Clear();
        var sharedDiagnostics = new List<PortalDiagnostic>();
        await using var invalidSettings = new PortalDesktopSettings(destination: connection.UniqueName!,
            application: new PortalApplication("org.runic.MissingIdentity", diagnostic =>
            { sharedDiagnostics.Add(diagnostic); throw new InvalidOperationException("observer failure"); }));
        Check(await invalidSettings.ReadAsync() is PlatformResult<DesktopAppearance>.Unavailable { Reason: UnavailableReason.BackendUnavailable },
            "shared registration failure remains typed despite diagnostic observer failure");
        Check(service.Calls is ["Register"] && sharedDiagnostics is [{ Code: "portal-identity-registration-failed" }],
            "shared registration failure prevents requests and emits one actionable diagnostic");
        service.RegistrationError = null;
        Console.WriteLine("PASS desktop portals: settings, notification permission/actions, installed activation and owned file descriptors.");
    }
    private static MessageBuffer Activation(DBusConnection connection, string destination)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: "/org/runic/PortalTests", @interface: "org.freedesktop.Application", member: "ActivateAction", signature: "sava{sv}");
        writer.WriteString("runic-notification");
        writer.WriteArray(new[] { VariantValue.String("previous-process\nopen\nrunic-test://result/1") });
        var data = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart(); writer.WriteString("activation-token"); writer.WriteVariant(VariantValue.String("test-wayland-token"));
        writer.WriteDictionaryEntryStart(); writer.WriteString("desktop-startup-id"); writer.WriteVariant(VariantValue.String("test-x11-id"));
        writer.WriteDictionaryEnd(data);
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
    internal string? RegisteredId;
    internal string? RegistrationError;
    internal List<string> Calls = [];
    internal string? RequiredIdentity;
    internal Dictionary<string, string> RegisteredPeers = [];
    internal int UnidentifiedCalls;
    internal bool HoldNotification;
    internal TaskCompletionSource NotificationHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        Calls.Add(context.Request.MemberAsString!);
        if (context.Request.MemberAsString != "Register" && RequiredIdentity is { } expected
            && (!RegisteredPeers.TryGetValue(context.Request.SenderAsString!, out var actual) || actual != expected))
        {
            UnidentifiedCalls++;
            context.ReplyError("org.freedesktop.portal.Error.NotAllowed", "Unidentified connection");
            return ValueTask.CompletedTask;
        }
        if (context.Request.MemberAsString == "Register")
        {
            if (RegistrationError is { } error) { context.ReplyError(error, "Registration failed"); return ValueTask.CompletedTask; }
            RegisteredId = reader.ReadString();
            if (!RegisteredPeers.TryAdd(context.Request.SenderAsString!, RegisteredId))
            { context.ReplyError("org.freedesktop.portal.Error.Failed", "Already registered"); return ValueTask.CompletedTask; }
            var options = reader.ReadDictionaryStart();
            CheckRegistrationOptions(reader.HasNext(options));
            using var writer = context.CreateReplyWriter(null); context.Reply(writer.CreateMessage());
        }
        else if (context.Request.MemberAsString == "ReadAll")
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
            if (HoldNotification) return HoldAsync(context);
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
            if (context.Request.SignatureAsString == "sha{sv}")
            {
                using var handle = reader.ReadHandle<SafeFileHandle>();
                var bytes = new byte[64]; var length = RandomAccess.Read(handle, bytes, 0);
                FileContents = System.Text.Encoding.UTF8.GetString(bytes, 0, length);
            }
            else _ = reader.ReadString(); // URI or picker title.
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
    private async ValueTask HoldAsync(MethodContext context)
    {
        NotificationHeld.TrySetResult();
        await ReleaseNotification.Task;
        using var writer = context.CreateReplyWriter(null); context.Reply(writer.CreateMessage());
    }
    private static void CheckRegistrationOptions(bool hasOptions)
    { if (hasOptions) throw new InvalidOperationException("Unexpected registry options."); }
}
