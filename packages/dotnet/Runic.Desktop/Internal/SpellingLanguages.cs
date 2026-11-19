using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// Resolves the BCP 47 tags of DesktopHostOptions.SpellCheckingLanguages to the dictionaries of a platform spell checker:
// enchant's on Linux (WebKitGTK), named like de_CH or en_US, and NSSpellChecker's on macOS, named like de or en_GB. Both
// checkers belong to the process, not to a window. Runic.Desktop.Gtk4 uses it through InternalsVisibleTo. Tags are parsed
// and matched by SpellingDictionaryNames, which the preview Runic.Spelling shares as source.
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
        var available = SpellingDictionaryNames.Index(installed);
        List<string> dictionaries = [];
        List<string> missing = [];
        foreach (var tag in tags)
        {
            var names = SpellingDictionaryNames.Choose(tag, userLanguages, available, SpellingDictionaryMatch.SameRegion);
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
        var user = DesktopLanguages.Preferred;
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
    internal static string[] UserTags(IReadOnlyList<string> userLanguages) => SpellingDictionaryNames.UserTags(userLanguages);

    // The name of a dictionary for a tag: language_REGION, or language_Script_REGION, as enchant and hunspell name them.
    internal static string? DictionaryName(string tag) => SpellingDictionaryNames.DictionaryName(tag);

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
