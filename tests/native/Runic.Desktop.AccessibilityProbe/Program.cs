using System.Globalization;
using System.IO;

// Started by the accessibility fixture (Runic.Desktop.WebViewSmoke and Runic.Desktop.Gtk4.Smoke, --accessibility, and the
// WPF Windows tests' ScreenReader case) with
// its process ID. Reads and changes the fixture's page through the platform accessibility API, as a screen reader does,
// checks what each host is expected to expose (Expectations.cs) and writes a receipt. --screen-reader also runs a screen
// reader, VoiceOver on macOS, NVDA on Windows or Orca on Linux, and checks what it speaks (SpeechScenario.cs).
//
//   Runic.Desktop.AccessibilityProbe --pid <pid> --host gtk3|gtk4|webview2|wkwebview|wpf [--receipt <file>]
//       [--screen-reader voiceover|nvda|orca] [--nvda <nvda.exe>] [--survey]
//
// --survey reports every check without failing, to learn what a new host or engine version exposes. The screen
// reader's own log goes beside the receipt.
var pid = int.Parse(Argument("--pid") ?? throw new ArgumentException("--pid is required."), CultureInfo.InvariantCulture);
var host = Argument("--host") ?? throw new ArgumentException("--host is required.");
var receiptPath = Argument("--receipt");
var survey = args.Contains("--survey", StringComparer.Ordinal);
var screenReaderName = Argument("--screen-reader");

using var deadline = TestTimeouts.Cancellation(TimeSpan.FromMinutes(4));
var report = new Report(host, survey) { ScreenReader = screenReaderName };
ScreenReader? screenReader = null;
try
{
    screenReader = ScreenReader.Create(screenReaderName, Argument("--nvda"),
        Path.GetDirectoryName(Path.GetFullPath(receiptPath ?? Path.Combine(Path.GetTempPath(), "runic-accessibility", "receipt.json")))!);
    if (screenReader is not null) await screenReader.StartAsync(deadline.Token);
    await using var client = await CreateClientAsync(pid, deadline.Token);
    report.Api = client.Api;
    var expectations = Expectations.For(host, screenReaderName);
    using var scenario = new Scenario(client, report, expectations);
    await scenario.RunAsync(deadline.Token);
    if (screenReader is not null) await new SpeechScenario(client, report, expectations, screenReader, scenario.Kept).RunAsync(deadline.Token);
    await scenario.FinishAsync(deadline.Token);
}
catch (Exception error)
{
    report.Fail("probe", error.ToString());
}
finally
{
    if (screenReader is not null) await screenReader.QuitAsync();
    if (receiptPath is not null) report.Write(receiptPath);
}
return report.ExitCode;

string? Argument(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static async Task<AccessibilityClient> CreateClientAsync(int pid, CancellationToken cancellationToken)
{
#if WINDOWS
    return await WindowsAccessibility.CreateAsync(pid, Listening, cancellationToken);
#else
    if (OperatingSystem.IsMacOS()) return await MacAccessibility.CreateAsync(pid, Listening, cancellationToken);
    if (OperatingSystem.IsLinux()) return await LinuxAccessibility.CreateAsync(pid, Listening, cancellationToken);
    throw new PlatformNotSupportedException("Build the net10.0-windows target for Windows.");
#endif
}

// The fixture waits for this line before it opens its page.
static void Listening() => Console.WriteLine("PROBE LISTENING");
