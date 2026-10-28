using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// Resolves the BCP 47 tags of DesktopHostOptions.SpellCheckingLanguages to the dictionaries of a platform spell checker:
// enchant's on Linux (WebKitGTK), named like de_CH or en_US, and NSSpellChecker's on macOS, named like de or en_GB. Both
// checkers belong to the process, not to a window. Linked into Runic.Desktop.Gtk4.
internal static class SpellingLanguages
{
    // The dictionaries to load, in the order of the tags, and the tags no installed dictionary serves.
    internal readonly record struct Resolution(string[] Dictionaries, string[] Missing);

    // installed: the checker's dictionaries, or null when they cannot be listed; each tag then becomes the names a
    // checker would look for (WebKitGTK skips names it has no dictionary for), and none is reported missing.
    // userLanguages: the user's languages, as BCP 47 tags or POSIX locales (de_CH.UTF-8), most preferred first; they
    // choose the region of a tag without one, such as en for a user in en_GB.
    internal static Resolution Resolve(IReadOnlyList<string> tags, IReadOnlyList<string> userLanguages, IReadOnlyCollection<string>? installed)
    {
        Dictionary<string, string>? available = null;
        if (installed is not null)
        {
            available = new(StringComparer.Ordinal);
            foreach (var name in installed.Order(StringComparer.Ordinal))
            {
                if (Parse(name) is { } parsed) available.TryAdd(Name(parsed), name);
            }
        }
        List<string> dictionaries = [];
        List<string> missing = [];
        foreach (var tag in tags)
        {
            var names = ResolveOne(tag, userLanguages, available);
            if (names.Count == 0) missing.Add(tag);
            foreach (var name in names)
            {
                if (!dictionaries.Contains(name, StringComparer.Ordinal)) dictionaries.Add(name);
            }
        }
        return new([.. dictionaries], [.. missing]);
    }

    internal const string DictionaryMissingCode = "spell-checking-dictionary-missing";

    // WebKitGTK's dictionaries (webkit_web_context_set_spell_checking_languages) for DesktopHostOptions.SpellCheckingLanguages,
    // and the warning for the tags without one. No languages selects the user's; when none of theirs has a dictionary,
    // the list stays empty and WebKitGTK chooses. The names of missing dictionaries stay in the list, where WebKitGTK
    // skips them, so a missing language never falls back to another.
    internal static (string[] Dictionaries, DesktopDiagnostic? Missing) ForWebKitGtk(IReadOnlyList<string> languages)
    {
        var installed = EnchantDictionaries();
        var user = LinuxUserLanguages(Environment.GetEnvironmentVariable);
        if (languages.Count == 0) return (Resolve(UserTags(user), user, installed).Dictionaries, null);
        var resolution = Resolve(languages, user, installed);
        if (resolution.Missing.Length == 0) return (resolution.Dictionaries, null);
        string[] dictionaries = [.. resolution.Dictionaries, .. resolution.Missing.Select(DictionaryName).OfType<string>()];
        return (dictionaries, MissingDictionaries(resolution.Missing,
            "Install a hunspell dictionary for each language, such as hunspell-de-ch, or with Nix pass hunspellDicts.de_CH " +
            "in buildRunicApplication's spellingDictionaries; or remove the language."));
    }

    // spell-checking-dictionary-missing: the checker has no dictionary for these languages and does not check them.
    internal static DesktopDiagnostic MissingDictionaries(IReadOnlyList<string> tags, string remediation) => new(
        DesktopErrorCategory.Unavailable,
        DictionaryMissingCode,
        $"No spelling dictionary is installed for {string.Join(", ", tags)}; the spell checker does not check {(tags.Count == 1 ? "it" : "them")}.",
        Retryable: true,
        Remediation: remediation)
    {
        Severity = DesktopDiagnosticSeverity.Warning,
        Option = "DesktopHostOptions.SpellCheckingLanguages",
    };

    private static string? s_reportedMissing;

    // Whether to report these missing languages: once until the languages change, not for every window that applies
    // them. Null records languages that all have a dictionary.
    internal static bool FirstReport(DesktopDiagnostic? diagnostic)
    {
        var previous = Interlocked.Exchange(ref s_reportedMissing, diagnostic?.Message);
        return diagnostic is not null && !string.Equals(previous, diagnostic.Message, StringComparison.Ordinal);
    }

    // The tags of the user's languages, for a checker told no languages: each once, without C and POSIX.
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

