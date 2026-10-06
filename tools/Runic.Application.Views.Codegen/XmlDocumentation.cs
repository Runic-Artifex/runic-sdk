using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

/// <summary>
/// Reads the C# XML documentation file beside a compiled assembly and renders
/// a member's <c>&lt;summary&gt;</c> as TSDoc text. The bootstrap build writes
/// the file; without it, generated TypeScript simply has no member comments.
/// </summary>
internal static partial class XmlDocumentation
{
    private static readonly Dictionary<Assembly, Dictionary<string, XElement>?> Files = [];

    /// <summary>The rendered summary of a type, property, field or method, or null.</summary>
    internal static string? Summary(MemberInfo member)
    {
        var element = Find(member);
        // CommunityToolkit's [ObservableProperty] documents the generated
        // property as <inheritdoc cref="F:...field"/>; follow such references.
        for (var depth = 0; depth < 4 && element is not null && element.Element("summary") is null
             && (string?)element.Element("inheritdoc")?.Attribute("cref") is { } cref; depth++)
            element = Load((member as Type ?? member.DeclaringType)!.Assembly)?.GetValueOrDefault(cref);
        if (element?.Element("summary") is { } summary) return Render(summary);
        // A positional record documents its properties as primary-constructor parameters.
        if (member is PropertyInfo property && property.DeclaringType is { } declaring
            && Find(declaring)?.Elements("param").FirstOrDefault(param => (string?)param.Attribute("name") == property.Name) is { } parameter)
            return Render(parameter);
        return null;
    }

    /// <summary>Formats documentation text as a TSDoc block at the given indentation.</summary>
    internal static void Append(StringBuilder source, string indent, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var lines = text.Replace("*/", "*\\/", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 1)
        {
            source.AppendLine($"{indent}/** {lines[0]} */");
            return;
        }
        source.AppendLine($"{indent}/**");
        foreach (var line in lines)
            source.AppendLine(line.Length == 0 ? $"{indent} *" : $"{indent} * {line}");
        source.AppendLine($"{indent} */");
    }

    private static XElement? Find(MemberInfo member)
    {
        var type = member as Type ?? member.DeclaringType;
        if (type is null) return null;
        var members = Load(type.Assembly);
        if (members is null) return null;
        switch (member)
        {
            case Type:
                return members.GetValueOrDefault("T:" + TypeId(type));
            case PropertyInfo:
                return members.GetValueOrDefault("P:" + TypeId(type) + "." + member.Name);
            case FieldInfo:
                return members.GetValueOrDefault("F:" + TypeId(type) + "." + member.Name);
            case MethodInfo method:
            {
                var prefix = "M:" + TypeId(type) + "." + method.Name;
                var candidates = members.Where(entry => entry.Key == prefix || entry.Key.StartsWith(prefix + "(", StringComparison.Ordinal))
                    .ToArray();
                if (candidates.Length <= 1) return candidates.FirstOrDefault().Value;
                var exact = prefix + (method.GetParameters().Length == 0 ? ""
                    : "(" + string.Join(",", method.GetParameters().Select(parameter => TypeId(parameter.ParameterType))) + ")");
                return members.GetValueOrDefault(exact);
            }
            default:
                return null;
        }
    }

    private static string TypeId(Type type)
    {
        if (type.IsGenericType && !type.IsGenericTypeDefinition) type = type.GetGenericTypeDefinition();
        return (type.FullName ?? type.Name).Replace('+', '.');
    }

    private static Dictionary<string, XElement>? Load(Assembly assembly)
    {
        if (Files.TryGetValue(assembly, out var cached)) return cached;
        Dictionary<string, XElement>? members = null;
        try
        {
            var location = assembly.IsDynamic ? "" : assembly.Location;
            var path = location.Length == 0 ? null : Path.ChangeExtension(location, ".xml");
            if (path is not null && File.Exists(path))
            {
                members = new(StringComparer.Ordinal);
                foreach (var element in XDocument.Load(path).Descendants("member"))
                    if ((string?)element.Attribute("name") is { Length: > 0 } name) members.TryAdd(name, element);
            }
        }
        // Documentation is optional. An unreadable or malformed file only
        // means the generated TypeScript has no comments.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
        {
            members = null;
        }
        Files.Add(assembly, members);
        return members;
    }

    private static string? Render(XElement element)
    {
        var text = new StringBuilder();
        RenderNodes(element.Nodes(), text);
        var paragraphs = text.ToString().Split('\n')
            .Select(line => Whitespace().Replace(line, " ").Trim())
            .ToList();
        // Collapse runs of blank lines and trim the ends.
        var lines = new List<string>();
        foreach (var line in paragraphs)
            if (line.Length > 0 || (lines.Count > 0 && lines[^1].Length > 0)) lines.Add(line);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private static void RenderNodes(IEnumerable<XNode> nodes, StringBuilder text)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case XText value:
                    text.Append(Whitespace().Replace(value.Value, " "));
                    break;
                case XElement element:
                    RenderElement(element, text);
                    break;
            }
        }
    }

    private static void RenderElement(XElement element, StringBuilder text)
    {
        switch (element.Name.LocalName)
        {
            case "see" or "seealso":
                if ((string?)element.Attribute("langword") is { } word) text.Append('`').Append(word).Append('`');
                else if ((string?)element.Attribute("cref") is { } cref)
                    text.Append('`').Append(element.Value is { Length: > 0 } label ? label : CrefName(cref)).Append('`');
                else if ((string?)element.Attribute("href") is { } href)
                    text.Append(element.Value is { Length: > 0 } link ? $"{link} ({href})" : href);
                else RenderNodes(element.Nodes(), text);
                break;
            case "paramref" or "typeparamref":
                text.Append('`').Append((string?)element.Attribute("name")).Append('`');
                break;
            case "c" or "code":
                text.Append('`').Append(Whitespace().Replace(element.Value, " ").Trim()).Append('`');
                break;
            case "para":
                text.Append("\n\n");
                RenderNodes(element.Nodes(), text);
                text.Append("\n\n");
                break;
            case "br":
                text.Append('\n');
                break;
            case "list":
                text.Append('\n');
                foreach (var item in element.Elements("item"))
                {
                    text.Append("\n- ");
                    if (item.Element("term") is { } term)
                    {
                        RenderNodes(term.Nodes(), text);
                        if (item.Element("description") is not null) text.Append(": ");
                    }
                    if (item.Element("description") is { } description) RenderNodes(description.Nodes(), text);
                    else if (item.Element("term") is null) RenderNodes(item.Nodes(), text);
                }
                text.Append("\n\n");
                break;
            default:
                RenderNodes(element.Nodes(), text);
                break;
        }
    }

    // "T:Ns.Outer.Type`1" or "M:Ns.Type.Method(System.String)" -> "Type" or "Method".
    private static string CrefName(string cref)
    {
        var name = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var parameters = name.IndexOf('(', StringComparison.Ordinal);
        if (parameters >= 0) name = name[..parameters];
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0) name = name[..tick];
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    [GeneratedRegex(@"[ \t\r\n]+")]
    private static partial Regex Whitespace();
}
