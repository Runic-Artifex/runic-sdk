using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// The declaration map of a generated client's contract (#952): <c>&lt;view&gt;.contract.d.ts.map</c> leads each
/// member of the client's interfaces to the C# declaration or handler that the development source map (#951) lists for
/// it, so Go to Definition on <c>client.increment()</c> in a TypeScript editor opens the ViewModel's C#.
/// </summary>
/// <remarks>
/// <para>
/// The emitter ends a declaration line with <see cref="Mark"/>: the member's source map id and the name the line
/// declares. Once the module text is final, <see cref="Complete"/> removes the marks and writes a source map v3 whose
/// <c>sources</c> are relative to the map, so a committed client is the same on every machine. A member whose source
/// has no relative path (another drive) or no location (no PDB) stays unmapped; a contract with no mapped member gets
/// no map and no <c>sourceMappingURL</c> comment.
/// </para>
/// <para>
/// tsserver looks a position up by binary search and, without an exact hit, takes the next mapping, and one invalid
/// segment disables the whole map. So every line starts with a generated-only segment, each mapped name is followed by
/// one, and positions other than a mapped name stay in the <c>.d.ts</c> instead of jumping to the next member.
/// </para>
/// </remarks>
internal static partial class ClientDeclarationMap
{
    /// <summary>The suffix of a generated client's contract module, beside its <c>&lt;view&gt;.ts</c>.</summary>
    internal const string Suffix = ".contract.d.ts";

    private const char Marker = '\u0006';
    private static readonly List<(string Id, string Name)> Targets = [];

    /// <summary>The contract module of a client module: <c>counter.ts</c> → <c>counter.contract.d.ts</c>.</summary>
    internal static string ContractPath(string typescriptPath) =>
        Path.Combine(Path.GetDirectoryName(typescriptPath) ?? "", Path.GetFileNameWithoutExtension(typescriptPath) + Suffix);

    /// <summary>The module specifier of a client module's contract, as its <c>.ts</c> imports it.</summary>
    internal static string Specifier(string typescriptPath) => $"./{Path.GetFileNameWithoutExtension(typescriptPath)}.contract.js";

    /// <summary>
    /// A mark that ends the line declaring <paramref name="name"/>, for the source map entry <paramref name="id"/>
    /// (a ViewModel id or <c>{viewModel}/{kind}/{member}</c>).
    /// </summary>
    internal static string Mark(string id, string name)
    {
        Targets.Add((id, name));
        return $"{Marker}{Targets.Count - 1}{Marker}";
    }

    /// <summary>
    /// Removes the marks from the final contract text and returns it, with a <c>sourceMappingURL</c> comment when a
    /// member is mapped, and the map's JSON, or <see langword="null"/> when no member is.
    /// </summary>
    internal static (string Text, string? Map) Complete(string path, string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var sourcePaths = new Dictionary<string, string?>(StringComparer.Ordinal);
        var names = new List<(int Line, int Column, int Length, string Source, int SourceLine, int SourceColumn)>();
        for (var line = 0; line < lines.Length; line++)
        {
            var marks = Marks().Matches(lines[line]);
            if (marks.Count == 0) continue;
            lines[line] = Marks().Replace(lines[line], "");
            foreach (Match mark in marks)
            {
                var (id, name) = Targets[int.Parse(mark.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)];
                if (Column(lines[line], name) is not { } column || BridgeSourceMap.LocationOf(id) is not { } location) continue;
                if (!sourcePaths.TryGetValue(location.File, out var source))
                    sourcePaths[location.File] = source = RelativeSource(directory, location.File);
                if (source is null) continue;
                names.Add((line, column, name.Length, source, location.Line - 1, location.Column - 1));
            }
        }
        var body = string.Join('\n', lines);
        if (names.Count == 0) return (body, null);
        if (!body.EndsWith('\n')) body += "\n";
        var file = Path.GetFileName(path);
        body += $"//# sourceMappingURL={file}.map\n";
        return (body, Render(file, body, names));
    }

