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

    /// <summary>
    /// Stops pkexec if it still runs as the calling user, which it does until authorization succeeded and it
    /// became root. False once the program runs as root, or after the process exited.
    /// </summary>
    bool TryStopBeforeStart(Process process);
}

internal sealed partial class LinuxPolkitHost : IPolkitHost
{
    public static readonly LinuxPolkitHost Instance = new();

    private static readonly string[] PkexecLocations = ["/run/wrappers/bin/pkexec", "/usr/bin/pkexec", "/bin/pkexec"];

    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x1, StatxMode = 0x2, StatxUid = 0x8;
    private const int ModeTypeMask = 0xF000, ModeRegular = 0x8000, ModeDirectory = 0x4000, ModeSymlink = 0xA000;
    private const int SetUserId = 0x800, Sticky = 0x200, GroupOrOtherWrite = 0x12, AnyExecute = 0x49;
    private const int ENOENT = 2, ENOTDIR = 20, EPERM = 1, SIGKILL = 9;

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
        // Every directory from / down must be root's and closed to other writers, or sticky so that others cannot
        // rename or remove root's entries in it (as /nix/store is). No component may be a symbolic link: pkexec
        // matches the policy against the program's real path.
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
            if ((status.Mode & GroupOrOtherWrite) != 0 && (status.Mode & Sticky) == 0) return ProgramCheck.Replaceable;
            path = path == "/" ? "/" + segments[i] : path + "/" + segments[i];
        }
    }

    public bool TryStopBeforeStart(Process process)
    {
        try
        {
            if (process.HasExited) return false;
        }
        catch (InvalidOperationException) { return false; }
        // Signalling a process needs a matching real user; pkexec keeps the caller's until it switches to root.
        if (Kill(process.Id, SIGKILL) == 0) return true;
        var errno = Marshal.GetLastPInvokeError();
        Debug.Assert(errno is EPERM or 3, "kill fails with EPERM for root's program or ESRCH after exit");
        return false;
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
}
