using System.Collections.Concurrent;
using System.Globalization;

namespace Runic.Application.Views;

// FlowCultureFromLocale: a bridged invocation of a window (a page's request, a menu command) runs with
// CultureInfo.CurrentCulture and CurrentUICulture of the window's locale, so ToString("N"), string.Format and
// DataAnnotations messages format as the page does. Both cultures are AsyncLocal values: the turns, awaits and
// continuations that the invocation starts capture them, and Dispose restores the caller's.
internal readonly struct LocaleCultureScope : IDisposable
{
    private static readonly ConcurrentDictionary<string, CultureInfo?> Cultures = new(StringComparer.OrdinalIgnoreCase);
    private readonly CultureInfo? _culture;
    private readonly CultureInfo? _uiCulture;

    private LocaleCultureScope(CultureInfo culture, CultureInfo uiCulture)
    {
        _culture = culture;
        _uiCulture = uiCulture;
    }

    // Enters the culture of the locale, or nothing for null and a locale without a culture.
    internal static LocaleCultureScope Enter(string? locale)
    {
        if (locale is null || Resolve(locale) is not { } culture) return default;
        var scope = new LocaleCultureScope(CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        return scope;
    }

    // The culture of a locale: the tag's, else the nearest parent's (de-CH-u-co-phonebk, de-CH, de). Null when none has
    // one, as with invariant globalization: the invocation then keeps the process's cultures.
    internal static CultureInfo? Resolve(string locale) => Cultures.GetOrAdd(locale, static tag =>
    {
        var subtags = tag.Split('-');
        for (var length = subtags.Length; length >= 1; length--)
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(string.Join('-', subtags, 0, length));
                if (!culture.Equals(CultureInfo.InvariantCulture)) return culture;
            }
            catch (CultureNotFoundException) { }
        }
        return null;
    });

    public void Dispose()
    {
        if (_culture is null) return;
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture!;
    }
}
