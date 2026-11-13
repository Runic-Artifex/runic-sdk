using System.Globalization;
using System.Text;

namespace Runic.Application.Views.Desktop.Hosting;

// The guest's standard input in hosted mode: its first line is the host's offer, and its end means that the host went
// away (host-protocol.md, Lines). Read on a thread of its own, since a console's standard input blocks; later lines are
// ignored, and nothing beyond a line's 4 KiB limit is kept.
internal sealed class HostInput
{
    private readonly TaskCompletionSource<string?> _firstLine = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal HostInput(Stream input)
    {
        new Thread(() => Read(input)) { IsBackground = true, Name = "Runic host input" }.Start();
    }

    // The first line without its line end, longer than the limit as an empty line, or null when input ended first.
    internal Task<string?> FirstLine => _firstLine.Task;

    // Completes when standard input ends.
    internal Task Ended => _ended.Task;

    private void Read(Stream input)
    {
        try
        {
            var line = new MemoryStream();
            var tooLong = false;
            var buffer = new byte[4_096];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (_firstLine.Task.IsCompleted) continue;
                for (var i = 0; i < read; i++)
                {
                    var b = buffer[i];
                    if (b == '\n')
                    {
                        var bytes = line.GetBuffer().AsSpan(0, (int)line.Length);
                        if (bytes.Length > 0 && bytes[^1] == '\r') bytes = bytes[..^1];
                        _firstLine.TrySetResult(tooLong ? "" : Encoding.UTF8.GetString(bytes));
                        break;
                    }
                    if (line.Length <= HostProtocol.MaximumLineBytes) line.WriteByte(b);
                    else tooLong = true;
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or NotSupportedException or UnauthorizedAccessException) { }
        finally
        {
            _firstLine.TrySetResult(null);
            _ended.TrySetResult();
        }
    }
}

// What hosted mode knows about this process's privileges and its parent's (host-protocol.md, Same user, Elevated
// host). Elevated: root on Linux and macOS (an effective uid 0), an elevated token on Windows. SameUser: the parent runs
// as this process's user, as GuestPrivileges shows it; false when that cannot be shown. ParentElevated: that same,
// verified parent runs elevated; false when it cannot be shown or SameUser is false.
internal readonly record struct GuestPrivilegeFacts(bool Elevated, bool SameUser, bool ParentElevated)
{
    // A standard user's process started by its own user: the in-process runners' guest.
    internal static GuestPrivilegeFacts StandardUser => new(Elevated: false, SameUser: true, ParentElevated: false);
}

// Whether hosted mode may start with these privileges: the refusal code and its message, or no code and, for an
// elevated pair, the decision for the log (event 2025).
internal readonly record struct GuestPrivilegeDecision(string? Code, string? Message);

// Hosted mode refuses a guest that runs with more rights than the host chose (host-protocol.md, Same user, Elevated
// host; Security requirements 8). The checks fail closed: a guest that cannot show that its parent runs as its own
// user, or, when both sides allow an elevated pair, that its parent runs elevated, refuses. Nothing the host sends is
// evidence: the offer's elevatedGuest is the host's opt-in, and the guest checks its parent's token or uids itself.
internal static class GuestPrivileges
{
    internal const string OtherUserMessage = "The application runs as another user than the process that started it, so it cannot be hosted.";

    internal static GuestPrivilegeFacts Read()
    {
        // Root on Linux and macOS, an elevated token on Windows.
        var elevated = Environment.IsPrivilegedProcess;
        var parent = Parent();
        return new GuestPrivilegeFacts(elevated, parent.SameUser, parent.SameUser && parent.Elevated);
    }

    // Every combination outside the one pair both sides opted into keeps its refusal: an elevated guest without its
    // own opt-in, or whose host's offer does not allow it, refuses with guest-elevated as before.
    internal static GuestPrivilegeDecision Decide(GuestPrivilegeFacts facts, bool guestAllowsElevatedHost, bool hostAllowsElevatedGuest)
    {
        if (!facts.Elevated) return facts.SameUser ? default : new("guest-other-user", OtherUserMessage);
        if (!guestAllowsElevatedHost)
            return new("guest-elevated", "The application runs elevated, so it cannot be hosted; it does not allow an elevated host (RunicGuestOptions.AllowElevatedHost).");
        if (!hostAllowsElevatedGuest)
            return new("guest-elevated", "The application runs elevated, so it cannot be hosted; the host's offer does not allow an elevated guest (RunicGuestStartInfo.AllowElevatedGuest).");
        if (!facts.SameUser) return new("guest-other-user", OtherUserMessage);
        if (!facts.ParentElevated)
            return new("guest-elevated-host-not-elevated",
                "The application runs elevated and allows an elevated host, but the process that started it does not run elevated, so it cannot be hosted.");
        return new(null, "The application runs elevated, as does the process that started it, as the same user; both allow an elevated pair, so it is hosted elevated.");
    }

