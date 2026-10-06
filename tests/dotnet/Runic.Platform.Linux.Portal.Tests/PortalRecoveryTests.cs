using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Tmds.DBus.Protocol;

internal static class PortalRecoveryTests
{
    internal static async Task RunAsync()
    {
        const string name = "org.runic.RecoveryPortal";
        const string appId = "org.runic.RecoveryApplication";
        using var first = new DBusConnection(DBusAddress.Session!);
        using var second = new DBusConnection(DBusAddress.Session!);
        using var third = new DBusConnection(DBusAddress.Session!);
        await first.ConnectAsync(); await second.ConnectAsync(); await third.ConnectAsync();
        var original = new DesktopPortalService(first) { RequiredIdentity = appId };
        var idleReplacement = new DesktopPortalService(second) { RequiredIdentity = appId };
        var replacement = new DesktopPortalService(third) { RequiredIdentity = appId };
        first.AddMethodHandler(original); second.AddMethodHandler(idleReplacement); third.AddMethodHandler(replacement);
        Check(await first.TryRequestNameAsync(name, RequestNameOptions.AllowReplacement), "original portal name");
        var application = new PortalApplication(appId);
        await using var notifications = new PortalNotifications(destination: name, application: application);
        Check(await notifications.ShowAsync(new("before", "Before restart", "Body")) is PlatformResult<PlatformUnit>.Success, "initial identified submission");
        Check(original.Calls is ["Register", "AddNotification"], "register before first portal method");

        // First replace an idle portal. A read-only capability probe permits the
        // asynchronous owner notification to settle without replaying submissions.
        // The previous owner is still alive, so a probe can succeed against it before
        // the owner change is observed. Wait until the client registered with the new owner.
        Check(await second.TryRequestNameAsync(name, RequestNameOptions.ReplaceExisting | RequestNameOptions.AllowReplacement), "idle portal replacement");
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            while (await notifications.RequestPermissionAsync(deadline.Token) is not PlatformResult<PlatformUnit>.Success
                || !idleReplacement.Calls.Contains("Register"))
                await Task.Delay(10, deadline.Token);
        }
        Check(await notifications.ShowAsync(new("idle-after", "After idle restart", "Body")) is PlatformResult<PlatformUnit>.Success, "idle service recovers without application restart");
        Check(idleReplacement.Calls.Count(x => x == "Register") == 1 && idleReplacement.Calls.Count(x => x == "AddNotification") == 1, "idle recovery registers once and does not replay the previous notification");

        // Keep the old process and session bus alive: only replace the service owner.
        // An in-flight request must fail rather than being sent again to its successor.
        idleReplacement.HoldNotification = true;
        var pending = notifications.ShowAsync(new("uncertain", "In flight", "Body")).AsTask();
        await idleReplacement.NotificationHeld.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(await third.TryRequestNameAsync(name, RequestNameOptions.ReplaceExisting | RequestNameOptions.AllowReplacement), "portal replacement");
        Check(await pending.WaitAsync(TimeSpan.FromSeconds(3)) is PlatformResult<PlatformUnit>.Unavailable, "in-flight owner loss reported without replay");
        Check(replacement.Calls.Count == 0, "uncertain submission was not replayed");
        idleReplacement.ReleaseNotification.TrySetResult();
        var recoveredAction = new TaskCompletionSource<DesktopNotificationActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        notifications.Activated += (_, activation) => { if (activation.NotificationId == "after") recoveredAction.TrySetResult(activation); };
        Check(await notifications.ShowAsync(new("after", "After restart", "Body") { Actions = [new("open", "Open result")] }) is PlatformResult<PlatformUnit>.Success, "same notification service recovers on next operation");
        Check((await recoveredAction.Task.WaitAsync(TimeSpan.FromSeconds(3))).ActionId == "open", "replacement action subscription is live");
        Check(replacement.Calls is ["Register", "AddNotification"], "replacement registered before submission");
        Check(await notifications.RemoveAsync("after") is PlatformResult<PlatformUnit>.Success, "withdraw after replacement");
        Check(replacement.Calls.Count(x => x == "Register") == 1, "unchanged owner does not register twice");

        // The same immutable identity is also used by fresh settings and FD calls.
        await using var settings = new PortalDesktopSettings(destination: name, application: application);
        Check(await settings.ReadAsync() is PlatformResult<DesktopAppearance>.Success, "settings use shared identity");
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "shared identity");
            using var handle = File.OpenHandle(file);
            var response = await new PortalTransport(destination: name, file: handle, application: application)
                .RequestAsync("x11:1234", "OpenFile", "", default);
            Check(response.Code == 0 && replacement.FileContents == "shared identity", "FD operation uses shared identity");
        }
        finally { File.Delete(file); }
        foreach (var method in new[] { "OpenURI", "OpenFile" })
        {
            var response = await new PortalTransport(destination: name, application: application)
                .RequestAsync("x11:1234", method, "https://example.org/", default);
            Check(response.Code == 0, "URI and chooser operations use shared identity");
        }
        Check(replacement.RegisteredPeers.Count == 5 && replacement.RegisteredPeers.Values.All(x => x == appId), "five owned connections share one application identity");
        Check(replacement.UnidentifiedCalls == 0, "no unidentified portal calls crossed the backend boundary");

        // The retained settings connection follows a replaced portal owner.
        using var fourth = new DBusConnection(DBusAddress.Session!);
        await fourth.ConnectAsync();
        var settingsReplacement = new DesktopPortalService(fourth) { RequiredIdentity = appId };
        fourth.AddMethodHandler(settingsReplacement);
        Check(await fourth.TryRequestNameAsync(name, RequestNameOptions.ReplaceExisting), "settings portal replacement");
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            while (await settings.ReadAsync(deadline.Token) is not PlatformResult<DesktopAppearance>.Success || settingsReplacement.ReadAllSenders.Count == 0)
                await Task.Delay(10, deadline.Token);
        }
        Check(settingsReplacement.Calls is ["Register", "ReadAll"], "settings re-register with the replacement before reading");
        Console.WriteLine("PASS portal restart: owner-bound in-flight failure, no replay, re-registration, and identity across notifications/settings/FD handoff.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
