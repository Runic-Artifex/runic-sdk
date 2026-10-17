using System.Diagnostics;
using System.Runtime.Versioning;

// What VoiceOver speaks, from its AppleScript interface ("content of last phrase"), while the probe acts on the page
// through the accessibility API. Needs VoiceOver's AppleScript control (SCREnableAppleScript and
// /private/var/db/Accessibility/.VoiceOverAppleScriptEnabled), which GitHub's macOS runner image enables.
[SupportedOSPlatform("macos")]
internal sealed class VoiceOverScenario(AccessibilityClient client, Report report, Expectations expectations, int kept)
{
    private static readonly TimeSpan SpeechTimeout = TimeSpan.FromSeconds(10);
    private readonly List<string> _phrases = [];
    private readonly Lock _lock = new();

    // VoiceOver runs while the probe acts, started before the accessibility checks (StartAsync): WebKit posts the
    // page's notifications only while an assistive application is running.
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var listening = ListenAsync(stop.Token);
        try
        {
            var slider = await FindAsync(ControlKind.Slider, Fixture.Volume, cancellationToken);
            var keep = await FindAsync(ControlKind.SpinButton, Fixture.Keep, cancellationToken);
            var fruit = await FindAsync(ControlKind.ListBox, Fixture.Fruit, cancellationToken);

            await client.PressAsync(await FindAsync(ControlKind.Button, Fixture.Announce, cancellationToken), cancellationToken);
            await ExpectAsync("voiceover-announcement-polite", Fixture.PoliteAnnouncement, cancellationToken);
            await client.PressAsync(await FindAsync(ControlKind.Button, Fixture.Alert, cancellationToken), cancellationToken);
            await ExpectAsync("voiceover-announcement-assertive", Fixture.AssertiveAnnouncement, cancellationToken);
            await client.PressAsync(await FindAsync(ControlKind.Button, Fixture.Save, cancellationToken), cancellationToken);
            await ExpectAsync("voiceover-live-region", Fixture.Saved(2), cancellationToken);

            // VoiceOver follows the keyboard focus and reads the control; then the value the user changes.
            await FocusAsync(slider, keep, "voiceover-slider-focus", Fixture.Volume, cancellationToken);
            await client.StepAsync(slider, -1, cancellationToken);
            await ExpectAsync("voiceover-slider-value", "6", cancellationToken);
            await FocusAsync(keep, slider, "voiceover-spinbutton-focus", Fixture.Keep, cancellationToken);
            await client.StepAsync(keep, 1, cancellationToken);
            // aria-valuetext: without it VoiceOver reads an ARIA spin button's value as a percentage of its range.
            await ExpectAsync("voiceover-spinbutton-value", (kept + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " restore points", cancellationToken);
            await FocusAsync(fruit, slider, "voiceover-listbox-focus", Fixture.Fruit, cancellationToken);
            await client.SelectAsync(fruit, "Apple", cancellationToken);
            await ExpectAsync("voiceover-listbox-selection", "Apple", cancellationToken);
        }
        finally
        {
            await stop.CancelAsync();
            await listening;
            lock (_lock) Console.WriteLine("VoiceOver phrases: " + string.Join(" | ", _phrases));
        }
    }

    // VoiceOver says the focused control once, then a hint; the sampled last phrase can miss the first. Focus moves away
    // and back once before the check fails.
    private async Task FocusAsync(Control control, Control other, string id, string text, CancellationToken cancellationToken)
    {
        await client.FocusAsync(control, cancellationToken);
        if (await HeardAsync(text, cancellationToken, TimeSpan.FromSeconds(4)) is null)
        {
            await client.FocusAsync(other, cancellationToken);
            await Task.Delay(1000, cancellationToken);
            await client.FocusAsync(control, cancellationToken);
        }
        await ExpectAsync(id, text, cancellationToken);
    }

    private async Task<Control> FindAsync(ControlKind kind, string name, CancellationToken cancellationToken)
        => await client.FindAsync(kind, name, cancellationToken) ?? throw new InvalidOperationException($"No {kind} '{name}'.");

    internal static async Task StartAsync(CancellationToken cancellationToken)
    {
        // VoiceOver, which the starter launches, would inherit redirected output and hold it open.
        using (var starter = Process.Start(new ProcessStartInfo("/System/Library/CoreServices/VoiceOver.app/Contents/MacOS/VoiceOverStarter") { UseShellExecute = false })
            ?? throw new InvalidOperationException("VoiceOverStarter did not start."))
        {
            await starter.WaitForExitAsync(cancellationToken);
        }
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var (exitCode, output) = await RunAsync("/usr/bin/osascript", ["-e", "tell application \"VoiceOver\" to get content of last phrase"], cancellationToken);
            if (exitCode == 0) return;
            if (DateTime.UtcNow > until) throw new InvalidOperationException("VoiceOver did not answer AppleScript: " + output);
            await Task.Delay(500, cancellationToken);
        }
    }

    internal static async Task QuitAsync()
    {
        try { await RunAsync("/usr/bin/osascript", ["-e", "tell application \"VoiceOver\" to quit"], CancellationToken.None); }
        catch (InvalidOperationException) { }
    }

    // VoiceOver's last phrase, sampled; each change is one phrase.
    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        var last = "";
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var (exitCode, output) = await RunAsync("/usr/bin/osascript", ["-e", "tell application \"VoiceOver\" to get content of last phrase"], cancellationToken);
                var phrase = output.Trim();
                if (exitCode == 0 && phrase != last)
                {
                    last = phrase;
                    lock (_lock) _phrases.Add(phrase);
                    Console.WriteLine($"  VoiceOver: {phrase}");
                }
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException) { }
        }
    }

    // The first phrase from now on (and the current one) that contains the text, or null after the timeout.
    private async Task<string?> HeardAsync(string text, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        int mark;
        lock (_lock) mark = Math.Max(0, _phrases.Count - 1);
        var until = DateTime.UtcNow + (timeout ?? SpeechTimeout);
        string? spoken = null;
        while (spoken is null && DateTime.UtcNow < until)
        {
            lock (_lock) spoken = _phrases.Skip(mark).FirstOrDefault(phrase => phrase.Contains(text, StringComparison.Ordinal));
            if (spoken is null) await Task.Delay(100, cancellationToken);
        }
        return spoken;
    }

    private async Task ExpectAsync(string id, string text, CancellationToken cancellationToken)
    {
        int mark;
        lock (_lock) mark = Math.Max(0, _phrases.Count - 1);
        var spoken = await HeardAsync(text, cancellationToken);
        var gap = expectations.Gap(id);
        if (spoken is not null) report.Pass(id, $"VoiceOver said '{spoken}'");
        else
        {
            string heard;
            lock (_lock) heard = string.Join(" | ", _phrases.Skip(mark));
            if (gap is not null) report.Gap(id, $"VoiceOver did not say '{text}'; it said: {heard}", gap);
            else report.Fail(id, $"VoiceOver did not say '{text}'; it said: {heard}");
        }
    }

    // Each AppleScript call is bounded: VoiceOver can be slow to answer its first.
    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string[] arguments, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(15));
        try { return await RunCoreAsync(file, arguments, bounded.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (-1, $"{file} did not answer within 15 seconds"); }
    }

    private static async Task<(int ExitCode, string Output)> RunCoreAsync(string file, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{file} did not start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw;
        }
        return (process.ExitCode, await output + await error);
    }
}
