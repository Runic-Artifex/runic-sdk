using Runic.Desktop.Internal;
using TUnit.Core;
using static Runic.Spelling.Tests.Check;

namespace Runic.Spelling.Tests;

// The dictionary a BCP 47 tag is checked with (SpellingDictionaryNames, shared with Runic.Desktop, NearestRegion), and
// which source wins when several have a language.
public sealed class DictionaryChoiceTests
{
    private static readonly string[] Shipped = ["de_CH", "de_DE", "en_GB", "en_US"];

    private static string? Choose(string tag, string[] available, params string[] userLanguages) =>
        SpellingDictionaryNames.Choose(tag, userLanguages, SpellingDictionaryNames.Index(available), SpellingDictionaryMatch.NearestRegion) is [var name, ..] ? name : null;

    [Test]
    [Arguments("de-CH", "de_CH")]
    [Arguments("en-gb", "en_GB")]
    // A region without a dictionary reads with its neighbour's, then the language's usual one.
    [Arguments("en-AU", "en_GB")]
    [Arguments("en-NZ", "en_GB")]
    [Arguments("en-CA", "en_US")]
    [Arguments("de-AT", "de_DE")]
    [Arguments("de-LI", "de_CH")]
    [Arguments("en-JM", "en_US")]
    [Arguments("de", "de_DE")]
    // A language with none is not checked, rather than read as another.
    [Arguments("fr", null)]
    [Arguments("fr-CH", null)]
    [Arguments("not a tag", null)]
    [DisplayName("ATagTakesTheNearestDictionary(tag: $tag)")]
    public void ATagTakesTheNearestDictionary(string tag, string? expected) => Equal(expected, Choose(tag, Shipped));

    [Test]
    public void ALanguageWithoutRegionTakesTheUsersRegionThenTheUsualOneThenAny()
    {
        Equal("en_GB", Choose("en", Shipped, "en_GB.UTF-8"));
        Equal("en_US", Choose("en", Shipped, "de_DE.UTF-8"));
        // The first of the user's languages that is this language decides; its neighbour serves it.
        Equal("en_GB", Choose("en", Shipped, "de-CH", "en-AU", "en-US"));
        Equal("de_CH", Choose("de", Shipped, "de-CH"));
        // Any of the language's when its usual one is not there.
        Equal("de_CH", Choose("de", ["de_CH"]));
        // A dictionary named for the language alone serves its regions, after the usual region.
        Equal("de", Choose("de-CH", ["de", "en"]));
        Equal("de_DE", Choose("de-CH", ["de", "de_DE"]));
        // Scripts stay apart.
        Equal("sr_Latn", Choose("sr-Latn-RS", ["sr", "sr_Latn"]));
        Equal("sr", Choose("sr", ["sr", "sr_Latn"]));
        Equal(null, Choose("sr-Latn", ["sr"]));
        // Names with a suffix, as LibreOffice's de_DE_frami, read as their language and region.
        Equal("de_DE_frami", Choose("de-DE", ["de_DE_frami", "en_US"]));
    }

