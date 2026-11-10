using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Platform;

namespace HostedShell;

// `HostedShell --probe`: the shell starts the counter without its own window, and checks on a real desktop that the
// counter's window opens as a native window of the shell with the counter's menu bar, that the bar follows the shell's
// language, and that the counter exits when the shell stops it. The counter's own behaviour has unit tests
// (../Counter/Tests).
internal static class ShellProbe
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync(RunicDesktopHost host)
    {
        using var timeout = new CancellationTokenSource(Wait);
        var locale = host.Services.GetRequiredService<IRunicApplicationLocale>();
        await locale.SetLocaleAsync("en", timeout.Token);
        var applications = host.HostedApplications;
        var opened = new TaskCompletionSource<RunicHostedWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        applications.WindowOpened += (_, e) => opened.TrySetResult(e.Window);

        await using var counter = await applications.StartAsync(Guests.Counter, new RunicHostedApplicationOptions { Policy = new CounterPolicy() },
            timeout.Token);
        var window = await opened.Task.WaitAsync(timeout.Token);
        Check(window.Type == "CounterTool.CounterWindow" && window.Presentation.IsOpen, $"The counter's {window.Type} is not open in a window of the shell.");

        await WaitForMenuAsync(window, "Counter", timeout.Token);
        await locale.SetLocaleAsync("de", timeout.Token);
        await WaitForMenuAsync(window, "Zähler", timeout.Token);
        await host.Desktop.SetColorSchemeAsync(DesktopColorScheme.Dark, timeout.Token);

        Check(await counter.StopAsync(timeout.Token) == 0, "The counter did not exit cleanly.");
        Check(window.Closed.IsCompleted && !window.Presentation.IsOpen, "The counter's window is open after it stopped.");
        Console.WriteLine("HOSTED_WINDOWS_OK|window|menu-bar|language|stop");
        return 0;
    }

    private static async Task WaitForMenuAsync(RunicHostedWindow window, string title, CancellationToken cancellationToken)
    {
        while (window.MenuBar is not [{ Title: var first }, ..] || first != title) await Task.Delay(50, cancellationToken);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