    // Whether this process and its parent run as the same user, on this system; false when that cannot be shown.
    internal static bool SameUser() => Parent().SameUser;

    // This process's parent: whether it runs as the same user and whether it runs elevated, from one read of it.
    private static ParentFacts Parent()
    {
        if (OperatingSystem.IsLinux()) return ParentOnLinux(ReadLines);
        if (OperatingSystem.IsMacOS()) return ParentOnMacOS();
        if (OperatingSystem.IsWindows()) return WindowsProcesses.Parent();
        return default;
    }

    // The parent as one read of it showed it. Elevated is meaningful only with SameUser.
    internal readonly record struct ParentFacts(bool SameUser, bool Elevated);

    // A process as the Windows check sees it: when it was created (FILETIME), the user SID of its token (TokenUser) and
    // whether the token is elevated (TokenElevation, or a TokenIntegrityLevel of High or above).
    internal readonly record struct WindowsProcessIdentity(long CreationTime, byte[] UserSid, bool Elevated = false);

    // Windows: the user SIDs of this process's token and its parent's must be equal. The parent is the process that
    // created this one (InheritedFromUniqueProcessId), held open while it is read; one created after this process is
    // another process that reused the gone parent's id. A parent that is gone, or whose token cannot be read, as for
    // another user's process, fails.
    internal static bool SameUserOnWindows(Func<WindowsProcessIdentity?> self, Func<int?> parentId, Func<int, WindowsProcessIdentity?> parent) =>
        ParentOnWindows(self, parentId, parent).SameUser;

    // The same check, and whether that verified parent's token is elevated, as read while the parent was held open.
    internal static ParentFacts ParentOnWindows(Func<WindowsProcessIdentity?> self, Func<int?> parentId, Func<int, WindowsProcessIdentity?> parent)
    {
        if (self() is not { } own || parentId() is not { } id || id <= 0 || parent(id) is not { } creator) return default;
        var same = creator.CreationTime <= own.CreationTime && own.UserSid.Length > 0 && own.UserSid.AsSpan().SequenceEqual(creator.UserSid);
        return new ParentFacts(same, same && creator.Elevated);
    }

    // Linux: the real and effective uids of this process and its parent from /proc; all four must be equal. A parent
    // that is gone (PPid 0 or 1) or whose status cannot be read, as under hidepid for another user's process, fails.
    internal static bool SameUserOnLinux(Func<string, string[]?> read) => ParentOnLinux(read).SameUser;

    // The same check; the parent runs elevated when, from the same read of its status, its effective uid is 0.
    internal static ParentFacts ParentOnLinux(Func<string, string[]?> read)
    {
        if (read("/proc/self/status") is not { } self || Field(self, "PPid:", 0) is not { } parent || parent <= 1 ||
            Field(self, "Uid:", 0) is not { } real || Field(self, "Uid:", 1) is not { } effective)
            return default;
        if (read($"/proc/{parent.ToString(CultureInfo.InvariantCulture)}/status") is not { } parentStatus ||
            Field(parentStatus, "Uid:", 0) is not { } parentReal || Field(parentStatus, "Uid:", 1) is not { } parentEffective)
            return default;
        var same = real == effective && parentReal == real && parentEffective == real;
        return new ParentFacts(same, same && parentEffective == 0);
    }

