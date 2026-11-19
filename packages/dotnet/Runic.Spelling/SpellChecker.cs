using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;
using WeCantSpell.Hunspell;

namespace Runic.Spelling;

/// <summary>A word the checker does not know, by where it is in the text it was given.</summary>
/// <param name="Start">Its first UTF-16 code unit in the text.</param>
/// <param name="Length">Its length in UTF-16 code units.</param>
/// <param name="Word">The word as the text has it.</param>
public readonly record struct Misspelling(int Start, int Length, string Word);

/// <summary>A part of a text the checker leaves out, such as a placeholder, markup or a comment, in UTF-16 code units.</summary>
/// <param name="Start">Its first UTF-16 code unit in the text.</param>
/// <param name="Length">Its length in UTF-16 code units.</param>
public readonly record struct SpellingRange(int Start, int Length);

/// <summary>What a <see cref="SpellChecker"/> knows besides its dictionary.</summary>
public sealed class SpellCheckerOptions
{
    /// <summary>Words that count as spelled right, such as an application's or a project's own words and names; a text
    /// of several words adds each. A word written with a capital must be written so; one in lower case may also start a
    /// sentence, and any may be written all in capitals or with <c>'s</c>.</summary>
    public IEnumerable<string>? KnownWords { get; init; }

    /// <summary>The user's own words, which count as spelled right too; the checker sees words added and removed at
    /// once.</summary>
    public SpellingUserWords? UserWords { get; init; }
}

/// <summary>
/// Checks spelling with one Hunspell dictionary and the words the application and the user know. Safe to use from
/// several threads. A dictionary is read once and shared by every checker of it.
/// </summary>
/// <remarks>
/// <para>Words are runs of letters, with apostrophes (straight or curly) and hyphens between letters. A word of words
/// joined by hyphens, such as <c>sea-glass</c>, is right when each part is, and each wrong part is marked on its own.</para>
/// <para>Not checked: the ranges the caller names, web and mail addresses, words with digits, single letters, and
/// words of up to <see cref="AcronymLength"/> letters all in capitals, such as NATO.</para>
/// </remarks>
public sealed partial class SpellChecker
{
    /// <summary>The longest word all in capitals that is taken as an abbreviation and not checked.</summary>
    public const int AcronymLength = 6;

    private const int MaxSuggestions = 8;
    private const int MaxSuggestedWordLength = 60;

    private readonly WordList _dictionary;
    private readonly KnownWords _known;
    private readonly SpellingUserWords? _userWords;

    private SpellChecker(SpellingDictionary dictionary, WordList words, KnownWords known, SpellingUserWords? userWords)
    {
        Dictionary = dictionary;
        _dictionary = words;
        _known = known;
        _userWords = userWords;
    }

    /// <summary>Reads <paramref name="dictionary"/>, or takes it from an earlier read, and makes a checker of it.</summary>
    /// <exception cref="IOException">Its files could not be read.</exception>
    public static Task<SpellChecker> LoadAsync(SpellingDictionary dictionary, SpellCheckerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        var known = KnownWords.Of(options?.KnownWords ?? []);
        return Task.Run(() => new SpellChecker(dictionary, WordLists.Load(dictionary), known, options?.UserWords), cancellationToken);
    }

    /// <summary>The dictionary it checks with.</summary>
    public SpellingDictionary Dictionary { get; }

    /// <summary>A checker of the same dictionary and user's words that knows <paramref name="knownWords"/> in place of
    /// this one's known words, such as when a project's words change.</summary>
    public SpellChecker WithKnownWords(IEnumerable<string> knownWords)
    {
        ArgumentNullException.ThrowIfNull(knownWords);
        return new(Dictionary, _dictionary, KnownWords.Of(knownWords), _userWords);
    }

