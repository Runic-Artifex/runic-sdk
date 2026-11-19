using Runic.Desktop.Internal;

namespace Runic.Spelling;

/// <summary>Where an installed dictionary comes from. Closed: these are the three sources.</summary>
public enum SpellingDictionarySource
{
    /// <summary>The application's own folder: the dictionaries the package bundles (English), the folders the application
    /// names, or <see cref="SpellingDictionaries.FolderVariable"/>.</summary>
    Bundled,

    /// <summary>Downloaded from the catalogue into the folder all Runic applications of the user share.</summary>
    Downloaded,

    /// <summary>A Hunspell dictionary of the computer, such as one in <c>/usr/share/hunspell</c>.</summary>
    System,
}

/// <summary>An installed Hunspell dictionary: a <c>.dic</c> file and its <c>.aff</c> file.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "A spelling dictionary, as Hunspell names its word lists; not a collection.")]
public sealed class SpellingDictionary
{
    internal SpellingDictionary(string name, SpellingDictionarySource source, string dictionaryPath, string affixPath)
    {
        Name = name;
        Source = source;
        DictionaryPath = dictionaryPath;
        AffixPath = affixPath;
        var parsed = SpellingDictionaryNames.Parse(name);
        LanguageTag = parsed is { } language
            ? string.Join('-', new[] { language.Language, language.Script, language.Region }.OfType<string>())
            : name.Replace('_', '-');
    }

    /// <summary>The dictionary's name as Hunspell names it, such as <c>de_CH</c>, <c>en_GB</c> or <c>de</c>.</summary>
    public string Name { get; }

    /// <summary>The language and region it checks, as a BCP 47 tag, such as <c>de-CH</c>.</summary>
    public string LanguageTag { get; }

    /// <summary>Where it comes from.</summary>
    public SpellingDictionarySource Source { get; }

    /// <summary>The full path of its <c>.dic</c> file.</summary>
    public string DictionaryPath { get; }

    /// <summary>The full path of its <c>.aff</c> file.</summary>
    public string AffixPath { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Source}, {DictionaryPath})";
}