    // The user's languages on Linux, as gettext reads them: LANGUAGE's list, then LC_ALL, LC_MESSAGES and LANG.
    internal static string[] LinuxUserLanguages(Func<string, string?> environment)
    {
        List<string> languages = [];
        if (environment("LANGUAGE") is { Length: > 0 } list) languages.AddRange(list.Split(':', StringSplitOptions.RemoveEmptyEntries));
        foreach (var name in (ReadOnlySpan<string>)["LC_ALL", "LC_MESSAGES", "LANG"])
        {
            if (environment(name) is { Length: > 0 } value) languages.Add(value);
        }
        return [.. languages];
    }

    // The name of a dictionary for a tag: language_REGION, or language_Script_REGION, as enchant and hunspell name them.
    internal static string? DictionaryName(string tag) => Parse(tag) is { } parsed ? Name(parsed) : null;

    private static List<string> ResolveOne(string tag, IReadOnlyList<string> userLanguages, Dictionary<string, string>? available)
    {
        if (Parse(tag) is not { } parsed) return [];
        var language = parsed.Language;
        if (available is null)
        {
            if (parsed.Region is not null) return [Name(parsed)];
            // A bare language needs a region to name a hunspell dictionary: the user's, else the language's likely one.
            var region = UserRegions(language, userLanguages).FirstOrDefault() ?? LikelyRegion(language);
            return region is null ? [language] : [Name(parsed with { Region = region }), language];
        }
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
        if (LikelyRegion(language) is { } likely && Find(available, Name(parsed with { Region = likely })) is { } common) return [common];
        // Any region of the language, the same one each time.
        foreach (var (name, actual) in available.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (Parse(name) is { } candidate && candidate.Language == language && candidate.Script == parsed.Script) return [actual];
        }
        return [];
    }

    private static string? Find(Dictionary<string, string> available, string name) => available.GetValueOrDefault(name);

    private static IEnumerable<string> UserRegions(string language, IReadOnlyList<string> userLanguages)
    {
        foreach (var value in userLanguages)
        {
            if (Parse(value) is { Region: { } region } user && user.Language == language) yield return region;
        }
    }

    // The language's most common region from the culture data, such as US for en and DE for de; null without ICU.
    private static string? LikelyRegion(string language)
    {
        try
        {
            var name = CultureInfo.CreateSpecificCulture(language).Name;
            return Parse(name) is { Language: var found, Region: { } region } && found == language ? region : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static string Name(Parsed parsed) =>
        parsed.Script is { } script
            ? parsed.Region is { } region ? $"{parsed.Language}_{script}_{region}" : $"{parsed.Language}_{script}"
            : parsed.Region is { } onlyRegion ? $"{parsed.Language}_{onlyRegion}" : parsed.Language;

    private readonly record struct Parsed(string Language, string? Script, string? Region);

    // A BCP 47 tag (de-CH, sr-Latn-RS), a POSIX locale (de_CH.UTF-8@euro) or a dictionary name (en_US): its language,
    // script and region; variants and extensions do not name a dictionary. Null for C, POSIX and what is not a language.
    private static Parsed? Parse(string value)
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
            (subtags[index].Length == 3 && subtags[index].All(char.IsAsciiDigit))))
        {
            region = subtags[index].ToUpperInvariant();
        }
        return new(language.ToLowerInvariant(), script, region);
    }

    private static bool IsLetters(string value) => value.All(char.IsAsciiLetter);

    // The dictionaries enchant has, through the libenchant-2 that WebKitGTK loaded: null when it cannot be listed.
    internal static unsafe string[]? EnchantDictionaries()
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libenchant-2.so.2", out var library)) return null;
        if (!NativeLibrary.TryGetExport(library, "enchant_broker_init", out var init) ||
            !NativeLibrary.TryGetExport(library, "enchant_broker_list_dicts", out var list) ||
            !NativeLibrary.TryGetExport(library, "enchant_broker_free", out var free))
        {
            return null;
        }
        var broker = ((delegate* unmanaged[Cdecl]<nint>)init)();
        if (broker == 0) return null;
        List<string> names = [];
        var handle = GCHandle.Alloc(names);
        try
        {
            ((delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte*, nint, void>, nint, void>)list)(
                broker, &DescribeDictionary, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
            ((delegate* unmanaged[Cdecl]<nint, void>)free)(broker);
        }
        return [.. names];
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void DescribeDictionary(byte* tag, byte* providerName, byte* providerDescription, byte* providerFile, nint data)
    {
        if (tag == null || GCHandle.FromIntPtr(data).Target is not List<string> names) return;
        if (Marshal.PtrToStringUTF8((nint)tag) is { Length: > 0 } name) names.Add(name);
    }
}
