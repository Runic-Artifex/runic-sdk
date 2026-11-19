using System.Globalization;

namespace Runic.Desktop.Internal;

// How a dictionary is chosen for a BCP 47 tag among dictionaries named like de_CH, sr_Latn_RS or de.
internal enum SpellingDictionaryMatch
{
    // The platform spell checker's rule (Runic.Desktop, D051.5): a tag with a region takes its region's dictionary or the
    // language's alone, never another region's spelling.
    SameRegion,

    // An application's own checker (Runic.Spelling): a region without a dictionary reads with the nearest one, as
    // Australian English with British, then the language's usual region, then any of the language's.
    NearestRegion,
}

// BCP 47 tags, POSIX locales and Hunspell dictionary names, and the dictionary a tag is checked with. Shared as source
// by Runic.Desktop (SpellingLanguages, for the platform checkers) and the preview Runic.Spelling (an application's own
// checker), so both name and choose dictionaries the same way without a package reference between them. It needs
// nothing but the base class library.
internal static class SpellingDictionaryNames
{
    // For a region with no dictionary of its own, the one nearest to how it spells (NearestRegion only).
    internal static readonly IReadOnlyDictionary<string, string> Neighbours = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["en_AU"] = "en_GB", ["en_NZ"] = "en_GB", ["en_IE"] = "en_GB", ["en_ZA"] = "en_GB", ["en_IN"] = "en_GB",
        ["en_CA"] = "en_US", ["de_AT"] = "de_DE", ["de_LU"] = "de_DE", ["de_BE"] = "de_DE", ["de_LI"] = "de_CH",
    };

    // The region a language is usually checked in when the culture data cannot say, as without ICU (NearestRegion only).
    private static readonly IReadOnlyDictionary<string, string> UsualRegions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["en"] = "US", ["da"] = "DK", ["sv"] = "SE", ["nb"] = "NO", ["nn"] = "NO", ["cs"] = "CZ", ["el"] = "GR", ["uk"] = "UA",
        ["ga"] = "IE", ["cy"] = "GB", ["ca"] = "ES", ["eu"] = "ES", ["gl"] = "ES", ["sl"] = "SI", ["et"] = "EE",
        ["pt"] = "BR", ["fa"] = "IR", ["he"] = "IL", ["ko"] = "KR", ["vi"] = "VN", ["ka"] = "GE", ["hy"] = "AM",
    };

    internal readonly record struct Parsed(string Language, string? Script, string? Region);

    // The installed names by their normal form (de_CH for de-CH.dic or de_CH_frami), the first in ordinal order winning;
    // null for null.
    internal static Dictionary<string, string>? Index(IReadOnlyCollection<string>? installed)
    {
        if (installed is null) return null;
        Dictionary<string, string> available = new(StringComparer.Ordinal);
        foreach (var name in installed.Order(StringComparer.Ordinal))
        {
            if (Parse(name) is { } parsed) available.TryAdd(Name(parsed), name);
        }
        return available;
    }

    // The names to use for a tag, best first: with available, the chosen one (or none); without, the names a checker
    // would look for. userLanguages: the user's languages, as BCP 47 tags or POSIX locales (de_CH.UTF-8), most preferred
    // first; they choose the region of a tag without one, such as en for a user in en_GB.
    internal static List<string> Choose(string tag, IReadOnlyList<string> userLanguages, Dictionary<string, string>? available,
        SpellingDictionaryMatch match)
    {
        if (Parse(tag) is not { } parsed) return [];
        var language = parsed.Language;
        if (available is null)
        {
            if (parsed.Region is not null) return [Name(parsed)];
            // A bare language needs a region to name a hunspell dictionary: the user's, else the language's likely one.
            var region = UserRegions(language, userLanguages).FirstOrDefault() ?? LikelyRegion(language, match);
            return region is null ? [language] : [Name(parsed with { Region = region }), language];
        }
        return match == SpellingDictionaryMatch.SameRegion
            ? SameRegion(parsed, userLanguages, available)
            : Nearest(parsed, userLanguages, available);
    }

    private static List<string> SameRegion(Parsed parsed, IReadOnlyList<string> userLanguages, Dictionary<string, string> available)
    {
        var language = parsed.Language;
        if (parsed.Region is not null)
        {
            // The tag's region, or, as BCP 47 lookup does, the language alone; never another region's spelling.
            if (Find(available, Name(parsed)) is { } exact) return [exact];
            if (parsed.Script is not null && Find(available, Name(parsed with { Script = null })) is { } withoutScript) return [withoutScript];
            return Find(available, language) is { } general ? [general] : [];
        }
        if (Find(available, Name(parsed)) is { } bare) return [bare];
        foreach (var region in UserRegions(language, userLanguages))
        {
            if (Find(available, Name(parsed with { Region = region })) is { } user) return [user];
        }
        if (LikelyRegion(language, SpellingDictionaryMatch.SameRegion) is { } likely &&
            Find(available, Name(parsed with { Region = likely })) is { } common) return [common];
        return AnyOf(parsed, available);
    }

    // The first that is there of: the tag's region and the region's neighbour; for a tag without a region, each of the
    // user's regions of the language and its neighbour; the language's usual region; the language alone; any region of
    // the language, the same one each time. A script stays apart: sr-Latn never reads with sr_RS or sr, which are
    // Cyrillic.
    private static List<string> Nearest(Parsed parsed, IReadOnlyList<string> userLanguages, Dictionary<string, string> available)
    {
        List<string> candidates = [];
        void AddRegion(string region)
        {
            candidates.Add(Name(parsed with { Region = region }));
            if (parsed.Script is null && Neighbours.TryGetValue($"{parsed.Language}_{region}", out var neighbour)) candidates.Add(neighbour);
        }
        if (parsed.Region is { } own) AddRegion(own);
        else
        {
            foreach (var region in UserRegions(parsed.Language, userLanguages)) AddRegion(region);
        }
        if (LikelyRegion(parsed.Language, SpellingDictionaryMatch.NearestRegion) is { } likely)
            candidates.Add(Name(parsed with { Region = likely }));
        candidates.Add(Name(parsed with { Region = null }));
        foreach (var candidate in candidates)
        {
            if (Find(available, candidate) is { } found) return [found];
        }
        return AnyOf(parsed, available);
    }

    // Any region of the language in the same script, the same one each time.
    private static List<string> AnyOf(Parsed parsed, Dictionary<string, string> available)
    {
        string[] names = [.. available.Keys];
        Array.Sort(names, StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (Parse(name) is { } candidate && candidate.Language == parsed.Language && candidate.Script == parsed.Script) return [available[name]];
        }
        return [];
    }

    // The tags of the user's languages: each once, without C and POSIX.
    internal static string[] UserTags(IReadOnlyList<string> userLanguages)
    {
        List<string> tags = [];
        foreach (var value in userLanguages)
        {
            if (Parse(value) is not { } parsed) continue;
            var tag = parsed.Region is { } region ? $"{parsed.Language}-{region}" : parsed.Language;
            if (!tags.Contains(tag, StringComparer.Ordinal)) tags.Add(tag);
        }
        return [.. tags];
    }

    // The name of a dictionary for a tag: language_REGION, or language_Script_REGION, as enchant and hunspell name them.
    internal static string? DictionaryName(string tag) => Parse(tag) is { } parsed ? Name(parsed) : null;

    private static string? Find(Dictionary<string, string> available, string name) => available.GetValueOrDefault(name);

    private static IEnumerable<string> UserRegions(string language, IReadOnlyList<string> userLanguages)
    {
        foreach (var value in userLanguages)
        {
            if (Parse(value) is { Region: { } region } user && user.Language == language) yield return region;
        }
    }

    // The language's most common region from the culture data, such as US for en and DE for de; without ICU null, or for
    // NearestRegion the usual region of a short list.
    private static string? LikelyRegion(string language, SpellingDictionaryMatch match)
    {
        try
        {
            var name = CultureInfo.CreateSpecificCulture(language).Name;
            if (Parse(name) is { Language: var found, Region: { } region } && found == language) return region;
        }
        catch (CultureNotFoundException)
        {
        }
        return match == SpellingDictionaryMatch.NearestRegion ? UsualRegions.GetValueOrDefault(language) ?? FallbackRegion(language) : null;
    }

    // Without culture data: the language's code in capitals, as de_DE and fr_FR.
    private static string? FallbackRegion(string language) => language.Length == 2 ? language.ToUpperInvariant() : null;

    internal static string Name(Parsed parsed) =>
        parsed.Script is { } script
            ? parsed.Region is { } region ? $"{parsed.Language}_{script}_{region}" : $"{parsed.Language}_{script}"
            : parsed.Region is { } onlyRegion ? $"{parsed.Language}_{onlyRegion}" : parsed.Language;

    // A BCP 47 tag (de-CH, sr-Latn-RS), a POSIX locale (de_CH.UTF-8@euro) or a dictionary name (en_US): its language,
    // script and region; variants and extensions do not name a dictionary. Null for C, POSIX and what is not a language.
    internal static Parsed? Parse(string value)
    {
        var end = value.AsSpan().IndexOfAny('.', '@');
        var subtags = (end < 0 ? value : value[..end]).Split('-', '_');
        var language = subtags[0];
        if (language.Length is < 2 or > 3 || !IsLetters(language)) return null;
        string? script = null;
        string? region = null;
        var index = 1;
        if (index < subtags.Length && subtags[index].Length == 4 && IsLetters(subtags[index]))
        {
            script = char.ToUpperInvariant(subtags[index][0]) + subtags[index][1..].ToLowerInvariant();
            index++;
        }
        if (index < subtags.Length && ((subtags[index].Length == 2 && IsLetters(subtags[index])) ||
            (subtags[index].Length == 3 && !subtags[index].AsSpan().ContainsAnyExceptInRange('0', '9'))))
        {
            region = subtags[index].ToUpperInvariant();
        }
        return new(language.ToLowerInvariant(), script, region);
    }

    private static bool IsLetters(string value) => value.All(char.IsAsciiLetter);
}