    [Test]
    public void TheApplicationsDictionariesComeBeforeDownloadedOnesAndThoseBeforeTheSystems()
    {
        using var temporary = new TemporaryFolder();
        var app = temporary.Folder("app", "dictionaries");
        var downloads = temporary.Folder("home", ".local", "share", "runic", "dictionaries");
        var system = temporary.Folder("share", "hunspell");
        var variable = temporary.Folder("override");
        foreach (var folder in new[] { app, system, variable }) TemporaryFolder.Dictionary(folder, "en_GB", "colour");
        TemporaryFolder.Downloaded(downloads, "en_GB", "colour");
        TemporaryFolder.Downloaded(downloads, "de_CH", "Strasse");
        TemporaryFolder.Dictionary(system, "de_CH", "Strasse");
        TemporaryFolder.Dictionary(system, "fr_FR", "couleur");
        // A download in progress is not installed.
        TemporaryFolder.Dictionary(Path.Combine(downloads, ".download-it_IT-1"), "it_IT", "colore");
        var environment = Environments.Fake(SpellingPlatform.Linux, temporary.Path, ("XDG_DATA_DIRS", Path.Combine(temporary.Path, "share")));

        var dictionaries = new SpellingDictionaries(new() { Environment = environment });

        Equal(SpellingDictionarySource.Bundled, dictionaries.Find("en-GB")!.Source);
        Equal(Path.Combine(app, "en_GB.dic"), dictionaries.Find("en-GB")!.DictionaryPath);
        Equal(SpellingDictionarySource.Downloaded, dictionaries.Find("de-CH")!.Source);
        Equal(SpellingDictionarySource.System, dictionaries.Find("fr")!.Source);
        Equal(null, dictionaries.Find("it"));
        Sequence(["en_GB", "de_CH", "fr_FR"], dictionaries.GetInstalled().Select(dictionary => dictionary.Name));
        Equal("de-CH", dictionaries.Find("de-CH")!.LanguageTag);

        // RUNIC_SPELLING_DICTIONARIES comes first.
        var overridden = new SpellingDictionaries(new() { Environment = environment with { Variable = name =>
            name == SpellingDictionaries.DirectoryEnvironmentVariable ? variable : environment.Variable(name) } });
        Equal(Path.Combine(variable, "en_GB.dic"), overridden.Find("en")!.DictionaryPath);

        // Without the system's.
        Equal(null, new SpellingDictionaries(new() { Environment = environment, UseSystemDictionaries = false }).Find("fr"));
        // The application's folders replace dictionaries/ beside the program.
        var own = new SpellingDictionaries(new() { Environment = environment, ApplicationDirectories = [variable], UseSystemDictionaries = false });
        Equal(Path.Combine(variable, "en_GB.dic"), own.Find("en-GB")!.DictionaryPath);
    }

    [Test]
    public void ABetterMatchOfALaterSourceWinsOverAWorseOneOfAnEarlier()
    {
        using var temporary = new TemporaryFolder();
        var downloads = temporary.Folder("downloads");
        TemporaryFolder.Downloaded(downloads, "de", "Strasse");
        var system = temporary.Folder("system");
        TemporaryFolder.Dictionary(system, "de_CH", "Strasse");

        var dictionaries = new SpellingDictionaries(new()
        {
            Environment = Environments.Fake(SpellingPlatform.Linux, temporary.Path, ("DICPATH", system)),
            DownloadDirectory = downloads,
        });

        Equal("de_CH", dictionaries.Find("de-CH")!.Name);
        Equal("de", dictionaries.Find("de-DE")!.Name);
    }

    [Test]
    public void AvailabilitySaysWhatChecksALanguageAndWhatCanBeDownloaded()
    {
        using var temporary = new TemporaryFolder();
        var system = temporary.Folder("system");
        TemporaryFolder.Dictionary(system, "de_DE", "Strasse");
        var dictionaries = new SpellingDictionaries(new()
        {
            Environment = Environments.Fake(SpellingPlatform.Linux, temporary.Path, ("DICPATH", system)),
            ApplicationDirectories = [Environments.Bundled],
        });

        var english = dictionaries.GetAvailability("en-GB");
        Equal(SpellingAvailabilityStatus.Bundled, english.Status);
        Equal(null, english.Download);
        Equal(null, english.DownloadSize);

        // Swiss German: Germany's is installed, Switzerland's can be downloaded.
        var swiss = dictionaries.GetAvailability("de-CH");
        Equal(SpellingAvailabilityStatus.System, swiss.Status);
        Equal("de_DE", swiss.Installed!.Name);
        Equal("de_CH", swiss.Download!.Name);
        Equal(swiss.Download.Size, swiss.DownloadSize);
        True(swiss.DownloadSize > 1_000_000, $"de_CH has megabytes, not {swiss.DownloadSize} bytes");
        Equal(SpellingAvailabilityStatus.System, dictionaries.GetAvailability("de-DE").Status);
        Equal(null, dictionaries.GetAvailability("de-DE").Download);

        var french = dictionaries.GetAvailability("fr-FR");
        Equal(SpellingAvailabilityStatus.Downloadable, french.Status);
        Equal(null, french.Installed);
        Equal("fr", french.Download!.Name);

        Equal(SpellingAvailabilityStatus.Unavailable, dictionaries.GetAvailability("zu").Status);
        Equal(SpellingAvailabilityStatus.Unavailable, dictionaries.GetAvailability("not a tag").Status);
    }
}
