using System.Collections.Concurrent;
using System.Globalization;
using Runic.Desktop.Internal;

namespace Runic.Spelling;

/// <summary>
/// The Hunspell dictionaries an application can check spelling with, from three sources, and the one a language is
/// checked with. Safe to use from several threads; each call looks at the directories again, so a dictionary another Runic
/// application downloaded is found without a restart.
/// </summary>
/// <remarks>
/// <para>Sources, the first of a name winning: the application's own directories (<see cref="SpellingDictionarySource.Bundled"/>:
/// <see cref="DirectoryEnvironmentVariable"/>, then the package's English dictionaries beside the program, or
/// <see cref="SpellingDictionariesOptions.ApplicationDirectories"/>), then downloaded dictionaries
/// (<see cref="SpellingDictionarySource.Downloaded"/>), then the computer's (<see cref="SpellingDictionarySource.System"/>).
/// A dictionary that matches a language better wins over one of an earlier source that matches it worse: <c>de-CH</c>
/// takes the system's <c>de_CH</c> over a downloaded <c>de</c>.</para>
/// <para>Nothing is requested over the network but by <see cref="DownloadAsync"/>, which the application calls when the
/// user asks for a dictionary.</para>
/// </remarks>
public sealed partial class SpellingDictionaries
{
    /// <summary>The environment variable naming a directory of dictionaries that comes before every other, for development
    /// or a dictionary of the user's own: <c>RUNIC_SPELLING_DICTIONARIES</c>. Its dictionaries count as
    /// <see cref="SpellingDictionarySource.Bundled"/>.</summary>
    public const string DirectoryEnvironmentVariable = "RUNIC_SPELLING_DICTIONARIES";

    private readonly SpellingEnvironment _environment;
    private readonly IReadOnlyList<string> _applicationFolders;
    private readonly bool _useSystem;
    private readonly IReadOnlyList<string> _userLanguages;
    private readonly HttpClient? _httpClient;
    private readonly bool _allowLoopback;

    /// <summary>Dictionaries as <paramref name="options"/> say; the defaults without them.</summary>
    public SpellingDictionaries(SpellingDictionariesOptions? options = null)
    {
        options ??= new();
        _environment = options.Environment ?? SpellingEnvironment.Current;
        _applicationFolders = SpellingFolders.ApplicationDirectories(_environment, options.ApplicationDirectories);
        DownloadDirectory = options.DownloadDirectory ?? SpellingFolders.DownloadDirectory(_environment);
        _useSystem = options.UseSystemDictionaries;
        _userLanguages = options.UserLanguages ?? (CultureInfo.CurrentUICulture.Name is { Length: > 0 } culture ? [culture] : []);
        _httpClient = options.HttpClient;
        Catalog = options.Catalog ?? SpellingCatalog.Default;
        _allowLoopback = options.AllowLoopbackDownloads;
    }

    /// <summary>The directory all Runic applications of the user share for downloaded dictionaries:
    /// <c>$XDG_DATA_HOME/runic/dictionaries</c> (by default <c>~/.local/share/runic/dictionaries</c>) on Linux,
    /// <c>%LOCALAPPDATA%\Runic\dictionaries</c> on Windows, <c>~/Library/Application Support/Runic/dictionaries</c> on
    /// macOS.</summary>
    public static string SharedDownloadDirectory => SpellingFolders.DownloadDirectory(SpellingEnvironment.Current);

    /// <summary>The directory downloaded dictionaries are installed in, one directory each.</summary>
    public string DownloadDirectory { get; }

    /// <summary>The dictionaries that can be downloaded.</summary>
    public SpellingCatalog Catalog { get; }

    /// <summary>Every installed dictionary, the first of a name in the order of the sources; in that order.</summary>
    public IReadOnlyList<SpellingDictionary> GetInstalled() => [.. Installed().Values];

    /// <summary>The installed dictionary <paramref name="languageTag"/> is checked with; <see langword="null"/> when none
    /// serves it. A language is never checked with another language's dictionary.</summary>
    /// <param name="languageTag">A BCP 47 tag, such as <c>de-CH</c> or <c>en</c>.</param>
    /// <remarks>The first that is installed of: the tag's region; its neighbour (Australian, Irish, New Zealand, South
    /// African and Indian English read with British, Canadian with American, Austrian, Luxembourg and Belgian German with
    /// Germany's, Liechtenstein's with Switzerland's); for a tag without a region, the region of each of the user's
    /// languages that is this language, and its neighbour; the language's usual region (<c>en_US</c>, <c>de_DE</c>); the
    /// language alone (<c>de</c>); then any other of the language's, in order.</remarks>
    public SpellingDictionary? Find(string languageTag)
    {
        ArgumentNullException.ThrowIfNull(languageTag);
        var installed = Installed();
        return Choose(languageTag, installed.Keys) is { } name ? installed[name] : null;
    }

