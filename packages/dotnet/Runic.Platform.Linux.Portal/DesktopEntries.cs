using System.Collections.Immutable;

namespace Runic.Platform.Linux.Portal;

// One application desktop entry: the keys of its [Desktop Entry] group this provider uses.
// https://specifications.freedesktop.org/desktop-entry/latest/recognized-keys.html
internal sealed class DesktopEntry
{
    internal const string Group = "Desktop Entry";
    internal required string Id { get; init; }
    internal required string FilePath { get; init; }
    internal required string Name { get; init; }
    internal string? GenericName { get; init; }
    internal string? Comment { get; init; }
    internal string? Icon { get; init; }
    internal string? Exec { get; init; }
    internal string? WorkingDirectory { get; init; }
    internal bool Terminal { get; init; }
    internal bool NoDisplay { get; init; }
    internal bool DBusActivatable { get; init; }
    internal ImmutableArray<string> MimeTypes { get; init; } = [];
    internal ImmutableArray<string> OnlyShowIn { get; init; } = [];
    internal ImmutableArray<string> NotShowIn { get; init; } = [];

    // An entry that is not a usable application: not an application, deleted with Hidden, missing a required key, or
    // whose TryExec program is not installed. It still shadows entries of the same ID in later directories.
    internal static DesktopEntry? Parse(string id, string path, DesktopKeyFile file, DesktopLocale locale, ImmutableArray<string> searchPath)
    {
        if (file.Groups.IsEmpty || file.Groups[0] != Group) return null;
        if (file.String(Group, "Type") != "Application" || file.Boolean(Group, "Hidden")) return null;
        string? name = file.LocaleString(Group, "Name", locale);
        if (string.IsNullOrWhiteSpace(name)) return null;
        string? exec = file.String(Group, "Exec");
        bool dbus = file.Boolean(Group, "DBusActivatable");
        if (string.IsNullOrWhiteSpace(exec) && !dbus) return null;
        if (file.String(Group, "TryExec") is { Length: > 0 } tryExec && FindProgram(tryExec, searchPath) is null) return null;
        return new DesktopEntry
        {
            Id = id, FilePath = path, Name = name,
            GenericName = file.LocaleString(Group, "GenericName", locale),
            Comment = file.LocaleString(Group, "Comment", locale),
            Icon = file.LocaleString(Group, "Icon", locale) is { Length: > 0 } icon ? icon : null,
            Exec = string.IsNullOrWhiteSpace(exec) ? null : exec,
            WorkingDirectory = file.String(Group, "Path") is { Length: > 0 } directory ? directory : null,
            Terminal = file.Boolean(Group, "Terminal"),
            NoDisplay = file.Boolean(Group, "NoDisplay"),
            DBusActivatable = dbus,
            MimeTypes = file.List(Group, "MimeType"),
            OnlyShowIn = file.List(Group, "OnlyShowIn"),
            NotShowIn = file.List(Group, "NotShowIn"),
        };
    }

    /// <summary>Whether the desktop shows the entry in application lists.</summary>
    internal bool Shown(ImmutableArray<string> desktops) =>
        !NoDisplay && (OnlyShowIn.IsEmpty || OnlyShowIn.Any(desktops.Contains)) && !NotShowIn.Any(desktops.Contains);

    internal DesktopApplicationInfo Info(ImmutableArray<string> desktops, SharedMimeDatabase? mime) => new(Id, Name)
    {
        Description = Comment ?? GenericName,
        IconName = Icon,
        ContentTypes = [.. MimeTypes.Select(type => mime?.Unalias(type) ?? type).Distinct(StringComparer.OrdinalIgnoreCase)],
        Hidden = !Shown(desktops),
    };

