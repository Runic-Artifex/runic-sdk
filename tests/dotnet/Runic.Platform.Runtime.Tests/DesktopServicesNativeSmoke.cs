using Runic.Desktop;
using Runic.Platform;
using Runic.Platform.Linux;
using Runic.Platform.MacOS;
using Runic.Platform.Runtime;
using Runic.Platform.Windows;

// Opt-in interactive fixture. Also keeps all three providers' new ABI paths in the native AOT consumer.
internal static class DesktopServicesNativeSmoke
{
    internal static async Task RunAsync(INativePickerOwner owner, CancellationToken token)
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
            var portals = new Runic.Platform.Linux.Portal.PortalApplication(applicationId, diagnostic => Console.WriteLine($"Portal diagnostic: {diagnostic}"));
            launcher = portals.CreateFileLauncher(new Gtk3PortalWindowOwner(owner));
            notifications = portals.CreateNotifications();
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

internal static class NativePlatformSmoke
{
    internal static async Task<int> RunAsync(bool manualDesktopServices, bool manualSelection)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The native platform smoke supports Windows, macOS and Linux.");

        string root = Path.Combine(Path.GetTempPath(), $"runic-platform-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"),
            "<!doctype html><meta charset=\"utf-8\"><title>Runic platform smoke</title><p>Runic platform smoke</p>");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var options = new DesktopHostOptions
            {
                WaitForConnection = false,
                Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 },
            };
            await using var host = await DesktopHost.StartAsync(options, deadline.Token);
            await using var surface = await host.CreateSurfaceAsync(new()
            {
                Path = "platform-smoke",
                RootFolder = root,
                Content = "index.html",
            }, deadline.Token);
            await using var window = await surface.OpenWindowAsync(new()
            {
                Browser = BrowserKind.Embedded,
                PresentationPolicy = DesktopPresentationPolicy.RequestedOnly,
                Width = 720,
                Height = 540,
            }, deadline.Token);
            var owner = new DesktopWindowPickerOwner(window);

            if (manualSelection)
                await ExerciseManualSelectionAsync(owner, deadline.Token);
            if (manualDesktopServices)
                await DesktopServicesNativeSmoke.RunAsync(owner, deadline.Token);

            Console.WriteLine("PASS independent Runic.Desktop and Runic.Platform native service smoke.");
            return 0;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ExerciseManualSelectionAsync(INativePickerOwner owner, CancellationToken cancellationToken)
    {
        Console.WriteLine("MANUAL: select a readable file outside the app sandbox; its bytes will not be printed.");
        IPickerBackend backend = OperatingSystem.IsWindows()
            ? WindowsPlatformProvider.CreateFileDialogs(owner)
            : OperatingSystem.IsMacOS()
                ? MacOSPlatformProvider.CreateFileDialogs(owner)
                : LinuxPlatformProvider.CreateFileDialogs(owner);
        await using var lifetime = new PresentationLifetime(() => owner.IsAvailable, owner.Generation);
        var files = new PresentationFiles(lifetime, backend);
        var result = await files.OpenFileAsync(new(), cancellationToken);
        if (result is not PickerResult<IReadFileLease>.Selected selected)
            throw new InvalidOperationException("Manual native selection must select a file to prove acquired access.");
        await using (selected.Value)
        await using (var stream = await selected.Value.OpenReadAsync(cancellationToken))
        {
            var sample = new byte[4096];
            int read = await stream.ReadAsync(sample, cancellationToken);
            Console.WriteLine($"PASS manual selection: read {read} byte(s) and released the selected-file lease.");
        }
    }

    private sealed class DesktopWindowPickerOwner(DesktopWindow window) : INativePickerOwner
    {
        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => window.IsOpen;
        public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) =>
            window.DispatchNativeAsync(action, cancellationToken);
    }
}
