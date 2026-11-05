using System.Globalization;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

// The direction of a window locale (IRunicWindowLocale) for the native window: the culture's TextInfo, or, where the
// process has no culture data (InvariantGlobalization), the locale's script or language.
internal static class WindowLocaleDirection
{
    // Scripts written right to left (CLDR), as the page's textDirection uses.
    private static readonly HashSet<string> RightToLeftScripts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Adlm", "Arab", "Hebr", "Mand", "Mend", "Nkoo", "Rohg", "Samr", "Syrc", "Thaa", "Yezi",
    };

    // Languages whose likely script is one of them (CLDR likelySubtags).
    private static readonly HashSet<string> RightToLeftLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "arc", "ckb", "dv", "fa", "he", "iw", "ks", "lrc", "mzn", "nqo", "ps", "rhg", "sd", "syr", "ug", "ur", "yi",
    };

    internal static DesktopLayoutDirection Of(string locale)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(locale, predefinedOnly: true);
            if (culture.Name.Length > 0)
                return culture.TextInfo.IsRightToLeft ? DesktopLayoutDirection.RightToLeft : DesktopLayoutDirection.LeftToRight;
        }
        catch (CultureNotFoundException) { }
        return FromTag(locale);
    }

    // BCP 47: language, up to three extlang subtags, then the script. A singleton (u, t, x, …) starts the extensions and
    // the private use subtags, whose values are not scripts (ar-u-nu-latn is Arabic with Latin digits); a tag that starts
    // with one (x-…, i-…) has neither a script nor a known language.
    internal static DesktopLayoutDirection FromTag(string locale)
    {
        var parts = locale.Split('-', '_');
        if (parts[0].Length < 2) return DesktopLayoutDirection.LeftToRight;
        var index = 1;
        while (index < parts.Length && index <= 3 && parts[0].Length <= 3 && IsLetters(parts[index], 3)) index++;
        if (index < parts.Length && IsLetters(parts[index], 4))
            return RightToLeftScripts.Contains(parts[index]) ? DesktopLayoutDirection.RightToLeft : DesktopLayoutDirection.LeftToRight;
        return RightToLeftLanguages.Contains(parts[0]) ? DesktopLayoutDirection.RightToLeft : DesktopLayoutDirection.LeftToRight;
    }

    private static bool IsLetters(string part, int length)
    {
        if (part.Length != length) return false;
        foreach (var c in part)
            if (!char.IsAsciiLetter(c)) return false;
        return true;
    }
}
