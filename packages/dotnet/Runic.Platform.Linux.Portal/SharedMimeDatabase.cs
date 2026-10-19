using System.Collections.Immutable;
using System.Text;
using System.Xml;

namespace Runic.Platform.Linux.Portal;

// The Shared MIME-info database that update-mime-database writes into each data directory's mime subdirectory:
// globs2, magic, aliases, subclasses, icons, generic-icons and the per-type XML files with their descriptions.
// https://specifications.freedesktop.org/shared-mime-info/latest/ar01s02.html
// Directories load from the lowest precedence (the last XDG_DATA_DIRS entry) to XDG_DATA_HOME, so __NOGLOBS__ and
// __NOMAGIC__ in a later directory replace what earlier ones defined. Root-XML and tree magic rules are not used.
internal sealed class SharedMimeDatabase
{
    internal const string OctetStream = "application/octet-stream", TextPlain = "text/plain";
    // update-mime-database's magic is far below this; a larger extent is not read.
    private const int MaximumExtent = 1 << 20;
    private const long MaximumSourceBytes = 64L << 20;

    private readonly List<Glob> _globs = [];
    private readonly Dictionary<string, List<Glob>> _suffixes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _subclasses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _genericIcons = new(StringComparer.OrdinalIgnoreCase);
    private List<MagicSection> _magic = [];
    private ImmutableArray<string> _mimeDirectories = [];

    /// <summary>The number of leading bytes the magic rules can examine.</summary>
    internal int MagicExtent { get; private set; }

    private sealed record Glob(string Pattern, string Type, int Weight, bool CaseSensitive, bool Literal, int Precedence, int Index)
    {
        internal string Folded { get; } = Pattern.ToLowerInvariant();
    }

    private sealed class Matchlet(int offset, int range, byte[] value, byte[]? mask)
    {
        internal readonly List<Matchlet> Children = [];
        internal int Extent => offset + range - 1 + value.Length;

        internal bool Matches(ReadOnlySpan<byte> data)
        {
            for (int start = offset; start < offset + range && start + value.Length <= data.Length; start++)
            {
                if (!Equal(data.Slice(start, value.Length))) continue;
                if (Children.Count == 0) return true;
                foreach (var child in Children) if (child.Matches(data)) return true;
                return false;
            }
            return false;
        }

        private bool Equal(ReadOnlySpan<byte> candidate)
        {
            if (mask is null) return candidate.SequenceEqual(value);
            for (int i = 0; i < value.Length; i++)
                if ((candidate[i] & mask[i]) != (value[i] & mask[i])) return false;
            return true;
        }
    }

    private sealed record MagicSection(int Priority, string Type, List<Matchlet> Rules, int Precedence, int Index)
    {
        internal bool Matches(ReadOnlySpan<byte> data)
        {
            foreach (var rule in Rules) if (rule.Matches(data)) return true;
            return false;
        }
    }

    /// <summary>Loads the database from the data directories, highest precedence first (XDG_DATA_HOME, then XDG_DATA_DIRS).</summary>
    internal static SharedMimeDatabase Load(ImmutableArray<string> dataSearch)
    {
        var database = new SharedMimeDatabase { _mimeDirectories = [.. dataSearch.Select(static directory => Path.Combine(directory, "mime"))] };
        var magic = new List<MagicSection>();
        for (int precedence = database._mimeDirectories.Length - 1; precedence >= 0; precedence--)
        {
            string directory = database._mimeDirectories[precedence];
            if (!Directory.Exists(directory)) continue;
            database.LoadGlobs(directory, precedence);
            LoadMagic(Path.Combine(directory, "magic"), precedence, magic);
            foreach (var (alias, type) in Pairs(Path.Combine(directory, "aliases"), ' ')) database._aliases[alias] = type;
            foreach (var (type, parent) in Pairs(Path.Combine(directory, "subclasses"), ' '))
            {
                if (!database._subclasses.TryGetValue(type, out var parents)) database._subclasses[type] = parents = [];
                if (!parents.Contains(parent, StringComparer.OrdinalIgnoreCase)) parents.Add(parent);
            }
            foreach (var (type, icon) in Pairs(Path.Combine(directory, "icons"), ':')) database._icons[type] = icon;
            foreach (var (type, icon) in Pairs(Path.Combine(directory, "generic-icons"), ':')) database._genericIcons[type] = icon;
        }
        // Higher priority first; among equal priorities the directory of higher precedence, then file order.
        database._magic = [.. magic.OrderByDescending(static section => section.Priority).ThenBy(static section => section.Precedence).ThenBy(static section => section.Index)];
        database.MagicExtent = Math.Min(MaximumExtent, database._magic.SelectMany(static section => Flatten(section.Rules)).Select(static rule => rule.Extent).DefaultIfEmpty(0).Max());
        foreach (var glob in database._globs)
        {
            if (glob.Literal || !IsSimpleSuffix(glob.Pattern)) continue;
            string key = glob.CaseSensitive ? glob.Pattern[1..] : glob.Folded[1..];
            if (!database._suffixes.TryGetValue(key, out var list)) database._suffixes[key] = list = [];
            list.Add(glob);
        }
        return database;
    }

