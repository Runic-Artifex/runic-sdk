using System.Collections.Immutable;

namespace Runic.Platform.Linux.Portal;

// Icon lookup by the Icon Theme specification: a theme, the themes it inherits, then hicolor; within a theme the
// directories that match the size, then the closest; unthemed icons last. PNG and SVG only.
// https://specifications.freedesktop.org/icon-theme/latest/
internal sealed class IconThemeLookup
{
    internal const string DefaultTheme = "hicolor";
    private static readonly string[] Extensions = ["png", "svg"];
    private readonly ImmutableArray<string> _baseDirectories, _fallbackDirectories;
    private readonly ImmutableArray<Theme> _themes;
    private readonly Dictionary<string, HashSet<string>?> _listings = new(StringComparer.Ordinal);

    internal enum DirectoryType { Fixed, Scalable, Threshold }

    internal sealed record ThemeDirectory(string Name, int Size, int Scale, DirectoryType Type, int MinSize, int MaxSize, int Threshold)
    {
        internal bool Matches(int size, int scale) => Scale == scale && Type switch
        {
            DirectoryType.Fixed => Size == size,
            DirectoryType.Scalable => MinSize <= size && size <= MaxSize,
            _ => Size - Threshold <= size && size <= Size + Threshold,
        };

        // The specification's DirectorySizeDistance, with the threshold bounds its pseudocode means.
        internal int Distance(int size, int scale)
        {
            int wanted = size * scale;
            (int low, int high) = Type switch
            {
                DirectoryType.Fixed => (Size * Scale, Size * Scale),
                DirectoryType.Scalable => (MinSize * Scale, MaxSize * Scale),
                _ => ((Size - Threshold) * Scale, (Size + Threshold) * Scale),
            };
            return wanted < low ? low - wanted : wanted > high ? wanted - high : 0;
        }
    }

    internal sealed record Theme(string Name, ImmutableArray<string> Inherits, ImmutableArray<ThemeDirectory> Directories);

    internal sealed record Found(string Path, ThemeDirectory? Directory);

    internal IconThemeLookup(DesktopApplicationsOptions options, string theme)
    {
        _baseDirectories = [Path.Combine(options.HomeDirectory, ".icons"), .. options.DataSearch.Select(static directory => Path.Combine(directory, "icons"))];
        _fallbackDirectories = [.. _baseDirectories, .. options.PixmapDirectories];
        _themes = Chain(theme);
    }

    internal ImmutableArray<string> ThemeNames => [.. _themes.Select(static theme => theme.Name)];

    internal static bool IsThemeName(string name) =>
        name.Length is > 0 and <= 255 && name != "." && name != ".." && name.All(static c => c > ' ' && c < 0x7f && c is not '/' and not ',');

    // The theme, depth first through the themes it inherits, then hicolor, which is always last.
    private ImmutableArray<Theme> Chain(string name)
    {
        var chain = ImmutableArray.CreateBuilder<Theme>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string current, bool last)
        {
            if (current == DefaultTheme && !last || !IsThemeName(current) || !seen.Add(current) || seen.Count > 32 || Load(current) is not { } theme) return;
            chain.Add(theme);
            foreach (var parent in theme.Inherits) Visit(parent, false);
        }
        Visit(name, false);
        Visit(DefaultTheme, true);
        return chain.ToImmutable();
    }

    // The first index.theme found in the base directories describes the theme.
    private Theme? Load(string name)
    {
        foreach (var directory in _baseDirectories)
        {
            if (DesktopKeyFile.Load(Path.Combine(directory, name, "index.theme")) is not { } file || !file.HasGroup("Icon Theme")) continue;
            var directories = ImmutableArray.CreateBuilder<ThemeDirectory>();
            foreach (var subdirectory in file.List("Icon Theme", "Directories", ',').Concat(file.List("Icon Theme", "ScaledDirectories", ',')).Distinct(StringComparer.Ordinal))
            {
                if (subdirectory.Split('/').Any(static part => part is "" or "." or "..") || Number(file, subdirectory, "Size") is not { } size) continue;
                var type = file.String(subdirectory, "Type")?.Trim() switch { "Fixed" => DirectoryType.Fixed, "Scalable" => DirectoryType.Scalable, _ => DirectoryType.Threshold };
                directories.Add(new ThemeDirectory(subdirectory, size, Math.Max(1, Number(file, subdirectory, "Scale") ?? 1), type,
                    Number(file, subdirectory, "MinSize") ?? size, Number(file, subdirectory, "MaxSize") ?? size, Number(file, subdirectory, "Threshold") ?? 2));
            }
            return new Theme(name, file.List("Icon Theme", "Inherits", ','), directories.ToImmutable());
        }
        return null;
    }

    private static int? Number(DesktopKeyFile file, string group, string key) =>
        int.TryParse(file.String(group, key)?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value) && value is > 0 and <= 65536 ? value : null;

    /// <summary>Finds the first of <paramref name="names"/> in each theme of the chain before trying the next theme,
    /// then unthemed icons, as the specification's FindBestIcon does.</summary>
    internal Found? Find(IReadOnlyList<string> names, int size, int scale)
    {
        foreach (var theme in _themes)
            foreach (var name in names)
                if (Lookup(theme, name, size, scale) is { } found) return found;
        foreach (var name in names)
            foreach (var directory in _fallbackDirectories)
                foreach (var extension in Extensions)
                    if (Has(directory, name + "." + extension)) return new Found(Path.Combine(directory, name + "." + extension), null);
        return null;
    }

    private Found? Lookup(Theme theme, string name, int size, int scale)
    {
        foreach (var directory in theme.Directories)
        {
            if (!directory.Matches(size, scale)) continue;
            foreach (var root in _baseDirectories)
                foreach (var extension in Extensions)
                    if (Has(Path.Combine(root, theme.Name, directory.Name), name + "." + extension))
                        return new Found(Path.Combine(root, theme.Name, directory.Name, name + "." + extension), directory);
        }
        Found? closest = null;
        int minimum = int.MaxValue;
        foreach (var directory in theme.Directories)
            foreach (var root in _baseDirectories)
                foreach (var extension in Extensions)
                {
                    if (!Has(Path.Combine(root, theme.Name, directory.Name), name + "." + extension)) continue;
                    int distance = directory.Distance(size, scale);
                    if (distance < minimum) { minimum = distance; closest = new Found(Path.Combine(root, theme.Name, directory.Name, name + "." + extension), directory); }
                }
        return closest;
    }

    // One listing per directory and lookup, instead of a stat per name, size and extension.
    private bool Has(string directory, string file)
    {
        if (!_listings.TryGetValue(directory, out var names))
        {
            try
            {
                names = Directory.Exists(directory)
                    ? new HashSet<string>(Directory.EnumerateFiles(directory).Select(static path => Path.GetFileName(path)), StringComparer.Ordinal)
                    : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { names = null; }
            _listings[directory] = names;
        }
        return names is not null && names.Contains(file);
    }

    /// <summary>A name and the names it falls back to by dropping '-' suffixes: foo-bar-baz, foo-bar, foo.</summary>
    internal static IEnumerable<string> DashFallbacks(string name)
    {
        for (string current = name; current.Length > 0;)
        {
            yield return current;
            int dash = current.LastIndexOf('-');
            if (dash <= 0) yield break;
            current = current[..dash];
        }
    }
}
