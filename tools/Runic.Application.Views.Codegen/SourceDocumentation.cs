using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

/// <summary>
/// Reads the <c>///</c> comment of a partial property's defining declaration
/// from source. For a partial property the compiler writes the implementation
/// part's comment to the XML documentation file, and MVVM source generators
/// document their implementation as <c>&lt;inheritdoc/&gt;</c>, so the summary
/// the developer wrote on <c>[ObservableProperty] public partial T X { get; set; }</c>
/// or <c>[Reactive] public partial T X { get; set; }</c> is only in source.
/// </summary>
/// <remarks>
/// The portable PDB names the files that declare the type. A light line scan
/// finds the defining declaration there, which has accessors without bodies,
/// and reads the <c>///</c> lines above it and its attributes. When the scan
/// cannot tell one declaration apart, as for two types in one file with a
/// partial property of the same name, it returns nothing and the client keeps
/// the property undocumented, as before.
/// </remarks>
internal static partial class SourceDocumentation
{
    private static readonly Dictionary<string, string[]?> Files = new(StringComparer.Ordinal);

    /// <summary>The documentation of the declaring part of a partial property, as a member element, or null.</summary>
    internal static XElement? PartialProperty(PropertyInfo property)
    {
        if (property.DeclaringType is not { } type) return null;
        var definition = new Regex(@"\bpartial\b[^;={}]*?(?<![\w@])@?" + Regex.Escape(property.Name)
            + @"\s*\{\s*(?:(?:public|internal|protected|private)\s+)*(?:get|set|init)\s*;", RegexOptions.CultureInvariant);
        var matches = new List<(string[] Lines, int Line)>();
        foreach (var path in SourceLocator.Documents(type))
        {
            if (Lines(path) is not { } lines) continue;
            for (var line = 0; line < lines.Length; line++)
                if (definition.IsMatch(lines[line])) matches.Add((lines, line));
        }
        // Prefer declarations inside a type of the reflected name.
        if (matches.Count > 1)
            matches = matches.Where(match => EnclosingTypeName(match.Lines, match.Line) == SimpleName(type)).ToList();
        return matches.Count == 1 ? Element(matches[0].Lines, matches[0].Line) : null;
    }

    // The name in the nearest type declaration above the line.
    private static string? EnclosingTypeName(string[] lines, int line)
    {
        for (var index = line; index >= 0; index--)
            if (TypeDeclaration().Match(lines[index]) is { Success: true } match) return match.Groups["name"].Value;
        return null;
    }

    private static string SimpleName(Type type)
    {
        var tick = type.Name.IndexOf('`', StringComparison.Ordinal);
        return tick >= 0 ? type.Name[..tick] : type.Name;
    }

    private static XElement? Element(string[] lines, int declaration)
    {
        // Walk up past attribute lines and blank lines to the /// block.
        var comment = new List<string>();
        for (var index = declaration - 1; index >= 0; index--)
        {
            var trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith("///", StringComparison.Ordinal)) comment.Insert(0, trimmed[3..]);
            else if (comment.Count == 0 && (trimmed.Length == 0 || trimmed.StartsWith('['))) continue;
            else break;
        }
        if (comment.Count == 0) return null;
        try
        {
            return XElement.Parse("<member>" + string.Join('\n', comment) + "</member>", LoadOptions.PreserveWhitespace);
        }
        // Malformed XML in a comment (CS1570) has no summary to copy.
        catch (XmlException)
        {
            return null;
        }
    }

    private static string[]? Lines(string path)
    {
        if (Files.TryGetValue(path, out var lines)) return lines;
        try
        {
            lines = File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n');
        }
        // Documentation is optional; an unreadable file only means no comment.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            lines = null;
        }
        Files.Add(path, lines);
        return lines;
    }

    [GeneratedRegex(@"\b(?:class|struct|record|interface)\s+(?:class\s+|struct\s+)?(?<name>[A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclaration();
}
