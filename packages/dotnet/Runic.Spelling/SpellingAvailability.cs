namespace Runic.Spelling;

/// <summary>Whether a language can be checked, and with what. Open: a later version may add values.</summary>
public enum SpellingAvailabilityStatus
{
    /// <summary>No installed dictionary serves the language and the catalog has none.</summary>
    Unavailable,

    /// <summary>No installed dictionary serves the language, but one can be downloaded
    /// (<see cref="SpellingAvailability.Download"/>).</summary>
    Downloadable,

    /// <summary>The application's own dictionary serves it.</summary>
    Bundled,

    /// <summary>A dictionary downloaded earlier, by this or another Runic application, serves it.</summary>
    Downloaded,

    /// <summary>A dictionary of the computer serves it.</summary>
    System,
}

/// <summary>Whether a language can be checked: the dictionary it is checked with now, and one to download, so an
/// application can say "French: not installed, download 1.4 MB".</summary>
public sealed class SpellingAvailability
{
    internal SpellingAvailability(string languageTag, SpellingDictionary? installed, SpellingCatalogEntry? download)
    {
        LanguageTag = languageTag;
        Installed = installed;
        Download = download;
    }

    /// <summary>The language asked about, as given.</summary>
    public string LanguageTag { get; }

    /// <summary>What checks it now.</summary>
    public SpellingAvailabilityStatus Status => Installed?.Source switch
    {
        SpellingDictionarySource.Bundled => SpellingAvailabilityStatus.Bundled,
        SpellingDictionarySource.Downloaded => SpellingAvailabilityStatus.Downloaded,
        SpellingDictionarySource.System => SpellingAvailabilityStatus.System,
        _ => Download is null ? SpellingAvailabilityStatus.Unavailable : SpellingAvailabilityStatus.Downloadable,
    };

    /// <summary>The installed dictionary the language is checked with; <see langword="null"/> when none serves it.</summary>
    public SpellingDictionary? Installed { get; }

    /// <summary>A catalog dictionary that serves the language better than <see cref="Installed"/>, such as <c>de_CH</c>
    /// for <c>de-CH</c> when only <c>de_DE</c> is installed, or at all; <see langword="null"/> when there is none or the
    /// best one is installed.</summary>
    public SpellingCatalogEntry? Download { get; }

    /// <summary>The bytes <see cref="Download"/> fetches; <see langword="null"/> without one.</summary>
    public long? DownloadSize => Download?.Size;
}
