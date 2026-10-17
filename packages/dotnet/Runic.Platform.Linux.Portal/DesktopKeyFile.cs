using System.Collections.Immutable;
using System.Text;

namespace Runic.Platform.Linux.Portal;

// The key file format of the Desktop Entry specification, which desktop entries, mimeapps.list, mimeinfo.cache,
// index.theme and kdeglobals share: groups, comments, localized keys and the string and list escapes.
// https://specifications.freedesktop.org/desktop-entry/latest/basic-format.html
// Lines that are neither a group, a comment nor a key are skipped, so one malformed line does not hide a file.
internal sealed class DesktopKeyFile
{
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> _groups = new(StringComparer.Ordinal);

    /// <summary>The groups in file order; a repeated group merges into its first occurrence.</summary>
    internal ImmutableArray<string> Groups { get; private set; } = [];

    internal const long MaximumFileBytes = 4 << 20;

    internal static DesktopKeyFile? Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumFileBytes) return null;
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static DesktopKeyFile Parse(string text)
    {
        var file = new DesktopKeyFile();
        var order = ImmutableArray.CreateBuilder<string>();
        Dictionary<string, Dictionary<string, string>>? group = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            if (trimmed[0] == '[')
            {
                int end = trimmed.IndexOf(']');
                if (end <= 1 || trimmed[(end + 1)..].Trim().Length != 0) { group = null; continue; }
                string name = trimmed[1..end];
                if (!file._groups.TryGetValue(name, out group))
                {
                    file._groups[name] = group = new(StringComparer.Ordinal);
                    order.Add(name);
                }
                continue;
            }
            if (group is null) continue;
            int equals = trimmed.IndexOf('=');
            if (equals <= 0) continue;
            string key = trimmed[..equals].TrimEnd(), locale = "";
            string value = trimmed[(equals + 1)..].TrimStart(' ', '\t');
            int open = key.IndexOf('[');
            if (open >= 0)
            {
                if (open == 0 || !key.EndsWith(']')) continue;
                locale = StripEncoding(key[(open + 1)..^1]);
                key = key[..open];
            }
            if (!group.TryGetValue(key, out var values)) group[key] = values = new(StringComparer.Ordinal);
            values[locale] = value; // The last occurrence wins, as in GLib.
        }
        file.Groups = order.ToImmutable();
        return file;
    }

    internal bool HasGroup(string group) => _groups.ContainsKey(group);

    /// <summary>The keys of <paramref name="group"/> without a locale, in no particular order.</summary>
    internal IEnumerable<string> Keys(string group) =>
        _groups.TryGetValue(group, out var keys) ? keys.Where(static pair => pair.Value.ContainsKey("")).Select(static pair => pair.Key) : [];

    /// <summary>The raw, still escaped value of an unlocalized key.</summary>
    internal string? Raw(string group, string key) =>
        _groups.TryGetValue(group, out var keys) && keys.TryGetValue(key, out var values) && values.TryGetValue("", out var value) ? value : null;

    internal string? String(string group, string key) => Raw(group, key) is { } raw ? Unescape(raw) : null;

    /// <summary>The value for the best matching locale, falling back to the unlocalized key.</summary>
    internal string? LocaleString(string group, string key, DesktopLocale locale)
    {
        if (!_groups.TryGetValue(group, out var keys) || !keys.TryGetValue(key, out var values)) return null;
        foreach (var candidate in locale.Candidates)
            if (values.TryGetValue(candidate, out var localized)) return Unescape(localized);
        return values.TryGetValue("", out var value) ? Unescape(value) : null;
    }

    internal bool Boolean(string group, string key) => Raw(group, key)?.Trim() is "true" or "1";

    internal ImmutableArray<string> List(string group, string key, char separator = ';') =>
        Raw(group, key) is { } raw ? SplitList(raw, separator) : [];

    // Elements are separated by an unescaped separator; "\;" is a literal semicolon. A trailing separator ends the list.
    internal static ImmutableArray<string> SplitList(string raw, char separator = ';')
    {
        var items = ImmutableArray.CreateBuilder<string>();
        var current = new StringBuilder();
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\\' && i + 1 < raw.Length && raw[i + 1] == separator) { current.Append(separator); i++; continue; }
            if (c == '\\' && i + 1 < raw.Length) { current.Append(c).Append(raw[i + 1]); i++; continue; }
            if (c == separator)
            {
                var item = Unescape(current.ToString()).Trim();
                if (item.Length > 0) items.Add(item);
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        var last = Unescape(current.ToString()).Trim();
        if (last.Length > 0) items.Add(last);
        return items.ToImmutable();
    }

    // The string escapes \s \n \t \r and \\. An unknown escape is kept as written, so a backslash meant for the
    // Exec quoting rules, such as \$, reaches them.
    internal static string Unescape(string raw)
    {
        if (!raw.Contains('\\')) return raw;
        var text = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c != '\\' || i + 1 == raw.Length) { text.Append(c); continue; }
            char next = raw[++i];
            switch (next)
            {
                case 's': text.Append(' '); break;
                case 'n': text.Append('\n'); break;
                case 't': text.Append('\t'); break;
                case 'r': text.Append('\r'); break;
                case '\\': text.Append('\\'); break;
                default: text.Append('\\').Append(next); break;
            }
        }
        return text.ToString();
    }

    // The encoding part of a locale is ignored when matching: lang_COUNTRY.ENCODING@MODIFIER.
    private static string StripEncoding(string locale)
    {
        int dot = locale.IndexOf('.');
        if (dot < 0) return locale;
        int at = locale.IndexOf('@', dot);
        return at < 0 ? locale[..dot] : locale[..dot] + locale[at..];
    }
}

