using Windows.Win32;
using Windows.Win32.System.Diagnostics.ToolHelp;
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
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        NativeError.Windows();
        var snapshot = PInvoke.CreateToolhelp32Snapshot(CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        if (snapshot.Value == (void*)-1) throw NativeError.Win32("Create process snapshot", Marshal.GetLastPInvokeError());
        try
        {
            PROCESSENTRY32W entry = default;
            entry.dwSize = (uint)sizeof(PROCESSENTRY32W);
            var rows = ImmutableArray.CreateBuilder<ProcessSnapshot>();
            var result = PInvoke.Process32FirstW(snapshot, &entry);
            while (result)
            {
                rows.Add(new(entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile.ToString()));
                result = PInvoke.Process32NextW(snapshot, &entry);
            }
            var error = Marshal.GetLastPInvokeError();
            if (error != 18) throw NativeError.Win32("Enumerate process snapshot", error);
            return rows.ToImmutable();
        }
        finally { _ = PInvoke.CloseHandle(snapshot); }
    }

    /// <summary>Finds a process in a new snapshot; null means it was absent at observation time.</summary>
    public ProcessSnapshot? Find(uint processId) => Enumerate().FirstOrDefault(row => row.ProcessId == processId);

    /// <summary>Returns immediate children observed in one snapshot.</summary>
    public ImmutableArray<ProcessSnapshot> GetChildren(uint parentProcessId) =>
        [.. Enumerate().Where(row => row.ParentProcessId == parentProcessId)];

}
