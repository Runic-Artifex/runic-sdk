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
public sealed record ProcessSnapshot(int ProcessId, int ParentProcessId, string ExecutableName)
{
    /// <summary>The full path of the executable image (<c>QueryFullProcessImageNameW</c>), such as
    /// <c>C:\Program Files\App\app.exe</c>; null when Windows denies access, as for some system and protected
    /// processes, or the process exited before it was read.</summary>
    public string? ImagePath { get; init; }
}

/// <summary>Local process inspection and control. Instances retain no native resources.</summary>
/// <remarks>Snapshot reads are synchronous. Closing, terminating and starting processes are asynchronous: their native
/// calls and waits run off the caller's thread, and cancellation stops waiting without killing, closing or rolling back
/// anything. Process IDs are reused by Windows; act on an ID from a recent snapshot.</remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Clients are instances like the other administration clients; the members were interface implementations before 0.9 and stay instance members.")]
public sealed partial class WindowsProcessClient
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
                rows.Add(new(unchecked((int)entry.th32ProcessID), unchecked((int)entry.th32ParentProcessID), entry.szExeFile.ToString())
                {
                    ImagePath = ReadImagePath(entry.th32ProcessID)
                });
                result = PInvoke.Process32NextW(snapshot, &entry);
            }
            var error = Marshal.GetLastPInvokeError();
            if (error != 18) throw NativeError.Win32("Enumerate process snapshot", error);
            return rows.ToImmutable();
        }
        finally { _ = PInvoke.CloseHandle(snapshot); }
    }

    /// <summary>Finds a process in a new snapshot; null means it was absent at observation time.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="processId"/> is negative.</exception>
    public ProcessSnapshot? Find(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(processId);
        return Enumerate().FirstOrDefault(row => row.ProcessId == processId);
    }

    /// <summary>Finds the running instances of a program by the full path of its executable, without regard to case.</summary>
    /// <remarks>Programs that share a file name in different folders are told apart. Processes whose
    /// <see cref="ProcessSnapshot.ImagePath"/> cannot be read are not matched.</remarks>
    /// <exception cref="ArgumentException"><paramref name="imagePath"/> is not a full path.</exception>
    public ImmutableArray<ProcessSnapshot> FindByImagePath(string imagePath)
    {
        NativeError.Text(imagePath, nameof(imagePath));
        if (!Path.IsPathFullyQualified(imagePath)) throw new ArgumentException("The image path must be a full path.", nameof(imagePath));
        var path = Path.GetFullPath(imagePath);
        return [.. Enumerate().Where(row => string.Equals(row.ImagePath, path, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>Returns immediate children observed in one snapshot.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="parentProcessId"/> is negative.</exception>
    public ImmutableArray<ProcessSnapshot> GetChildren(int parentProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(parentProcessId);
        return [.. Enumerate().Where(row => row.ParentProcessId == parentProcessId)];
    }

}
