using System.Security.Cryptography;
using TUnit.Core;
using static Runic.Spelling.Tests.Check;

namespace Runic.Spelling.Tests;

// The English dictionaries the package bundles, as its build targets copy them into an application's output.
public sealed class BundledDictionaryTests
{
    // As packages/dotnet/Runic.Spelling/dictionaries/README.md lists them.
    private static readonly Dictionary<string, string> Pinned = new()
    {
        ["en_US.aff"] = "526e1dc32d1c0e8806dd72809c017b5546f2104b08b9424a4c47311aca526cca",
        ["en_US.dic"] = "ab012ae86ce4b46b60db5b150869288de9c4a87bc747911764e582125b1b961e",
        ["en_GB.aff"] = "b9fb2eef4b64cf39243565579dcedb84747d7986be0a8048f428f60db1fa0f2e",
        ["en_GB.dic"] = "4a174235eb36947496f495815b2a04019fd45818b035b84a2368bedc45bacf5d",
        ["README_en_US.txt"] = "d63c711523fc9504fb71e1278489c6d7bb8cafcba75158c107cd17844955470f",
        ["README_en_GB.txt"] = "dd2f9d4e0e2d7b20cdb4b3e8c8f6dfb2268e459a0d063e951e95b4b1986ca185",
        ["LICENSE_WeCantSpell.Hunspell.txt"] = "6d69e66d4533c9b9048db18d3f340b077313dc5d33324066f1b91cfbca15be00",
    };

    [Test]
    public void TheBundledDictionariesAreThePinnedFilesWithTheirLicences()
    {
        // Every file, and nothing else: the README of the pins stays in the package's source.
        Sequence(Pinned.Keys.Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(Environments.Bundled).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal));
        foreach (var (file, hash) in Pinned)
            Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Environments.Bundled, file)))), file);
    }

    [Test]
    public void TheApplicationFindsThemBesideItself()
    {
        using var temporary = new TemporaryFolder();
        // The default application folder is dictionaries/ beside the program: this suite's output.
        var environment = SpellingEnvironment.Current with { Variable = _ => null, Home = temporary.Path };
        var dictionaries = new SpellingDictionaries(new() { Environment = environment, UseSystemDictionaries = false });

        Sequence(["en_GB", "en_US"], dictionaries.GetInstalled().Select(dictionary => dictionary.Name));
        Equal(SpellingDictionarySource.Bundled, dictionaries.Find("en")!.Source);
    }

    [Test]
    public async Task EachDictionaryReadsItsEnglish()
    {
        var dictionaries = new SpellingDictionaries(new() { ApplicationFolders = [Environments.Bundled], UseSystemDictionaries = false, DownloadFolder = Path.GetTempPath() + "none" });
        var american = await SpellChecker.LoadAsync(dictionaries.Find("en-US")!);
        var british = await SpellChecker.LoadAsync(dictionaries.Find("en-GB")!);
        const string text = "The colour of the harbour, the color of the sea.";

        Sequence(["colour", "harbour"], american.Check(text).Select(found => found.Word));
        Sequence(["color"], british.Check(text).Select(found => found.Word));
        // en_GB is SCOWL's large dictionary: both "among" and "amongst", the Oxford -ize and -ise.
        Equal(0, british.Check("Among the realised and realized, amongst others, she learned and learnt.").Count);
        // A dictionary is read once: another checker of it shares the read.
        Equal(0, (await SpellChecker.LoadAsync(dictionaries.Find("en-US")!)).Check("The color of the sea.").Count);
    }
}
