using System.Globalization;
using System.Text;

namespace Runic.Desktop.Internal;

// The processes that still use a generated browser profile. Chromium passes --user-data-dir to every helper it starts
// (zygotes, GPU, network and storage services, renderers), so on Linux a process whose command line carries the
// profile's own argument is a process of that browser. Runic's barrier on the browser's output pipes does not cover
// them all: Google Chrome's Linux launcher puts its standard output and error behind two `cat` processes before it starts
// the browser, so only those hold Runic's pipes, and a kill of the browser's tree that ends them first closes the pipes
// while the network service still writes its cache into the profile (#1273).
internal static class BrowserProfileProcesses
{
    // A process that names the profile, or null. Only Linux exposes other processes' command lines this way; elsewhere
    // the output pipes remain the barrier (macOS starts the browser itself, and Windows refuses to delete a file in use).
    internal static int? FindUser(string profile)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var argument = Encoding.UTF8.GetBytes("--user-data-dir=" + profile);
        string[] entries;
        try
        {
            entries = Directory.GetDirectories("/proc");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        foreach (var entry in entries)
        {
            if (!int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
                || processId == Environment.ProcessId)
            {
                continue;
            }
            byte[] commandLine;
            try
            {
                commandLine = File.ReadAllBytes(Path.Combine(entry, "cmdline"));
            }
            // The process ended, or belongs to a user whose command line is hidden.
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (NamesArgument(commandLine, argument)) return processId;
        }
        return null;
    }

    // Whether one of the NUL-separated arguments equals argument exactly: a longer path that starts with the profile's
    // is another profile.
    internal static bool NamesArgument(ReadOnlySpan<byte> commandLine, ReadOnlySpan<byte> argument)
    {
        while (!commandLine.IsEmpty)
        {
            var end = commandLine.IndexOf((byte)0);
            var current = end < 0 ? commandLine : commandLine[..end];
            if (current.SequenceEqual(argument)) return true;
            if (end < 0) break;
            commandLine = commandLine[(end + 1)..];
        }
        return false;
    }
}