    /// <summary>True when <paramref name="path"/> is a declaration map this generator wrote for its contract.</summary>
    internal static bool IsGeneratedMap(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.EndsWith(Suffix + ".map", StringComparison.Ordinal)) return false;
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[256];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read).StartsWith("{\"version\":3,\"file\":" + JsonSerializer.Serialize(name[..^".map".Length], Json),
                StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    // The column of the declared name: after the indentation and "export interface " or "readonly ".
    private static int? Column(string line, string name)
    {
        var column = line.Length - line.TrimStart(' ').Length;
        foreach (var prefix in (string[])["export interface ", "readonly "])
            if (string.CompareOrdinal(line, column, prefix, 0, prefix.Length) == 0) { column += prefix.Length; break; }
        return string.CompareOrdinal(line, column, name, 0, name.Length) == 0 ? column : null;
    }

    // A source path relative to the map, with forward slashes; null on another drive or share.
    private static string? RelativeSource(string directory, string file)
    {
        var relative = Path.GetRelativePath(directory, file);
        return Path.IsPathRooted(relative) ? null : relative.Replace('\\', '/');
    }

    private static string Render(string file, string body, List<(int Line, int Column, int Length, string Source, int SourceLine, int SourceColumn)> names)
    {
        var sources = names.Select(name => name.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var sourceIndex = sources.Select((source, index) => (source, index)).ToDictionary(pair => pair.source, pair => pair.index, StringComparer.Ordinal);
        var byLine = names.GroupBy(name => name.Line).ToDictionary(group => group.Key, group => group.OrderBy(name => name.Column).ToArray());
        var lines = body.Split('\n');
        var lineCount = body.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        var mappings = new StringBuilder();
        int previousSource = 0, previousSourceLine = 0, previousSourceColumn = 0;
        for (var line = 0; line < lineCount; line++)
        {
            if (line != 0) mappings.Append(';');
            int previousColumn = 0;
            var first = true;
            void Segment(int column)
            {
                if (!first) mappings.Append(',');
                first = false;
                Vlq(mappings, column - previousColumn);
                previousColumn = column;
            }
            byLine.TryGetValue(line, out var named);
            if (named is null || named[0].Column != 0) Segment(0);
            foreach (var name in named ?? [])
            {
                Segment(name.Column);
                var index = sourceIndex[name.Source];
                Vlq(mappings, index - previousSource);
                previousSource = index;
                Vlq(mappings, name.SourceLine - previousSourceLine);
                previousSourceLine = name.SourceLine;
                Vlq(mappings, name.SourceColumn - previousSourceColumn);
                previousSourceColumn = name.SourceColumn;
                var end = name.Column + name.Length;
                if (end < lines[line].Length) Segment(end);
            }
        }
        // tsserver ignores a map with sourcesContent; "file" names the declarations.
        return $"{{\"version\":3,\"file\":{JsonSerializer.Serialize(file, Json)},\"sourceRoot\":\"\",\"sources\":{JsonSerializer.Serialize(sources, Json)},\"names\":[],\"mappings\":{JsonSerializer.Serialize(mappings.ToString(), Json)}}}\n";
    }

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const string Base64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    // Base64 VLQ: the sign in the lowest bit, then 5-bit groups, least significant first, with bit 6 as continuation.
    private static void Vlq(StringBuilder builder, int value)
    {
        var remaining = value < 0 ? ((-(long)value) << 1) | 1 : (long)value << 1;
        do
        {
            var digit = (int)(remaining & 31);
            remaining >>= 5;
            if (remaining > 0) digit |= 32;
            builder.Append(Base64[digit]);
        }
        while (remaining > 0);
    }

    [GeneratedRegex("\u0006(\\d+)\u0006")]
    private static partial Regex Marks();
}
