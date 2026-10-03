using System.Diagnostics;

namespace Runic.Application.Testing.Tests;

// A separate managed supervisor stays responsive even if Bun starves its event
// loop. Keep one generated-client child per checkout; terminate AND reap it
// before releasing the lock, including on timeout or memory-limit failure.
internal static class GeneratedHarnessProcess
{
    internal static async Task RunAsync(string executable, IReadOnlyList<string> arguments, string root)
    {
        var lockPath = Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests", "obj",
            "generated-client-process.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { WorkingDirectory = root,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException($"Could not start {executable}.");
        Console.WriteLine($"Generated client child started: PID {process.Id} ({executable}), limit 512 MiB / 30 s.");
        var output = DrainBoundedAsync(process.StandardOutput);
        var error = DrainBoundedAsync(process.StandardError);
        var clock = Stopwatch.StartNew();
        var exit = process.WaitForExitAsync();
        string? failure = null;
        long peakBytes = 0;
        try
        {
            while (!exit.IsCompleted)
            {
                process.Refresh();
                // The child can exit between HasExited and reading its memory.
                try { if (!process.HasExited) peakBytes = Math.Max(peakBytes, process.PeakWorkingSet64); }
                catch (InvalidOperationException) when (process.HasExited) { }
                if (peakBytes > 512L * 1024 * 1024) { failure = "exceeded 512 MiB"; break; }
                if (clock.Elapsed > TimeSpan.FromSeconds(30)) { failure = "exceeded 30 seconds"; break; }
                await Task.WhenAny(exit, Task.Delay(50)).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await exit.ConfigureAwait(false);
            Console.WriteLine($"Generated client child exited: PID {process.Id}, code {process.ExitCode}, observed peak {peakBytes / 1024} KiB.");
        }
        var diagnostics = await output.ConfigureAwait(false) + await error.ConfigureAwait(false);
        if (failure is not null || process.ExitCode != 0)
            throw new InvalidOperationException($"{executable} generated-client harness {failure ?? $"failed with exit code {process.ExitCode}"}.\n{diagnostics}");
        Console.Write(diagnostics);
    }

    private static async Task<string> DrainBoundedAsync(StreamReader reader)
    {
        var retained = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            if (retained.Length < 16_384) retained.Append(buffer, 0, Math.Min(count, 16_384 - retained.Length));
        return retained.ToString();
    }
}
