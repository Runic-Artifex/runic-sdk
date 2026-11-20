namespace Runic.Spelling;

/// <summary>Where <see cref="SpellingDictionaries"/> looks for dictionaries and installs downloaded ones.</summary>
public sealed class SpellingDictionariesOptions
{
    /// <summary>The application's own directories of dictionaries (<see cref="SpellingDictionarySource.Bundled"/>), each with
    /// <c>&lt;name&gt;.dic</c> and <c>&lt;name&gt;.aff</c> files, the first of a name winning. <see langword="null"/>, the
    /// default: <c>dictionaries</c> beside the program, where the package's build targets copy the English dictionaries,
    /// and on macOS in the bundle's <c>Contents/Resources</c>. <see cref="SpellingDictionaries.DirectoryEnvironmentVariable"/> comes
    /// before them either way.</summary>
    public IReadOnlyList<string>? ApplicationDirectories { get; init; }

    /// <summary>The directory downloaded dictionaries are installed in and read from. <see langword="null"/>, the default:
    /// the directory all Runic applications of the user share (<see cref="SpellingDictionaries.SharedDownloadDirectory"/>).</summary>
    public string? DownloadDirectory { get; init; }

    /// <summary>Whether the computer's Hunspell dictionaries are used. Default <see langword="true"/>.</summary>
    public bool UseSystemDictionaries { get; init; } = true;

    /// <summary>The user's languages, most preferred first, as BCP 47 tags or POSIX locales (<c>de_CH.UTF-8</c>): they
    /// choose the region of a language asked for without one, such as <c>en_GB</c> for <c>en</c>. Runic applications pass
    /// <c>Runic.Platform.DesktopLanguages.Preferred</c>. <see langword="null"/>, the default: the current UI culture.</summary>
    public IReadOnlyList<string>? UserLanguages { get; init; }

    /// <summary>The client downloads use. <see langword="null"/>, the default: one of the package's own, which sends a
    /// generic <c>User-Agent</c> and nothing that identifies the user or the application.</summary>
    public HttpClient? HttpClient { get; init; }

    // The tests' fakes: the folders' environment, and a catalogue of a local server.
    internal SpellingEnvironment? Environment { get; init; }

    internal SpellingCatalog? Catalog { get; init; }

    internal bool AllowLoopbackDownloads { get; init; }
}
