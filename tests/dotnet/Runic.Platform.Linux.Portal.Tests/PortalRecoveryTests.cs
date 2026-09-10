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
        await first.ConnectAsync(); await second.ConnectAsync();
        var original = new DesktopPortalService(first) { RequiredIdentity = appId };
        var replacement = new DesktopPortalService(second) { RequiredIdentity = appId };
        first.AddMethodHandler(original); second.AddMethodHandler(replacement);
        Check(await first.TryRequestNameAsync(name, RequestNameOptions.AllowReplacement), "original portal name");
        var application = new PortalApplication(appId);
        await using var notifications = new PortalNotifications(destination: name, application: application);
        Check(await notifications.ShowAsync(new("before", "Before restart", "Body")) is PlatformResult<Unit>.Success, "initial identified submission");
        Check(original.Calls is ["Register", "AddNotification"], "register before first portal method");

        // Keep the old process and session bus alive: only replace the service owner.
        // An in-flight request must fail rather than being sent again to its successor.
        original.HoldNotification = true;
        var pending = notifications.ShowAsync(new("uncertain", "In flight", "Body")).AsTask();
        await original.NotificationHeld.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(await second.TryRequestNameAsync(name, RequestNameOptions.ReplaceExisting | RequestNameOptions.AllowReplacement), "portal replacement");
        Check(await pending.WaitAsync(TimeSpan.FromSeconds(3)) is PlatformResult<Unit>.Unavailable, "in-flight owner loss reported without replay");
        Check(replacement.Calls.Count == 0, "uncertain submission was not replayed");
        original.ReleaseNotification.TrySetResult();
        Check(await notifications.ShowAsync(new("after", "After restart", "Body")) is PlatformResult<Unit>.Success, "same notification service recovers on next operation");
        Check(replacement.Calls is ["Register", "AddNotification"], "replacement registered before submission");
        Check(await notifications.RemoveAsync("after") is PlatformResult<Unit>.Success, "withdraw after replacement");
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
        Console.WriteLine("PASS portal restart: owner-bound in-flight failure, no replay, re-registration, and identity across notifications/settings/FD handoff.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
