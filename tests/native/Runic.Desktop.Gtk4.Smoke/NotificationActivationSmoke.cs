using Runic.Desktop;
using Runic.Desktop.Gtk4;
using Runic.Platform;
using Runic.Platform.Linux.Gtk4;
using Runic.Platform.Linux.Portal;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[SupportedOSPlatform("linux")]
internal static partial class NotificationActivationSmoke
{
    internal static int Run(string[] args)
    {
        using var watchdog = new Timer(_ => Environment.Exit(1), null, TimeSpan.FromMinutes(6), Timeout.InfiniteTimeSpan);
        try
        {
            bool submit = args.Contains("--notification-submit", StringComparer.Ordinal);
            if (submit) return SubmitAsync().GetAwaiter().GetResult();
            return Gtk4Application.Run(() => ReceiveAsync(args.Contains("--notification-live", StringComparer.Ordinal)));
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static PortalApplication Application() => new(
        Environment.GetEnvironmentVariable("RUNIC_TEST_APP_ID") ?? "com.runic.tests.Activation",
        diagnostic => Console.WriteLine(diagnostic));
    private static DesktopNotification Notification() => new("runic-cold-test", "Runic activation test", "Click Open result to present the Runic window.")
    { Actions = [new("open", "Open result")] };
    private static void Success(PlatformResult<Unit> result)
    {
        if (result is not PlatformResult<Unit>.Success) throw new InvalidOperationException(result.ToString());
    }
    private static async Task<int> SubmitAsync()
    {
        await using var notifications = Application().CreateNotifications();
        Success(await notifications.ShowAsync(Notification()));
        Console.WriteLine($"SUBMITTED pid={Environment.ProcessId}; exiting without withdrawing the notification.");
        return 0;
    }

    private static async Task<int> ReceiveAsync(bool live)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var notifications = Application().CreateNotifications();
        var activation = new TaskCompletionSource<DesktopNotificationActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Install the receiver before acquiring the application name: the bus
        // can deliver the queued cold action as soon as name ownership succeeds.
        notifications.Activated += (_, value) =>
        {
            Console.WriteLine($"ACTION notification={value.NotificationId} action={value.ActionId}");
            if (value.NotificationId == "runic-cold-test" && value.ActionId == "open") activation.TrySetResult(value);
        };
        Success(await notifications.RequestPermissionAsync(deadline.Token));
        await using var host = new Gtk4WindowHostFactory().Create();
        await host.OpenAsync(new Uri("about:blank"), new() { Width = 640, Height = 420, Hidden = true }, deadline.Token);
        var owner = new Gtk4PortalWindowOwner(new HostOwner((IDesktopNativeDispatchWindowHost)host));
        if (live) Success(await notifications.ShowAsync(Notification(), deadline.Token));
        Console.WriteLine($"WAITING pid={Environment.ProcessId} live={live}");
        try
        {
            var received = await activation.Task.WaitAsync(deadline.Token);
            await owner.PresentAsync(received.PlatformContext, deadline.Token);
            bool focused = false;
            for (int attempt = 0; attempt < 50 && !focused; attempt++)
            {
                await owner.InvokeAsync(window => focused = IsActive(window) != 0, deadline.Token);
                if (!focused) await Task.Delay(100, deadline.Token);
            }
            string receipt = $"pid={Environment.ProcessId} notification={received.NotificationId} action={received.ActionId} token={received.PlatformContext?.ActivationToken is not null} focused={focused}";
            Console.WriteLine(receipt);
            var receiptPath = Environment.GetEnvironmentVariable("RUNIC_TEST_ACTIVATION_RECEIPT");
            if (receiptPath is not null) await File.WriteAllTextAsync(receiptPath, receipt, deadline.Token);
            if (!focused) throw new InvalidOperationException("The notification action arrived, but the compositor did not focus its window.");
            Console.WriteLine("PASS notification action and GTK4 window focus.");
            return 0;
        }
        finally
        {
            Success(await notifications.RemoveAsync("runic-cold-test"));
            await host.CloseAsync();
        }
    }

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_window_is_active")]
    internal static partial int IsActive(nint window);
}
