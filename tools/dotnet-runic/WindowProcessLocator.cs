using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

/// <summary>
/// Finds the Window process below the <c>dotnet watch</c> or <c>dotnet run</c> process that <c>dev</c> started, so an
/// IDE can attach its .NET debugger to it (<c>[dev] Window started: process &lt;pid&gt;</c>).
/// </summary>
/// <remarks>
/// The Window is the shallowest descendant that runs the project's program: the application host named by MSBuild's
/// <c>RunCommand</c> (on macOS, the development bundle's executable), or <c>dotnet</c> with the project's assembly when the
/// project has no application host. Linux reads <c>/proc</c>, macOS runs <c>ps</c>, Windows takes a ToolHelp snapshot.
/// </remarks>
internal static partial class WindowProcessLocator
{
    /// <summary>The program the Window process runs: the executable's file name without extension, and its assembly.</summary>
    internal sealed record WindowProgram(string Executable, string Assembly);

    /// <summary>
    /// The Window program of a project: the file name of <c>RunCommand</c> when it is not the dotnet host, else the
    /// assembly name. The assembly is <c>&lt;AssemblyName&gt;.dll</c>.
    /// </summary>
    internal static WindowProgram CreateProgram(string runCommand, string assemblyName)
    {
        // Only .exe is an extension: an application named My.App keeps its dot.
        string name = FileName(runCommand);
        if (name.Length == 0 || IsDotNetHost(name)) name = assemblyName;
        return new(name, assemblyName + ".dll");
    }

    /// <summary>Finds the Window process below <paramref name="root"/>, or null when it has not started (yet).</summary>
    internal static async Task<int?> FindAsync(int root, WindowProgram program, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProcessTreeSignal.ProcessEntry> processes;
        try
        {
            processes = OperatingSystem.IsLinux() ? ProcessTreeSignal.ReadProcSnapshot("/proc")
                : OperatingSystem.IsWindows() ? ReadToolHelpSnapshot()
                : await PsSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DevDevelopmentException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        return Select(root, processes, program);
    }

    /// <summary>The shallowest descendant of <paramref name="root"/> that runs <paramref name="program"/>.</summary>
    internal static int? Select(int root, IReadOnlyList<ProcessTreeSignal.ProcessEntry> processes, WindowProgram program)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(program);
        var children = processes.ToLookup(process => process.ParentId);
        var level = new List<int> { root };
        var seen = new HashSet<int> { root };
        while (level.Count != 0)
        {
            var next = new List<int>();
            foreach (int parent in level)
            {
                foreach (ProcessTreeSignal.ProcessEntry child in children[parent].OrderBy(process => process.Id))
                {
                    if (!seen.Add(child.Id)) continue;
                    if (Runs(child, program)) return child.Id;
                    next.Add(child.Id);
                }
            }
            level = next;
        }
        return null;
    }

    /// <summary>Whether a process runs the Window program, from its exact arguments when known, else its command line.</summary>
    internal static bool Runs(ProcessTreeSignal.ProcessEntry process, WindowProgram program)
    {
        ArgumentNullException.ThrowIfNull(process);
        IReadOnlyList<string> arguments = process.Arguments
            ?? process.CommandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (arguments.Count == 0) return false;
        string executable = FileName(arguments[0]);
        if (IsDotNetHost(executable))
        {
            // dotnet App.dll, or dotnet exec [runtime options] App.dll; never dotnet run, watch or build.
            if (arguments.Count < 2) return false;
            if (IsAssembly(arguments[1], program)) return true;
            return arguments[1] == "exec" && arguments.Skip(2).Any(argument => IsAssembly(argument, program));
        }
        if (Same(executable, program.Executable)) return true;
        // ps prints the command line without quotes, so a path with spaces is split; match the program at its end.
        return process.Arguments is null && ProgramAtStart(process.CommandLine, program.Executable);
    }

    private static bool IsAssembly(string argument, WindowProgram program) =>
        FileName(argument).Equals(program.Assembly, StringComparison.OrdinalIgnoreCase);

    private static bool ProgramAtStart(string commandLine, string executable)
    {
        foreach (char separator in "/\\")
        {
            string marker = separator + executable;
            int index = commandLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                int end = index + marker.Length;
                if (end < commandLine.Length && commandLine.AsSpan(end).StartsWith(".exe", StringComparison.OrdinalIgnoreCase)) end += 4;
                // Only the program's own path, before the first option.
                if ((end == commandLine.Length || commandLine[end] == ' ') && !commandLine[..index].Contains(" -", StringComparison.Ordinal))
                {
                    return true;
                }
                index = commandLine.IndexOf(marker, index + 1, StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }

    private static string FileName(string path)
    {
        string name = path.Replace('\\', '/').Split('/')[^1];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static bool Same(string left, string right) =>
        left.Equals(right, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static bool IsDotNetHost(string name) =>
        FileName(name).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// macOS: <c>ps</c> twice, the executable path (<c>comm</c>, which keeps spaces as the last column) and the arguments,
    /// so the application host is matched by its path and <c>dotnet App.dll</c> by its arguments.
    /// </summary>
    private static async Task<IReadOnlyList<ProcessTreeSignal.ProcessEntry>> PsSnapshotAsync(CancellationToken cancellationToken)
    {
        Dictionary<int, (int Parent, string Value)> executables = await PsColumnAsync("comm=", cancellationToken).ConfigureAwait(false);
        Dictionary<int, (int Parent, string Value)> commandLines = await PsColumnAsync("args=", cancellationToken).ConfigureAwait(false);
        var processes = new List<ProcessTreeSignal.ProcessEntry>(executables.Count);
        foreach ((int pid, (int parent, string executable)) in executables)
        {
            string commandLine = commandLines.TryGetValue(pid, out var line) ? line.Value : executable;
            // Exact for the application host; dotnet App.dll is matched from the split command line.
            processes.Add(IsDotNetHost(executable)
                ? new(pid, parent, commandLine)
                : new(pid, parent, commandLine, [executable]));
        }
        return processes;
    }

    private static async Task<Dictionary<int, (int Parent, string Value)>> PsColumnAsync(string column, CancellationToken cancellationToken)
    {
        CommandResult result = await CommandRunner.RunAsync("ps", Environment.CurrentDirectory,
            ["-A", "-o", "pid=,ppid=," + column], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException($"ps exited with code {result.ExitCode}");
        var values = new Dictionary<int, (int, string)>();
        foreach (string line in result.StandardOutput.Split('\n'))
        {
            string[] parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 &&
                int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) &&
                int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parent))
            {
                values[pid] = (parent, parts[2].Trim());
            }
        }
        return values;
    }

    /// <summary>Windows: every process with its parent and executable file name, from a ToolHelp snapshot.</summary>
    private static unsafe List<ProcessTreeSignal.ProcessEntry> ReadToolHelpSnapshot()
    {
        nint snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == -1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var processes = new List<ProcessTreeSignal.ProcessEntry>();
            var entry = new ProcessEntry32 { Size = (uint)sizeof(ProcessEntry32) };
            for (bool more = Process32First(snapshot, &entry); more; more = Process32Next(snapshot, &entry))
            {
                var name = new ReadOnlySpan<char>(entry.ExeFile, 260);
                int end = name.IndexOf('\0');
                string executable = (end < 0 ? name : name[..end]).ToString();
                processes.Add(new((int)entry.ProcessId, (int)entry.ParentProcessId, executable, [executable]));
            }
            return processes;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    private const uint SnapshotProcesses = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Process32First(nint snapshot, ProcessEntry32* entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Process32Next(nint snapshot, ProcessEntry32* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
