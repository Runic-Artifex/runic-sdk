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

// The DevTools extensions Runic installed into a WebView2 user data folder, by extension id and folder, kept in the
// folder itself (D067.6): in Development the host removes those whose folder is no longer listed, and never touches an
// extension it did not install.
internal static class DevToolsExtensionRecord
{
    internal const string FileName = "Runic.DevToolsExtensions.json";

    // The record of a user data folder; empty when there is none or it cannot be read.
    internal static Dictionary<string, string> Read(string userDataFolder)
    {
        try
        {
            var path = Path.Combine(userDataFolder, FileName);
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var record = new Dictionary<string, string>(StringComparer.Ordinal);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return record;
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind == System.Text.Json.JsonValueKind.String && entry.Value.GetString() is { Length: > 0 } folder)
                    record[entry.Name] = folder;
            }
            return record;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return new(StringComparer.Ordinal); }
    }

    internal static void Write(string userDataFolder, IReadOnlyDictionary<string, string> record)
    {
        Directory.CreateDirectory(userDataFolder);
        using var stream = File.Create(Path.Combine(userDataFolder, FileName));
        using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        foreach (var (id, folder) in record.OrderBy(static entry => entry.Key, StringComparer.Ordinal)) writer.WriteString(id, folder);
        writer.WriteEndObject();
    }

    // recorded: the record before this run; listed: the folders of this run; installed: what this run installed, by id.
    // The new record is what this run installed, and the recorded extensions of listed folders that did not install this
    // time (they may still be in the profile). Stale: the recorded ids that are in neither, to remove from the profile.
    internal static (Dictionary<string, string> Record, IReadOnlyList<string> Stale) Reconcile(
        IReadOnlyDictionary<string, string> recorded, IReadOnlyList<string> listed, IReadOnlyDictionary<string, string> installed)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var record = new Dictionary<string, string>(installed, StringComparer.Ordinal);
        var installedFolders = installed.Values.ToHashSet(comparer);
        var stale = new List<string>();
        foreach (var (id, folder) in recorded)
        {
            if (record.ContainsKey(id)) continue;
            if (listed.Contains(folder, comparer) && !installedFolders.Contains(folder)) record[id] = folder;
            else stale.Add(id);
        }
        return (record, stale);
    }
}
