using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.Processes;

/// <summary>A process observed in a point-in-time Toolhelp snapshot.</summary>
/// <param name="ProcessId">The process identifier, which Windows may later reuse.</param>
/// <param name="ParentProcessId">The recorded parent identifier; its process may already have exited.</param>
/// <param name="ExecutableName">The executable filename reported by Toolhelp, not a full path.</param>
public sealed record ProcessSnapshot(uint ProcessId, uint ParentProcessId, string ExecutableName);

/// <summary>Read-only local process inspection. Instances retain no native resources.</summary>
public sealed partial class WindowsProcessClient : IWindowsProcessClient
{
    /// <summary>Reads a local process snapshot. Failure is never represented as an empty snapshot.</summary>
    public unsafe ImmutableArray<ProcessSnapshot> Enumerate()
    {
        NativeError.Windows();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) throw NativeError.Win32("Create process snapshot", Marshal.GetLastPInvokeError());
        try
        {
            Entry entry = default;
            entry.Size = (uint)sizeof(Entry);
            var rows = ImmutableArray.CreateBuilder<ProcessSnapshot>();
            var result = Process32FirstW(snapshot, &entry);
            while (result != 0)
            {
                rows.Add(new(entry.ProcessId, entry.ParentProcessId, new string(entry.ExecutableName)));
                result = Process32NextW(snapshot, &entry);
            }
            var error = Marshal.GetLastPInvokeError();
            if (error != 18) throw NativeError.Win32("Enumerate process snapshot", error);
            return rows.ToImmutable();
        }
        finally { _ = CloseHandle(snapshot); }
    }

    /// <summary>Finds a process in a new snapshot; null means it was absent at observation time.</summary>
    public ProcessSnapshot? Find(uint processId) => Enumerate().FirstOrDefault(row => row.ProcessId == processId);

    /// <summary>Returns immediate children observed in one snapshot.</summary>
    public ImmutableArray<ProcessSnapshot> GetChildren(uint parentProcessId) =>
        [.. Enumerate().Where(row => row.ParentProcessId == parentProcessId)];

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Entry
    {
        internal uint Size;
        internal uint Usage;
        internal uint ProcessId;
        internal nuint DefaultHeap;
        internal uint ModuleId;
        internal uint Threads;
        internal uint ParentProcessId;
        internal int BasePriority;
        internal uint Flags;
        internal fixed char ExecutableName[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static unsafe partial int Process32FirstW(nint snapshot, Entry* entry);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static unsafe partial int Process32NextW(nint snapshot, Entry* entry);
    [LibraryImport("kernel32.dll")] private static partial int CloseHandle(nint handle);
}
