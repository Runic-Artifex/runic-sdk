using TUnit.Core;
using static Runic.Spelling.Tests.Check;

namespace Runic.Spelling.Tests;

// The checker's words and rules (ported from Artificer's Storyline's WorldSpelling), skip ranges, known words and the
// user's own words.
public sealed class CheckerTests
{
    private static readonly SpellingDictionaries Dictionaries = new(new()
    {
        ApplicationFolders = [Environments.Bundled],
        UseSystemDictionaries = false,
        DownloadFolder = Path.Combine(Path.GetTempPath(), "runic-spelling-tests-none"),
    });

    private static Task<SpellChecker> Checker(string tag = "en-US", SpellCheckerOptions? options = null) =>
        SpellChecker.LoadAsync(Dictionaries.Find(tag)!, options);

    private static IEnumerable<string> Wrong(SpellChecker checker, string text, params SpellingRange[] skip) =>
        checker.Check(text, skip).Select(found => found.Word);

    [Test]
    public async Task WordsAreMarkedWhereTheyAreInTheText()
    {
        const string text = "A “wrold” of salt-white sea, and the tidewrack’s edge, ‘teh’ end.";
        var checker = await Checker();

        var found = checker.Check(text);

        Sequence(["wrold", "tidewrack’s", "teh"], found.Select(word => text.Substring(word.Start, word.Length)));
        Sequence(["wrold", "tidewrack’s", "teh"], found.Select(word => word.Word));
        // A word of joined words marks the part that is wrong.
        Equal(new Misspelling(6, 5, "wrold"), checker.Check("A sea-wrold.").Single());
        Sequence(["wrold", "teh"], Wrong(checker, "wrold-teh"));
        // Offsets are UTF-16: a letter outside the BMP counts two.
        Equal(new Misspelling(3, 5, "wrold"), checker.Check("𝒜 wrold").Single());
    }

    [Test]
    public async Task StraightAndCurlyApostrophesAreTheSame()
    {
        var checker = await Checker();

        Equal(0, checker.Check("Don't, don’t, it's, it’s, the sea's, the sea’s.").Count);
        // An apostrophe at either end, as of a quotation, is not part of the word.
        Equal(0, checker.Check("'quoted' ‘quoted’").Count);
    }

    [Test]
    public async Task AddressesNumbersAbbreviationsAndSingleLettersAreNotChecked()
    {
        var checker = await Checker();

        Equal(0, checker.Check("See https://exmaple.org/wrold or www.wrold.test, or write to wrold@exmaple.org.").Count);
        Equal(0, checker.Check("In 412 SD the HMS Gale and the NASAA ships left; 3rd b1 x-ray, q.").Count);
        // Longer words in capitals are checked, as shouting.
        Sequence(["WROLDLY"], Wrong(checker, "WROLDLY"));
    }

    [Test]
    public async Task TheRangesTheCallerNamesAreNotChecked()
    {
        var checker = await Checker();
        // A Translations editor skips MF2 placeholders and markup.
        const string message = "Helllo {$userName}, you have {#b}{$count} mesages{/b}.";
        SpellingRange[] placeholders = [Range(message, "{$userName}"), Range(message, "{#b}"), Range(message, "{$count}"), Range(message, "{/b}")];

        Sequence(["Helllo", "mesages"], Wrong(checker, message, placeholders));
        // Without them the placeholders' names count as words.
        True(Wrong(checker, message).Contains("userName"), "userName is marked without its range");
        // A word is never taken across a range: each side is checked on its own.
        const string split = "wor{$x}ld";
        Sequence(["wor", "ld"], Wrong(checker, split, Range(split, "{$x}")));
        // Overlapping ranges, in any order, and an empty one.
        const string overlapping = "teh <!-- a note, wrold --> end";
        Sequence(["teh"], Wrong(checker, overlapping, new SpellingRange(10, 10), new SpellingRange(4, 22), new SpellingRange(0, 0)));
        Throws<ArgumentOutOfRangeException>(() => checker.Check("short", [new SpellingRange(3, 5)]));
        Throws<ArgumentOutOfRangeException>(() => checker.Check("short", [new SpellingRange(-1, 2)]));
    }

