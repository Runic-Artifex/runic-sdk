using Runic.Platform;
using Runic.Platform.Windows;

// Opt-in installed-app checks. Normal CI runs the portable policy tests instead.
internal static class NotificationTests
{
    private const string NotificationId = "native-relaunch";

    internal static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Notifications require Windows.");
        using var watchdog = new Timer(_ => Environment.Exit(1), null, TimeSpan.FromSeconds(150), Timeout.InfiniteTimeSpan);
        try
        {
            if (args[0] == "--notification-activation")
            {
                if (args.Length != 3) throw new ArgumentException("Expected activation URI and a trusted receipt path.");
                var uri = new Uri(args[1]);
                if (uri.Scheme != "runic-p1-test" || uri.Host != "notification" || uri.AbsolutePath != "/" || uri.Fragment.Length != 0)
                    throw new ArgumentException("Unexpected activation target.");
                var values = uri.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2))
                    .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "");
                if (values.Count != 2 || !values.TryGetValue("runic-notification", out var id) || id != NotificationId ||
                    !values.TryGetValue("runic-action", out var action) || action is not ("default" or "open"))
                    throw new ArgumentException("Unexpected notification/action routing.");
                // The installer supplies the receipt path, never the URI payload. Reject replays.
                using var stream = new FileStream(Path.GetFullPath(args[2]), FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream);
                await writer.WriteLineAsync($"{Environment.ProcessId}\n{id}\n{action}");
                Console.WriteLine($"PASS protocol activation in process {Environment.ProcessId}: {id}/{action}");
                return 0;
            }

            string mode = args.Length == 2 ? args[1] : throw new ArgumentException("Choose submit, live, relaunch or remove.");
            if (mode is not ("submit" or "live" or "relaunch" or "remove")) throw new ArgumentException("Unknown notification mode.");
            string appId = Environment.GetEnvironmentVariable("RUNIC_TEST_APP_ID") ?? throw new ArgumentException("Set RUNIC_TEST_APP_ID to an installed test identity.");
            await using var notifications = WindowsPlatformProvider.CreateNotifications(appId);
            if (mode == "remove") { Require(await notifications.RemoveAsync(NotificationId), "remove"); return 0; }
            var activated = new TaskCompletionSource<DesktopNotificationActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
            notifications.Activated += (_, activation) => activated.TrySetResult(activation);
            Require(await notifications.RequestPermissionAsync(), "permission/eligibility");
            var notification = new DesktopNotification(NotificationId, "Runic Windows notification test", "Click Open result, or the notification body for the body-activation check.")
            {
                Actions = [new("open", "Open result")],
                ActivationUri = mode == "relaunch" ? new Uri("runic-p1-test://notification/") : null,
            };
            try
            {
                Require(await notifications.ShowAsync(notification), "first submission");
                // Same-ID replacement exercises native tag/group handling as well.
                Require(await notifications.ShowAsync(notification with { Title = "Runic Windows notification test (updated)" }), "replacement");
                Console.WriteLine($"SUBMITTED process={Environment.ProcessId} mode={mode}");
                Console.Out.Flush();
                if (mode == "live")
                {
                    var activation = await activated.Task.WaitAsync(TimeSpan.FromSeconds(120));
                    var expected = Environment.GetEnvironmentVariable("RUNIC_TEST_EXPECT_ACTION") ?? "open";
                    if (activation.NotificationId != NotificationId || activation.ActionId != expected)
                        throw new InvalidOperationException($"Unexpected activation: {activation}");
                    Console.WriteLine($"PASS live callback: {activation}");
                }
            }
            finally
            {
                // Relaunch deliberately leaves the delivered toast after this process exits.
                if (mode != "relaunch") Require(await notifications.RemoveAsync(NotificationId), "withdrawal");
            }
            Console.WriteLine("PASS notification API checks; relaunch mode still requires an external activation receipt.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Require(PlatformResult<Unit> result, string operation)
    {
        Console.WriteLine($"{operation}: {result}");
        if (result is not PlatformResult<Unit>.Success) throw new InvalidOperationException($"Notification {operation} failed: {result}");
    }
}
