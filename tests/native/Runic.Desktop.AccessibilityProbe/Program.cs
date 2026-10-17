using System.Globalization;

// Started by the accessibility fixture (Runic.Desktop.WebViewSmoke and Runic.Desktop.Gtk4.Smoke, --accessibility) with
// its process ID. Reads and changes the fixture's page through the platform accessibility API, as a screen reader does,
// checks what each host is expected to expose (Expectations.cs) and writes a receipt. On macOS, --voiceover also runs
// VoiceOver and checks what it speaks.
//
//   Runic.Desktop.AccessibilityProbe --pid <pid> --host gtk3|gtk4|webview2|wkwebview [--receipt <file>] [--voiceover]
//       [--survey]
//
// --survey reports every check without failing, to learn what a new host or engine version exposes.
var pid = int.Parse(Argument("--pid") ?? throw new ArgumentException("--pid is required."), CultureInfo.InvariantCulture);
var host = Argument("--host") ?? throw new ArgumentException("--host is required.");
var receiptPath = Argument("--receipt");
var survey = args.Contains("--survey", StringComparer.Ordinal);
var voiceOver = args.Contains("--voiceover", StringComparer.Ordinal);

using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
var report = new Report(host, survey);
try
{
    await using var client = await CreateClientAsync(pid, deadline.Token);
    report.Api = client.Api;
    using var scenario = new Scenario(client, report, Expectations.For(host));
    await scenario.RunAsync(deadline.Token);
    if (voiceOver)
    {
#if WINDOWS
        throw new PlatformNotSupportedException("--voiceover runs on macOS.");
#else
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("--voiceover runs on macOS.");
        await new VoiceOverScenario(client, report, Expectations.For(host), scenario.Kept).RunAsync(deadline.Token);
#endif
    }
    await scenario.FinishAsync(deadline.Token);
}
catch (Exception error)
{
    report.Fail("probe", error.ToString());
}
finally
{
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
    await Task.CompletedTask;
    return new WindowsAccessibility(pid, Listening);
#else
    if (OperatingSystem.IsMacOS()) return await MacAccessibility.CreateAsync(pid, Listening, cancellationToken);
    if (OperatingSystem.IsLinux()) return await LinuxAccessibility.CreateAsync(pid, Listening, cancellationToken);
    throw new PlatformNotSupportedException("Build the net10.0-windows target for Windows.");
#endif
}

// The fixture waits for this line before it opens its page.
static void Listening() => Console.WriteLine("PROBE LISTENING");
