using System.Runtime.Versioning;

// VoiceOver, and what it speaks from its AppleScript interface ("content of last phrase"). Needs VoiceOver's AppleScript
// control (SCREnableAppleScript and /private/var/db/Accessibility/.VoiceOverAppleScriptEnabled), which GitHub's macOS
// runner image enables, and the Automation permission that eng/ci/macos-voiceover.sh grants.
[SupportedOSPlatform("macos")]
internal sealed class VoiceOver : ScreenReader
{
    private const string LastPhrase = "tell application \"VoiceOver\" to get content of last phrase";

    internal override string Name => "VoiceOver";

    internal override string Id => "voiceover";

    internal override bool Sampled => true;

    internal override async Task StartAsync(CancellationToken cancellationToken)
    {
        // VoiceOver, which the starter launches, would inherit redirected output and hold it open.
        using (var starter = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "/System/Library/CoreServices/VoiceOver.app/Contents/MacOS/VoiceOverStarter") { UseShellExecute = false })
            ?? throw new InvalidOperationException("VoiceOverStarter did not start."))
        {
            await starter.WaitForExitAsync(cancellationToken);
        }
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            // VoiceOver can be slow to answer its first AppleScript call.
            var (exitCode, output) = await RunAsync("/usr/bin/osascript", ["-e", LastPhrase], cancellationToken);
            if (exitCode == 0) return;
            if (DateTime.UtcNow > until) throw new InvalidOperationException("VoiceOver did not answer AppleScript: " + output);
            await Task.Delay(500, cancellationToken);
        }
    }

    internal override async Task QuitAsync()
    {
        try { await RunAsync("/usr/bin/osascript", ["-e", "tell application \"VoiceOver\" to quit"], CancellationToken.None); }
        catch (InvalidOperationException) { }
    }

    // VoiceOver's last phrase, sampled; each change is one phrase.
    internal override async Task ListenAsync(Action<string> heard, CancellationToken cancellationToken)
    {
        var last = "";
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var (exitCode, output) = await RunAsync("/usr/bin/osascript", ["-e", LastPhrase], cancellationToken);
                var phrase = output.Trim();
                if (exitCode == 0 && phrase != last)
                {
                    last = phrase;
                    heard(phrase);
                }
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException) { }
        }
    }
}
