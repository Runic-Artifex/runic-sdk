using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Runic.Platform.Linux.Polkit;

internal enum ProgramCheck
{
    Trusted,
    Missing,
    NotRealPath,
    Replaceable,
}

/// <summary>The operating-system checks around pkexec, replaceable in tests.</summary>
internal interface IPolkitHost
{
    /// <summary>A setuid-root pkexec, or null when there is none.</summary>
    string? FindPkexec(string? configured);

    /// <summary>Whether only root can replace the program between this check and pkexec running it.</summary>
    ProgramCheck CheckProgram(string program);

    /// <summary>A pidfd for the process, or an invalid handle where the kernel has none.</summary>
    ProcessHandle OpenProcess(int pid);

    /// <summary>
    /// Stops pkexec if it still runs as the calling user, which it does until authorization succeeded and it
    /// became root. False once the program runs as root, after the process exited, and always when the caller
    /// itself runs as root, because then pkexec and the program cannot be told apart.
    /// </summary>
    bool TryStopBeforeStart(Process process, ProcessHandle pidfd);
}

/// <summary>A pidfd, closed on disposal; invalid (-1) when the kernel or libc has no pidfd_open.</summary>
internal sealed partial class ProcessHandle(int descriptor) : IDisposable
{
    public static ProcessHandle None => new(-1);
    public int Descriptor { get; private set; } = descriptor;
    public bool IsValid => Descriptor >= 0;

    public void Dispose()
    {
        if (Descriptor >= 0) _ = Close(Descriptor);
        Descriptor = -1;
    }

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int descriptor);
}

internal sealed partial class LinuxPolkitHost : IPolkitHost
{
    public static readonly LinuxPolkitHost Instance = new();

    private static readonly string[] PkexecLocations = ["/run/wrappers/bin/pkexec", "/usr/bin/pkexec", "/bin/pkexec"];

    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x1, StatxMode = 0x2, StatxUid = 0x8;
    private const int ModeTypeMask = 0xF000, ModeRegular = 0x8000, ModeDirectory = 0x4000, ModeSymlink = 0xA000;
    private const int SetUserId = 0x800, Sticky = 0x200, GroupOrOtherWrite = 0x12, OtherWrite = 0x2, AnyExecute = 0x49;
    private const int ENOENT = 2, ENOTDIR = 20, ENOSYS = 38, SIGKILL = 9;
    // The same numbers on every Linux architecture: syscalls added after 4.20 share one table.
    private const int SysPidfdSendSignal = 424, SysPidfdOpen = 434;

    public string? FindPkexec(string? configured)
    {
        foreach (var candidate in configured is null ? PkexecLocations : [configured])
        {
            // pkexec itself may be a symbolic link to the setuid binary; follow it.
            if (Stat(candidate, follow: true) is { } status && (status.Mode & ModeTypeMask) == ModeRegular
                && status.Uid == 0 && (status.Mode & SetUserId) != 0 && (status.Mode & AnyExecute) != 0)
                return candidate;
        }
        return null;
    }

    public ProgramCheck CheckProgram(string program)
    {
        // A deployment check in the unprivileged application, not part of the security boundary: it refuses to
        // start a program that someone other than root could have replaced. Every directory from / down must be
        // root's and closed to other writers (a sticky directory may be group-writable). No component may be a
        // symbolic link: pkexec matches the policy against the program's real path.
        var path = "/";
        var segments = program[1..].Split('/');
        for (var i = 0; ; i++)
        {
            if (Stat(path, follow: false, out var errno) is not { } status)
                return errno is ENOENT or ENOTDIR ? ProgramCheck.Missing : ProgramCheck.Replaceable;
            var type = status.Mode & ModeTypeMask;
            if (type == ModeSymlink) return ProgramCheck.NotRealPath;
            if (status.Uid != 0) return ProgramCheck.Replaceable;
            var last = i == segments.Length;
            if (last)
            {
                if (type != ModeRegular) return ProgramCheck.Missing;
                return (status.Mode & GroupOrOtherWrite) == 0 && (status.Mode & AnyExecute) != 0 ? ProgramCheck.Trusted : ProgramCheck.Replaceable;
            }
            if (type != ModeDirectory) return ProgramCheck.Missing;
            // A sticky directory may be writable by its group, as /nix/store (root:nixbld 1775) is, but not by
            // others: /tmp (1777) lets any user create the next path component before the program is installed.
            if ((status.Mode & OtherWrite) != 0 || ((status.Mode & GroupOrOtherWrite) != 0 && (status.Mode & Sticky) == 0))
                return ProgramCheck.Replaceable;
            path = path == "/" ? "/" + segments[i] : path + "/" + segments[i];
        }
    }

    public ProcessHandle OpenProcess(int pid)
    {
        try
        {
            var descriptor = Syscall(SysPidfdOpen, pid, 0, 0, 0);
            return descriptor >= 0 ? new ProcessHandle((int)descriptor) : ProcessHandle.None;
        }
        catch (EntryPointNotFoundException) { return ProcessHandle.None; }
    }

    public bool TryStopBeforeStart(Process process, ProcessHandle pidfd)
    {
        // A caller running as root (sudo) can signal the program too, so a successful kill would not prove that
        // nothing started; it waits for the program like an ordinary caller does after authorization.
        if (GetRealUserId() == 0) return false;
        try
        {
            if (process.HasExited) return false;
        }
        catch (InvalidOperationException) { return false; }
        // Signalling a process needs a matching real user; pkexec keeps the caller's until it switches to root.
        if (pidfd.IsValid)
        {
            if (Syscall(SysPidfdSendSignal, pidfd.Descriptor, SIGKILL, 0, 0) == 0) return true;
            if (Marshal.GetLastPInvokeError() != ENOSYS) return false; // EPERM: root's program; ESRCH: exited
        }
        return Kill(process.Id, SIGKILL) == 0;
    }

    private static Status? Stat(string path, bool follow) => Stat(path, follow, out _);

    private static unsafe Status? Stat(string path, bool follow, out int errno)
    {
        // struct statx has the same layout on every architecture: stx_uid at 20, stx_mode (16 bits) at 28.
        var buffer = stackalloc byte[256];
        if (Statx(AtFdCwd, path, follow ? 0 : AtSymlinkNoFollow, StatxType | StatxMode | StatxUid, buffer) != 0)
        {
            errno = Marshal.GetLastPInvokeError();
            return null;
        }
        errno = 0;
        return new Status(*(uint*)(buffer + 20), *(ushort*)(buffer + 28));
    }

    private readonly record struct Status(uint Uid, int Mode);

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int Statx(int directory, string path, int flags, uint mask, byte* buffer);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial nint Syscall(nint number, nint first, nint second, nint third, nint fourth);

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetRealUserId();
}