    /// <summary>Whether <paramref name="languageTag"/> can be checked: the installed dictionary it is checked with, and a
    /// downloadable one that would serve it better or at all.</summary>
    /// <param name="languageTag">A BCP 47 tag, such as <c>de-CH</c>.</param>
    public SpellingAvailability GetAvailability(string languageTag)
    {
        ArgumentNullException.ThrowIfNull(languageTag);
        var installed = Installed();
        var current = Choose(languageTag, installed.Keys) is { } name ? installed[name] : null;
        SpellingCatalogEntry? download = null;
        if (Choose(languageTag, [.. installed.Keys, .. Catalog.Entries.Select(entry => entry.Name)]) is { } best && !installed.ContainsKey(best))
            download = Catalog.Find(best);
        return new(languageTag, current, download);
    }

    // The installed dictionaries by their normal name (de_CH for de_CH_frami), the first in the order of the sources.
    private Dictionary<string, SpellingDictionary> Installed()
    {
        Dictionary<string, SpellingDictionary> found = new(StringComparer.Ordinal);
        void Add(SpellingDictionary dictionary)
        {
            if (SpellingDictionaryNames.Parse(dictionary.Name) is { } parsed) found.TryAdd(SpellingDictionaryNames.Name(parsed), dictionary);
        }
        foreach (var folder in _applicationFolders)
            foreach (var dictionary in InFolder(folder, SpellingDictionarySource.Bundled)) Add(dictionary);
        foreach (var dictionary in Downloaded()) Add(dictionary);
        if (_useSystem)
            foreach (var folder in SpellingFolders.SystemFolders(_environment))
                foreach (var dictionary in InFolder(folder, SpellingDictionarySource.System)) Add(dictionary);
        return found;
    }

    private string? Choose(string tag, IEnumerable<string> names)
    {
        var available = names.Distinct(StringComparer.Ordinal).ToDictionary(name => name, StringComparer.Ordinal);
        return SpellingDictionaryNames.Choose(tag, _userLanguages, available, SpellingDictionaryMatch.NearestRegion) is [var name, ..] ? name : null;
    }

    // A folder's dictionaries: each .dic with its .aff, named by its file, or by the folder when the file's name is not a
    // language (Windows keeps them in a folder per language).
    internal static IEnumerable<SpellingDictionary> InFolder(string folder, SpellingDictionarySource source)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(folder)) return [];
            files = [.. Directory.EnumerateFiles(folder, "*.dic").Order(StringComparer.Ordinal)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        List<SpellingDictionary> dictionaries = [];
        foreach (var file in files)
        {
            var affix = Path.ChangeExtension(file, ".aff");
            if (!File.Exists(affix)) continue;
            var name = Path.GetFileNameWithoutExtension(file);
            if (SpellingDictionaryNames.Parse(name) is null)
            {
                name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                if (SpellingDictionaryNames.Parse(name) is null) continue;
            }
            dictionaries.Add(new(name, source, Path.GetFullPath(file), Path.GetFullPath(affix)));
        }
        return dictionaries;
    }

    // The downloaded dictionaries: <folder>/<name>/<name>.dic and .aff, without the folders of downloads in progress, and
    // only those whose files are the ones they were installed with (InstalledDictionaryCheck); another one counts as not
    // installed, so GetAvailability offers it for download again.
    private List<SpellingDictionary> Downloaded()
    {
        string[] folders;
        try
        {
            if (!Directory.Exists(DownloadDirectory)) return [];
            folders = [.. Directory.EnumerateDirectories(DownloadDirectory).Where(folder => !Path.GetFileName(folder).StartsWith('.')).Order(StringComparer.Ordinal)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        List<SpellingDictionary> dictionaries = [];
        foreach (var folder in folders)
        {
            var name = Path.GetFileName(folder);
            var dictionary = Path.Combine(folder, name + ".dic");
            var affix = Path.Combine(folder, name + ".aff");
            if (SpellingDictionaryNames.Parse(name) is null || !File.Exists(dictionary) || !File.Exists(affix)) continue;
            if (InstalledDictionaryCheck.Expected(folder, name, Catalog) is not { } expected || !InstalledDictionaryCheck.IsIntact(expected)) continue;
            dictionaries.Add(new(name, SpellingDictionarySource.Downloaded, Path.GetFullPath(dictionary), Path.GetFullPath(affix))
            {
                InstalledFiles = expected,
            });
        }
        return dictionaries;
    }

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_installing = new(StringComparer.Ordinal);
}
