namespace Runic.Desktop.Internal;

// The unpacked browser extensions that a WebView2 window loads in Development (DesktopHostOptions.DevToolsExtensions,
// sdk#1043): DevTools extensions such as React DevTools, Vue DevTools or Runic's own panel. Outside Development there are
// none, so a production window never enables extensions in its WebView2 environment.
internal static class DevToolsExtensionFolders
{
    // More folders for a development session, separated by Path.PathSeparator (';' on Windows): a tool such as
    // `dotnet runic dev` sets it, so a panel it ships loads without a change to the application.
    internal const string Variable = "RUNIC_DESKTOP_DEVTOOLS_EXTENSIONS";

    // The application's folders, then the variable's, each once in its full form (a folder named twice, with a different
    // case or a trailing separator, loads once). None outside Development (WebContentRules.IsDevelopment). The variable's
    // entries that are blank or not fully qualified are skipped: its value comes from the session, not the application.
    internal static IReadOnlyList<string> Resolve(IReadOnlyList<string> configured, Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(readEnvironment);
        if (!WebContentRules.IsDevelopment(readEnvironment)) return [];
        var folders = new List<string>();
        foreach (var folder in configured) Add(folders, folder);
        if (readEnvironment(Variable) is { Length: > 0 } list)
        {
            foreach (var folder in list.Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                Add(folders, folder);
        }
        return folders;
    }

    // DesktopHostOptions.DevToolsExtensions: each entry is the full path of a folder.
    internal static void Validate(IReadOnlyList<string>? folders, string parameter)
    {
        ArgumentNullException.ThrowIfNull(folders, parameter);
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || folder.Contains('\0'))
                throw new ArgumentException(
                    $"DevToolsExtensions must hold the full paths of unpacked extension folders; '{folder}' is not one.", parameter);
        }
    }

    private static void Add(List<string> folders, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Contains('\0') || !Path.IsPathFullyQualified(folder)) return;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!folders.Contains(full, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)) folders.Add(full);
    }
}
