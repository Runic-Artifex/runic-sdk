using System.Diagnostics;
using Runic.Platform;
using Runic.Platform.Linux.Portal;
using TUnit.Core;

// The real xdg-desktop-portal with its GNOME backend on a private session bus: CI starts both in a Fedora container
// (eng/ci/real-portal.sh) and sets RUNIC_TEST_REAL_PORTAL. Each preference is changed through its GSettings key, as
// GNOME Settings does, and must reach both ReadAsync and a running WatchAsync. The variable keeps the case away from
// a developer's own settings.
internal sealed class RealPortalAppearanceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Test, Explicit, Category("RealPortalAppearance"), DisplayName("settings: colour scheme, accent, contrast and reduced motion from the real GNOME portal")]
    public async Task GnomePortalAppearanceAsync()
    {
        if (Environment.GetEnvironmentVariable("RUNIC_TEST_REAL_PORTAL") != "gnome")
            throw new InvalidOperationException("Run under eng/ci/real-portal.sh, which sets RUNIC_TEST_REAL_PORTAL=gnome on a private session bus.");
        Gsettings("org.gnome.desktop.interface", "color-scheme", "prefer-light");
        Gsettings("org.gnome.desktop.interface", "accent-color", "blue");
        Gsettings("org.gnome.desktop.a11y.interface", "high-contrast", "false");
        Gsettings("org.gnome.desktop.a11y.interface", "reduced-motion", "no-preference");

        await using var settings = new PortalApplication().CreateSettings();
        var baseline = await ReadUntilAsync(settings, appearance => appearance.ColorScheme == DesktopColorScheme.Light, "the light baseline");
        Check(baseline is { HighContrast: false, ReducedMotion: false } && Near(baseline.AccentColor, 0.208, 0.518, 0.894),
            $"baseline: {baseline}");

        using var watching = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var watch = settings.WatchAsync(watching.Token).GetAsyncEnumerator(watching.Token);
        Check(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopAppearance>.Success { Value.ColorScheme: DesktopColorScheme.Light },
            $"initial watched value: {watch.Current}");

        await ChangeAsync(watch, settings, "org.gnome.desktop.interface", "color-scheme", "prefer-dark",
            appearance => appearance.ColorScheme == DesktopColorScheme.Dark, "dark colour scheme");
        // libadwaita's orange accent, #ed5b00.
        await ChangeAsync(watch, settings, "org.gnome.desktop.interface", "accent-color", "orange",
            appearance => Near(appearance.AccentColor, 0.929, 0.357, 0.0), "orange accent");
        await ChangeAsync(watch, settings, "org.gnome.desktop.a11y.interface", "high-contrast", "true",
            appearance => appearance.HighContrast == true, "high contrast");
        await ChangeAsync(watch, settings, "org.gnome.desktop.a11y.interface", "reduced-motion", "reduce",
            appearance => appearance.ReducedMotion == true, "reduced motion");
        var final = await ReadUntilAsync(settings, _ => true, "the final read");
        Check(final is { ColorScheme: DesktopColorScheme.Dark, HighContrast: true, ReducedMotion: true } && Near(final.AccentColor, 0.929, 0.357, 0.0),
            $"every change is kept: {final}");
        Console.WriteLine($"PASS real GNOME portal appearance: {baseline} -> {final}, each change seen by ReadAsync and WatchAsync.");
    }

    private static async Task ChangeAsync(IAsyncEnumerator<PlatformResult<DesktopAppearance>> watch, IDesktopSettings settings,
        string schema, string key, string value, Func<DesktopAppearance, bool> expected, string description)
    {
        Gsettings(schema, key, value);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var next = watch.MoveNextAsync().AsTask();
            Check(await next.WaitAsync(Wait - deadline.Elapsed) && watch.Current is PlatformResult<DesktopAppearance>.Success,
                $"{description}: the watch ended or failed: {watch.Current}");
            if (watch.Current is PlatformResult<DesktopAppearance>.Success { Value: var watched } && expected(watched)) break;
        }
        var read = await settings.ReadAsync();
        Check(read is PlatformResult<DesktopAppearance>.Success { Value: var appearance } && expected(appearance), $"{description}: ReadAsync returned {read}");
        Console.WriteLine($"{description}: {schema} {key}={value} seen by WatchAsync after {deadline.ElapsedMilliseconds} ms and by ReadAsync.");
    }

    // The portal backend starts on the first request; give it time to publish the first values.
    private static async Task<DesktopAppearance> ReadUntilAsync(IDesktopSettings settings, Func<DesktopAppearance, bool> expected, string description)
    {
        var deadline = Stopwatch.StartNew();
        PlatformResult<DesktopAppearance> last;
        do
        {
            last = await settings.ReadAsync();
            if (last is PlatformResult<DesktopAppearance>.Success { Value: var appearance } && expected(appearance)) return appearance;
            await Task.Delay(200);
        } while (deadline.Elapsed < Wait);
        throw new InvalidOperationException($"{description}: last read {last}");
    }

    private static bool Near(DesktopAccentColor? color, double red, double green, double blue) =>
        color is { } c && Math.Abs(c.Red - red) < 0.01 && Math.Abs(c.Green - green) < 0.01 && Math.Abs(c.Blue - blue) < 0.01;

    private static void Gsettings(string schema, string key, string value)
    {
        var start = new ProcessStartInfo("gsettings") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "set", schema, key, value }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Check(process.ExitCode == 0, $"gsettings set {schema} {key} {value}: {output}");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
