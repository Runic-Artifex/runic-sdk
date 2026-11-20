namespace Runic.Spelling;

// The operating systems whose folders differ.
internal enum SpellingPlatform
{
    Linux,
    MacOS,
    Windows,
}

// What the folders are worked out from: the process's own by default, fakes in the tests. SystemRoot is the root the
// fixed system folders (/usr/share, /Library/Spelling) are under: / for the process, a temporary folder in the tests, so
// they never read the dictionaries of the computer they run on.
internal sealed record SpellingEnvironment(SpellingPlatform Platform, Func<string, string?> Variable, string Home, string BaseDirectory,
    string SystemRoot = "/")
{
    internal static SpellingEnvironment Current { get; } = new(
        OperatingSystem.IsWindows() ? SpellingPlatform.Windows : OperatingSystem.IsMacOS() ? SpellingPlatform.MacOS : SpellingPlatform.Linux,
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        AppContext.BaseDirectory);

    // A variable's value, or null when it is unset or empty.
    internal string? Value(string name) => Variable(name) is { Length: > 0 } value ? value : null;

    internal char ListSeparator => Platform == SpellingPlatform.Windows ? ';' : ':';
}

// Where dictionaries are looked for and installed (README.md, "Where dictionaries are").
internal static class SpellingFolders
{
    // The folder all Runic applications of the user share: $XDG_DATA_HOME/runic, %LOCALAPPDATA%\Runic,
    // ~/Library/Application Support/Runic.
    internal static string SharedRunicFolder(SpellingEnvironment environment) => environment.Platform switch
    {
        SpellingPlatform.Windows => Path.Combine(
            environment.Value("LOCALAPPDATA") ?? Path.Combine(environment.Home, "AppData", "Local"), "Runic"),
        SpellingPlatform.MacOS => Path.Combine(environment.Home, "Library", "Application Support", "Runic"),
        _ => Path.Combine(DataHome(environment), "runic"),
    };

    internal static string DownloadDirectory(SpellingEnvironment environment) => Path.Combine(SharedRunicFolder(environment), "dictionaries");

    // The application's own folders: RUNIC_SPELLING_DICTIONARIES when set, then dictionaries/ beside the program, and on
    // macOS in the bundle's Contents/Resources, where `dotnet runic package macos` puts it.
    internal static IReadOnlyList<string> ApplicationDirectories(SpellingEnvironment environment, IReadOnlyList<string>? folders)
    {
        List<string> result = [];
        if (environment.Value(SpellingDictionaries.DirectoryEnvironmentVariable) is { } chosen) result.Add(chosen);
        if (folders is not null)
        {
            result.AddRange(folders);
            return result;
        }
        result.Add(Path.Combine(environment.BaseDirectory, "dictionaries"));
        if (environment.Platform == SpellingPlatform.MacOS)
            result.Add(Path.GetFullPath(Path.Combine(environment.BaseDirectory, "..", "Resources", "dictionaries")));
        return result;
    }

    // The folders of the system's Hunspell dictionaries, the first of a name winning:
    // Linux: $DICPATH; hunspell/, myspell/ and myspell/dicts/ under $XDG_DATA_HOME and each $XDG_DATA_DIRS entry (where Nix
    //        profiles and buildRunicApplication's spellingDictionaries appear); /usr/share/hunspell, /usr/share/myspell
    //        and /usr/share/myspell/dicts.
    // macOS: ~/Library/Spelling, /Library/Spelling.
    // Windows: each language's folder under %APPDATA%\Microsoft\Spelling.
    internal static IReadOnlyList<string> SystemFolders(SpellingEnvironment environment)
    {
        List<string> folders = [];
        void Add(string folder)
        {
            if (folder.Length > 0 && !folders.Contains(folder, StringComparer.Ordinal)) folders.Add(folder);
        }
        switch (environment.Platform)
        {
            case SpellingPlatform.MacOS:
                Add(Path.Combine(environment.Home, "Library", "Spelling"));
                Add(Path.Combine(environment.SystemRoot, "Library", "Spelling"));
                break;
            case SpellingPlatform.Windows:
                var spelling = Path.Combine(environment.Value("APPDATA") ?? Path.Combine(environment.Home, "AppData", "Roaming"), "Microsoft", "Spelling");
                try
                {
                    if (Directory.Exists(spelling))
                        foreach (var language in Directory.EnumerateDirectories(spelling).Order(StringComparer.OrdinalIgnoreCase)) Add(language);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
                break;
            default:
                foreach (var entry in Split(environment.Value("DICPATH"), environment.ListSeparator)) Add(entry);
                var usrShare = Path.Combine(environment.SystemRoot, "usr", "share");
                var dataDirs = environment.Value("XDG_DATA_DIRS") is { } configured
                    ? Split(configured, ':')
                    : [Path.Combine(environment.SystemRoot, "usr", "local", "share"), usrShare];
                foreach (var share in (string[])[DataHome(environment), .. dataDirs, usrShare])
                {
                    Add(Path.Combine(share, "hunspell"));
                    Add(Path.Combine(share, "myspell"));
                    Add(Path.Combine(share, "myspell", "dicts"));
                }
                break;
        }
        return folders;
    }

    private static string DataHome(SpellingEnvironment environment) =>
        environment.Value("XDG_DATA_HOME") is { } data && Path.IsPathRooted(data) ? data : Path.Combine(environment.Home, ".local", "share");

    private static string[] Split(string? list, char separator) =>
        list is null ? [] : list.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