    /// <summary>The files the database reads, for change detection by a cache.</summary>
    internal static IEnumerable<string> SourceFiles(ImmutableArray<string> dataSearch) =>
        dataSearch.SelectMany(static directory => ((string[])["globs2", "globs", "magic", "aliases", "subclasses", "icons", "generic-icons"])
            .Select(name => Path.Combine(directory, "mime", name)));

    private static IEnumerable<Matchlet> Flatten(IEnumerable<Matchlet> rules) => rules.SelectMany(static rule => Flatten(rule.Children).Prepend(rule));

    // globs2: weight:type:pattern[:flags[:...]]; the deprecated globs (type:pattern, weight 50) only without globs2.
    private void LoadGlobs(string directory, int precedence)
    {
        bool weighted = File.Exists(Path.Combine(directory, "globs2"));
        var lines = Lines(Path.Combine(directory, weighted ? "globs2" : "globs"));
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var fields = line.Split(':');
            int weight = 50;
            string type, pattern;
            bool caseSensitive = false;
            if (weighted)
            {
                if (fields.Length < 3 || !int.TryParse(fields[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out weight)) continue;
                type = fields[1]; pattern = fields[2];
                if (fields.Length > 3) caseSensitive = fields[3].Split(',').Contains("cs");
            }
            else
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                type = line[..colon]; pattern = line[(colon + 1)..];
            }
            if (type.Length == 0 || pattern.Length == 0) continue;
            if (pattern == "__NOGLOBS__")
            {
                // Discards the patterns of earlier directories; this file's own patterns follow the marker.
                if (deleted.Add(type)) _globs.RemoveAll(glob => glob.Precedence != precedence && glob.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
                continue;
            }
            _globs.Add(new Glob(pattern, type, Math.Clamp(weight, 0, 100), caseSensitive, pattern.AsSpan().IndexOfAny("*?[") < 0, precedence, _globs.Count));
        }
    }

