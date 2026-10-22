using System.Diagnostics;
using System.IO;

// A screen reader that the probe runs while it acts on the page, and what it speaks, one phrase at a time. It starts
// before the accessibility checks: WebKit posts the page's notifications only while an assistive application runs, and
// WebKitGTK's web process reads the registered events when it starts.
internal abstract class ScreenReader
{
    /// <summary>The name in the log, e.g. "NVDA".</summary>
    internal abstract string Name { get; }

    /// <summary>The prefix of the speech checks, e.g. "nvda".</summary>
    internal abstract string Id { get; }

    /// <summary>Whether phrases are sampled (the current phrase) rather than reported as each is spoken.</summary>
    internal virtual bool Sampled => false;

    internal abstract Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Reports each phrase the screen reader speaks until cancelled.</summary>
    internal abstract Task ListenAsync(Action<string> heard, CancellationToken cancellationToken);

    /// <summary>Stops the screen reader; never throws.</summary>
    internal abstract Task QuitAsync();

    // --screen-reader voiceover|nvda|orca. The screen reader's own output goes to logs (the directory of the receipt).
    internal static ScreenReader? Create(string? name, string? nvda, string logs) => name switch
    {
        null => null,
#if WINDOWS
        "nvda" => new Nvda(nvda ?? throw new ArgumentException("--screen-reader nvda needs --nvda <nvda.exe>."), logs),
#else
        "voiceover" when OperatingSystem.IsMacOS() => new VoiceOver(),
        "orca" when OperatingSystem.IsLinux() => new Orca(logs),
#endif
        _ => throw new PlatformNotSupportedException($"--screen-reader {name}: VoiceOver runs on macOS, NVDA on Windows and Orca on Linux."),
    };

    // A bounded run of a helper command, with its output.
    internal static async Task<(int ExitCode, string Output)> RunAsync(string file, string[] arguments, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var limit = timeout ?? TimeSpan.FromSeconds(15);
        bounded.CancelAfter(limit);
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{file} did not start.");
        var output = process.StandardOutput.ReadToEndAsync(bounded.Token);
        var error = process.StandardError.ReadToEndAsync(bounded.Token);
        try
        {
            await process.WaitForExitAsync(bounded.Token);
            return (process.ExitCode, await output + await error);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); }
            catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            return (-1, $"{file} did not answer within {limit.TotalSeconds} seconds");
        }
    }

    // Reads the complete lines (UTF-8) that a screen reader's process appends to a file from the offset on; the speech
    // checks listen from where the file ends when they start.
    internal static async Task FollowAsync(string path, long from, Action<string> line, CancellationToken cancellationToken)
    {
        var position = from;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length > position)
                    {
                        stream.Position = position;
                        var bytes = new byte[stream.Length - position];
                        await stream.ReadExactlyAsync(bytes, cancellationToken);
                        var end = Array.LastIndexOf(bytes, (byte)'\n');
                        if (end >= 0)
                        {
                            position += end + 1;
                            foreach (var complete in System.Text.Encoding.UTF8.GetString(bytes, 0, end).Split('\n')) line(complete.TrimEnd('\r'));
                        }
                    }
                }
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { await Task.Delay(100, CancellationToken.None); }
        }
    }

    internal static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
}
