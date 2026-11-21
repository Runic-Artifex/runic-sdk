using Runic.Application.Views.Wpf;
using Runic.Desktop;
using Runic.Platform;
using System.Windows;
using TUnit.Core;

// The accessibility fixture (Runic.Desktop.WebViewSmoke's AccessibilitySmoke) in a RunicWebView, whose WPF window
// announces through WpfPlatformServices.ForWindow: in the page's live region while the page has the keyboard focus
// (I-203), and while the focus is on the WPF window itself, where WebView2 leaves it for a moment after it activates the
// window for a UI Automation action (sdk#1203); the fixture puts it there before the assertive message. The probe reads and changes the page through UI Automation; with --nvda it also checks what NVDA says. The
// screen-readers job runs it with NVDA (.github/workflows/ci.yml):
//
//   dotnet run --project tests/dotnet/Runic.Application.Views.Wpf.Windows.Tests -c Release -- --treenode-filter
//     "/*/*/*/*[Category=ScreenReader]" --test-parameter probe=<Runic.Desktop.AccessibilityProbe.exe>
//     --test-parameter receipt=<receipt.json> [--test-parameter nvda=<nvda.exe>]
internal sealed partial class ScreenReaderChecks
{
    [Test, Explicit, Category("ScreenReader"), NotInParallel("WpfSuite", Order = 40), RecordedRetry(RetryReason.Speech)]
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
            await loaded.Task.WaitScaledAsync(TimeSpan.FromSeconds(10));
            await using var host = await DesktopHost.StartAsync(new()
            {
                WindowHostFactory = child.CreateWindowHostFactory(),
                ConnectionTimeout = TestTimeouts.Scale(TimeSpan.FromSeconds(20)),
            });
            var exitCode = await AccessibilitySmoke.RunAsync(host, "wpf", command, async (message, priority, cancellationToken) =>
            {
                var announced = await platform.AnnounceAsync(message, priority == DesktopAnnouncementPriority.Assertive
                    ? PlatformAnnouncementPriority.Assertive : PlatformAnnouncementPriority.Polite, cancellationToken);
                Program.Check(announced is PlatformResult<PlatformUnit>.Success, $"The WPF window did not announce: {announced}.");
            }, focusWindowItself: _ => new(window.Dispatcher.Invoke(() =>
            {
                // The WPF window itself, with no WPF element focused, as after WebView2 activated it. WPF passes the focus
                // on to the control's child window at once, which is where the page announces too.
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                SetFocus(handle);
                return GetFocus() is var focus && (focus == handle || focus == child.Handle);
            })));
            Program.Check(exitCode == 0, $"The accessibility fixture exited with {exitCode}.");
        }
        finally { window.Close(); }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")] private static partial nint SetFocus(nint window);
    [System.Runtime.InteropServices.LibraryImport("user32.dll")] private static partial nint GetFocus();

    private static string? Parameter(string name) =>
        TestContext.Parameters.TryGetValue(name, out var values) && values.LastOrDefault() is { Length: > 0 } value ? value : null;
}
