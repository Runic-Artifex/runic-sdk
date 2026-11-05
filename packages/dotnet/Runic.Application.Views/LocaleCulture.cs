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
    // one, as with invariant globalization: the invocation then keeps the process's cultures. Only cultures .NET predefines
    // are used, so CultureInfo's own cache holds a bounded set; and this cache keeps at most MaximumCachedLocales tags, so
    // tags that come from remote clients and are assigned without negotiation cannot grow memory without limit.
    internal static CultureInfo? Resolve(string locale)
    {
        if (Cultures.TryGetValue(locale, out var cached)) return cached;
        var resolved = Lookup(locale);
        if (Cultures.Count < MaximumCachedLocales) Cultures.TryAdd(locale, resolved);
        return resolved;
    }

    internal const int MaximumCachedLocales = 256;

    // The number of cached tags, for tests.
    internal static int CachedLocales => Cultures.Count;

    private static CultureInfo? Lookup(string tag)
    {
        var subtags = tag.Split('-');
        for (var length = subtags.Length; length >= 1; length--)
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(string.Join('-', subtags, 0, length), predefinedOnly: true);
                if (!culture.Equals(CultureInfo.InvariantCulture)) return culture;
            }
            catch (CultureNotFoundException) { }
        }
        return null;
    }

    public void Dispose()
    {
        if (_culture is null) return;
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture!;
    }
}
