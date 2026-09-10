using Runic.Application.Platform.Desktop;
using Runic.Platform;
using Runic.Platform.Linux;
using Runic.Platform.MacOS;
using Runic.Platform.Windows;

// Opt-in interactive fixture. Also keeps all three providers' new ABI paths in the native AOT consumer.
internal static class DesktopServicesNativeSmoke
{
    internal static async Task RunAsync(DesktopNativeOwner owner, CancellationToken token)
    {
        var applicationId = Environment.GetEnvironmentVariable("RUNIC_TEST_APP_ID");
        var service = Environment.GetEnvironmentVariable("RUNIC_TEST_SERVICE") ?? "All";
        DesktopFileOperation? selectedOperation = service switch
        {
            "All" or "Notifications" => null,
            "Open" => DesktopFileOperation.Open,
            "ChooseApplication" => DesktopFileOperation.ChooseApplication,
            "Reveal" => DesktopFileOperation.Reveal,
            _ => throw new ArgumentException("RUNIC_TEST_SERVICE must be All, Notifications, Open, ChooseApplication or Reveal."),
        };
        IDesktopFileLauncher launcher;
        IDesktopNotifications notifications;
        if (OperatingSystem.IsWindows())
        {
            launcher = WindowsPlatformProvider.CreateFileLauncher(owner);
            notifications = WindowsPlatformProvider.CreateNotifications(applicationId ?? "Runic.Platform.Tests");
        }
        else if (OperatingSystem.IsMacOS())
        {
            launcher = MacOSPlatformProvider.CreateFileLauncher(owner);
            notifications = MacOSPlatformProvider.CreateNotifications();
        }
        else
        {
            launcher = LinuxPlatformProvider.CreateFileLauncher(owner);
            notifications = LinuxPlatformProvider.CreateNotifications(applicationId, diagnostic => Console.WriteLine($"Notification diagnostic: {diagnostic}"));
        }
        await using (notifications)
        {
            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            notifications.Activated += (_, activation) =>
            {
                Console.WriteLine($"Notification action: {activation}");
                if (activation.NotificationId == "native-services" && activation.ActionId == "open") activated.TrySetResult();
            };
            if (service == "Notifications")
            {
                RequireSuccess(await notifications.RequestPermissionAsync(token), "notification eligibility");
                try
                {
                    RequireSuccess(await notifications.ShowAsync(new("native-services", "Runic notification test", "Click Open result to verify live activation.")
                    { Actions = [new("open", "Open result")] }, token), "notification submission");
                    Console.WriteLine("Waiting for the actual Open result callback (120 seconds).");
                    await activated.Task.WaitAsync(TimeSpan.FromSeconds(120), token);
                    Console.WriteLine("PASS live notification activation.");
                }
                finally { RequireSuccess(await notifications.RemoveAsync("native-services", CancellationToken.None), "notification withdrawal"); }
                return;
            }
            if (selectedOperation is null)
            {
                RequireSuccess(await notifications.RequestPermissionAsync(token), "notification eligibility");
                RequireSuccess(await notifications.ShowAsync(new("native-services", "Runic desktop services", "Test the Open result action, then return to the terminal.")
                { Actions = [new("open", "Open result")] }, token), "notification submission");
            }
            string directory = Path.Combine(Path.GetTempPath(), "runic-desktop-services-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "result.txt");
                await File.WriteAllTextAsync(path, "Runic native file handoff smoke test.\n", token);
                foreach (var operation in selectedOperation is { } selected ? [selected] : Enum.GetValues<DesktopFileOperation>())
                {
                    Console.WriteLine($"Exercise {operation}; choose or dismiss the native application picker when it appears.");
                    var result = await launcher.LaunchAsync(path, operation, token);
                    Console.WriteLine($"{operation}: {result}");
                    if (operation == DesktopFileOperation.ChooseApplication && result is PlatformResult<Unit>.Failed { Code: FailureCode.UserDismissed })
                        Console.WriteLine("Picker dismissal reported by the native API.");
                    else RequireSuccess(result, operation.ToString());
                    if (OperatingSystem.IsWindows() && operation == DesktopFileOperation.ChooseApplication)
                        Console.WriteLine("Windows success acknowledges shell handling, including dismissal; confirm the visible outcome separately.");
                }
                Console.WriteLine("Inspect the notification action output and opened file-manager/application windows. Press Enter to withdraw the notification and finish.");
                var receipt = Environment.GetEnvironmentVariable("RUNIC_TEST_CONFIRM_FILE");
                if (receipt is null)
                {
                    if (await Console.In.ReadLineAsync(token) is null) throw new InvalidOperationException("No interactive confirmation was supplied.");
                }
                else
                {
                    // A coordinator creates this fresh test-owned file only after inspecting the UI.
                    if (File.Exists(receipt)) throw new InvalidOperationException("Confirmation file must not already exist.");
                    Console.WriteLine($"Waiting for UI confirmation file: {receipt}");
                    while (!File.Exists(receipt)) await Task.Delay(100, token);
                }
            }
            finally
            {
                try
                {
                    if (selectedOperation is null) RequireSuccess(await notifications.RemoveAsync("native-services", CancellationToken.None), "notification withdrawal");
                }
                finally { Directory.Delete(directory, recursive: true); }
            }
        }
    }
    private static void RequireSuccess(PlatformResult<Unit> result, string operation)
    {
        if (result is not PlatformResult<Unit>.Success) throw new InvalidOperationException($"{operation} failed: {result}");
        Console.WriteLine($"PASS {operation}: native API accepted the request.");
    }
}
