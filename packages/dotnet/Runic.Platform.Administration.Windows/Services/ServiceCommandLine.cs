namespace Runic.Platform.Administration.Windows.Services;

/// <summary>Finds the executable in a service's configured command line the way the Service Control Manager starts it
/// (<c>CreateProcess</c> without an application name).</summary>
internal static class ServiceCommandLine
{
    /// <summary>Returns the executable path of <paramref name="commandLine"/>.</summary>
    /// <remarks>A quoted path is the text inside the quotes. An unquoted path is ambiguous when it contains whitespace:
    /// the SCM tries each prefix that ends before a space or tab, shortest first, adding <c>.exe</c> when the prefix has
    /// no extension, and starts the first file that exists. With <paramref name="fileExists"/> (the local computer) that
    /// search is repeated. Without it, or when no prefix exists, the shortest prefix that ends in <c>.exe</c> is returned,
    /// otherwise the first whitespace-delimited word. A leading <c>\??\</c> is removed. Environment references are kept.</remarks>
    internal static string ExecutablePath(string commandLine, Func<string, bool>? fileExists)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var text = commandLine.TrimStart(' ', '\t');
        if (text.StartsWith(@"\??\", StringComparison.Ordinal)) text = text[4..];
        if (text.Length == 0) return "";
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end < 0 ? text[1..] : text[1..end];
        }
        var candidates = new List<string>();
        for (var i = 0; i <= text.Length; i++)
            if (i == text.Length || text[i] is ' ' or '\t')
                if (i > 0 && text[i - 1] is not (' ' or '\t')) candidates.Add(text[..i]);
        if (fileExists is not null)
            foreach (var candidate in candidates)
            {
                if (fileExists(candidate)) return candidate;
                if (Path.GetExtension(candidate).Length == 0 && fileExists(candidate + ".exe")) return candidate + ".exe";
            }
        foreach (var candidate in candidates)
            if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return candidate;
        return candidates[0];
    }
}
