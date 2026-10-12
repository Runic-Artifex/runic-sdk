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
        using var process = new Process { StartInfo = new ProcessStartInfo { WorkingDirectory = root,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        // An aborting child stays alive while the kernel hands its core dump to the
        // system handler. That takes seconds for Bun or .NET, and far longer on a
        // loaded runner, so the 30 s limit would hide the cause the abort printed.
        // A core limit of 0 does not prevent that on Linux: a piped core_pattern
        // runs the handler regardless, and the GitHub runner's systemd-coredump is
        // passed a fixed unlimited size, so it reads the whole dump (15 s per Bun
        // abort, over 40 s when several overlap). Linux runs no handler at all for
        // a limit of exactly 1 byte. Where prlimit cannot set it (no prlimit, or a
        // hard limit of 0), the limit is 0, which other Unix systems honor.
        // Unix children start through exec and keep their PID.
        if (OperatingSystem.IsWindows()) process.StartInfo.FileName = executable;
        else
        {
            process.StartInfo.FileName = "/bin/sh";
            foreach (var argument in new[] { "-c", "prlimit --pid $$ --core=1 2>/dev/null || ulimit -c 0; exec \"$@\"", "sh", executable })
                process.StartInfo.ArgumentList.Add(argument);
        }
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        // A --coverage run passes its CLR profiler to every child. Instrumented Debug IL of
        // BridgeOperationRouter.Wire is invalid (InvalidProgramException), so the .NET fixture
        // exporter then aborted in local `bun run test application` runs (W260-014).
        foreach (var name in process.StartInfo.Environment.Keys.Where(IsCoverageProfilerVariable).ToList())
            process.StartInfo.Environment.Remove(name);
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
                if (clock.Elapsed > TimeSpan.FromSeconds(30))
                {
                    failure = IsDumpingCore(process.Id) ? "exceeded 30 seconds while writing its core dump" : "exceeded 30 seconds";
                    break;
                }
                await Task.WhenAny(exit, Task.Delay(50)).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await exit.ConfigureAwait(false);
            Console.WriteLine($"Generated client child exited: PID {process.Id}, code {process.ExitCode}, observed peak {peakBytes / 1024} KiB.");
        }
        var (stdout, stderr) = (await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        if (failure is not null || process.ExitCode != 0)
            throw new InvalidOperationException($"{executable} generated-client harness {failure ?? DescribeExit(process.ExitCode)}.\n" +
                $"stderr:\n{(stderr.Length == 0 ? "(empty)" : stderr)}\nstdout:\n{(stdout.Length == 0 ? "(empty)" : stdout)}");
        Console.Write(stdout + stderr);
    }

    // On Unix a child killed by a signal, such as the abort after an unhandled
    // .NET exception, reports 128 plus the signal number.
    private static bool IsCoverageProfilerVariable(string name) =>
        name.StartsWith("CORECLR_ENABLE_PROFILING", StringComparison.Ordinal) || name.StartsWith("CORECLR_PROFILER", StringComparison.Ordinal)
        || name.StartsWith("CODE_COVERAGE_", StringComparison.Ordinal) || name.StartsWith("MicrosoftInstrumentationEngine_", StringComparison.Ordinal);

    private static string DescribeExit(int code) => code switch
    {
        134 when !OperatingSystem.IsWindows() => "aborted (exit code 134, SIGABRT)",
        137 when !OperatingSystem.IsWindows() => "was killed (exit code 137, SIGKILL)",
        139 when !OperatingSystem.IsWindows() => "crashed (exit code 139, SIGSEGV)",
        _ => $"failed with exit code {code}",
    };

    // An aborted child that is still alive is held by the core dump handler.
    private static bool IsDumpingCore(int processId)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try { return File.ReadLines($"/proc/{processId}/status").Contains("CoreDumping:\t1"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    // Keeps the last 16 KiB: an abort or a failed assertion reports its cause at the end.
    private static async Task<string> DrainBoundedAsync(StreamReader reader)
    {
        const int Limit = 16_384;
        var retained = new System.Text.StringBuilder();
        var buffer = new char[4096];
        long dropped = 0;
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            retained.Append(buffer, 0, count);
            if (retained.Length <= Limit) continue;
            dropped += retained.Length - Limit;
            retained.Remove(0, retained.Length - Limit);
        }
        return dropped == 0 ? retained.ToString() : $"[{dropped} earlier characters omitted]\n{retained}";
    }
}