    // The binary magic format: "MIME-Magic\0\n", then sections "[priority:type]\n" of matchlet lines
    // "[indent]>offset=<u16 length><value>[&<mask>][~word size][+range]\n". Numbers in the value are big-endian;
    // host-order values carry a word size and are swapped on little-endian machines.
    private static void LoadMagic(string path, int precedence, List<MagicSection> sections)
    {
        byte[] data;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumSourceBytes) return;
            data = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        ReadOnlySpan<byte> header = "MIME-Magic\0\n"u8;
        if (!data.AsSpan().StartsWith(header)) return;
        int position = header.Length;
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (position < data.Length)
        {
            if (data[position] != '[') { SkipLine(data, ref position); continue; }
            int close = Array.IndexOf(data, (byte)']', position);
            if (close < 0 || close + 1 >= data.Length || data[close + 1] != '\n') return;
            string heading = Encoding.UTF8.GetString(data, position + 1, close - position - 1);
            position = close + 2;
            int colon = heading.IndexOf(':');
            MagicSection? section = null;
            if (colon > 0 && int.TryParse(heading.AsSpan(0, colon), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int priority))
                section = new MagicSection(Math.Clamp(priority, 0, 100), heading[(colon + 1)..], [], precedence, sections.Count);
            var stack = new List<Matchlet>();
            while (position < data.Length && data[position] != '[')
            {
                if (!TryReadMatchlet(data, ref position, out int indent, out var matchlet, out bool noMagic)) continue;
                if (section is null) continue;
                if (noMagic)
                {
                    if (deleted.Add(section.Type)) sections.RemoveAll(other => other.Precedence != precedence && other.Type.Equals(section.Type, StringComparison.OrdinalIgnoreCase));
                    continue;
                }
                if (indent == 0) { section.Rules.Add(matchlet!); stack.Clear(); stack.Add(matchlet!); continue; }
                if (indent > stack.Count) continue; // A child without its parent.
                stack[indent - 1].Children.Add(matchlet!);
                stack.RemoveRange(indent, stack.Count - indent);
                stack.Add(matchlet!);
            }
            if (section is { Rules.Count: > 0 }) sections.Add(section);
        }
    }

    private static bool TryReadMatchlet(byte[] data, ref int position, out int indent, out Matchlet? matchlet, out bool noMagic)
    {
        matchlet = null; noMagic = false;
        int start = position;
        indent = ReadNumber(data, ref position) ?? 0;
        if (position >= data.Length || data[position] != '>') { position = start; SkipLine(data, ref position); return false; }
        position++;
        if (ReadNumber(data, ref position) is not { } offset || position + 3 > data.Length || data[position] != '=')
        { SkipLine(data, ref position); return false; }
        int length = data[position + 1] << 8 | data[position + 2];
        position += 3;
        if (position + length > data.Length) { position = data.Length; return false; }
        byte[] value = data.AsSpan(position, length).ToArray();
        position += length;
        byte[]? mask = null;
        int wordSize = 1, range = 1;
        if (position < data.Length && data[position] == '&')
        {
            if (position + 1 + length > data.Length) { position = data.Length; return false; }
            mask = data.AsSpan(position + 1, length).ToArray();
            position += 1 + length;
        }
        if (position < data.Length && data[position] == '~') { position++; wordSize = ReadNumber(data, ref position) ?? 1; }
        if (position < data.Length && data[position] == '+') { position++; range = ReadNumber(data, ref position) ?? 1; }
        // An unknown character where the newline belongs is a future extension: the line is ignored.
        if (position >= data.Length || data[position] != '\n') { SkipLine(data, ref position); return false; }
        position++;
        if (indent == 0 && offset == 0 && value.AsSpan().SequenceEqual("__NOMAGIC__"u8)) { noMagic = true; return true; }
        if (length == 0 || range < 1 || offset < 0 || offset > MaximumExtent) return false;
        if (wordSize > 1 && BitConverter.IsLittleEndian && length % wordSize == 0)
        {
            for (int i = 0; i < length; i += wordSize)
            {
                Array.Reverse(value, i, wordSize);
                if (mask is not null) Array.Reverse(mask, i, wordSize);
            }
        }
        matchlet = new Matchlet(offset, Math.Min(range, MaximumExtent), value, mask);
        return true;
    }

    private static int? ReadNumber(byte[] data, ref int position)
    {
        long value = 0;
        int start = position;
        while (position < data.Length && data[position] is >= (byte)'0' and <= (byte)'9' && position - start < 9)
            value = value * 10 + (data[position++] - '0');
        return position == start ? null : (int)value;
    }

    private static void SkipLine(byte[] data, ref int position)
    {
        int newline = Array.IndexOf(data, (byte)'\n', position);
        position = newline < 0 ? data.Length : newline + 1;
    }

    private static List<string> Lines(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumSourceBytes) return [];
            return [.. File.ReadAllLines(path, Encoding.UTF8).Select(static line => line.TrimEnd('\r'))];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<(string, string)> Pairs(string path, char separator)
    {
        foreach (var line in Lines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            int split = line.IndexOf(separator);
            if (split <= 0 || split == line.Length - 1) continue;
            yield return (line[..split].Trim(), line[(split + 1)..].Trim());
        }
    }

    private static bool IsSimpleSuffix(string pattern) =>
        pattern.Length > 2 && pattern[0] == '*' && pattern[1] == '.' && pattern.AsSpan(2).IndexOfAny("*?[\\") < 0;

    /// <summary>The canonical name of <paramref name="type"/>, resolving an alias.</summary>
    internal string Unalias(string type) => _aliases.TryGetValue(type, out var canonical) ? canonical : type;

    /// <summary>The types whose glob patterns match <paramref name="name"/> best, without touching a file: literal patterns
    /// before all others, then the highest weight, then the longest pattern. Several types mean the name is ambiguous.</summary>
    internal ImmutableArray<string> MatchName(string name)
    {
        if (name.Length == 0) return [];
        string folded = name.ToLowerInvariant();
        var matches = _globs.Where(glob => glob.Literal && (glob.CaseSensitive ? glob.Pattern == name : glob.Folded == folded)).ToList();
        if (matches.Count == 0)
        {
            // As xdgmime does, a case-sensitive suffix such as *.C for C++ beats a case-insensitive one such as *.c.
            for (int dot = name.IndexOf('.'); dot >= 0; dot = name.IndexOf('.', dot + 1))
                if (_suffixes.TryGetValue(name[dot..], out var exact)) matches.AddRange(exact.Where(static glob => glob.CaseSensitive));
            if (matches.Count == 0)
                for (int dot = name.IndexOf('.'); dot >= 0; dot = name.IndexOf('.', dot + 1))
                    if (_suffixes.TryGetValue(folded[dot..], out var any)) matches.AddRange(any.Where(static glob => !glob.CaseSensitive));
            foreach (var glob in _globs)
                if (!glob.Literal && !IsSimpleSuffix(glob.Pattern) && (glob.CaseSensitive ? FnMatch(glob.Pattern, name) : FnMatch(glob.Folded, folded)))
                    matches.Add(glob);
        }
        if (matches.Count == 0) return [];
        int weight = matches.Max(static glob => glob.Weight);
        matches.RemoveAll(glob => glob.Weight != weight);
        int length = matches.Max(static glob => glob.Pattern.Length);
        return [.. matches.Where(glob => glob.Pattern.Length == length).OrderBy(static glob => glob.Precedence).ThenBy(static glob => glob.Index)
            .Select(glob => Unalias(glob.Type)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The type of the highest-priority magic rule that matches <paramref name="data"/>, or null.</summary>
    internal string? MatchContent(ReadOnlySpan<byte> data)
    {
        foreach (var section in _magic)
            if (section.Matches(data)) return Unalias(section.Type);
        return null;
    }

    /// <summary>The types <paramref name="type"/> is also an instance of, nearest first: explicit subclasses, text/plain
    /// for text types and, last, application/octet-stream for every streamable type.</summary>
    /// <remarks>inode, x-scheme-handler and x-content types are not byte streams, so they get no octet-stream parent.</remarks>
    internal ImmutableArray<string> Parents(string type)
    {
        type = Unalias(type);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { type };
        var queue = new Queue<string>([type]);
        while (queue.TryDequeue(out var current))
        {
            var parents = _subclasses.TryGetValue(current, out var explicitParents) ? explicitParents.Select(Unalias).ToList() : [];
            if (current.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) parents.Add(TextPlain);
            foreach (var parent in parents)
            {
                if (parent.Equals(OctetStream, StringComparison.OrdinalIgnoreCase) || !seen.Add(parent)) continue;
                result.Add(parent);
                queue.Enqueue(parent);
            }
        }
        if (Streamable(type) && !type.Equals(OctetStream, StringComparison.OrdinalIgnoreCase)) result.Add(OctetStream);
        return [.. result];
    }

    private static bool Streamable(string type) =>
        !type.StartsWith("inode/", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("x-scheme-handler/", StringComparison.OrdinalIgnoreCase)
        && !type.StartsWith("x-content/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="type"/> equals <paramref name="ancestor"/> or is a subclass of it.</summary>
    internal bool IsA(string type, string? ancestor)
    {
        if (ancestor is null) return false;
        type = Unalias(type); ancestor = Unalias(ancestor);
        return type.Equals(ancestor, StringComparison.OrdinalIgnoreCase) || Parents(type).Contains(ancestor, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The type's icon name: from the icons file, else media-subtype.</summary>
    internal string IconName(string type)
    {
        type = Unalias(type);
        return _icons.TryGetValue(type, out var icon) ? icon : type.Replace('/', '-');
    }

    /// <summary>The type's generic icon name: from the generic-icons file, else media-x-generic.</summary>
    internal string GenericIconName(string type)
    {
        type = Unalias(type);
        return _genericIcons.TryGetValue(type, out var icon) ? icon : type[..Math.Max(0, type.IndexOf('/'))] + "-x-generic";
    }

    /// <summary>The extensions of the type's simple *.ext patterns, main one first.</summary>
    internal ImmutableArray<string> Extensions(string type)
    {
        type = Unalias(type);
        return [.. _globs.Where(glob => !glob.Literal && IsSimpleSuffix(glob.Pattern) && Unalias(glob.Type).Equals(type, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static glob => glob.Weight).ThenBy(static glob => glob.Precedence).ThenBy(static glob => glob.Index)
            .Select(static glob => glob.Pattern[2..]).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The type's description from &lt;MIME&gt;/media/subtype.xml for the locale, from the directory of highest
    /// precedence that has the file.</summary>
    internal string? Description(string type, DesktopLocale locale)
    {
        type = Unalias(type);
        if (!DesktopMimeType.IsValid(type)) return null;
        int slash = type.IndexOf('/');
        foreach (var directory in _mimeDirectories)
        {
            string path = Path.Combine(directory, type[..slash], type[(slash + 1)..] + ".xml");
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                if (info.Length > MaximumSourceBytes) return null;
                return ReadComment(path, locale);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException) { return null; }
        }
        return null;
    }

    private static string? ReadComment(string path, DesktopLocale locale)
    {
        var comments = new Dictionary<string, string>(StringComparer.Ordinal);
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true });
        if (!reader.ReadToFollowing("mime-type") || reader.IsEmptyElement) return null;
        int depth = reader.Depth;
        reader.Read();
        while (!reader.EOF && reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1 && reader.LocalName == "comment")
            {
                string language = (reader.XmlLang ?? "").Replace('-', '_');
                comments.TryAdd(language, reader.ReadElementContentAsString().Trim()); // Moves past the element.
                continue;
            }
            reader.Read();
        }
        foreach (var candidate in locale.Candidates)
            if (comments.TryGetValue(candidate, out var localized) && localized.Length > 0) return localized;
        return comments.TryGetValue("", out var comment) && comment.Length > 0 ? comment : null;
    }

    // fnmatch(3) without flags: * and ? match any character, including a leading dot; [...] is a set with ranges and
    // ! or ^ negation; a backslash escapes the next character.
    internal static bool FnMatch(string pattern, string name)
    {
        int p = 0, n = 0, starPattern = -1, starName = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && Step(pattern, ref p, name[n])) { n++; continue; }
            if (p < pattern.Length && pattern[p] == '*') { starPattern = p++; starName = n; continue; }
            if (starPattern < 0) return false;
            p = starPattern + 1;
            n = ++starName;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    // Matches one name character against the pattern element at p, advancing p past it. A star is not an element.
    private static bool Step(string pattern, ref int p, char c)
    {
        switch (pattern[p])
        {
            case '*': return false;
            case '?': p++; return true;
            case '[':
                int set = p;
                if (MatchSet(pattern, ref set, c, out bool matched)) { if (matched) p = set; return matched; }
                if (c != '[') return false; // An unterminated set is a literal '['.
                p++; return true;
            case '\\' when p + 1 < pattern.Length:
                if (pattern[p + 1] != c) return false;
                p += 2; return true;
            default:
                if (pattern[p] != c) return false;
                p++; return true;
        }
    }

    // Reads [set] at pattern[p]; on success p is past the set. An unterminated set is a literal '['.
    private static bool MatchSet(string pattern, ref int p, char c, out bool matched)
    {
        matched = false;
        int i = p + 1;
        bool negate = i < pattern.Length && pattern[i] is '!' or '^';
        if (negate) i++;
        bool first = true, found = false;
        while (i < pattern.Length && (pattern[i] != ']' || first))
        {
            first = false;
            char low = pattern[i];
            if (low == '\\' && i + 1 < pattern.Length) low = pattern[++i];
            char high = low;
            if (i + 2 < pattern.Length && pattern[i + 1] == '-' && pattern[i + 2] != ']')
            {
                high = pattern[i + 2];
                if (high == '\\' && i + 3 < pattern.Length) high = pattern[++i + 2];
                i += 2;
            }
            if (c >= low && c <= high) found = true;
            i++;
        }
        if (i >= pattern.Length) return false;
        p = i + 1;
        matched = found != negate;
        return true;
    }
}

// MIME type syntax from RFC 6838: type/subtype, each starting with a letter or digit, of restricted name characters.
internal static class DesktopMimeType
{
    internal static bool IsValid(string type)
    {
        int slash = type.IndexOf('/');
        return slash > 0 && slash < type.Length - 1 && type.IndexOf('/', slash + 1) < 0 && type.Length <= 255
            && Part(type.AsSpan(0, slash)) && Part(type.AsSpan(slash + 1));
    }

    private static bool Part(ReadOnlySpan<char> part)
    {
        if (part.Length is 0 or > 127 || !char.IsAsciiLetterOrDigit(part[0])) return false;
        foreach (char c in part)
            if (!char.IsAsciiLetterOrDigit(c) && "!#$&^_.+-".IndexOf(c) < 0) return false;
        return true;
    }
}