// A POSIX LC_MESSAGES locale and the keys it matches, in the specification's order:
// lang_COUNTRY@MODIFIER, lang_COUNTRY, lang@MODIFIER, lang, then the unlocalized key.
// https://specifications.freedesktop.org/desktop-entry/latest/localized-keys.html
internal sealed class DesktopLocale
{
    internal static readonly DesktopLocale None = new("");
    internal ImmutableArray<string> Candidates { get; }

    internal DesktopLocale(string locale)
    {
        string value = locale.Trim(), modifier = "", country = "";
        int at = value.IndexOf('@');
        if (at >= 0) { modifier = value[(at + 1)..]; value = value[..at]; }
        int dot = value.IndexOf('.');
        if (dot >= 0) value = value[..dot];
        int underscore = value.IndexOf('_');
        if (underscore >= 0) { country = value[(underscore + 1)..]; value = value[..underscore]; }
        string lang = value;
        if (lang.Length == 0 || lang is "C" or "POSIX") { Candidates = []; return; }
        var candidates = ImmutableArray.CreateBuilder<string>();
        if (country.Length > 0 && modifier.Length > 0) candidates.Add($"{lang}_{country}@{modifier}");
        if (country.Length > 0) candidates.Add($"{lang}_{country}");
        if (modifier.Length > 0) candidates.Add($"{lang}@{modifier}");
        candidates.Add(lang);
        Candidates = candidates.ToImmutable();
    }

    // The application's UI culture when it has one, as .NET derives it from the POSIX locale or the application sets
    // it; otherwise LC_ALL, LC_MESSAGES and LANG, in POSIX order.
    internal static string FromProcess()
    {
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        if (!string.IsNullOrEmpty(culture.Name)) return FromCultureName(culture.Name);
        foreach (var variable in (string[])["LC_ALL", "LC_MESSAGES", "LANG"])
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value) return value;
        return "";
    }

    // de-DE becomes de_DE, sr-Latn-RS sr_RS@latin and zh-Hant-TW zh_TW.
    internal static string FromCultureName(string name)
    {
        var parts = name.Split('-');
        string lang = parts[0], country = "", modifier = "";
        foreach (var part in parts.Skip(1))
        {
            if (part.Length == 4) { if (part.Equals("Latn", StringComparison.OrdinalIgnoreCase) && lang != "en") modifier = "latin"; }
            else if (part.Length is 2 or 3 && country.Length == 0) country = part.ToUpperInvariant();
        }
        return lang + (country.Length > 0 ? "_" + country : "") + (modifier.Length > 0 ? "@" + modifier : "");
    }
}
