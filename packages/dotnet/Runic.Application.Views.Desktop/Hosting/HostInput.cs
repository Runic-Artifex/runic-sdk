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

// Hosted mode refuses a guest that runs with more rights than the host chose (host-protocol.md, Security requirements 8).
internal static class GuestPrivileges
{
    internal static string? Check()
    {
        // Root on Linux and macOS, an elevated token on Windows.
        if (Environment.IsPrivilegedProcess) return "guest-elevated";
        return OperatingSystem.IsLinux() && ParentRunsAsAnotherUser() ? "guest-other-user" : null;
    }

    // Linux: the real user ids of this process and its parent from /proc; false when either cannot be read.
    private static bool ParentRunsAsAnotherUser()
    {
        try
        {
            var self = File.ReadAllLines("/proc/self/status");
            if (Field(self, "PPid:") is not { } parent || parent <= 1 || Field(self, "Uid:") is not { } user) return false;
            var parentStatus = File.ReadAllLines($"/proc/{parent.ToString(CultureInfo.InvariantCulture)}/status");
            return Field(parentStatus, "Uid:") is { } parentUser && parentUser != user;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static long? Field(string[] lines, string name)
    {
        foreach (var line in lines)
        {
            if (!line.StartsWith(name, StringComparison.Ordinal)) continue;
            var first = line[name.Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        return null;
    }
}
