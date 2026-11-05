using System.Globalization;
using System.Runtime.InteropServices;

namespace Runic.Platform;

/// <summary>
/// The languages the user prefers, as the operating system lists them, for choosing the application's locale.
/// </summary>
/// <remarks>
/// <para>
/// Negotiate the list against the languages the application ships and set the result as its locale, unless the user
/// chose one in its settings:
/// </para>
/// <code>
/// var locale = settings.Language ?? AppCatalog.ResolveLocale(DesktopLanguages.Preferred);
/// await services.GetRequiredService&lt;IRunicApplicationLocale&gt;().SetLocaleAsync(locale);
/// </code>
/// <para>
/// It needs no window or host and works in command-line tools too.
/// </para>
/// </remarks>
public static class DesktopLanguages
{
    /// <summary>
    /// The user's preferred languages as BCP 47 tags, such as <c>de-CH</c> or <c>zh-Hans-CN</c>, most preferred first,
    /// each once. Each read asks the operating system again.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>Windows: the user's display languages (<c>GetUserPreferredUILanguages</c>).</description></item>
    /// <item><description>macOS: the user's preferred languages (<c>CFLocaleCopyPreferredLanguages</c>, as
    /// <c>NSLocale.preferredLanguages</c>).</description></item>
    /// <item><description>Linux and other systems: the list gettext reads, <c>LANGUAGE</c> then <c>LC_ALL</c>,
    /// <c>LC_MESSAGES</c> and <c>LANG</c>, converted from POSIX locales such as <c>de_CH.UTF-8</c>; <c>LANGUAGE</c>
    /// counts only when the messages locale is not <c>C</c> or <c>POSIX</c>, as gettext does.</description></item>
    /// </list>
    /// When the system lists none, the list holds <see cref="CultureInfo.CurrentUICulture"/>'s name, and is empty for
    /// the invariant culture.
    /// </remarks>
    public static IReadOnlyList<string> Preferred
    {
        get
        {
            string[] system;
            try
            {
                system = OperatingSystem.IsWindows() ? Windows()
                    : OperatingSystem.IsMacOS() ? MacOs()
                    : FromEnvironment(Environment.GetEnvironmentVariable);
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
            {
                system = [];
            }
            if (system.Length > 0) return system;
            var current = CultureInfo.CurrentUICulture.Name;
            return current.Length == 0 ? [] : [current];
        }
    }

    // The languages gettext reads on Linux, as BCP 47 tags.
    internal static string[] FromEnvironment(Func<string, string?> environment)
    {
        List<string> tags = [];
        var messages = Value(environment, "LC_ALL") ?? Value(environment, "LC_MESSAGES") ?? Value(environment, "LANG");
        // gettext ignores LANGUAGE when the messages locale is C.
        if (messages is not null && !IsC(messages) && Value(environment, "LANGUAGE") is { } list)
            foreach (var entry in list.Split(':', StringSplitOptions.RemoveEmptyEntries)) Add(tags, FromPosix(entry));
        if (messages is not null) Add(tags, FromPosix(messages));
        return [.. tags];
    }

    private static string? Value(Func<string, string?> environment, string name) =>
        environment(name) is { Length: > 0 } value ? value : null;

    private static bool IsC(string locale)
    {
        var end = locale.AsSpan().IndexOfAny('.', '@');
        var name = end < 0 ? locale.AsSpan() : locale.AsSpan(0, end);
        return name is "C" or "POSIX";
    }

    // A POSIX locale (de_CH.UTF-8, sr_RS@latin) or a tag as a BCP 47 tag: language, script and region; the codeset is
    // dropped and @latin/@cyrillic become the script. Null for C, POSIX and what is not a language.
    internal static string? FromPosix(string value)
    {
        if (IsC(value)) return null;
        var at = value.IndexOf('@', StringComparison.Ordinal);
        var modifier = at < 0 ? null : value[(at + 1)..];
        var name = at < 0 ? value : value[..at];
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0) name = name[..dot];
        var subtags = name.Split('_', '-');
        if (subtags[0].Length is < 2 or > 8 || !subtags[0].All(char.IsAsciiLetter)) return null;
        List<string> parts = [subtags[0].ToLowerInvariant()];
        var script = modifier switch
        {
            "latin" => "Latn",
            "cyrillic" => "Cyrl",
            "devanagari" => "Deva",
            _ => null,
        };
        foreach (var subtag in subtags.Skip(1))
        {
            // Extensions and private use (-u-ca-gregorian, -x-…) do not choose a language.
            if (subtag.Length == 1) break;
            if (subtag.Length == 0 || !subtag.All(char.IsAsciiLetterOrDigit)) return null;
            if (subtag.Length == 4 && subtag.All(char.IsAsciiLetter))
                parts.Add(char.ToUpperInvariant(subtag[0]) + subtag[1..].ToLowerInvariant());
            else if (subtag.Length == 2 && subtag.All(char.IsAsciiLetter))
            {
                if (script is not null) { parts.Add(script); script = null; }
                parts.Add(subtag.ToUpperInvariant());
            }
            else parts.Add(subtag);
        }
        if (script is not null) parts.Insert(1, script);
        return string.Join('-', parts);
    }

