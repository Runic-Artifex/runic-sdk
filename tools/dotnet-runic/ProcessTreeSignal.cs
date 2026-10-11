using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

/// <summary>
/// Asks an application started through <c>dotnet run</c> or <c>dotnet watch</c> to stop as a terminal Ctrl+C
/// would, so the application can close its windows and release its work before the process tree is killed.
/// </summary>
/// <remarks>
/// On Unix the <c>dotnet run</c> processes in the tree receive SIGTERM, which <c>dotnet run</c> forwards to the
/// application before it exits with the application's exit code. <c>dotnet watch</c> itself is not signalled: it
/// exits on SIGTERM without stopping its child, and it waits for a file change once the application has exited.
/// On Windows the tool's children share a hidden console, so a helper process attaches to that console and
/// raises Ctrl+C for every process on it.
/// </remarks>
internal static partial class ProcessTreeSignal
{
    internal const string CtrlCHelperArgument = "__runic-send-ctrl-c";

    private const int SigTerm = 15;
    private const uint CtrlCEvent = 0;

    /// <summary>Signals the processes that stop the application and returns the ones to wait for.</summary>
    internal static async Task<IReadOnlyList<int>> RequestStopAsync(int root, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await SendCtrlCAsync(root, cancellationToken).ConfigureAwait(false);
            return [root];
        }

        IReadOnlyList<int> targets = SelectStopTargets(root, await SnapshotAsync(cancellationToken).ConfigureAwait(false));
        foreach (int target in targets)
        {
            _ = Kill(target, SigTerm);
        }
        return targets;
    }

    /// <summary>Chooses the <c>dotnet run</c> processes below <paramref name="root"/>, or the root itself.</summary>
    internal static IReadOnlyList<int> SelectStopTargets(int root, IReadOnlyList<ProcessEntry> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var children = processes.ToLookup(process => process.ParentId);
        var targets = new List<int>();
        var pending = new Queue<int>([root]);
        var seen = new HashSet<int>();
        while (pending.TryDequeue(out int pid))
        {
            if (!seen.Add(pid)) continue;
            ProcessEntry? entry = processes.FirstOrDefault(process => process.Id == pid);
            if (entry is not null && IsDotNetRun(entry.CommandLine))
            {
                // dotnet run forwards the signal; its application and helper processes are left to it.
                targets.Add(pid);
                continue;
            }
            foreach (ProcessEntry child in children[pid]) pending.Enqueue(child.Id);
        }
        return targets.Count == 0 ? [root] : targets;
    }

    internal static bool IsDotNetRun(string commandLine)
    {
        string[] tokens = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Contains("run", StringComparer.Ordinal) &&
            !tokens.Any(token => token == "watch" ||
                token.EndsWith("dotnet-watch.dll", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool HasExited(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Kill(pid, 0) != 0 && Marshal.GetLastPInvokeError() == 3; // ESRCH
        }
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    internal static void Kill(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            CommandRunner.TryTerminate(process);
        }
        catch (ArgumentException)
        {
        }
    }

    private static async Task<IReadOnlyList<ProcessEntry>> SnapshotAsync(CancellationToken cancellationToken)
    {
        CommandResult result = await CommandRunner.RunAsync("ps", Environment.CurrentDirectory,
            ["-A", "-o", "pid=,ppid=,args="], cancellationToken).ConfigureAwait(false);
        var processes = new List<ProcessEntry>();
        foreach (string line in result.StandardOutput.Split('\n'))
        {
            string[] parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) &&
                int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parent))
            {
                processes.Add(new(pid, parent, parts.Length == 3 ? parts[2] : ""));
            }
        }
        return processes;
    }

    private static async Task SendCtrlCAsync(int root, CancellationToken cancellationToken)
    {
        (string executable, IReadOnlyList<string> prefix) = SelfCommand();
        CommandResult result = await CommandRunner.RunAsync(executable, Environment.CurrentDirectory,
            [.. prefix, CtrlCHelperArgument, root.ToString(CultureInfo.InvariantCulture)],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException($"Could not send Ctrl+C to process {root} (helper exit code {result.ExitCode}).");
        }
    }

    /// <summary>The command that starts this program again, through the dotnet host when it runs from a DLL.</summary>
    internal static (string Executable, IReadOnlyList<string> Prefix) SelfCommand()
    {
        string executable = Environment.ProcessPath
            ?? throw new IOException("The path of the running program is unknown.");
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase) &&
            Environment.GetCommandLineArgs() is [{ Length: > 0 } entry, ..])
        {
            return (executable, [entry]);
        }
        return (executable, []);
    }

    /// <summary>Runs in a helper process: raises Ctrl+C on the console of <paramref name="processId"/>.</summary>
    internal static int RunCtrlCHelper(string processId)
    {
        if (!OperatingSystem.IsWindows() ||
            !uint.TryParse(processId, NumberStyles.None, CultureInfo.InvariantCulture, out uint pid))
        {
            return Program.UsageFailure;
        }
        _ = FreeConsole();
        // Ignore the event here; every other process on the console receives it.
        return AttachConsole(pid) && SetConsoleCtrlHandler(0, true) && GenerateConsoleCtrlEvent(CtrlCEvent, 0)
            ? Program.Success
            : Program.DevelopmentFailure;
    }

    internal sealed record ProcessEntry(int Id, int ParentId, string CommandLine);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(nint handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