    /// <summary>The executable a program name or absolute path refers to, or null.</summary>
    internal static string? FindProgram(string program, ImmutableArray<string> searchPath)
    {
        if (program.Contains('/')) return Path.IsPathFullyQualified(program) && IsExecutable(program) ? program : null;
        foreach (var directory in searchPath)
        {
            string candidate = Path.Combine(directory, program);
            if (IsExecutable(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsExecutable(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            return OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

// The desktop entries of every data directory's applications subdirectory, by desktop file ID. An ID is the path
// below applications with '/' replaced by '-'; the first directory in XDG_DATA_HOME, XDG_DATA_DIRS order that has an
// ID provides it, even when its entry is not usable.
// https://specifications.freedesktop.org/desktop-entry/latest/file-naming.html
internal sealed class DesktopEntryCatalog
{
    private readonly DesktopApplicationsOptions _options;
    private readonly DesktopLocale _locale;
    private readonly ImmutableArray<string> _dataSearch;
    private readonly List<Dictionary<string, string>> _directories = [];
    private readonly Dictionary<string, int> _owner = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DesktopEntry?> _parsed = new(StringComparer.Ordinal);

    internal DesktopEntryCatalog(DesktopApplicationsOptions options, DesktopLocale locale)
    {
        _options = options; _locale = locale; _dataSearch = options.DataSearch;
        for (int index = 0; index < _dataSearch.Length; index++)
        {
            var ids = Scan(Path.Combine(_dataSearch[index], "applications"));
            _directories.Add(ids);
            foreach (var id in ids.Keys) _owner.TryAdd(id, index);
        }
    }

    internal int DirectoryCount => _directories.Count;
    internal string ApplicationsDirectory(int index) => Path.Combine(_dataSearch[index], "applications");

    /// <summary>The IDs physically present in data directory <paramref name="index"/>.</summary>
    internal IEnumerable<string> IdsIn(int index) => _directories[index].Keys;

    /// <summary>The data directory that provides <paramref name="id"/>, or -1.</summary>
    internal int Owner(string id) => _owner.TryGetValue(id, out int index) ? index : -1;

    /// <summary>The usable entry for <paramref name="id"/>, or null when it is unknown, shadowed by an unusable entry or unusable.</summary>
    internal DesktopEntry? Get(string id)
    {
        if (_parsed.TryGetValue(id, out var cached)) return cached;
        DesktopEntry? entry = null;
        if (_owner.TryGetValue(id, out int index) && DesktopKeyFile.Load(_directories[index][id]) is { } file)
            entry = DesktopEntry.Parse(id, _directories[index][id], file, _locale, _options.SearchPath);
        _parsed[id] = entry;
        return entry;
    }

    internal IEnumerable<DesktopEntry> All() => _owner.Keys.Order(StringComparer.Ordinal).Select(Get).OfType<DesktopEntry>();

    private static Dictionary<string, string> Scan(string root)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return ids;
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 8, IgnoreInaccessible = true, AttributesToSkip = 0 };
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.desktop", options).Order(StringComparer.Ordinal))
            {
                if (!path.EndsWith(".desktop", StringComparison.Ordinal)) continue;
                ids.TryAdd(Path.GetRelativePath(root, path).Replace('/', '-'), path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return ids;
    }
}

// Default applications and associations from mimeapps.list, mimeinfo.cache and the entries' MimeType keys.
// https://specifications.freedesktop.org/mime-apps/latest/
internal sealed class DesktopAssociations
{
    internal const string DefaultGroup = "Default Applications", AddedGroup = "Added Associations", RemovedGroup = "Removed Associations";

    // A directory of mimeapps.list files in the specification's order: XDG_CONFIG_HOME, XDG_CONFIG_DIRS,
    // XDG_DATA_HOME/applications, XDG_DATA_DIRS/applications. Desktop-specific files only name defaults.
    private sealed record Level(ImmutableArray<Lists> DesktopDefaults, Lists Main, int DataDirectory, Lists Cache);

    // One file's groups, keyed by canonical type.
    private sealed class Lists
    {
        internal static readonly Lists Empty = new(null, null);
        private readonly Dictionary<string, Dictionary<string, ImmutableArray<string>>> _groups = new(StringComparer.Ordinal);

        internal Lists(DesktopKeyFile? file, SharedMimeDatabase? mime)
        {
            if (file is null) return;
            foreach (var group in file.Groups)
            {
                var lists = new Dictionary<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in file.Keys(group))
                {
                    string type = mime?.Unalias(key) ?? key;
                    var ids = file.List(group, key);
                    lists[type] = lists.TryGetValue(type, out var earlier) ? [.. earlier, .. ids] : ids;
                }
                _groups[group] = lists;
            }
        }

        internal ImmutableArray<string> Get(string group, string type) =>
            _groups.TryGetValue(group, out var lists) && lists.TryGetValue(type, out var ids) ? ids : [];
    }

    private readonly DesktopEntryCatalog _catalog;
    private readonly SharedMimeDatabase _mime;
    private readonly List<Level> _levels = [];
    private readonly Dictionary<int, Dictionary<string, List<string>>> _declared = [];
    private readonly Dictionary<string, ImmutableArray<string>> _associations = new(StringComparer.OrdinalIgnoreCase);

    internal DesktopAssociations(DesktopApplicationsOptions options, DesktopEntryCatalog catalog, SharedMimeDatabase mime)
    {
        _catalog = catalog; _mime = mime;
        var desktops = options.CurrentDesktops.Select(static desktop => desktop.ToLowerInvariant()).Distinct().ToImmutableArray();
        foreach (var directory in (ImmutableArray<string>)[options.ConfigHome, .. options.ConfigDirectories])
            _levels.Add(LoadLevel(directory, desktops, -1));
        for (int index = 0; index < catalog.DirectoryCount; index++)
            _levels.Add(LoadLevel(catalog.ApplicationsDirectory(index), desktops, index));
    }

    private Level LoadLevel(string directory, ImmutableArray<string> desktops, int dataDirectory) => new(
        [.. desktops.Select(desktop => new Lists(DesktopKeyFile.Load(Path.Combine(directory, desktop + "-mimeapps.list")), _mime))],
        new Lists(DesktopKeyFile.Load(Path.Combine(directory, "mimeapps.list")), _mime),
        dataDirectory,
        dataDirectory < 0 ? Lists.Empty : new Lists(DesktopKeyFile.Load(Path.Combine(directory, "mimeinfo.cache")), _mime));

    /// <summary>The applications associated with exactly <paramref name="type"/>, most preferred first, by the
    /// specification's algorithm: added associations, then each directory's entries, minus removed associations and
    /// entries shadowed by a directory of higher precedence.</summary>
    internal ImmutableArray<string> Associations(string type)
    {
        type = _mime.Unalias(type);
        if (_associations.TryGetValue(type, out var cached)) return cached;
        var results = new List<string>();
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var level in _levels)
        {
            // An addition applies only to an entry at this level or a later one; later directories' entries are not
            // yet blocked, earlier ones are.
            foreach (var id in level.Main.Get(AddedGroup, type))
                if (!blocked.Contains(id) && _catalog.Owner(id) >= Math.Max(0, level.DataDirectory) && Usable(id) && !results.Contains(id)) results.Add(id);
            blocked.UnionWith(level.Main.Get(RemovedGroup, type));
            if (level.DataDirectory < 0) continue;
            foreach (var id in Declared(level, type))
                if (!blocked.Contains(id) && !results.Contains(id)) results.Add(id);
            blocked.UnionWith(_catalog.IdsIn(level.DataDirectory));
        }
        return _associations[type] = [.. results];
    }

    // The usable entries of one directory whose MimeType key lists the type: in mimeinfo.cache order, which
    // update-desktop-database writes, then any the cache does not know, by ID. The entries themselves decide.
    private IEnumerable<string> Declared(Level level, string type)
    {
        if (!_declared.TryGetValue(level.DataDirectory, out var byType))
        {
            byType = new(StringComparer.OrdinalIgnoreCase);
            foreach (var id in _catalog.IdsIn(level.DataDirectory).Order(StringComparer.Ordinal))
            {
                if (_catalog.Owner(id) != level.DataDirectory || _catalog.Get(id) is not { } entry) continue;
                foreach (var declared in entry.MimeTypes.Select(_mime.Unalias).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!byType.TryGetValue(declared, out var ids)) byType[declared] = ids = [];
                    ids.Add(id);
                }
            }
            _declared[level.DataDirectory] = byType;
        }
        if (!byType.TryGetValue(type, out var declaredIds)) return [];
        var cached = level.Cache.Get("MIME Cache", type).Where(declaredIds.Contains);
        return cached.Concat(declaredIds).Distinct(StringComparer.Ordinal);
    }

    /// <summary>The applications for <paramref name="type"/> and, after them, for the types it is an instance of.</summary>
    internal ImmutableArray<string> Recommended(string type)
    {
        type = _mime.Unalias(type);
        return [.. ((IEnumerable<string>)[type]).Concat(_mime.Parents(type)).SelectMany(candidate => Associations(candidate)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The default application for <paramref name="type"/>: for the type and then each type it is an instance of,
    /// the first usable, associated entry each mimeapps.list names, else the most preferred association.</summary>
    /// <remarks>The implicit application/octet-stream parent is not inherited: an unknown format has no default rather
    /// than a hex editor's, while <see cref="Recommended"/> still lists such applications last.</remarks>
    internal string? Default(string type)
    {
        type = _mime.Unalias(type);
        var parents = _mime.Parents(type).Where(static parent => !parent.Equals(SharedMimeDatabase.OctetStream, StringComparison.OrdinalIgnoreCase));
        foreach (var candidate in ((IEnumerable<string>)[type]).Concat(parents))
        {
            var associated = Recommended(candidate);
            foreach (var level in _levels)
                foreach (var file in level.DesktopDefaults.Append(level.Main))
                    foreach (var id in file.Get(DefaultGroup, candidate))
                        if (Usable(id) && associated.Contains(id)) return id;
            if (Associations(candidate) is [var preferred, ..]) return preferred;
        }
        return null;
    }

    private bool Usable(string id) => _catalog.Get(id) is not null;
}
