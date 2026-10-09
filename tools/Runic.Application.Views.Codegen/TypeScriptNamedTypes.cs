using System.Text;
using System.Text.Json;
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
    private static readonly Dictionary<(Type Type, string Shape), Entry> ByShape = [];
    private static readonly List<Entry> Entries = [];

    // Global TypeScript types and keywords that generated code relies on, and
    // reserved words and type operators that cannot name a type (a C# type can
    // be named @class or @default). A named type must not shadow or use them.
    private static readonly HashSet<string> Globals = new(StringComparer.Ordinal)
    {
        "Array", "BigInt", "Boolean", "Date", "Error", "Function", "JSON", "Map", "Math", "Number", "Object",
        "Omit", "Partial", "Pick", "Promise", "RangeError", "Readonly", "ReadonlyArray", "Record", "Set",
        "String", "Symbol", "TypeError", "any", "bigint", "boolean", "never", "null", "number", "object",
        "string", "symbol", "undefined", "unknown", "void",
        "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do",
        "else", "enum", "export", "extends", "false", "finally", "for", "function", "if", "implements",
        "import", "in", "infer", "instanceof", "interface", "is", "keyof", "let", "new", "package", "private",
        "protected", "public", "readonly", "return", "static", "super", "switch", "this", "throw", "true",
        "try", "type", "typeof", "unique", "var", "while", "with", "yield",
    };

    internal static bool IsNamed(BridgeTypeNode node) =>
        node.Kind is BridgeWireKind.Enum or BridgeWireKind.Dto or BridgeWireKind.Union;

    /// <summary>Returns the placeholder for a named node and records its definition.</summary>
    /// <remarks>
    /// Uses of one C# type share a declaration unless their member
    /// nullability differs, as for <c>Box&lt;string&gt;</c> and
    /// <c>Box&lt;string?&gt;</c> when the box has a member of type <c>T</c>.
    /// </remarks>
    internal static string Reference(BridgeTypeNode node)
    {
        var key = (node.NonNullableType, Shape(node));
        if (!ByShape.TryGetValue(key, out var entry))
        {
            entry = new Entry(Entries.Count, key.NonNullableType, node);
            ByShape.Add(key, entry);
            Entries.Add(entry);
        }
        return $"{Marker}{entry.Index}{Marker}";
    }

    // A named node's included members and their nullability, down to the named
    // types they contain. The same DTO can have different exclusions when
    // ViewModels declared in different assemblies use different policies.
    private static string Shape(BridgeTypeNode node) => node.Kind switch
    {
        BridgeWireKind.Dto => string.Join(",", node.Members.Select(member => JsonSerializer.Serialize(member.WireName) + ":" + Use(member.Type))),
        BridgeWireKind.Union => string.Join(",", node.Cases.Select(@case => Shape(@case.Type))),
        _ => "",
    };

    private static string Use(BridgeTypeNode node) => (node.IsNullable ? "?" : "")
        + (node.Element is { } element ? "[" + Use(element) + "]" : "")
        + (node.Value is { } value ? "{" + Use(value) + "}" : "")
        + (IsNamed(node) ? "(" + Shape(node) + ")" : "");

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

        // Uses of one C# type with different shapes are told apart by the
        // nullable annotations of their type arguments: Box<string> keeps
        // BoxOfString and Box<string?> becomes BoxOfNullableOfString.
        foreach (var entry in Entries)
            entry.BaseName = Entries.Count(other => other.Type == entry.Type) > 1
                ? BaseName(entry.Type, entry.Node.Nullability) : BaseName(entry.Type, null);

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
        return string.Concat(qualifiers.Take(level).Reverse()) + entry.BaseName;
    }

    // Declaring types, then namespace segments, innermost first.
    private static List<string> Qualifiers(Type type)
    {
        var result = new List<string>();
        for (var outer = type.DeclaringType; outer is not null; outer = outer.DeclaringType) result.Add(BaseName(outer, null));
        if (type.Namespace is { } ns)
            result.AddRange(ns.Split('.').Reverse().Where(segment => segment.Length > 0)
                .Select(segment => Identifier(char.ToUpperInvariant(segment[0]) + segment[1..])));
        return result;
    }

    // Page<NoteRow> -> PageOfNoteRow, Box<string[]> -> BoxOfArrayOfString,
    // and with annotations Box<string?> -> BoxOfNullableOfString like
    // Box<int?> -> BoxOfNullableOfInt32. The result is always a TypeScript
    // identifier.
    private static string BaseName(Type type, TypeNullability? nullability)
    {
        if (type.IsArray) return "ArrayOf" + ArgumentName(type.GetElementType()!, nullability?.Element);
        if (type.IsByRef || type.IsPointer) return BaseName(type.GetElementType()!, nullability);
        if (Nullable.GetUnderlyingType(type) is { } underlying) return "NullableOf" + BaseName(underlying, nullability);
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0) name = name[..tick];
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
            name += "Of" + string.Join("And", type.GenericTypeArguments.Select((argument, index) =>
                ArgumentName(argument, nullability?.Argument(index))));
        return Identifier(name);
    }

    private static string ArgumentName(Type type, TypeNullability? nullability) =>
        (nullability?.IsNullable == true && !type.IsValueType ? "NullableOf" : "") + BaseName(type, nullability);

    private static string Identifier(string name)
    {
        var result = new StringBuilder(name.Length);
        foreach (var character in name)
            if (char.IsLetterOrDigit(character) || character is '_' or '$') result.Append(character);
        if (result.Length == 0 || char.IsDigit(result[0])) result.Insert(0, '_');
        return result.ToString();
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
        internal string BaseName { get; set; } = "";
        internal string? Name { get; set; }
    }
}
