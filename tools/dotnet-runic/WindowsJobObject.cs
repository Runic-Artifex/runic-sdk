using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Runic.Application.Tool;

/// <summary>
/// On Windows, a kill-on-close Job object that holds a process <c>dev</c> started and every process below it, so they
/// end together even after a process between them has exited (#1163). The frontend's package manager (bun, npm.cmd)
/// can exit on the console's Ctrl+C while the Node.js process below it, with the Vite server and the
/// <c>runic-translations serve</c> it started, keeps running. That Node.js process then has no living parent, so
/// <see cref="Process.Kill(bool)"/> on the package manager no longer reaches it.
/// </summary>
/// <remarks>
/// <see cref="Terminate"/> ends every process in the job. Closing the job's handle ends them too, so a <c>dev</c> that
/// crashes or is killed leaves none of them behind: Windows closes its handles when it exits. The handle is not
/// inherited, so no child keeps the job open. A process is assigned right after it started, before it has had the time
/// to start its own; Windows 8 and later nest jobs, so a <c>dev</c> that an IDE runs in its own job (Runic DevKit for
/// Visual Studio does) can still assign its children. Only processes that need it get a job: the build servers that
/// <c>dotnet watch</c> or a build start must outlive <c>dev</c>, and a job would end them with it.
/// </remarks>
internal sealed partial class WindowsJobObject : IDisposable
{
    private readonly object _gate = new();
    private nint _job;

    private WindowsJobObject(nint job)
    {
        _job = job;
    }

    /// <summary>
    /// Puts the started <paramref name="process"/> in a new kill-on-close job. Null elsewhere than on Windows, or when
    /// Windows refuses; the caller then ends the process tree as before.
    /// </summary>
    internal static WindowsJobObject? TryAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        nint job = CreateJobObject(0, 0);
        if (job == 0)
        {
            return null;
        }

        try
        {
            var limits = new ExtendedLimitInformation { BasicLimits = { LimitFlags = KillOnJobClose } };
            if (SetInformationJobObject(job, ExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()) &&
                AssignProcessToJobObject(job, process.Handle))
            {
                return new WindowsJobObject(job);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The process exited before it could be assigned; there is nothing for a job to hold.
        }

        _ = CloseHandle(job);
        return null;
    }

    /// <summary>Ends every process still in the job, whether or not the process it was made for has exited.</summary>
    internal void Terminate()
    {
        lock (_gate)
        {
            if (_job != 0)
            {
                _ = TerminateJobObject(_job, 1);
            }
        }
    }

    /// <summary>Closes the job, which ends whatever it still holds.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_job == 0)
            {
                return;
            }

            _ = CloseHandle(_job);
            _job = 0;
        }
    }

    private const int ExtendedLimitInformationClass = 9;
    private const uint KillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimits;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial nint CreateJobObject(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int informationClass, ref ExtendedLimitInformation information, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
