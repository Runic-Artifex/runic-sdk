using TUnit.Core;
using static Runic.Spelling.Tests.Check;

namespace Runic.Spelling.Tests;

// The system's Hunspell folders and the shared Runic folders on each operating system, with fake folders and
// environment variables.
public sealed class SystemFolderTests
{
    [Test]
    public void LinuxReadsDicpathThenTheXdgDataFoldersThenUsrShare()
    {
        var environment = Environments.Fake(SpellingPlatform.Linux, "/r",
            ("DICPATH", "/dicts/a:/dicts/b"),
            ("XDG_DATA_HOME", "/r/data"),
            ("XDG_DATA_DIRS", "/nix/profile/share:/run/current-system/sw/share:/usr/share"));

        Sequence([
            "/dicts/a", "/dicts/b",
            "/r/data/hunspell", "/r/data/myspell", "/r/data/myspell/dicts",
            "/nix/profile/share/hunspell", "/nix/profile/share/myspell", "/nix/profile/share/myspell/dicts",
            "/run/current-system/sw/share/hunspell", "/run/current-system/sw/share/myspell", "/run/current-system/sw/share/myspell/dicts",
            "/usr/share/hunspell", "/usr/share/myspell", "/usr/share/myspell/dicts",
        ], SpellingFolders.SystemFolders(environment));

        // Without the variables: ~/.local/share, then the XDG defaults.
        Sequence([
            "/r/home/.local/share/hunspell", "/r/home/.local/share/myspell", "/r/home/.local/share/myspell/dicts",
            "/usr/local/share/hunspell", "/usr/local/share/myspell", "/usr/local/share/myspell/dicts",
            "/usr/share/hunspell", "/usr/share/myspell", "/usr/share/myspell/dicts",
        ], SpellingFolders.SystemFolders(Environments.Fake(SpellingPlatform.Linux, "/r")));
        // A relative XDG_DATA_HOME is ignored, as the specification says.
        Equal("/r/home/.local/share/runic/dictionaries",
            SpellingFolders.DownloadFolder(Environments.Fake(SpellingPlatform.Linux, "/r", ("XDG_DATA_HOME", "data"))));
        Equal("/r/data/runic/dictionaries", SpellingFolders.DownloadFolder(environment));
    }

    [Test]
    public void ANixBuiltApplicationFindsItsSpellingDictionaries()
    {
        // buildRunicApplication's spellingDictionaries put each dictionary package's share/ on the wrapper's XDG_DATA_DIRS
        // (nix/lib/build-runic-application.nix); nixpkgs' hunspellDicts install share/hunspell/<name>.dic and .aff, and
        // link them from share/myspell/dicts.
        using var temporary = new TemporaryFolder();
        var package = temporary.Folder("nix", "store", "hash-hunspell-dict-de-ch");
        TemporaryFolder.Dictionary(Path.Combine(package, "share", "hunspell"), "de_CH", "Strasse");
        TemporaryFolder.Dictionary(Path.Combine(package, "share", "myspell", "dicts"), "de_CH", "Strasse");
        var environment = Environments.Fake(SpellingPlatform.Linux, temporary.Path,
            ("XDG_DATA_DIRS", $"{Path.Combine(package, "share")}:/run/current-system/sw/share"));

        var found = new SpellingDictionaries(new() { Environment = environment }).Find("de-CH");

        Equal(SpellingDictionarySource.System, found!.Source);
        Equal(Path.Combine(package, "share", "hunspell", "de_CH.dic"), found.DictionaryPath);
    }

    [Test]
    public void MacOSReadsTheUsersAndTheComputersSpellingFolders()
    {
        var environment = Environments.Fake(SpellingPlatform.MacOS, "/r", ("DICPATH", "/ignored"));

        Sequence(["/r/home/Library/Spelling", "/Library/Spelling"], SpellingFolders.SystemFolders(environment));
        Equal("/r/home/Library/Application Support/Runic/dictionaries", SpellingFolders.DownloadFolder(environment));
        // The application's dictionaries beside the program, or in the bundle's Resources.
        Sequence(["/r/app/dictionaries", "/r/Resources/dictionaries"], SpellingFolders.ApplicationFolders(environment, null));
    }

    [Test]
    public void WindowsReadsEachLanguagesFolderOfItsSpellingFolder()
    {
        using var temporary = new TemporaryFolder();
        var spelling = temporary.Folder("roaming", "Microsoft", "Spelling");
        // Windows' own word lists of a language: default.dic without an .aff, which is not a Hunspell dictionary.
        File.WriteAllText(Path.Combine(temporary.Folder("roaming", "Microsoft", "Spelling", "en-US"), "default.dic"), "colour\n");
        TemporaryFolder.Dictionary(Path.Combine(spelling, "de-CH"), "de_CH", "Strasse");
        // A file not named for a language is named for its folder.
        TemporaryFolder.Dictionary(Path.Combine(spelling, "fr-FR"), "index", "couleur");
        var environment = Environments.Fake(SpellingPlatform.Windows, temporary.Path,
            ("APPDATA", Path.Combine(temporary.Path, "roaming")), ("LOCALAPPDATA", Path.Combine(temporary.Path, "local")));

        Sequence([Path.Combine(spelling, "de-CH"), Path.Combine(spelling, "en-US"), Path.Combine(spelling, "fr-FR")],
            SpellingFolders.SystemFolders(environment));
        Equal(Path.Combine(temporary.Path, "local", "Runic", "dictionaries"), SpellingFolders.DownloadFolder(environment));

        var dictionaries = new SpellingDictionaries(new() { Environment = environment });
        Sequence(["de_CH", "fr-FR"], dictionaries.GetInstalled().Select(dictionary => dictionary.Name));
        Equal("fr-FR", dictionaries.Find("fr")!.Name);
        Equal(null, dictionaries.Find("en"));
    }

    [Test]
    public void TheUsersWordsAreInTheSharedRunicFolder()
    {
        Equal("/r/data/runic", SpellingFolders.SharedRunicFolder(Environments.Fake(SpellingPlatform.Linux, "/r", ("XDG_DATA_HOME", "/r/data"))));
        True(SpellingUserWords.SharedPath.EndsWith(Path.Combine("runic", "user-words.txt"), StringComparison.OrdinalIgnoreCase),
            $"the shared user words are in the Runic folder: {SpellingUserWords.SharedPath}");
    }
}
