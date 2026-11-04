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

    internal static DesktopLayoutDirection FromTag(string locale)
    {
        var parts = locale.Split('-', '_');
        foreach (var part in parts.AsSpan(1))
        {
            if (part.Length == 4 && char.IsAsciiLetter(part[0]))
                return RightToLeftScripts.Contains(part) ? DesktopLayoutDirection.RightToLeft : DesktopLayoutDirection.LeftToRight;
        }
        return RightToLeftLanguages.Contains(parts[0]) ? DesktopLayoutDirection.RightToLeft : DesktopLayoutDirection.LeftToRight;
    }
}