    /// <summary>The words <paramref name="text"/> spells wrong, in its order, outside <paramref name="skip"/>.</summary>
    /// <param name="text">The text, such as a message or a paragraph.</param>
    /// <param name="skip">Ranges of <paramref name="text"/> not to check, such as placeholders and markup, in any order;
    /// they may overlap. A word is never taken across one: the text on each side is checked on its own.</param>
    /// <exception cref="ArgumentOutOfRangeException">A range is not inside <paramref name="text"/>.</exception>
    public IReadOnlyList<Misspelling> Check(string text, IReadOnlyList<SpellingRange>? skip = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<(int Start, int End)> skipped = [];
        foreach (var range in skip ?? [])
        {
            if (range.Start < 0 || range.Length < 0 || range.Start > text.Length - range.Length)
                throw new ArgumentOutOfRangeException(nameof(skip), $"The range {range.Start}+{range.Length} is not inside the text of {text.Length}.");
            if (range.Length > 0) skipped.Add((range.Start, range.Start + range.Length));
        }
        if (text.Length == 0) return [];
        if (text.Contains("://", StringComparison.Ordinal) || text.Contains("www.", StringComparison.OrdinalIgnoreCase) || text.Contains('@'))
        {
            foreach (Match address in Address().Matches(text)) skipped.Add((address.Index, address.Index + address.Length));
        }
        var known = Known();
        List<Misspelling> found = [];
        var at = 0;
        foreach (var (start, end) in skipped.OrderBy(range => range.Start).Append((text.Length, text.Length)))
        {
            if (start > at) CheckPart(text, at, start, known, found);
            at = Math.Max(at, end);
        }
        return found;
    }

    /// <summary>Whether <paramref name="word"/> is spelled right: a known word, the user's, or the dictionary's; or one the
    /// checker does not check, such as an abbreviation.</summary>
    public bool IsKnown(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return IsKnown(word, Known());
    }

    /// <summary>What <paramref name="word"/> may have been meant as: the known and the user's words close to it first,
    /// then the dictionary's suggestions; at most eight.</summary>
    public IReadOnlyList<string> Suggest(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        var plain = Apostrophes(word);
        // King's: the suggestions for King, each with its 's.
        var suffix = plain.Length > 2 && plain.EndsWith("'s", StringComparison.OrdinalIgnoreCase) ? word[^2..] : "";
        var stem = suffix.Length > 0 ? word[..^2] : word;
        List<string> suggestions = [.. Close(stem, Known()).Take(MaxSuggestions / 2).Select(close => close + suffix)];
        if (stem.Length <= MaxSuggestedWordLength)
        {
            foreach (var suggestion in _dictionary.Suggest(Apostrophes(stem)))
            {
                if (suggestions.Count >= MaxSuggestions) break;
                // A word broken in two where one is a scrap, as "Ca sen" or "Case n" for "Casen", helps nobody.
                var parts = suggestion.Split(' ', '-');
                if (parts.Length > 1 && !stem.Contains(' ') && !stem.Contains('-') && parts.Any(part => part.Length <= 2)) continue;
                if (!suggestions.Contains(suggestion + suffix, StringComparer.Ordinal)) suggestions.Add(suggestion + suffix);
            }
        }
        return suggestions;
    }

    /// <summary>The words of <paramref name="text"/> from <paramref name="from"/> to <paramref name="to"/>: runs of
    /// letters, with apostrophes and hyphens between letters, and digits, which the checker leaves out. An apostrophe or
    /// hyphen at either end, as of a quotation, is not part of the word.</summary>
    internal static IEnumerable<(int Start, int End, string Text)> Words(string text, int from = 0, int to = -1)
    {
        if (to < 0) to = text.Length;
        var at = from;
        while (at < to)
        {
            if (!IsLetter(text, at))
            {
                at++;
                continue;
            }
            var start = at;
            while (at < to && (IsLetter(text, at) || (IsJoiner(text[at]) && at + 1 < to && IsLetter(text, at + 1) && IsLetter(text, at - 1))))
                at++;
            yield return (start, at, text[start..at]);
        }
    }

    /// <summary>Words that are not checked: a single letter, one with a digit, or an abbreviation all in capitals of up to
    /// <see cref="AcronymLength"/> letters.</summary>
    internal static bool Skips(string word)
    {
        var letters = 0;
        var lower = false;
        foreach (var c in word)
        {
            if (char.IsDigit(c)) return true;
            if (char.IsLetter(c)) letters++;
            if (char.IsLower(c)) lower = true;
        }
        return letters <= 1 || (!lower && letters <= AcronymLength);
    }

    private void CheckPart(string text, int from, int to, KnownWords[] known, List<Misspelling> found)
    {
        foreach (var (start, end, word) in Words(text, from, to))
        {
            if (IsKnown(word, known)) continue;
            // A word of words joined by hyphens is right when each of them is, as "salt-grey"; otherwise each wrong one
            // is marked.
            if (word.Contains('-'))
            {
                var at = start;
                foreach (var part in word.Split('-'))
                {
                    if (part.Length > 0 && !IsKnown(part, known)) found.Add(new(at, part.Length, part));
                    at += part.Length + 1;
                }
                continue;
            }
            found.Add(new(start, end - start, word));
        }
    }

