using Runic.Application.Views.Wpf;
using Runic.Desktop;
using Runic.Platform;
using System.Windows;
using TUnit.Core;

// The accessibility fixture (Runic.Desktop.WebViewSmoke's AccessibilitySmoke) in a RunicWebView, whose WPF window
// announces through WpfPlatformServices.ForWindow: in the page's live region while the page has the keyboard focus
// (I-203). The probe reads and changes the page through UI Automation; with --nvda it also checks what NVDA says. The
// screen-readers job runs it with NVDA (.github/workflows/ci.yml):
//
//   dotnet run --project tests/dotnet/Runic.Application.Views.Wpf.Windows.Tests -c Release -- --treenode-filter
//     "/*/*/*/*[Category=ScreenReader]" --test-parameter probe=<Runic.Desktop.AccessibilityProbe.exe>
//     --test-parameter receipt=<receipt.json> [--test-parameter nvda=<nvda.exe>]
internal sealed class ScreenReaderChecks
{
    [Test, Explicit, Category("ScreenReader"), NotInParallel("WpfSuite", Order = 40)]
    public async Task AccessibilityFixtureInARunicWebView()
    {
        var probe = Parameter("probe") ?? throw new InvalidOperationException("Pass --test-parameter probe=<Runic.Desktop.AccessibilityProbe.exe>.");
        List<string> command = [probe];
        if (Parameter("receipt") is { } receipt) command.AddRange(["--receipt", receipt]);
        if (Parameter("nvda") is { } nvda) command.AddRange(["--screen-reader", "nvda", "--nvda", nvda]);

        using var child = new RunicWebView();
        var window = new Window { Content = child, Width = 720, Height = 640, Title = "Runic WPF accessibility fixture" };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        child.Loaded += (_, _) => loaded.TrySetResult();
        var platform = WpfPlatformServices.ForWindow(window);
        window.Show();
        try
        {
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var host = await DesktopHost.StartAsync(new()
            {
                WindowHostFactory = child.CreateWindowHostFactory(),
                ConnectionTimeout = TimeSpan.FromSeconds(20),
            });
            var exitCode = await AccessibilitySmoke.RunAsync(host, "wpf", command, async (message, priority, cancellationToken) =>
            {
                var announced = await platform.AnnounceAsync(message, priority == DesktopAnnouncementPriority.Assertive
                    ? PlatformAnnouncementPriority.Assertive : PlatformAnnouncementPriority.Polite, cancellationToken);
                Program.Check(announced is PlatformResult<PlatformUnit>.Success, $"The WPF window did not announce: {announced}.");
            });
            Program.Check(exitCode == 0, $"The accessibility fixture exited with {exitCode}.");
        }
        finally { window.Close(); }
    }

    private static string? Parameter(string name) =>
        TestContext.Parameters.TryGetValue(name, out var values) && values.LastOrDefault() is { Length: > 0 } value ? value : null;
}
