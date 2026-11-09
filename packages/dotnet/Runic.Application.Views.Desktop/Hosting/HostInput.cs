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

// Hosted mode refuses a guest that runs with more rights than the host chose (host-protocol.md, Same user; Security
// requirements 8). The check fails closed: a guest that cannot show that its parent runs as its own user refuses.
internal static class GuestPrivileges
{
    internal static string? Check()
    {
        // Root on Linux and macOS, an elevated token on Windows.
        if (Environment.IsPrivilegedProcess) return "guest-elevated";
        if (OperatingSystem.IsLinux()) return SameUserOnLinux(ReadLines) ? null : "guest-other-user";
        if (OperatingSystem.IsMacOS()) return SameUserOnMacOS() ? null : "guest-other-user";
        // Windows compares the token users of the guest and its parent with the host's process code (SH3).
        return null;
    }

    // Linux: the real and effective uids of this process and its parent from /proc; all four must be equal. A parent
    // that is gone (PPid 0 or 1) or whose status cannot be read, as under hidepid for another user's process, fails.
    internal static bool SameUserOnLinux(Func<string, string[]?> read)
    {
        if (read("/proc/self/status") is not { } self || Field(self, "PPid:", 0) is not { } parent || parent <= 1 ||
            Field(self, "Uid:", 0) is not { } real || Field(self, "Uid:", 1) is not { } effective)
            return false;
        if (read($"/proc/{parent.ToString(CultureInfo.InvariantCulture)}/status") is not { } parentStatus ||
            Field(parentStatus, "Uid:", 0) is not { } parentReal || Field(parentStatus, "Uid:", 1) is not { } parentEffective)
            return false;
        return real == effective && parentReal == real && parentEffective == real;
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
    // be equal. A parent that is gone or that proc_pidinfo cannot describe fails.
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static bool SameUserOnMacOS()
    {
        try
        {
            var parent = MacOS.GetParentProcessId();
            if (parent <= 1) return false;
            // struct proc_bsdinfo (sys/proc_info.h), 136 bytes: pbi_uid (effective) at 20, pbi_ruid at 28.
            var info = new byte[136];
            if (MacOS.ProcessInformation(parent, MacOS.ProcessBsdInformation, 0, info, info.Length) < info.Length) return false;
            var parentEffective = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(20));
            var parentReal = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(28));
            var real = MacOS.GetUserId();
            return MacOS.GetEffectiveUserId() == real && parentReal == real && parentEffective == real;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
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