    private bool IsKnown(string word, KnownWords[] known)
    {
        if (Skips(word) || Listed(word, known)) return true;
        // Runic's, the King’s: the word the 's is added to.
        var plain = Apostrophes(word);
        if (plain.Length > 2 && (plain.EndsWith("'s", StringComparison.Ordinal) || plain.EndsWith("'S", StringComparison.Ordinal)) && Listed(plain[..^2], known))
            return true;
        return _dictionary.Check(plain) || (plain != word && _dictionary.Check(word));
    }

    private static bool Listed(string word, KnownWords[] known)
    {
        foreach (var words in known)
        {
            if (words.Contains(word)) return true;
        }
        return false;
    }

    private KnownWords[] Known() => _userWords is null ? [_known] : [_known, _userWords.Known];

    // The known words close to word: as close as one letter in four, the closest first.
    private static IEnumerable<string> Close(string word, KnownWords[] known)
    {
        if (word.Length < 2) return [];
        var lower = word.ToLowerInvariant();
        var allowed = Math.Max(1, word.Length / 4);
        List<(string Text, int Distance)> close = [];
        foreach (var words in known)
        {
            foreach (var candidate in words.Written)
            {
                if (Math.Abs(candidate.Length - word.Length) > allowed || candidate == word) continue;
                var distance = Distance(lower, candidate.ToLowerInvariant());
                if (distance <= allowed) close.Add((candidate, distance));
            }
        }
        return close.OrderBy(item => item.Distance).ThenBy(item => item.Text, StringComparer.Ordinal).Select(item => item.Text).Distinct(StringComparer.Ordinal);
    }

    // The dictionaries write a straight apostrophe; prose often has a curly one.
    internal static string Apostrophes(string word) => word.Contains('’') ? word.Replace('’', '\'') : word;

    private static bool IsLetter(string text, int at) =>
        at >= 0 && at < text.Length && (char.IsLetterOrDigit(text, at)
            || char.GetUnicodeCategory(text[at]) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            || (char.IsSurrogate(text[at]) && IsSurrogateLetter(text, at)));

    private static bool IsSurrogateLetter(string text, int at)
    {
        var high = char.IsLowSurrogate(text[at]) ? at - 1 : at;
        return high >= 0 && high + 1 < text.Length && char.IsSurrogatePair(text[high], text[high + 1]) && char.IsLetter(text, high);
    }

    private static bool IsJoiner(char c) => c is '\'' or '’' or '-';

    // The optimal string alignment distance: letters added, removed, changed or two side by side swapped.
    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        var before = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) current[j] = Math.Min(current[j], before[j - 2] + 1);
            }
            (before, previous, current) = (previous, current, before);
        }
        return previous[b.Length];
    }

    [GeneratedRegex(@"(?:\b(?:https?|ftp)://|\bwww\.)[^\s<>()\[\]{}]+|\b[\w.+-]+@[\w-]+(?:\.[\w-]+)+", RegexOptions.IgnoreCase)]
    private static partial Regex Address();
}

// A set of known words: as written, with straight apostrophes, and in capitals, for a word written all in capitals.
internal sealed class KnownWords
{
    internal static readonly KnownWords Empty = Of([]);

    private readonly FrozenSet<string> _words;
    private readonly FrozenSet<string> _upper;

    private KnownWords(FrozenSet<string> words, FrozenSet<string> upper, IReadOnlyList<string> written)
    {
        _words = words;
        _upper = upper;
        Written = written;
    }

    // The words as given, for suggestions.
    internal IReadOnlyList<string> Written { get; }

    internal static KnownWords Of(IEnumerable<string> texts)
    {
        HashSet<string> words = new(StringComparer.Ordinal);
        List<string> written = [];
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (var (_, _, word) in SpellChecker.Words(text))
            {
                if (words.Add(word)) written.Add(word);
                words.Add(SpellChecker.Apostrophes(word));
            }
        }
        return new(words.ToFrozenSet(StringComparer.Ordinal), words.Select(word => word.ToUpperInvariant()).ToFrozenSet(StringComparer.Ordinal), written);
    }

    internal bool Contains(string word)
    {
        if (_words.Contains(word) || _words.Contains(SpellChecker.Apostrophes(word))) return true;
        // Runic at the start of a sentence for runic; RUNIC in capitals.
        if (word.Length > 1 && char.IsUpper(word[0]) && _words.Contains(char.ToLowerInvariant(word[0]) + word[1..])) return true;
        return word.All(c => !char.IsLower(c)) && _upper.Contains(word.ToUpperInvariant());
    }
}
