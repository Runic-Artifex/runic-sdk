using System.Diagnostics;
using System.IO;
using System.Text;

// NVDA, and what it speaks: a portable NVDA (eng/ci/windows-nvda.ps1 creates one) runs with its own configuration and
// the silence synthesizer, and logs at the input/output level, where each speech sequence is a line
// "Speaking ['Volume', 'slider', '3']". Each sequence, its strings joined, is one phrase.
internal sealed class Nvda(string executable, string logs) : ScreenReader
{
    private readonly string _directory = Path.Combine(Path.GetFullPath(logs), "nvda");
    private Process? _nvda;

    private string Log => Path.Combine(_directory, "nvda.log");

    internal override string Name => "NVDA";

    internal override string Id => "nvda";

    internal override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        var config = Path.Combine(_directory, "config");
        Directory.CreateDirectory(config);
        // No welcome dialog, usage statistics question, update check or sounds; nothing that waits for a user.
        await File.WriteAllLinesAsync(Path.Combine(config, "nvda.ini"),
        [
            "[general]",
            "\tshowWelcomeDialogAtStartup = False",
            "\taskToExit = False",
            "\tplayStartAndExitSounds = False",
            "[update]",
            "\tautoCheck = False",
            "\tstartupNotification = False",
            "\tallowUsageStats = False",
            "\taskedAllowUsageStats = True",
            "[speech]",
            "\tsynth = silence",
        ], cancellationToken);
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false };
        // 12 is NVDA's input/output log level, which logs speech.
        foreach (var argument in new[] { "--log-level=12", $"--log-file={Log}", $"--config-path={config}" }) start.ArgumentList.Add(argument);
        _nvda = Process.Start(start) ?? throw new InvalidOperationException("NVDA did not start.");

        // The fixture waits a minute for the probe.
        var until = DateTime.UtcNow + TestTimeouts.Scale(TimeSpan.FromSeconds(45));
        while (!Initialized())
        {
            if (_nvda.HasExited) throw new InvalidOperationException($"NVDA exited with {_nvda.ExitCode}; see {Log}.");
            if (DateTime.UtcNow > until) throw new InvalidOperationException($"NVDA did not initialize within 45 seconds; see {Log}.");
            await Task.Delay(250, cancellationToken);
        }
        Console.WriteLine("NVDA started.");
    }

    private bool Initialized()
    {
        if (!File.Exists(Log)) return false;
        using var stream = new FileStream(Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Contains("NVDA initialized", StringComparison.Ordinal);
    }

    internal override Task ListenAsync(Action<string> heard, CancellationToken cancellationToken)
        => FollowAsync(Log, Length(Log), line =>
        {
            if (line.StartsWith("Speaking [", StringComparison.Ordinal) && Phrase(line["Speaking ".Length..]) is { Length: > 0 } phrase)
                heard(phrase);
        }, cancellationToken);

    internal override async Task QuitAsync()
    {
        if (_nvda is null) return;
        try
        {
            await RunAsync(Path.GetFullPath(executable), ["-q"], CancellationToken.None, TimeSpan.FromSeconds(30));
            using var bounded = TestTimeouts.Cancellation(TimeSpan.FromSeconds(15));
            await _nvda.WaitForExitAsync(bounded.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            try { _nvda.Kill(); }
            catch (InvalidOperationException) { }
        }
    }

    // The strings of a speech sequence's Python representation, e.g. ['Volume', LangChangeCommand ('en_US'), "it's"],
    // joined by spaces. Strings inside a command's parentheses are its arguments, not speech.
    internal static string Phrase(string sequence)
    {
        var words = new List<string>();
        var depth = 0;
        for (var index = 0; index < sequence.Length; index++)
        {
            var character = sequence[index];
            if (character == '(') depth++;
            else if (character == ')') depth--;
            else if (character is '\'' or '"')
            {
                var text = new StringBuilder();
                for (index++; index < sequence.Length && sequence[index] != character; index++)
                {
                    if (sequence[index] == '\\' && index + 1 < sequence.Length)
                    {
                        index++;
                        switch (sequence[index])
                        {
                            case 'n': text.Append('\n'); break;
                            case 'r': text.Append('\r'); break;
                            case 't': text.Append('\t'); break;
                            case 'x' when index + 2 < sequence.Length:
                                text.Append((char)Convert.ToInt32(sequence.Substring(index + 1, 2), 16));
                                index += 2;
                                break;
                            case 'u' when index + 4 < sequence.Length:
                                text.Append((char)Convert.ToInt32(sequence.Substring(index + 1, 4), 16));
                                index += 4;
                                break;
                            default: text.Append(sequence[index]); break;
                        }
                    }
                    else text.Append(sequence[index]);
                }
                if (depth == 0 && text.Length > 0) words.Add(text.ToString());
            }
        }
        return string.Join(" ", words.Select(word => word.Trim()).Where(word => word.Length > 0));
    }
}
