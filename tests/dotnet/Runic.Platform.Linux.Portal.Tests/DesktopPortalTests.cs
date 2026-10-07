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
        using (var watching = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            await using var watch = settings.WatchAsync(watching.Token).GetAsyncEnumerator(watching.Token);
            Check(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopAppearance>.Success { Value.ColorScheme: DesktopColorScheme.Dark }, "initial watched preference");
            // Wait past the polling interval: a subscribed watcher must not read again until a change.
            await Task.Delay(1500);
            int reads = service.Calls.Count(call => call == "ReadAll");
            await Task.Delay(1500);
            Check(service.Calls.Count(call => call == "ReadAll") == reads, "subscribed settings watch does not poll");
            service.ColorScheme = 2;
            using (var signal = connection.GetMessageWriter())
            {
                signal.WriteSignalHeader(path: "/org/freedesktop/portal/desktop", @interface: "org.freedesktop.portal.Settings", member: "SettingChanged", signature: "ssv");
                signal.WriteString("org.freedesktop.appearance"); signal.WriteString("color-scheme"); signal.WriteVariant(VariantValue.UInt32(2));
                connection.TrySendMessage(signal.CreateMessage());
            }
            var changed = watch.MoveNextAsync().AsTask();
            Check(await changed.WaitAsync(TimeSpan.FromMilliseconds(900)) && watch.Current is PlatformResult<DesktopAppearance>.Success { Value.ColorScheme: DesktopColorScheme.Light },
                "SettingChanged wakes the watcher before a polling interval");
            Check(service.ReadAllSenders.Count == 1, "settings reads and watch share one portal connection");
            service.ColorScheme = 1;
        }
        await using var notifications = new PortalNotifications(destination: connection.UniqueName!);
        var activated = new TaskCompletionSource<DesktopNotificationActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        notifications.Activated += (_, activation) => activated.TrySetResult(activation);
        Check(await notifications.RequestPermissionAsync() is PlatformResult<PlatformUnit>.Success, "notification backend capability");
        Check(await notifications.ShowAsync(new("saved", "Saved", "Body") { Actions = [new("open", "Open result")] }) is PlatformResult<PlatformUnit>.Success, "notification submission");
        Check((await activated.Task.WaitAsync(TimeSpan.FromSeconds(3))).ActionId == "open", "action arriving before method reply is routed");
        Check(await notifications.RemoveAsync("saved") is PlatformResult<PlatformUnit>.Success && service.Removed == "saved", "notification removal");
        for (int i = 0; i < 80; i++)
            Check(await notifications.ShowAsync(new($"bulk-{i}", "Bulk", "Body")) is PlatformResult<PlatformUnit>.Success, "unremoved notifications evict the oldest routing entry");
        var latest = new TaskCompletionSource<DesktopNotificationActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        notifications.Activated += (_, activation) => { if (activation.NotificationId == "latest") latest.TrySetResult(activation); };
        Check(await notifications.ShowAsync(new("latest", "Latest", "Body") { Actions = [new("open", "Open result")] }) is PlatformResult<PlatformUnit>.Success, "submission after eviction");
        Check((await latest.Task.WaitAsync(TimeSpan.FromSeconds(3))).ActionId == "open", "recent notification actions still route after eviction");
        service.Deny = true;
        Check(await notifications.ShowAsync(new("denied", "Title", "Body")) is PlatformResult<PlatformUnit>.Failed
        {
            Code: PlatformFailureCode.PermissionDenied,
            Diagnostic: { Domain: "org.freedesktop.portal.Error.NotAllowed", Code: 0, Message: null },
        }, "portal permission denial remains distinct and carries the D-Bus error");
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
                Check(service.Call == $"org.freedesktop.portal.OpenURI.{method}(sha{{sv}})" && service.VersionInterface == "org.freedesktop.portal.OpenURI",
                    "FD operations use the OpenURI interface and its version guard");
                Check(RandomAccess.Read(handle, new byte[4], 0) == 4, "the caller's file handle stays open after the handoff");
            }
        }
        finally { File.Delete(path); }
        const string appId = "org.runic.PortalTests";
        await using var installed = new PortalNotifications(destination: connection.UniqueName!, applicationId: appId);
        var cold = new TaskCompletionSource<DesktopNotificationActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        installed.Activated += (_, activation) => cold.TrySetResult(activation);
        service.Calls.Clear();
        Check(await installed.RequestPermissionAsync() is PlatformResult<PlatformUnit>.Success, "installed application name acquired");
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
            Check(denied ? rejected is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }
                : rejected is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "registration failure remains typed despite diagnostic observer failure");
            Check(service.Calls is ["Register"] && diagnostics.Count == 2 && diagnostics[0] == "portal-identity-registration-failed", "failed identity registration prevents notification submission and emits identity and operation diagnostics");
        }
        service.Calls.Clear();
        var sharedDiagnostics = new List<PortalDiagnostic>();
        await using var invalidSettings = new PortalDesktopSettings(destination: connection.UniqueName!,
            application: new PortalApplication("org.runic.MissingIdentity", diagnostic =>
            { sharedDiagnostics.Add(diagnostic); throw new InvalidOperationException("observer failure"); }));
        Check(await invalidSettings.ReadAsync() is PlatformResult<DesktopAppearance>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
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
    internal string? Removed, Parent, FileContents, Call, VersionInterface;
    internal bool Ask, Deny;
    internal uint Version = 3;
    internal string? RegisteredId;
    internal string? RegistrationError;
    internal List<string> Calls = [];
    internal string? RequiredIdentity;
    internal Dictionary<string, string> RegisteredPeers = [];
    internal int UnidentifiedCalls;
    internal uint ColorScheme = 1;
    internal HashSet<string> ReadAllSenders = [];
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
            ReadAllSenders.Add(context.Request.SenderAsString!);
            foreach (var (key, value) in new[] { ("color-scheme", VariantValue.UInt32(ColorScheme)), ("contrast", VariantValue.UInt32(1)),
                ("reduced-motion", VariantValue.UInt32(999)), ("accent-color", VariantValue.Struct(VariantValue.Double(0.25), VariantValue.Double(0.5), VariantValue.Double(1))),
                ("color-scheme", VariantValue.UInt32(ColorScheme)) }) // A repeated key must not fail the read.
            { writer.WriteDictionaryEntryStart(); writer.WriteString(key); writer.WriteVariant(value); }
            writer.WriteDictionaryEnd(values); writer.WriteDictionaryEnd(namespaces); context.Reply(writer.CreateMessage());
        }
        else if (context.Request.MemberAsString == "Get")
        { VersionInterface = reader.ReadString(); using var writer = context.CreateReplyWriter("v"); writer.WriteVariant(VariantValue.UInt32(Version)); context.Reply(writer.CreateMessage()); }
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
            Call = $"{context.Request.InterfaceAsString}.{context.Request.MemberAsString}({context.Request.SignatureAsString})";
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
