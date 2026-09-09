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
            notifications = LinuxPlatformProvider.CreateNotifications(applicationId);
        }
        await using (notifications)
        {
            notifications.Activated += (_, activation) => Console.WriteLine($"Notification action: {activation}");
            var permission = await notifications.RequestPermissionAsync(token);
            Console.WriteLine($"Notification authorization/backend: {permission}");
            if (permission is PlatformResult<Unit>.Success)
                Console.WriteLine($"Notification submission: {await notifications.ShowAsync(new("native-services", "Runic desktop services", "Test the Open result action, then return to the terminal.") { Actions = [new("open", "Open result")] }, token)}");
            string directory = Path.Combine(Path.GetTempPath(), "runic-desktop-services-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "result.txt");
                await File.WriteAllTextAsync(path, "Runic native file handoff smoke test.\n", token);
                foreach (var operation in Enum.GetValues<DesktopFileOperation>())
                {
                    Console.WriteLine($"Exercise {operation}; choose or dismiss the native application picker when it appears.");
                    Console.WriteLine(await launcher.LaunchAsync(path, operation, token));
                }
                Console.WriteLine("Inspect the notification action output and opened file-manager/application windows. Press Enter to withdraw the notification and finish.");
                await Console.In.ReadLineAsync(token);
            }
            finally
            {
                await notifications.RemoveAsync("native-services", CancellationToken.None);
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