    [Test]
    public async Task KnownWordsAreSpelledRight()
    {
        var checker = await Checker(options: new() { KnownWords = ["Runic Artifex", "tidewrack", "sea-glass", "Ilse’s"] });

        Equal(0, checker.Check("Runic Artifex and tidewrack, Tidewrack's edge, TIDEWRACK, sea-glass, Ilse's.").Count);
        // A word written with a capital must be written so.
        Sequence(["artifex"], Wrong(checker, "artifex"));
        // Another checker with other known words, the same dictionary.
        var other = checker.WithKnownWords(["brinewise"]);
        Sequence(["tidewrack"], Wrong(other, "tidewrack brinewise"));
        Equal(checker.Dictionary, other.Dictionary);
    }

    [Test]
    public async Task TheUsersOwnWordsAreSeenAtOnceAndKeptInTheirFile()
    {
        using var temporary = new TemporaryFolder();
        var path = Path.Combine(temporary.Path, "runic", "user-words.txt");
        var words = await SpellingUserWords.OpenAsync(path);
        var checker = await Checker(options: new() { UserWords = words });

        Sequence(["brinewise"], Wrong(checker, "brinewise"));
        True(await words.AddAsync("brinewise"), "brinewise is added");
        True(!await words.AddAsync("brinewise"), "brinewise is there");
        Equal(0, checker.Check("brinewise Brinewise").Count);
        Sequence(["brinewise"], File.ReadAllLines(path));

        // Another application's words are kept when this one adds.
        File.WriteAllLines(path, ["brinewise", "saltwort"]);
        True(await words.AddAsync("glimmerfen"), "glimmerfen is added");
        Sequence(["brinewise", "glimmerfen", "saltwort"], File.ReadAllLines(path));
        Sequence(["brinewise", "glimmerfen", "saltwort"], words.Words);
        True(words.Contains("saltwort"), "saltwort is read");

        True(await words.RemoveAsync("brinewise"), "brinewise is removed");
        Sequence(["brinewise"], Wrong(checker, "brinewise"));
        Sequence(["glimmerfen", "saltwort"], (await SpellingUserWords.OpenAsync(path)).Words);
        Throws<ArgumentException>(() => words.AddAsync("two words").GetAwaiter().GetResult());
    }

    [Test]
    public async Task SuggestionsOfferTheKnownWordsFirstThenTheDictionarys()
    {
        var checker = await Checker(options: new() { KnownWords = ["Ilse", "Cassen"] });

        Equal("Ilse", checker.Suggest("Ilsa")[0]);
        Equal("Ilse's", checker.Suggest("Ilsa's")[0]);
        Equal("Cassen", checker.Suggest("Casen")[0]);
        True(checker.Suggest("wrold").Contains("world"), "world is suggested for wrold");
        True(checker.Suggest("wrold").Count <= 8, "at most eight suggestions");
        True(checker.IsKnown("world") && !checker.IsKnown("wrold") && checker.IsKnown("NATO"), "IsKnown follows the rules");
    }

    [Test]
    public async Task ACheckerIsSafeToShareBetweenThreads()
    {
        var checker = await Checker("en-GB");
        const string text = "The colour of the harbour, wrold, the color of the sea-glass, teh end.";
        var expected = checker.Check(text);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            Enumerable.Range(0, 50).Select(_ => checker.Check(text)).ToArray())));

        foreach (var run in results.SelectMany(result => result)) Sequence(expected, run);
    }

    [Test]
    public async Task ADictionaryInACodePageIsRead()
    {
        // A system dictionary in ISO 8859-15, which .NET reads only with CodePagesEncodingProvider: œ is 0xBD.
        using var temporary = new TemporaryFolder();
        File.WriteAllBytes(Path.Combine(temporary.Path, "fr_FR.aff"), "SET ISO8859-15\n"u8.ToArray());
        File.WriteAllBytes(Path.Combine(temporary.Path, "fr_FR.dic"), [.. "2\nc"u8, 0xBD, .. "ur\nsoeur\n"u8]);
        var dictionary = new SpellingDictionaries(new() { ApplicationFolders = [temporary.Path], UseSystemDictionaries = false, DownloadFolder = temporary.Folder("downloads") }).Find("fr")!;

        True(WordLists.NeedsCodePages(dictionary.AffixPath), "ISO8859-15 needs the code pages");
        True(!WordLists.NeedsCodePages(Path.Combine(Environments.Bundled, "en_US.aff")), "UTF-8 needs none");
        var checker = await SpellChecker.LoadAsync(dictionary);
        Sequence(["coeur"], Wrong(checker, "cœur coeur soeur"));
    }

    private static SpellingRange Range(string text, string part) => new(text.IndexOf(part, StringComparison.Ordinal), part.Length);
}