    private static string[]? ReadLines(string path)
    {
        try { return File.ReadAllLines(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    // The index-th number after the line's name, or null.
    private static long? Field(string[] lines, string name, int index)
    {
        foreach (var line in lines)
        {
            if (!line.StartsWith(name, StringComparison.Ordinal)) continue;
            var fields = line[name.Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return index < fields.Length && long.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
        return null;
    }

    // macOS: this process's real and effective uids, and its parent's from proc_pidinfo(PROC_PIDTBSDINFO); all four must
    // be equal. A parent that is gone or that proc_pidinfo cannot describe fails. The parent runs elevated when, in the
    // same description, its effective uid is 0.
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static ParentFacts ParentOnMacOS()
    {
        try
        {
            var parent = MacOS.GetParentProcessId();
            if (parent <= 1) return default;
            // struct proc_bsdinfo (sys/proc_info.h), 136 bytes: pbi_uid (effective) at 20, pbi_ruid at 28.
            var info = new byte[136];
            if (MacOS.ProcessInformation(parent, MacOS.ProcessBsdInformation, 0, info, info.Length) < info.Length) return default;
            var parentEffective = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(20));
            var parentReal = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(28));
            var real = MacOS.GetUserId();
            var same = MacOS.GetEffectiveUserId() == real && parentReal == real && parentEffective == real;
            return new ParentFacts(same, same && parentEffective == 0);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return default; }
    }

    private static class MacOS
    {
        internal const int ProcessBsdInformation = 3; // PROC_PIDTBSDINFO

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getppid")]
        internal static extern int GetParentProcessId();

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
        internal static extern uint GetUserId();

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
        internal static extern uint GetEffectiveUserId();

        [System.Runtime.InteropServices.DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pidinfo")]
        internal static extern int ProcessInformation(int pid, int flavor, ulong argument, byte[] buffer, int size);
    }
}

// The process queries of GuestPrivileges.ParentOnWindows: NtQueryInformationProcess for the parent's id, OpenProcess
// with PROCESS_QUERY_LIMITED_INFORMATION, GetProcessTimes and the token's TokenUser, TokenElevation and
// TokenIntegrityLevel. A failure reads as null, except that a token whose elevation cannot be read reads as not elevated.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static partial class WindowsProcesses
{
    private const uint QueryLimitedInformation = 0x1000; // PROCESS_QUERY_LIMITED_INFORMATION
    private const uint TokenQuery = 0x0008;
    private const int TokenUserClass = 1;
    private const int ProcessBasicInformationClass = 0;

    internal static GuestPrivileges.ParentFacts Parent()
    {
        try { return GuestPrivileges.ParentOnWindows(Self, ParentId, Parent); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return default; }
    }

    internal static GuestPrivileges.WindowsProcessIdentity? Self() => Identity(GetCurrentProcess());

    // InheritedFromUniqueProcessId of PROCESS_BASIC_INFORMATION: six pointer-sized fields on 32-bit and 64-bit Windows.
    internal static unsafe int? ParentId()
    {
        var information = stackalloc nint[6];
        if (NtQueryInformationProcess(GetCurrentProcess(), ProcessBasicInformationClass, information, (uint)(6 * sizeof(nint)), out _) != 0) return null;
        var parent = information[5];
        return parent is > 0 and <= int.MaxValue ? (int)parent : null;
    }

    // The parent, held open while its creation time, user and elevation are read.
    internal static GuestPrivileges.WindowsProcessIdentity? Parent(int processId)
    {
        var process = OpenProcess(QueryLimitedInformation, false, (uint)processId);
        if (process == 0) return null;
        try { return Identity(process); }
        finally { _ = CloseHandle(process); }
    }

    private static unsafe GuestPrivileges.WindowsProcessIdentity? Identity(nint process)
    {
        if (!GetProcessTimes(process, out var created, out _, out _, out _)) return null;
        if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
        try
        {
            _ = GetTokenInformation(token, TokenUserClass, null, 0, out var size);
            if (size is 0 or > 4_096) return null;
            var buffer = new byte[size];
            fixed (byte* data = buffer)
            {
                if (!GetTokenInformation(token, TokenUserClass, data, size, out _)) return null;
                // TOKEN_USER: SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; }.
                var sid = *(nint*)data;
                if (sid == 0 || !IsValidSid(sid)) return null;
                var length = GetLengthSid(sid);
                if (length is 0 or > 68) return null;
                return new GuestPrivileges.WindowsProcessIdentity(created, new ReadOnlySpan<byte>((void*)sid, (int)length).ToArray(), Elevated(token));
            }
        }
        finally { _ = CloseHandle(token); }
    }

    // TOKEN_ELEVATION.TokenIsElevated, or a mandatory label of High integrity or above, as elevated-peer admission reads
    // it too.
    private static bool Elevated(nint token) => Runic.Desktop.Internal.WindowsProcessTokens.TokenElevated(token);

    [System.Runtime.InteropServices.LibraryImport("kernel32")]
    private static partial nint GetCurrentProcess();

    [System.Runtime.InteropServices.LibraryImport("ntdll")]
    private static unsafe partial int NtQueryInformationProcess(nint process, int informationClass, void* information, uint length, out uint returned);

    [System.Runtime.InteropServices.LibraryImport("kernel32", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool inherit, uint processId);

    [System.Runtime.InteropServices.LibraryImport("kernel32", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [System.Runtime.InteropServices.LibraryImport("advapi32", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [System.Runtime.InteropServices.LibraryImport("advapi32", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(nint token, int informationClass, void* information, uint length, out uint returned);

    [System.Runtime.InteropServices.LibraryImport("advapi32")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool IsValidSid(nint sid);

    [System.Runtime.InteropServices.LibraryImport("advapi32")]
    private static partial uint GetLengthSid(nint sid);

    [System.Runtime.InteropServices.LibraryImport("kernel32")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
