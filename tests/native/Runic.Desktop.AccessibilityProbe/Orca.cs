using System.Diagnostics;
using System.Runtime.Versioning;

// Orca, and what it speaks: Orca speaks through Speech Dispatcher, which runs here with a generic output module that
// appends each utterance to a file instead of synthesizing it, so the receipt holds what Orca gave the synthesizer.
// Orca, its settings and its Speech Dispatcher are private to the run (XDG directories under the log directory) and
// need the session and accessibility buses and the display of the fixture (dbus-run-session, xvfb-run).
[SupportedOSPlatform("linux")]
internal sealed class Orca(string logs) : ScreenReader
{
    private readonly string _directory = Path.Combine(Path.GetFullPath(logs), "orca");
    private Process? _orca;

    private string Spoken => Path.Combine(_directory, "spoken.txt");

    internal override string Name => "Orca";

    internal override string Id => "orca";

    internal override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        var config = Path.Combine(_directory, "config");
        var runtime = Path.Combine(_directory, "runtime");
        Directory.CreateDirectory(Path.Combine(config, "speech-dispatcher", "modules"));
        Directory.CreateDirectory(Path.Combine(_directory, "data"));
        Directory.CreateDirectory(runtime);
        File.SetUnixFileMode(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (_directory.IndexOfAny(['"', '\'', '\\', '$', '`']) >= 0)
            throw new ArgumentException($"The Orca log directory '{_directory}' cannot be quoted in the Speech Dispatcher configuration.");
        // No audio device: ALSA's null device takes the module's (empty) sound output.
        File.WriteAllLines(Path.Combine(config, "speech-dispatcher", "speechd.conf"),
        [
            "LogLevel 3",
            $"LogDir \"{_directory}\"",
            "AudioOutputMethod \"alsa\"",
            "AudioALSADevice \"null\"",
            "AddModule \"capture\" \"sd_generic\" \"capture.conf\"",
            "DefaultModule capture",
            "DefaultLanguage \"en\"",
        ]);
        // sd_generic replaces $DATA with the utterance, escaped for a single-quoted shell word, and runs the command
        // with /bin/sh.
        File.WriteAllLines(Path.Combine(config, "speech-dispatcher", "modules", "capture.conf"),
        [
            $"GenericExecuteSynth \"printf '%s\\\\n' \\'$DATA\\' >> '{Spoken}'\"",
            "AddVoice \"en\" \"MALE1\" \"capture\"",
            // Without its language here, the module recodes the utterance to ISO 8859-1.
            "GenericLanguage \"en\" \"en\" \"utf-8\"",
            "GenericLanguage \"en-US\" \"en\" \"utf-8\"",
        ]);

        // Orca's output, and that of the Speech Dispatcher it starts, goes to a file rather than to the probe's output,
        // which the fixture reads to its end.
        var output = Path.Combine(_directory, "orca-output.log");
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var argument in new[] { "-c", "exec orca --debug-file \"$1\" > \"$2\" 2>&1 < /dev/null", "sh", Path.Combine(_directory, "orca-debug.log"), output })
            start.ArgumentList.Add(argument);
        start.Environment["XDG_CONFIG_HOME"] = config;
        start.Environment["XDG_DATA_HOME"] = Path.Combine(_directory, "data");
        start.Environment["XDG_RUNTIME_DIR"] = runtime;
        start.Environment.Remove("SPEECHD_ADDRESS");
        _orca = Process.Start(start) ?? throw new InvalidOperationException("Orca did not start.");

        // Orca says "Screen reader on." once it listens and speaks.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!File.Exists(Spoken) || new FileInfo(Spoken).Length == 0)
        {
            if (_orca.HasExited) throw new InvalidOperationException($"Orca exited with {_orca.ExitCode}; see {output}.");
            if (DateTime.UtcNow > until) throw new InvalidOperationException($"Orca spoke nothing within 60 seconds; see {_directory}.");
            await Task.Delay(200, cancellationToken);
        }
        Console.WriteLine($"Orca started: {(await File.ReadAllTextAsync(Spoken, cancellationToken)).Trim()}");
    }

    internal override Task ListenAsync(Action<string> heard, CancellationToken cancellationToken)
        => FollowAsync(Spoken, Length(Spoken), heard, cancellationToken);

    internal override async Task QuitAsync()
    {
        if (_orca is { HasExited: false })
        {
            _orca.Kill();
            await _orca.WaitForExitAsync();
        }
        // Speech Dispatcher, which Orca started, leaves when it has no client for a few seconds; stop it now.
        var pid = Path.Combine(_directory, "runtime", "speech-dispatcher", "pid", "speech-dispatcher.pid");
        if (File.Exists(pid) && int.TryParse(File.ReadAllText(pid).Trim(), out var id))
        {
            try { Process.GetProcessById(id).Kill(); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
    }
}
