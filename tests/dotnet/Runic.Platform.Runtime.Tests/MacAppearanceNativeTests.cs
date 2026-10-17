using System.Diagnostics;
using Runic.Platform;
using Runic.Platform.MacOS;
using TUnit.Core;

// W315-011 on the macOS runner: the settings provider reads the preferences that eng/ci/macos-appearance.sh wrote
// before starting this process (accent colour, Increase contrast and Reduce motion reach a new process), and with
// live-dark=true it follows dark mode switched through System Events while it runs. Program.cs runs the category
// inside the Desktop event loop, so AppKit updates the effective appearance. The script restores every preference.
// RUNIC_TEST_APPEARANCE keeps the case away from a developer's own settings.
internal sealed class MacAppearanceNativeTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    [Test, Explicit, Category("NativeAppearance"), DisplayName("native AppKit: appearance preferences and a live dark mode change"), Timeout(120_000)]
    public async Task NativeAppearanceAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The appearance checks need macOS.");
        if (Environment.GetEnvironmentVariable("RUNIC_TEST_APPEARANCE") != "1")
            throw new InvalidOperationException("Run through eng/ci/macos-appearance.sh, which sets RUNIC_TEST_APPEARANCE=1 and restores the preferences.");
        await using var settings = MacOSPlatformProvider.CreateSettings();
        var read = await settings.ReadAsync(cancellationToken);
        Require(read is PlatformResult<DesktopAppearance>.Success, $"The appearance read failed: {read}.");
        var appearance = ((PlatformResult<DesktopAppearance>.Success)read).Value;
        Console.WriteLine($"Appearance: {appearance}");
        Require(appearance.AccentColor is not null && appearance.HighContrast is not null && appearance.ReducedMotion is not null,
            $"A preference was unknown: {appearance}.");
        if (Parameter("expect-accent") is { } accent)
        {
            var rgb = accent.Split(',').Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var actual = appearance.AccentColor!;
            Require(Math.Abs(actual.Red - rgb[0]) < 0.01 && Math.Abs(actual.Green - rgb[1]) < 0.01 && Math.Abs(actual.Blue - rgb[2]) < 0.01,
                $"Accent {actual} is not {accent}.");
        }
        if (Parameter("expect-contrast") is { } contrast)
            Require(appearance.HighContrast == bool.Parse(contrast), $"HighContrast {appearance.HighContrast} is not {contrast}.");
        if (Parameter("expect-motion") is { } motion)
            Require(appearance.ReducedMotion == bool.Parse(motion), $"ReducedMotion {appearance.ReducedMotion} is not {motion}.");
        Console.WriteLine($"PASS macOS appearance read: {appearance}");
        if (Parameter("live-dark") == "true") await LiveDarkModeAsync(settings, cancellationToken);
    }

    // Dark mode switched as System Settings does, through System Events, must reach a running watch.
    private static async Task LiveDarkModeAsync(IDesktopSettings settings, CancellationToken cancellationToken)
    {
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        watching.CancelAfter(TimeSpan.FromSeconds(60));
        await using var watch = settings.WatchAsync(watching.Token).GetAsyncEnumerator(watching.Token);
        Require(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopAppearance>.Success { Value.ColorScheme: DesktopColorScheme.Light },
            $"The watch did not start in light mode: {watch.Current}.");
        try
        {
            foreach (var (dark, expected) in new[] { (true, DesktopColorScheme.Dark), (false, DesktopColorScheme.Light) })
            {
                var started = Stopwatch.StartNew();
                SetDarkMode(dark);
                while (true)
                {
                    Require(await watch.MoveNextAsync().AsTask().WaitAsync(Step - started.Elapsed, watching.Token), "The watch ended.");
                    if (watch.Current is PlatformResult<DesktopAppearance>.Success { Value.ColorScheme: var scheme } && scheme == expected) break;
                }
                Console.WriteLine($"PASS macOS live appearance: dark mode {dark} reached WatchAsync as {expected} after {started.ElapsedMilliseconds} ms.");
            }
        }
        finally { SetDarkMode(false); }
    }

    private static void SetDarkMode(bool dark)
    {
        var start = new ProcessStartInfo("/usr/bin/osascript") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add($"tell application \"System Events\" to tell appearance preferences to set dark mode to {(dark ? "true" : "false")}");
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Require(process.ExitCode == 0, $"osascript could not set dark mode: {output}");
    }

    private static string? Parameter(string name) =>
        TestContext.Parameters.TryGetValue(name, out var values) && values.LastOrDefault() is { Length: > 0 } value ? value : null;

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