    private static void Add(List<string> tags, string? tag)
    {
        if (tag is not null && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
    }

    private static string[] Windows()
    {
        const uint MuiLanguageName = 0x8;
        uint size = 0;
        if (!GetUserPreferredUILanguages(MuiLanguageName, out _, 0, ref size) || size == 0) return [];
        var buffer = Marshal.AllocHGlobal(checked((int)size * sizeof(char)));
        try
        {
            if (!GetUserPreferredUILanguages(MuiLanguageName, out _, buffer, ref size)) return [];
            // A list of null-terminated names, ended by an empty one.
            var names = Marshal.PtrToStringUni(buffer, (int)size) ?? "";
            List<string> tags = [];
            foreach (var name in names.Split('\0', StringSplitOptions.RemoveEmptyEntries)) Add(tags, FromPosix(name));
            return [.. tags];
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string[] MacOs()
    {
        var languages = CFLocaleCopyPreferredLanguages();
        if (languages == 0) return [];
        try
        {
            List<string> tags = [];
            var count = CFArrayGetCount(languages);
            for (nint index = 0; index < count; index++)
            {
                var item = CFArrayGetValueAtIndex(languages, index);
                if (item == 0) continue;
                var length = CFStringGetLength(item);
                if (length is <= 0 or > 256) continue;
                var buffer = Marshal.AllocHGlobal((int)length * sizeof(char));
                try
                {
                    CFStringGetCharacters(item, new CFRange(0, length), buffer);
                    Add(tags, FromPosix(Marshal.PtrToStringUni(buffer, (int)length)));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return [.. tags];
        }
        finally
        {
            CFRelease(languages);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CFRange(nint location, nint length)
    {
        public readonly nint Location = location;
        public readonly nint Length = length;
    }

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserPreferredUILanguages(uint flags, out uint count, nint buffer, ref uint size);

    [DllImport(CoreFoundation, ExactSpelling = true)]
    private static extern nint CFLocaleCopyPreferredLanguages();

    [DllImport(CoreFoundation, ExactSpelling = true)]
    private static extern nint CFArrayGetCount(nint array);

    [DllImport(CoreFoundation, ExactSpelling = true)]
    private static extern nint CFArrayGetValueAtIndex(nint array, nint index);

    [DllImport(CoreFoundation, ExactSpelling = true)]
    private static extern nint CFStringGetLength(nint text);

    [DllImport(CoreFoundation, ExactSpelling = true)]
    private static extern void CFStringGetCharacters(nint text, CFRange range, nint buffer);

    [DllImport(CoreFoundation, ExactSpelling = true)]
    private static extern void CFRelease(nint value);
}
