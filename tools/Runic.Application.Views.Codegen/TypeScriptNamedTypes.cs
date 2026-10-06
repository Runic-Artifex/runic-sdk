using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Gives every enum, DTO and union in the generated contract one exported
/// TypeScript type named after its C# type, shared by all generated modules.
/// Emitters write a placeholder for each reference; once every module has been
/// emitted, <see cref="Complete"/> assigns names that collide with no generated
/// declaration and <see cref="Resolve"/> substitutes them.
/// </summary>
internal static partial class TypeScriptNamedTypes
{
    /// <summary>The generated module that declares the named types.</summary>
    internal const string ModuleName = "types";

    private const char Marker = '\u0001';
    private static readonly Dictionary<Type, Entry> ByType = [];
    private static readonly List<Entry> Entries = [];

    // Global TypeScript types and keywords that generated code relies on.
    // A named type must not shadow them in a module that imports it.
    private static readonly HashSet<string> Globals = new(StringComparer.Ordinal)
    {
        "Array", "BigInt", "Boolean", "Date", "Error", "Function", "JSON", "Map", "Math", "Number", "Object",
        "Omit", "Partial", "Pick", "Promise", "RangeError", "Readonly", "ReadonlyArray", "Record", "Set",
        "String", "Symbol", "TypeError", "any", "bigint", "boolean", "never", "null", "number", "object",
        "string", "symbol", "undefined", "unknown", "void",
    };

    internal static bool IsNamed(BridgeTypeNode node) =>
        node.Kind is BridgeWireKind.Enum or BridgeWireKind.Dto or BridgeWireKind.Union;

    /// <summary>Returns the placeholder for a named node and records its definition.</summary>
    internal static string Reference(BridgeTypeNode node)
    {
        var type = node.NonNullableType;
        if (!ByType.TryGetValue(type, out var entry))
        {
            entry = new Entry(Entries.Count, type, node);
            ByType.Add(type, entry);
            Entries.Add(entry);
        }
        return $"{Marker}{entry.Index}{Marker}";
    }

    /// <summary>True when at least one module referenced a named type.</summary>
    internal static bool Any => Entries.Count > 0;

    /// <summary>
    /// Builds every definition and assigns final names. <paramref name="modules"/>
    /// are the emitted module texts; their declarations and imports are reserved.
    /// </summary>
    internal static void Complete(IEnumerable<string> modules)
    {
        // A definition can reference further named types, which register
        // themselves while it is built.
        for (var index = 0; index < Entries.Count; index++)
            Entries[index].Definition = BridgeTypeGraph.TypeScriptDeclaration(Entries[index].Node, Reference(Entries[index].Node));

        var reserved = new HashSet<string>(Globals, StringComparer.Ordinal);
        foreach (var module in modules) reserved.UnionWith(DeclaredNames(module));

        // Qualify colliding names with their declaring types and namespace
        // segments, innermost first, until every name is distinct.
        var levels = Entries.ToDictionary(entry => entry, _ => 0);
        for (var round = 0; round < 64; round++)
        {
            var conflicts = Entries.GroupBy(entry => Candidate(entry, levels[entry]), StringComparer.Ordinal)
                .Where(group => group.Count() > 1 || reserved.Contains(group.Key))
                .SelectMany(group => group)
                .Where(entry => levels[entry] < Qualifiers(entry.Type).Count)
                .ToArray();
            if (conflicts.Length == 0) break;
            foreach (var entry in conflicts) levels[entry]++;
        }
        var taken = new HashSet<string>(reserved, StringComparer.Ordinal);
        foreach (var entry in Entries.OrderBy(entry => levels[entry])
                     .ThenBy(entry => entry.Type.FullName ?? entry.Type.Name, StringComparer.Ordinal))
        {
            var name = Candidate(entry, levels[entry]);
            for (var suffix = 2; !taken.Add(name); suffix++) name = Candidate(entry, levels[entry]) + suffix;
            entry.Name = name;
        }
    }

    /// <summary>Replaces placeholders with the assigned names.</summary>
    internal static string Resolve(string text) => Placeholder().Replace(text, match =>
        Entries[int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)].Name
            ?? throw new InvalidOperationException("Named TypeScript types have not been completed."));

    /// <summary>The sorted names a module text references.</summary>
    internal static IReadOnlyList<string> Used(string text) => Placeholder().Matches(text)
        .Select(match => Entries[int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)].Name!)
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// <summary>The <c>types.ts</c> module body (without its header).</summary>
    internal static string Module()
    {
        var source = new StringBuilder();
        foreach (var entry in Entries.OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            source.Append(Resolve(entry.Definition!));
            source.AppendLine();
        }
        return source.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string Candidate(Entry entry, int level)
    {
        var qualifiers = Qualifiers(entry.Type);
        return string.Concat(qualifiers.Take(level).Reverse()) + BaseName(entry.Type);
    }

    // Declaring types, then namespace segments, innermost first.
    private static List<string> Qualifiers(Type type)
    {
        var result = new List<string>();
        for (var outer = type.DeclaringType; outer is not null; outer = outer.DeclaringType) result.Add(BaseName(outer));
        if (type.Namespace is { } ns)
            result.AddRange(ns.Split('.').Reverse().Where(segment => segment.Length > 0)
                .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..]));
        return result;
    }

    private static string BaseName(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0) name = name[..tick];
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
            name += "Of" + string.Join("And", type.GenericTypeArguments.Select(BaseName));
        return name;
    }

    private static IEnumerable<string> DeclaredNames(string module)
    {
        var code = Comment().Replace(module, " ");
        foreach (Match match in Declaration().Matches(code)) yield return match.Groups[1].Value;
        foreach (Match match in Import().Matches(code))
            foreach (var part in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = part.StartsWith("type ", StringComparison.Ordinal) ? part[5..].Trim() : part;
                var alias = name.IndexOf(" as ", StringComparison.Ordinal);
                yield return alias >= 0 ? name[(alias + 4)..].Trim() : name;
            }
    }

    [GeneratedRegex("\u0001([0-9]+)\u0001")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"\b(?:interface|type|function|const|let|class|enum)\s+([A-Za-z_$][\w$]*)")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"\bimport\s+(?:type\s+)?\{([^}]*)\}")]
    private static partial Regex Import();

    private sealed class Entry(int index, Type type, BridgeTypeNode node)
    {
        internal int Index { get; } = index;
        internal Type Type { get; } = type;
        internal BridgeTypeNode Node { get; } = node;
        internal string? Definition { get; set; }
        internal string? Name { get; set; }
    }
}
