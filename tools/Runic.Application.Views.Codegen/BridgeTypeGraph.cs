using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Runic.Application.Views;

// This is deliberately a build-time graph.  The generated bridge only calls
// the emitted direct member accesses and BridgeWire helpers; it never walks a
// model through reflection at runtime.
internal sealed class BridgeTypeGraph
{
    private readonly List<BridgeTypeNode> _nodes;

    private BridgeTypeGraph(BridgeTypeNode root, List<BridgeTypeNode> nodes)
    {
        Root = root;
        _nodes = nodes;
    }

    internal BridgeTypeNode Root { get; }
    internal IReadOnlyList<BridgeTypeNode> Nodes => _nodes;

    /// <param name="type">The declared contract type.</param>
    /// <param name="nullability">The declaration's nullable annotations.</param>
    /// <param name="rootPath">The member path named in diagnostics.</param>
    /// <param name="origin">The declaration a diagnostic points at, normally the ViewModel property.</param>
    /// <param name="honorJsonIgnore">Whether the model assembly opts into unconditional JSON ignores on DTOs.</param>
    /// <param name="direction">
    /// Where the value crosses: only a value the page sends (<see cref="BridgeValueDirection.FromPage"/>) needs a
    /// constructible reader.
    /// </param>
    internal static BridgeTypeGraph Discover(Type type, NullabilityInfo? nullability = null, string? rootPath = null,
        MemberInfo? origin = null, bool honorJsonIgnore = false, BridgeValueDirection direction = BridgeValueDirection.Both)
    {
        ArgumentNullException.ThrowIfNull(type);
        var builder = new Builder(honorJsonIgnore, direction);
        var root = builder.Build(type, TypeNullability.From(nullability), rootPath ?? type.Name, [], null, origin: origin);
        return new(root, builder.Nodes);
    }

    /// <summary>The root's declared C# type, including nullable annotations.</summary>
    internal string RootCSharpType() => CSharpNodeType(Root);

    internal void AppendFingerprint(List<string> parts, string scope)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var visited = new HashSet<BridgeTypeNode>(ReferenceEqualityComparer.Instance);
        AppendFingerprint(parts, scope, Root, visited);
    }

    /// <summary>
    /// Emits the body of one node's codec class: <c>Write</c>, <c>Read</c> and, for a collection, <c>WriteItem</c>. The
    /// class holds no state, and its methods call the codec class of each child node, which <paramref name="codec"/>
    /// names. <see cref="BridgeSharedCodecs"/> keeps one such class per distinct node in the assembly.
    /// </summary>
    internal static void AppendCSharpNodeCodec(StringBuilder source, BridgeTypeNode node, Func<BridgeTypeNode, string> codec)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(codec);
        AppendWriter(source, node, codec);
        source.AppendLine();
        AppendReader(source, node, codec);
        if (node.Element is { } element)
        {
            source.AppendLine();
            source.AppendLine($"    internal static void WriteItem(global::System.Text.Json.Utf8JsonWriter writer, object? value) => {codec(element)}.Write(writer, ({CSharpNodeType(element)})value!);");
        }
    }

    /// <summary>
    /// Emits the body of a member's own codec class when the member has field paths (field-paths/1): <c>Write</c>,
    /// <c>Read</c> and <c>WriteItem</c> forward to the shared codec of the root, which <paramref name="codec"/> names, and
    /// <c>SetPath{i}</c> copies the value with one field replaced. The setters belong to the member, so they are not
    /// shared.
    /// </summary>
    internal void AppendCSharpFieldPathCodec(StringBuilder source, BridgeTypeNode container, IReadOnlyList<BridgeFieldLeaf> leaves,
        Func<BridgeTypeNode, string> codec)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(codec);
        var root = codec(Root);
        source.AppendLine($"    internal static void Write(global::System.Text.Json.Utf8JsonWriter writer, {CSharpNodeType(Root)} value) => {root}.Write(writer, value);");
        source.AppendLine($"    internal static {CSharpNodeType(Root)} Read(global::System.Text.Json.JsonElement element) => {root}.Read(element);");
        if (Root.Element is not null)
            source.AppendLine($"    internal static void WriteItem(global::System.Text.Json.Utf8JsonWriter writer, object? value) => {root}.WriteItem(writer, value);");
        AppendFieldPathSetters(source, container, leaves, codec);
    }

    // field-paths/1: SetPath{i} copies the container (an object member's value or a row) with the value at leaf i
    // replaced by the decoded wire value, one object at a time from the leaf up: With{node}_{member} copies one object
    // with one member changed, as BridgeFieldLeaf.CopyOf decided. Members the page does not see are kept.
    private static void AppendFieldPathSetters(StringBuilder source, BridgeTypeNode container, IReadOnlyList<BridgeFieldLeaf> leaves,
        Func<BridgeTypeNode, string> codec)
    {
        var helpers = new HashSet<(int, int)>();
        var withs = new StringBuilder();
        for (var index = 0; index < leaves.Count; index++)
        {
            var chain = leaves[index].Chain;
            source.AppendLine($"    internal static object SetPath{index}(object container, global::System.Text.Json.JsonElement value)");
            source.AppendLine("    {");
            source.AppendLine($"        var c0 = ({NonNullCSharpType(container)})container;");
            var owners = new BridgeTypeNode[chain.Length];
            owners[0] = container;
            for (var level = 1; level < chain.Length; level++)
            {
                owners[level] = chain[level - 1].Type;
                var access = $"c{level - 1}.{chain[level - 1].Property.Name}";
                source.AppendLine(chain[level - 1].Type.IsNullable
                    ? $"        var c{level} = {access} ?? throw new global::System.InvalidOperationException(\"{chain[level - 1].WireName} is null.\");"
                    : $"        var c{level} = {access};");
            }
            var expression = $"{codec(chain[^1].Type)}.Read(value)";
            for (var level = chain.Length - 1; level >= 0; level--)
            {
                expression = $"With{owners[level].Id}_{chain[level].Index}(c{level}, {expression})";
                if (helpers.Add((owners[level].Id, chain[level].Index))) AppendWith(withs, owners[level], chain[level], codec);
            }
            source.AppendLine($"        return {expression};");
            source.AppendLine("    }");
        }
        source.Append(withs);
    }

    // A node's C# type with its own nullable annotations and those of its type arguments, as its codec declares it,
    // but not null itself: the copies of field paths run on objects that the path reached.
    private static string NonNullCSharpType(BridgeTypeNode node)
    {
        var type = CSharpNodeType(node);
        return node.IsNullable && type.EndsWith('?') ? type[..^1] : type;
    }

    private static void AppendWith(StringBuilder pending, BridgeTypeNode owner, BridgeTypeMember member, Func<BridgeTypeNode, string> codec)
    {
        var type = NonNullCSharpType(owner);
        var signature = $"    private static {type} With{owner.Id}_{member.Index}({type} c, {CSharpNodeType(member.Type)} v)";
        var property = member.Property.Name;
        var body = BridgeFieldLeaf.CopyOf(owner).Kind switch
        {
            BridgeFieldCopy.With => $" => c with {{ {property} = v }};",
            BridgeFieldCopy.Constructor => $" {{ var copy = new {type}(c); copy.{property} = v; return copy; }}",
            BridgeFieldCopy.Clone => $" {{ var copy = c.Clone(); copy.{property} = v; return copy; }}",
            // Every settable member of the class is on the wire: its codec copies it.
            BridgeFieldCopy.Wire => $" {{ using var copy = global::Runic.Application.Views.Generated.BridgeFieldPath.ReplaceMember(writer => {codec(owner)}.Write(writer, c), {JsonSerializer.Serialize(member.WireName)}, writer => {codec(member.Type)}.Write(writer, v)); return {codec(owner)}.Read(copy.RootElement){(owner.IsNullable && !owner.NonNullableType.IsValueType ? "!" : owner.IsNullable ? ".Value" : "")}; }}",
            _ => throw new InvalidOperationException($"{type} cannot be copied."),
        };
        pending.AppendLine(signature + body);
    }

    private static void AppendFingerprint(List<string> parts, string scope, BridgeTypeNode node,
        HashSet<BridgeTypeNode> visited)
    {
        if (!visited.Add(node)) return;
        parts.Add($"wire:{scope}:{node.Id}:{node.Kind}:{TypeIdentity(node.Type)}:nullable:{node.IsNullable}:name:{node.WireName ?? string.Empty}");
        foreach (var member in node.Members)
        {
            parts.Add($"wire-member:{scope}:{node.Id}:{member.WireName}:{TypeIdentity(member.Property.PropertyType)}:read:{member.Readable}:write:{member.Writable}");
            AppendFingerprint(parts, scope, member.Type, visited);
        }
        if (node.Element is not null) AppendFingerprint(parts, scope, node.Element, visited);
        if (node.Value is not null) AppendFingerprint(parts, scope, node.Value, visited);
        foreach (var @case in node.Cases)
        {
            parts.Add($"wire-case:{scope}:{node.Id}:{@case.Discriminator}:{TypeIdentity(@case.Type.Type)}");
            AppendFingerprint(parts, scope, @case.Type, visited);
        }
        foreach (var @case in node.EnumCases)
            parts.Add($"wire-enum:{scope}:{node.Id}:{@case.WireName}:{@case.Field.Name}");
    }

    private static void AppendWriter(StringBuilder source, BridgeTypeNode node, Func<BridgeTypeNode, string> codec)
    {
        source.AppendLine($"    internal static void Write(global::System.Text.Json.Utf8JsonWriter writer, {CSharpNodeType(node)} value)");
        source.AppendLine("    {");
        if (node.IsNullable)
        {
            source.AppendLine("        if (value is null) { writer.WriteNullValue(); return; }");
        }
        var value = node.IsNullable && node.NonNullableType.IsValueType ? "value.Value" : "value";
        switch (node.Kind)
        {
            case BridgeWireKind.Boolean: source.AppendLine($"        writer.WriteBooleanValue({value});"); break;
            case BridgeWireKind.Int8: case BridgeWireKind.UInt8: case BridgeWireKind.Int16: case BridgeWireKind.UInt16:
            case BridgeWireKind.Int32: case BridgeWireKind.UInt32: source.AppendLine($"        writer.WriteNumberValue({value});"); break;
            case BridgeWireKind.Single: case BridgeWireKind.Double: source.AppendLine($"        global::Runic.Application.Views.Generated.BridgeWire.WriteFiniteNumber(writer, {value});"); break;
            case BridgeWireKind.Int64: case BridgeWireKind.UInt64: case BridgeWireKind.BigInteger: source.AppendLine($"        global::Runic.Application.Views.Generated.BridgeWire.WriteIntegerString(writer, {value});"); break;
            case BridgeWireKind.Decimal: source.AppendLine($"        global::Runic.Application.Views.Generated.BridgeWire.WriteDecimal(writer, {value});"); break;
            case BridgeWireKind.String: source.AppendLine($"        writer.WriteStringValue({value});"); break;
            case BridgeWireKind.Guid: source.AppendLine($"        global::Runic.Application.Views.Generated.BridgeWire.WriteGuid(writer, {value});"); break;
            case BridgeWireKind.DateOnly: source.AppendLine($"        global::Runic.Application.Views.Generated.BridgeWire.WriteDateOnly(writer, {value});"); break;
            case BridgeWireKind.TimeOnly: source.AppendLine($"        global::Runic.Application.Views.Generated.BridgeWire.WriteTimeOnly(writer, {value});"); break;
            case BridgeWireKind.DateTime: source.AppendLine($"        global::Runic.Application.Views.Generated.BridgeWire.WriteDateTime(writer, {value});"); break;
            case BridgeWireKind.DateTimeOffset: source.AppendLine($"        global::Runic.Application.Views.Generated.BridgeWire.WriteDateTimeOffset(writer, {value});"); break;
            case BridgeWireKind.TimeSpan: source.AppendLine($"        global::Runic.Application.Views.Generated.BridgeWire.WriteTimeSpan(writer, {value});"); break;
            case BridgeWireKind.Enum:
                source.AppendLine($"        switch ({value})");
                source.AppendLine("        {");
                // Aliases such as None = 0, Default = 0 share one value. The
                // first declared name is written; every name remains readable.
                var writtenValues = new HashSet<object>();
                foreach (var @case in node.EnumCases.Where(@case => writtenValues.Add(@case.Field.GetRawConstantValue()!)))
                    source.AppendLine($"            case {CSharpType(node.NonNullableType)}.{@case.Field.Name}: writer.WriteStringValue({Quote(@case.WireName)}); break;");
                source.AppendLine("            default: throw new global::System.ArgumentOutOfRangeException(nameof(value), \"A bridge enum value must be declared.\");");
                source.AppendLine("        }");
                break;
            case BridgeWireKind.Array:
            case BridgeWireKind.List:
                source.AppendLine("        writer.WriteStartArray();");
                source.AppendLine("        foreach (var item in " + value + ") " + codec(node.Element!) + ".Write(writer, item);");
                source.AppendLine("        writer.WriteEndArray();");
                break;
            case BridgeWireKind.StringDictionary:
                source.AppendLine("        writer.WriteStartObject();");
                source.AppendLine("        foreach (var entry in global::System.Linq.Enumerable.OrderBy(" + value + ", entry => entry.Key, global::System.StringComparer.Ordinal))");
                source.AppendLine("        { writer.WritePropertyName(entry.Key); " + codec(node.Value!) + ".Write(writer, entry.Value); }");
                source.AppendLine("        writer.WriteEndObject();");
                break;
            case BridgeWireKind.Dto:
                source.AppendLine("        writer.WriteStartObject();");
                foreach (var member in node.Members)
                    AppendMemberWriter(source, member, $"{value}.{member.Property.Name}", "        ", codec);
                source.AppendLine("        writer.WriteEndObject();");
                break;
            case BridgeWireKind.Union:
                source.AppendLine("        writer.WriteStartObject();");
                source.AppendLine("        switch (" + value + ")");
                source.AppendLine("        {");
                foreach (var @case in node.Cases)
                {
                    source.AppendLine($"            case {CSharpType(@case.Type.Type)} item{@case.Type.Id}:");
                    source.AppendLine($"                writer.WriteString(\"$case\", {Quote(@case.Discriminator)});");
                    foreach (var member in @case.Type.Members)
                        AppendMemberWriter(source, member, $"item{@case.Type.Id}.{member.Property.Name}", "                ", codec);
                    source.AppendLine("                break;");
                }
                source.AppendLine("            default: throw new global::System.NotSupportedException(\"The value is not a declared bridge-union case.\");");
                source.AppendLine("        }");
                source.AppendLine("        writer.WriteEndObject();");
                break;
            case BridgeWireKind.Custom:
                source.AppendLine($"        {CSharpType(node.CustomCodec!.CodecType)}.Write(writer, {value});");
                break;
            // The application references Runic.Translations; the Views packages do not (BridgeTextReferences).
            case BridgeWireKind.TextReference when BridgeTextReferences.IsTranslationReference(node.NonNullableType):
                source.AppendLine($"        global::System.Text.Json.JsonSerializer.Serialize(writer, {value}, global::Runic.Translations.TranslationReferenceJsonContext.Default.TranslationReference);");
                break;
            case BridgeWireKind.TextReference:
                source.AppendLine($"        {value}.WriteTo(writer);");
                break;
            case BridgeWireKind.Blob:
                source.AppendLine($"        global::Runic.Application.Views.Generated.BridgeWire.WriteBlob(writer, {value});");
                break;
            case BridgeWireKind.Upload:
                source.AppendLine($"        global::Runic.Application.Views.Generated.BridgeWire.WriteUpload(writer, {value});");
                break;
            // A default JsonElement holds no value; it crosses as null.
            case BridgeWireKind.Json when node.NonNullableType == typeof(JsonElement):
                source.AppendLine($"        if ({value}.ValueKind is global::System.Text.Json.JsonValueKind.Undefined) writer.WriteNullValue(); else {value}.WriteTo(writer);");
                break;
            case BridgeWireKind.Json:
                source.AppendLine($"        if ({value} is null) writer.WriteNullValue(); else {value}.WriteTo(writer);");
                break;
            default:
                source.AppendLine("        throw new global::System.NotSupportedException(\"The generated codec needs a closed-union writer.\");");
                break;
        }
        source.AppendLine("    }");
    }

    // A JsonElement member without a value is absent from the object.
    private static void AppendMemberWriter(StringBuilder source, BridgeTypeMember member, string access, string indent,
        Func<BridgeTypeNode, string> codec)
    {
        var write = $"writer.WritePropertyName({Quote(member.WireName)}); {codec(member.Type)}.Write(writer, {access});";
        source.AppendLine(member.Absent
            ? $"{indent}if ({access}.ValueKind is not global::System.Text.Json.JsonValueKind.Undefined) {{ {write} }}"
            : $"{indent}{write}");
    }

    private static void AppendReader(StringBuilder source, BridgeTypeNode node, Func<BridgeTypeNode, string> codec)
    {
        source.AppendLine($"    internal static {CSharpNodeType(node)} Read(global::System.Text.Json.JsonElement element)");
        source.AppendLine("    {");
        if (node.IsNullable)
        {
            source.AppendLine("        if (element.ValueKind is global::System.Text.Json.JsonValueKind.Null) return default!;");
        }
        switch (node.Kind)
        {
            case BridgeWireKind.Boolean: source.AppendLine("        return element.ValueKind is global::System.Text.Json.JsonValueKind.True ? true : element.ValueKind is global::System.Text.Json.JsonValueKind.False ? false : throw global::Runic.Application.Views.Generated.BridgeWire.Invalid(\"Expected a boolean.\");"); break;
            case BridgeWireKind.Int8: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadInt8(element);"); break;
            case BridgeWireKind.UInt8: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadUInt8(element);"); break;
            case BridgeWireKind.Int16: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadInt16(element);"); break;
            case BridgeWireKind.UInt16: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadUInt16(element);"); break;
            case BridgeWireKind.Int32: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadInt32(element);"); break;
            case BridgeWireKind.UInt32: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadUInt32(element);"); break;
            case BridgeWireKind.Single: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadSingle(element);"); break;
            case BridgeWireKind.Double: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadDouble(element);"); break;
            case BridgeWireKind.Int64: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadInt64String(element);"); break;
            case BridgeWireKind.UInt64: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadUInt64String(element);"); break;
            case BridgeWireKind.BigInteger: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadBigInteger(element);"); break;
            case BridgeWireKind.Decimal: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadDecimal(element);"); break;
            case BridgeWireKind.String: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadString(element);"); break;
            case BridgeWireKind.Guid: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadGuid(element);"); break;
            case BridgeWireKind.DateOnly: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadDateOnly(element);"); break;
            case BridgeWireKind.TimeOnly: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadTimeOnly(element);"); break;
            case BridgeWireKind.DateTime: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadDateTime(element);"); break;
            case BridgeWireKind.DateTimeOffset: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadDateTimeOffset(element);"); break;
            case BridgeWireKind.TimeSpan: source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadTimeSpan(element);"); break;
            case BridgeWireKind.Enum:
                source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadString(element) switch");
                source.AppendLine("        {");
                foreach (var @case in node.EnumCases)
                    source.AppendLine($"            {Quote(@case.WireName)} => {CSharpType(node.NonNullableType)}.{@case.Field.Name},");
                source.AppendLine("            _ => throw global::Runic.Application.Views.Generated.BridgeWire.Invalid(\"Unknown bridge enum name.\"),");
                source.AppendLine("        };");
                break;
            case BridgeWireKind.Array:
            case BridgeWireKind.List:
                AppendArrayReader(source, node, codec); break;
            case BridgeWireKind.StringDictionary:
                AppendDictionaryReader(source, node, codec); break;
            case BridgeWireKind.Dto:
                AppendDtoReader(source, node, codec); break;
            case BridgeWireKind.Union:
                source.AppendLine("        var discriminator = global::Runic.Application.Views.Generated.BridgeWire.ReadString(global::Runic.Application.Views.Generated.BridgeWire.RequiredProperty(element, \"$case\"));");
                source.AppendLine("        return discriminator switch");
                source.AppendLine("        {");
                foreach (var @case in node.Cases)
                    source.AppendLine($"            {Quote(@case.Discriminator)} => {codec(@case.Type)}.Read(element),");
                source.AppendLine("            _ => throw global::Runic.Application.Views.Generated.BridgeWire.Invalid(\"Unknown bridge-union case.\"),");
                source.AppendLine("        };");
                break;
            case BridgeWireKind.Custom:
                source.AppendLine($"        return {CSharpType(node.CustomCodec!.CodecType)}.Read(element);"); break;
            // TranslationReference.Parse applies the Translations transport's checks and limits; it throws JsonException.
            case BridgeWireKind.TextReference when BridgeTextReferences.IsTranslationReference(node.NonNullableType):
                source.AppendLine("        return global::Runic.Translations.TranslationReference.Parse(element.GetRawText());"); break;
            case BridgeWireKind.TextReference:
                source.AppendLine("        return global::Runic.Application.Views.RunicTextReference.Read(element);"); break;
            // A settable property that holds a blob: the page cannot set it (blobs/1).
            case BridgeWireKind.Blob:
                source.AppendLine("        throw global::Runic.Application.Views.Generated.BridgeWire.Invalid(\"A blob is never read from the page; send an upload instead.\");"); break;
            case BridgeWireKind.Upload:
                source.AppendLine("        return global::Runic.Application.Views.Generated.BridgeWire.ReadUpload(element);"); break;
            // The element belongs to a document that is disposed after the call.
            case BridgeWireKind.Json when node.NonNullableType == typeof(JsonElement):
                source.AppendLine("        return element.Clone();"); break;
            case BridgeWireKind.Json:
                if (!node.IsNullable)
                    source.AppendLine("        if (element.ValueKind is global::System.Text.Json.JsonValueKind.Null) throw global::Runic.Application.Views.Generated.BridgeWire.Invalid(\"Expected a JSON value other than null.\");");
                source.AppendLine("        return global::System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText())!;"); break;
            default:
                source.AppendLine("        throw new global::System.NotSupportedException(\"The generated codec needs a closed-union reader.\");"); break;
        }
        source.AppendLine("    }");
    }

    // Builds the declared collection type itself, so a codec's signature and
    // its reader agree for every collection that Builder accepts.
    private static void AppendArrayReader(StringBuilder source, BridgeTypeNode node, Func<BridgeTypeNode, string> codec)
    {
        var element = node.Element ?? throw new InvalidOperationException("A collection needs an element type.");
        var elementType = CSharpNodeType(element);
        source.AppendLine("        if (element.ValueKind is not global::System.Text.Json.JsonValueKind.Array) throw global::Runic.Application.Views.Generated.BridgeWire.Invalid(\"Expected an array.\");");
        source.AppendLine($"        var items = new global::System.Collections.Generic.List<{elementType}>(element.GetArrayLength());");
        source.AppendLine($"        foreach (var item in element.EnumerateArray()) items.Add({codec(element)}.Read(item));");
        var generic = node.NonNullableType.GetGenericTypeDefinitionOrNull();
        var result = node.Kind is BridgeWireKind.Array ? "items.ToArray()"
            : generic == typeof(ImmutableArray<>) ? "global::System.Collections.Immutable.ImmutableArray.CreateRange(items)"
            : generic == typeof(ImmutableList<>) ? "global::System.Collections.Immutable.ImmutableList.CreateRange(items)"
            : generic == typeof(System.Collections.ObjectModel.ObservableCollection<>)
                ? $"new global::System.Collections.ObjectModel.ObservableCollection<{elementType}>(items)"
            : generic == typeof(System.Collections.ObjectModel.ReadOnlyObservableCollection<>)
                ? $"new global::System.Collections.ObjectModel.ReadOnlyObservableCollection<{elementType}>(new global::System.Collections.ObjectModel.ObservableCollection<{elementType}>(items))"
            : generic == typeof(System.Collections.ObjectModel.ReadOnlyCollection<>)
                ? $"new global::System.Collections.ObjectModel.ReadOnlyCollection<{elementType}>(items)"
            // Mutable list contracts get a mutable list; read-only contracts
            // and IEnumerable<T> get an array.
            : generic == typeof(List<>) || generic == typeof(IList<>) || generic == typeof(ICollection<>) ? "items"
            : "items.ToArray()";
        source.AppendLine($"        return {result};");
    }

    private static void AppendDictionaryReader(StringBuilder source, BridgeTypeNode node, Func<BridgeTypeNode, string> codec)
    {
        var value = node.Value ?? throw new InvalidOperationException("A dictionary node needs a value type.");
        source.AppendLine("        if (element.ValueKind is not global::System.Text.Json.JsonValueKind.Object) throw global::Runic.Application.Views.Generated.BridgeWire.Invalid(\"Expected an object.\");");
        source.AppendLine($"        var result = new global::System.Collections.Generic.Dictionary<string, {CSharpNodeType(value)}>(global::System.StringComparer.Ordinal);");
        source.AppendLine($"        foreach (var property in element.EnumerateObject()) result.Add(property.Name, {codec(value)}.Read(property.Value));");
        source.AppendLine("        return result;");
    }

    private static void AppendDtoReader(StringBuilder source, BridgeTypeNode node, Func<BridgeTypeNode, string> codec)
    {
        var type = CSharpType(node.NonNullableType, node.Nullability);
        if (!node.Direction.HasFlag(BridgeValueDirection.FromPage))
        {
            // Only written to the page, so it needs no constructor (#312).
            source.AppendLine($"        throw new global::System.NotSupportedException({Quote($"{TypeIdentity(node.NonNullableType)} is only written to the page.")});");
            return;
        }
        source.AppendLine("        if (element.ValueKind is not global::System.Text.Json.JsonValueKind.Object) throw global::Runic.Application.Views.Generated.BridgeWire.Invalid(\"Expected an object.\");");
        foreach (var member in node.Members)
            source.AppendLine(member.Optional
                ? $"        var has_{member.Index} = element.TryGetProperty({Quote(member.WireName)}, out var {Local(member)});"
                : $"        var {Local(member)} = global::Runic.Application.Views.Generated.BridgeWire.RequiredProperty(element, {Quote(member.WireName)});");
        string Read(BridgeTypeMember member, string missing) => member.Optional
            ? $"has_{member.Index} ? {codec(member.Type)}.Read({Local(member)}) : {missing}"
            : $"{codec(member.Type)}.Read({Local(member)})";
        if (node.Constructor is { } constructor)
        {
            var arguments = constructor.GetParameters().Select(parameter =>
            {
                var member = node.Members.Single(candidate => string.Equals(candidate.Property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
                return Read(member, member.DefaultExpression ?? "default!");
            });
            source.AppendLine($"        return new {type}({string.Join(", ", arguments)});");
        }
        else
        {
            // A missing member keeps the value a new instance has, such as its initializer.
            if (node.Members.Any(member => member.Optional))
                source.AppendLine(node.NonNullableType.IsValueType
                    ? $"        var template = new {type}();"
                    : $"        {type}? template = null;");
            var template = node.NonNullableType.IsValueType ? "template" : $"(template ??= new {type}())";
            source.AppendLine($"        return new {type} {{");
            foreach (var member in node.Members)
                source.AppendLine($"            {member.Property.Name} = {Read(member, $"{template}.{member.Property.Name}")},");
            source.AppendLine("        };");
        }
    }

    // The C# value of a constructor parameter's default, as the compiler records it.
    private static string DefaultLiteral(ParameterInfo parameter, string path)
    {
        var value = parameter.DefaultValue;
        var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        if (value is null || value == DBNull.Value || value == Type.Missing) return "default!";
        var invariant = CultureInfo.InvariantCulture;
        if (type.IsEnum)
            return $"({CSharpType(type)})({Convert.ToString(Convert.ChangeType(value, Enum.GetUnderlyingType(type), invariant), invariant)})";
        return value switch
        {
            bool flag => flag ? "true" : "false",
            string text => Quote(text),
            char character => $"(char){(int)character}",
            int number => number.ToString(invariant),
            long number => number.ToString(invariant) + "L",
            uint number => number.ToString(invariant) + "U",
            ulong number => number.ToString(invariant) + "UL",
            short or ushort or byte or sbyte => $"({CSharpType(type)}){Convert.ToString(value, invariant)}",
            double number => double.IsNaN(number) ? "double.NaN" : double.IsPositiveInfinity(number) ? "double.PositiveInfinity"
                : double.IsNegativeInfinity(number) ? "double.NegativeInfinity" : number.ToString("R", invariant) + "D",
            float number => float.IsNaN(number) ? "float.NaN" : float.IsPositiveInfinity(number) ? "float.PositiveInfinity"
                : float.IsNegativeInfinity(number) ? "float.NegativeInfinity" : number.ToString("R", invariant) + "F",
            decimal number => number.ToString(invariant) + "M",
            _ => throw new BridgeTypeGraphException(path, $"The default value of the constructor parameter {parameter.Name} cannot be written in generated code; remove the default or use a property with a setter."),
        };
    }

    private static string Local(BridgeTypeMember member) => "value_" + member.Index.ToString(CultureInfo.InvariantCulture);
    private static string Quote(string text) => JsonSerializer.Serialize(text);
    private static string TypeIdentity(Type type) => type.FullName ?? type.Name;

    internal static string CSharpType(Type type) => CSharpType(type, null);

    // With use-site annotations, nested type arguments keep them, e.g.
    // Box<string?>, so generated code has no nullable warnings.
    private static string CSharpType(Type type, TypeNullability? nullability)
    {
        if (type.IsArray) return CSharpArgument(type.GetElementType()!, nullability?.Element) + "[]";
        if (!type.IsGenericType) return "global::" + (type.FullName ?? type.Name).Replace('+', '.');
        var name = (type.GetGenericTypeDefinition().FullName ?? type.Name);
        name = name[..name.IndexOf('`')].Replace('+', '.');
        return "global::" + name + "<" + string.Join(", ", type.GetGenericArguments()
            .Select((argument, index) => CSharpArgument(argument, nullability?.Argument(index)))) + ">";
    }

    private static string CSharpArgument(Type type, TypeNullability? nullability) =>
        CSharpType(type, nullability) + (nullability?.IsNullable == true && !type.IsValueType ? "?" : "");

    // The declared C# type with its nullable annotations, e.g.
    // ImmutableArray<string?>? or Point?. Codecs use it for signatures.
    internal static string CSharpNodeType(BridgeTypeNode node)
    {
        var type = node.Kind switch
        {
            BridgeWireKind.Array => CSharpNodeType(node.Element!) + "[]",
            BridgeWireKind.List => GenericName(node.NonNullableType) + "<" + CSharpNodeType(node.Element!) + ">",
            BridgeWireKind.StringDictionary => GenericName(node.NonNullableType) + "<string, " + CSharpNodeType(node.Value!) + ">",
            _ => CSharpType(node.NonNullableType, node.Nullability),
        };
        return node.IsNullable ? type + "?" : type;
    }

    private static string GenericName(Type type)
    {
        var generic = type.GetGenericTypeDefinition().FullName
            ?? throw new InvalidOperationException("A collection needs a generic type name.");
        return "global::" + generic[..generic.IndexOf('`')].Replace('+', '.');
    }

    private sealed class Builder(bool honorJsonIgnore, BridgeValueDirection direction)
    {
        private readonly NullabilityInfoContext _nullability = new();
        private int _nextId;
        internal List<BridgeTypeNode> Nodes { get; } = [];

        // A failure is reported at the innermost declaration that led to it:
        // the DTO property, union case or ViewModel member.
        internal BridgeTypeNode Build(Type declared, TypeNullability? nullability, string path, HashSet<Type> stack,
            RunicCodecAttribute? memberCodec, bool unionCase = false, MemberInfo? origin = null)
        {
            try
            {
                return BuildNode(declared, nullability, path, stack, memberCodec, unionCase);
            }
            catch (BridgeTypeGraphException error) when (error.Member is null && origin is not null)
            {
                throw new BridgeTypeGraphException(error.Path, error.Detail, origin, error.Code);
            }
        }

        private BridgeTypeNode BuildNode(Type declared, TypeNullability? nullability, string path, HashSet<Type> stack,
            RunicCodecAttribute? memberCodec, bool unionCase)
        {
            // Without annotations (a type from typeof, a union case, or a
            // contract argument the declaration does not close) every
            // position is non-nullable, so Box<string> means the same type
            // wherever it is used.
            var annotations = nullability ?? TypeNullability.NotNull(declared);
            var nullable = !declared.IsValueType
                ? annotations.IsNullable
                : Nullable.GetUnderlyingType(declared) is not null;
            var type = Nullable.GetUnderlyingType(declared) ?? declared;
            if (stack.Contains(type)) throw new BridgeTypeGraphException(path, "Recursive DTO graphs are not a bridge contract.");
            var node = new BridgeTypeNode(_nextId++, type, nullable) { Nullability = annotations, Direction = direction };
            Nodes.Add(node);
            if (TryScalar(type, out var scalar)) { node.Kind = scalar; return node; }
            // blobs/1: a blob goes to the page only; the page sends files as uploads.
            if (type == typeof(Runic.Application.Views.RunicBlob))
            {
                if (direction == BridgeValueDirection.FromPage)
                    throw new BridgeTypeGraphException(path, "A RunicBlob only goes to the page: it cannot be a command input or an interaction result. Take a RunicUpload, which the page creates with client.upload(file).",
                        code: BridgeDiagnosticCodes.Blob);
                node.Kind = BridgeWireKind.Blob;
                return node;
            }
            if (type == typeof(Runic.Application.Views.RunicUpload)) { node.Kind = BridgeWireKind.Upload; return node; }
            if (type.IsEnum)
            {
                if (type.IsDefined(typeof(FlagsAttribute), inherit: false))
                    throw new BridgeTypeGraphException(path, "Flags enums need an explicit combination codec.");
                node.Kind = BridgeWireKind.Enum;
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static).OrderBy(field => field.MetadataToken))
                {
                    var name = field.GetCustomAttribute<RunicAliasAttribute>(inherit: false)?.Name ?? field.Name;
                    if (!names.Add(name)) throw new BridgeTypeGraphException(path, $"Enum wire name {name} is declared more than once.");
                    node.EnumCases.Add(new(field, name));
                }
                return node;
            }
            var union = type.GetCustomAttribute<RunicUnionAttribute>(inherit: false);
            if (union is not null)
            {
                if (memberCodec is not null || type.GetCustomAttribute<RunicCodecAttribute>(inherit: true) is not null)
                    throw new BridgeTypeGraphException(path, "A closed union cannot also use an opaque custom codec.");
                BuildUnion(node, union, path, stack);
                return node;
            }
            var custom = memberCodec ?? type.GetCustomAttribute<RunicCodecAttribute>(inherit: true);
            if (custom is not null)
            {
                node.Kind = BridgeWireKind.Custom;
                node.CustomCodec = CustomCodecDescription.Create(type, custom.CodecType, path);
                return node;
            }
            if (BridgeTextReferences.Is(type))
            {
                node.Kind = BridgeWireKind.TextReference;
                return node;
            }
            if (type == typeof(JsonElement) || type == typeof(System.Text.Json.Nodes.JsonNode))
            {
                node.Kind = BridgeWireKind.Json;
                return node;
            }
            // JsonObject and JsonArray would otherwise be rejected as collections.
            if (typeof(System.Text.Json.Nodes.JsonNode).IsAssignableFrom(type) || type == typeof(JsonDocument))
                throw new BridgeTypeGraphException(path, $"{TypeIdentity(type)} is not a bridge value. Declare JsonNode or JsonElement for any JSON value, or a DTO for a known shape.");
            if (TryCollection(type, path, out var collection, out var element))
            {
                node.Kind = collection;
                stack.Add(type);
                // Array element annotations live in ElementType, not in
                // GenericTypeArguments: string?[] must keep its nullable items.
                node.Element = Build(element!, type.IsArray ? annotations.Element : annotations.Argument(0),
                    path + "[]", stack, null);
                stack.Remove(type);
                return node;
            }
            if (TryStringDictionary(type, out var value))
            {
                node.Kind = BridgeWireKind.StringDictionary;
                stack.Add(type);
                node.Value = Build(value!, annotations.Argument(1), path + "{}", stack, null);
                stack.Remove(type);
                return node;
            }
            // A concrete collection outside the supported list would
            // otherwise be treated as a DTO and silently serialize as {}.
            if (UnsupportedCollection(type) is { } collectionError) throw new BridgeTypeGraphException(path, collectionError);
            if (!IsDto(type)) throw new BridgeTypeGraphException(path, $"{TypeIdentity(type)} is not an explicitly supported bridge value. Use a supported scalar, collection, public DTO, RunicUnion, or RunicCodec.{NavigationResultHint(type)}");
            node.Kind = BridgeWireKind.Dto;
            stack.Add(type);
            var members = DataProperties(type, path).ToArray();
            // An empty union case is a valid tag-only case. Any other DTO
            // without readable properties would always cross the bridge as {}.
            if (members.Length == 0 && !unionCase)
                throw new BridgeTypeGraphException(path, $"{TypeIdentity(type)} has no public readable properties, so it would always serialize as {{}}. Add properties or a RunicCodec.");
            foreach (var property in members)
            {
                var info = MemberNullability(type, property, annotations);
                node.Members.Add(new BridgeTypeMember(node.Members.Count, property, WireName(property),
                    Build(property.PropertyType, info, path + "." + WireName(property), stack,
                        property.GetCustomAttribute<RunicCodecAttribute>(inherit: true), origin: property)));
            }
            if (direction.HasFlag(BridgeValueDirection.FromPage)) node.Constructor = SelectConstructor(type, node.Members, path);
            MarkOptionalMembers(node, path);
            stack.Remove(type);
            return node;
        }

        private void BuildUnion(BridgeTypeNode node, RunicUnionAttribute union, string path, HashSet<Type> stack)
        {
            if (!node.NonNullableType.IsAbstract && !node.NonNullableType.IsInterface)
                throw new BridgeTypeGraphException(path, "A bridge union root must be an abstract class or interface.");
            node.Kind = BridgeWireKind.Union;
            var seenTypes = new HashSet<Type>();
            var seenTags = new HashSet<string>(StringComparer.Ordinal);
            stack.Add(node.NonNullableType);
            foreach (var type in union.Cases.OrderBy(candidate => candidate.FullName, StringComparer.Ordinal))
            {
                if (!seenTypes.Add(type) || !type.IsPublic || type.IsNested || type.IsAbstract || type.ContainsGenericParameters || !node.NonNullableType.IsAssignableFrom(type))
                    throw new BridgeTypeGraphException(path, $"{TypeIdentity(type)} is not a distinct public concrete case of {TypeIdentity(node.NonNullableType)}.");
                if (type.GetCustomAttribute<RunicCodecAttribute>(inherit: true) is not null)
                    throw new BridgeTypeGraphException(path, $"{TypeIdentity(type)} cannot be both a union case and an opaque custom codec.");
                var tag = type.GetCustomAttribute<RunicUnionCaseAttribute>(inherit: false)?.Name;
                if (string.IsNullOrWhiteSpace(tag) || tag.Length > 128 || tag.Any(char.IsControl) || !seenTags.Add(tag))
                    throw new BridgeTypeGraphException(path, $"{TypeIdentity(type)} needs a unique RunicUnionCase discriminator of at most 128 printable characters.");
                // A case can contain a member whose declared type references the union root; this is a real cycle.
                var caseNode = Build(type, null, path + "." + tag, stack, null, unionCase: true, origin: type);
                if (caseNode.Members.Any(member => member.WireName == "$case"))
                    throw new BridgeTypeGraphException(path + "." + tag, "A union case cannot export the reserved $case field.");
                node.Cases.Add(new(tag, caseNode));
            }
            stack.Remove(node.NonNullableType);
        }

        private static bool TryScalar(Type type, out BridgeWireKind kind)
        {
            kind = type == typeof(bool) ? BridgeWireKind.Boolean
                : type == typeof(sbyte) ? BridgeWireKind.Int8
                : type == typeof(byte) ? BridgeWireKind.UInt8
                : type == typeof(short) ? BridgeWireKind.Int16
                : type == typeof(ushort) ? BridgeWireKind.UInt16
                : type == typeof(int) ? BridgeWireKind.Int32
                : type == typeof(uint) ? BridgeWireKind.UInt32
                : type == typeof(long) ? BridgeWireKind.Int64
                : type == typeof(ulong) ? BridgeWireKind.UInt64
                : type == typeof(float) ? BridgeWireKind.Single
                : type == typeof(double) ? BridgeWireKind.Double
                : type == typeof(decimal) ? BridgeWireKind.Decimal
                : type == typeof(string) ? BridgeWireKind.String
                : type == typeof(Guid) ? BridgeWireKind.Guid
                : type == typeof(DateOnly) ? BridgeWireKind.DateOnly
                : type == typeof(TimeOnly) ? BridgeWireKind.TimeOnly
                : type == typeof(DateTime) ? BridgeWireKind.DateTime
                : type == typeof(DateTimeOffset) ? BridgeWireKind.DateTimeOffset
                : type == typeof(TimeSpan) ? BridgeWireKind.TimeSpan
                : type == typeof(System.Numerics.BigInteger) ? BridgeWireKind.BigInteger
                : BridgeWireKind.None;
            return kind is not BridgeWireKind.None;
        }

        // A member of a closed generic DTO takes the nullability of its type
        // parameters from the use site: Box<string>.Value is a string and
        // Box<string?>.Value a string or null. Reflection cannot tell them
        // apart, because both uses share one runtime type. A member declared
        // on a generic base type follows the base type arguments through
        // each derived declaration (Derived<T> : Base<T>).
        private TypeNullability? MemberNullability(Type owner, PropertyInfo property, TypeNullability ownerNullability)
        {
            var declaring = property.DeclaringType!;
            if (!declaring.IsConstructedGenericType || DeclaringNullability(owner, ownerNullability, declaring) is not { } declaringNullability)
                return TypeNullability.From(_nullability.Create(property));
            var definition = declaring.GetGenericTypeDefinition()
                .GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public)
                .Single(candidate => candidate.HasSameMetadataDefinitionAs(property));
            if (!definition.PropertyType.ContainsGenericParameters)
                return TypeNullability.From(_nullability.Create(property));
            var index = 0;
            return Substitute(definition.PropertyType, _nullability.Create(definition), NullableAnnotations.Of(definition),
                ref index, declaringNullability);
        }

        // The annotations of a base type of the owner. The compiler records a
        // base type's annotations on the deriving type declaration.
        private static TypeNullability? DeclaringNullability(Type owner, TypeNullability nullability, Type declaring)
        {
            for (var current = owner; current != declaring; current = current.BaseType)
            {
                if (current?.BaseType is null) return null;
                var definition = current.IsConstructedGenericType ? current.GetGenericTypeDefinition() : current;
                var index = 0;
                if (Substitute(definition.BaseType!, null, NullableAnnotations.Of(definition), ref index, nullability) is not { } next)
                    return null;
                nullability = next;
            }
            return nullability;
        }

        // Walks the open type in the order of the compiler's annotation bytes,
        // as NullabilityInfoContext reads them. A type parameter is replaced
        // by the use site's annotations for its argument, made nullable when
        // declared `T?`. Other positions take the reflected nullability, or
        // the annotation byte where there is none (a base type).
        private static TypeNullability? Substitute(Type type, NullabilityInfo? info, Func<int, byte> annotations, ref int index,
            TypeNullability owner)
        {
            if (type.IsGenericParameter)
            {
                if (type.IsValueType) return TypeNullability.From(info);
                var annotated = annotations(index++) == NullableAnnotations.Annotated;
                return owner.Argument(type.GenericParameterPosition) is { } argument
                    ? annotated ? argument with { IsNullable = true } : argument
                    : TypeNullability.From(info);
            }
            TypeNullability? element = null;
            var nullable = false;
            if (type.IsValueType)
            {
                type = Nullable.GetUnderlyingType(type) ?? type;
                if (type.IsGenericType) index++;
            }
            else
            {
                nullable = info is null ? annotations(index) == NullableAnnotations.Annotated
                    : info.ReadState == NullabilityState.Nullable;
                index++;
                if (type.IsArray) element = Substitute(type.GetElementType()!, info?.ElementType, annotations, ref index, owner);
            }
            var arguments = new List<TypeNullability?>();
            if (type.IsGenericType)
            {
                var generic = type.GetGenericArguments();
                for (var position = 0; position < generic.Length; position++)
                    arguments.Add(Substitute(generic[position], info?.GenericTypeArguments.ElementAtOrDefault(position),
                        annotations, ref index, owner));
            }
            return new TypeNullability(nullable, element, arguments);
        }

        // Every collection the generated reader can construct. Derived or
        // custom collection classes are rejected rather than read as a List<T>.
        private static readonly HashSet<Type> SupportedCollections =
        [
            typeof(List<>), typeof(IReadOnlyList<>), typeof(IReadOnlyCollection<>), typeof(IList<>), typeof(ICollection<>),
            typeof(IEnumerable<>), typeof(ImmutableArray<>), typeof(ImmutableList<>),
            typeof(System.Collections.ObjectModel.ObservableCollection<>),
            typeof(System.Collections.ObjectModel.ReadOnlyObservableCollection<>),
            typeof(System.Collections.ObjectModel.ReadOnlyCollection<>),
        ];

        private static bool TryCollection(Type type, string path, out BridgeWireKind kind, out Type? element)
        {
            if (type.IsArray)
            {
                if (type.GetArrayRank() != 1)
                    throw new BridgeTypeGraphException(path, "Multi-dimensional arrays are not a bridge contract; use a jagged array or a list.");
                kind = BridgeWireKind.Array; element = type.GetElementType(); return true;
            }
            if (type.GetGenericTypeDefinitionOrNull() is { } generic && SupportedCollections.Contains(generic))
            {
                kind = BridgeWireKind.List; element = type.GenericTypeArguments[0]; return true;
            }
            kind = BridgeWireKind.None; element = null;
            return false;
        }

        private static string? UnsupportedCollection(Type type)
        {
            if (type == typeof(string)) return null;
            if (type.GetInterfaces().Append(type).Any(candidate => candidate.IsGenericType
                    && (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) || candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))))
                return $"{TypeIdentity(type)} is not a supported bridge dictionary. Use Dictionary<string, T>, IDictionary<string, T>, or IReadOnlyDictionary<string, T>.";
            return typeof(IEnumerable).IsAssignableFrom(type)
                ? $"{TypeIdentity(type)} is not a supported bridge collection. Use T[], List<T>, IReadOnlyList<T>, IReadOnlyCollection<T>, IList<T>, ICollection<T>, IEnumerable<T>, ImmutableArray<T>, ImmutableList<T>, ObservableCollection<T>, ReadOnlyObservableCollection<T>, or ReadOnlyCollection<T>."
                : null;
        }

        private static bool TryStringDictionary(Type type, out Type? value)
        {
            var dictionary = type.GetInterfaces().Append(type).FirstOrDefault(candidate => candidate.IsGenericType &&
                (candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) || candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>)) &&
                candidate.GenericTypeArguments[0] == typeof(string));
            value = dictionary?.GenericTypeArguments[1];
            return value is not null && (type.IsInterface || type.GetGenericTypeDefinitionOrNull() == typeof(Dictionary<,>));
        }

        // Framework types (object, StringBuilder, Uri, tuples, ...) have no
        // application-owned shape; DataProperties also stops at System types.
        private static bool IsDto(Type type) => type.IsPublic && (type.IsClass || type.IsValueType) && !type.IsAbstract
            && !typeof(Delegate).IsAssignableFrom(type)
            && type.Namespace != "System" && type.Namespace?.StartsWith("System.", StringComparison.Ordinal) != true;

        // A navigation result holds .NET content, so it never crosses the Bridge. The usual source is a
        // back command exposed to the View; name the bridgeable summary.
        private static string NavigationResultHint(Type type)
        {
            for (var current = type; current is not null; current = current.BaseType)
                if (current.IsGenericType && current.GetGenericTypeDefinition().FullName == "Runic.Navigation.NavigationResult`1")
                    return " A NavigationResult holds .NET content and cannot cross the Bridge: return its Outcome"
                        + " (a NavigationOutcome) instead. Runic.Navigation.ReactiveUI's CreateBackCommand() already returns"
                        + " a NavigationOutcome.";
            return "";
        }

        private IEnumerable<PropertyInfo> DataProperties(Type type, string path)
        {
            var hierarchy = new Stack<Type>();
            for (var current = type; current is not null && current != typeof(object) && !BridgeContractShape.IsFrameworkBase(current); current = current.BaseType)
                hierarchy.Push(current);
            var names = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
            foreach (var current in hierarchy)
            // Excluded properties first: their signature may need an assembly
            // the generator cannot load.
            foreach (var property in current.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.GetMethod is not null && !BridgeDtoContract.IsIgnored(property, honorJsonIgnore)
                    && property.GetIndexParameters().Length == 0))
            {
                var wire = WireName(property);
                if (names.TryGetValue(wire, out var other))
                    throw new BridgeTypeGraphException(path + "." + wire, $"Wire name collides between {other.DeclaringType?.Name}.{other.Name} and {property.DeclaringType?.Name}.{property.Name}; use RunicAlias or RunicIgnore.");
                names.Add(wire, property);
                yield return property;
            }
        }

        private static string WireName(PropertyInfo property) =>
            property.GetCustomAttribute<RunicAliasAttribute>(inherit: true)?.Name
            ?? property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name
            ?? char.ToLowerInvariant(property.Name[0]) + property.Name[1..];

        // A member that .NET fills in when the page leaves it out: a nullable
        // member, a constructor parameter with a default, and a JsonElement,
        // whose missing value is a default JsonElement (#312).
        private static void MarkOptionalMembers(BridgeTypeNode node, string path)
        {
            var parameters = node.Constructor?.GetParameters();
            for (var index = 0; index < node.Members.Count; index++)
            {
                var member = node.Members[index];
                var absent = member.Type is { Kind: BridgeWireKind.Json, IsNullable: false } && member.Type.NonNullableType == typeof(JsonElement);
                var parameter = parameters?.Single(candidate => string.Equals(candidate.Name, member.Property.Name, StringComparison.OrdinalIgnoreCase));
                var defaulted = parameter is { HasDefaultValue: true };
                node.Members[index] = member with
                {
                    Absent = absent,
                    Optional = absent || defaulted || member.Type.IsNullable,
                    DefaultExpression = defaulted ? DefaultLiteral(parameter!, path + "." + member.WireName) : null,
                };
            }
        }

        private static ConstructorInfo? SelectConstructor(Type type, List<BridgeTypeMember> members, string path)
        {
            var names = members.Select(member => member.Property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                .Where(candidate => candidate.GetParameters().Length == members.Count && candidate.GetParameters().All(parameter => parameter.Name is not null && names.Contains(parameter.Name)))
                .OrderByDescending(candidate => candidate.GetParameters().Length).FirstOrDefault();
            if (constructor is not null) return constructor;
            if (type.GetConstructor(Type.EmptyTypes) is not null && members.All(member => member.Writable)) return null;
            throw new BridgeTypeGraphException(path, $"{TypeIdentity(type)} is read from the page, as a command input, an interaction result or a settable property, so it needs a public constructor whose parameters map to every required property, or a public parameterless constructor with public setters. "
                + "Exclude computed or .NET-only DTO properties with [RunicIgnore] or an unconditional [JsonIgnore]. "
                + "[assembly: RunicIncludeJsonIgnored] in the presentation/ViewModel assembly keeps [JsonIgnore] properties in the contract. JsonIgnore conditions Never, WhenWritingNull and WhenWritingDefault remain included in the Bridge contract.");
        }
    }
}

internal enum BridgeWireKind
{
    None, Boolean, Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Single, Double, Decimal,
    String, Guid, DateOnly, TimeOnly, DateTime, DateTimeOffset, TimeSpan, BigInteger, Enum, Array, List,
    StringDictionary, Dto, Union, Custom,
    /// <summary>A text reference of Runic Translations: a RunicTextReference or a Runic.Translations.TranslationReference.</summary>
    TextReference,
    /// <summary>Any JSON value: a JsonElement or a JsonNode.</summary>
    Json,
    /// <summary>A RunicBlob (blobs/1): a URL the host serves, written to the page only.</summary>
    Blob,
    /// <summary>A RunicUpload (blobs/1): a file the page uploaded, named by its id.</summary>
    Upload,
}

internal sealed class BridgeTypeNode(int id, Type nonNullableType, bool isNullable)
{
    /// <summary>The use site's nullable annotations, when known.</summary>
    internal TypeNullability? Nullability { get; init; }
    internal int Id { get; } = id;
    internal Type NonNullableType { get; } = nonNullableType;
    internal Type Type { get; } = isNullable && nonNullableType.IsValueType ? typeof(Nullable<>).MakeGenericType(nonNullableType) : nonNullableType;
    internal bool IsNullable { get; } = isNullable;
    internal BridgeWireKind Kind { get; set; }
    internal string? WireName { get; set; }
    internal List<BridgeTypeMember> Members { get; } = [];
    internal BridgeTypeNode? Element { get; set; }
    internal BridgeTypeNode? Value { get; set; }
    internal List<BridgeUnionCase> Cases { get; } = [];
    internal List<BridgeEnumCase> EnumCases { get; } = [];
    internal ConstructorInfo? Constructor { get; set; }
    internal CustomCodecDescription? CustomCodec { get; set; }
    /// <summary>Where values of the graph that contains this node cross.</summary>
    internal BridgeValueDirection Direction { get; init; } = BridgeValueDirection.Both;
}

/// <summary>Where a Bridge value crosses: to the page, from it, or both.</summary>
[Flags]
internal enum BridgeValueDirection
{
    /// <summary>State, a command result, a declared failure or an interaction input.</summary>
    ToPage = 1,
    /// <summary>A command input or an interaction output.</summary>
    FromPage = 2,
    /// <summary>A settable state property.</summary>
    Both = ToPage | FromPage,
}

internal sealed record BridgeTypeMember(int Index, PropertyInfo Property, string WireName, BridgeTypeNode Type)
{
    /// <summary>.NET fills the member in when the page leaves it out.</summary>
    internal bool Optional { get; init; }
    /// <summary>A JsonElement member: absent from the object when it holds no value.</summary>
    internal bool Absent { get; init; }
    /// <summary>The constructor argument for a missing member, when its parameter has a default.</summary>
    internal string? DefaultExpression { get; init; }
    internal bool Readable => Property.GetMethod?.IsPublic == true;
    internal bool Writable => Property.SetMethod?.IsPublic == true;
}

internal sealed record BridgeUnionCase(string Discriminator, BridgeTypeNode Type);
internal sealed record BridgeEnumCase(FieldInfo Field, string WireName);

internal sealed record CustomCodecDescription(Type CodecType, string TypeScriptType, string TypeScriptDecoderExpression, string TypeScriptEncoderExpression)
{
    internal static CustomCodecDescription Create(Type valueType, Type codecType, string path)
    {
        var contract = codecType.GetInterfaces().FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IRunicCodec<>) && type.GenericTypeArguments[0] == valueType)
            ?? throw new BridgeTypeGraphException(path, $"{BridgeTypeGraph.CSharpType(codecType)} must implement IRunicCodec<{BridgeTypeGraph.CSharpType(valueType)}>.");
        _ = contract;
        var shape = codecType.GetCustomAttribute<RunicCodecShapeAttribute>(inherit: false);
        if (shape is null || string.IsNullOrWhiteSpace(shape.TypeScriptType) || string.IsNullOrWhiteSpace(shape.DecoderExpression))
            throw new BridgeTypeGraphException(path, $"{BridgeTypeGraph.CSharpType(codecType)} must provide TypeScriptType and TypeScriptDecoderExpression metadata.");
        return new(codecType, shape.TypeScriptType, shape.DecoderExpression, shape.EncoderExpression);
    }
}

internal sealed class BridgeTypeGraphException(string path, string message, MemberInfo? member = null, string? code = null)
    : NotSupportedException($"{path}: {message}")
{
    internal string Path { get; } = path;
    internal string Detail { get; } = message;
    /// <summary>The diagnostic code, when it is not RUNICBRIDGE003 (a value type that is not a bridge value).</summary>
    internal string? Code { get; } = code;
    /// <summary>The declaration the diagnostic points at, when known.</summary>
    internal MemberInfo? Member { get; } = member;
}

/// <summary>
/// The nullable annotations of one use of a type: the type itself, its array
/// element and its generic arguments. Unlike <see cref="NullabilityInfo"/>,
/// one can be built for a member of a closed generic type from the
/// annotations of the use site.
/// </summary>
/// <param name="IsNullable">True when a reference type is annotated nullable.
/// A nullable value type is a <see cref="Nullable{T}"/> type instead.</param>
internal sealed record TypeNullability(bool IsNullable, TypeNullability? Element, IReadOnlyList<TypeNullability?> Arguments)
{
    internal static TypeNullability? From(NullabilityInfo? info) => info is null ? null
        : new(info.ReadState == NullabilityState.Nullable, From(info.ElementType), [.. info.GenericTypeArguments.Select(From)]);

    internal TypeNullability? Argument(int index) => index < Arguments.Count ? Arguments[index] : null;

    /// <summary>The annotations of a type used without any nullable position.</summary>
    internal static TypeNullability NotNull(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return new(false, type.IsArray ? NotNull(type.GetElementType()!) : null,
            type.IsGenericType ? [.. type.GetGenericArguments().Select(NotNull)] : []);
    }
}

/// <summary>Reads the compiler's nullable annotation bytes of a member.</summary>
internal static class NullableAnnotations
{
    internal const byte Annotated = 2;
    private const string Attribute = "System.Runtime.CompilerServices.NullableAttribute";
    private const string Context = "System.Runtime.CompilerServices.NullableContextAttribute";

    /// <summary>
    /// Returns the annotation at a position of the member's type: 0 oblivious,
    /// 1 not annotated, 2 annotated. Without a NullableAttribute, the
    /// innermost NullableContextAttribute applies to every position.
    /// </summary>
    internal static Func<int, byte> Of(MemberInfo member)
    {
        if (member.CustomAttributes.FirstOrDefault(data => data.AttributeType.FullName == Attribute) is { ConstructorArguments: [var argument] })
        {
            if (argument.Value is byte single) return _ => single;
            if (argument.Value is IReadOnlyCollection<CustomAttributeTypedArgument> values)
            {
                var flags = values.Select(value => (byte)value.Value!).ToArray();
                return index => index < flags.Length ? flags[index] : (byte)0;
            }
        }
        for (var current = member; current is not null; current = current.DeclaringType)
            if (current.CustomAttributes.FirstOrDefault(data => data.AttributeType.FullName == Context) is { ConstructorArguments: [{ Value: byte context }] })
                return _ => context;
        return _ => 0;
    }
}

internal static class ContractNullability
{
    /// <summary>
    /// Returns the nullable annotation of a command or interaction type
    /// argument. The declared property type normally closes the contract
    /// directly (<c>ReactiveCommand&lt;string?, Unit&gt;</c>,
    /// <c>Interaction&lt;Unit, string?&gt;</c>); otherwise the annotation is
    /// unavailable and the argument is treated as non-nullable.
    /// </summary>
    internal static NullabilityInfo? Argument(PropertyInfo property, NullabilityInfoContext context,
        IReadOnlyList<Type> contractArguments, int index)
    {
        var declared = property.PropertyType;
        if (!declared.IsGenericType || !declared.GenericTypeArguments.SequenceEqual(contractArguments)) return null;
        var arguments = context.Create(property).GenericTypeArguments;
        return arguments.Length > index ? arguments[index] : null;
    }
}

internal static class TypeExtensions
{
    internal static Type? GetGenericTypeDefinitionOrNull(this Type type) => type.IsGenericType ? type.GetGenericTypeDefinition() : null;
}

/// <summary>
/// The text reference types (translation-text/1). <c>Runic.Translations.TranslationReference</c> is recognized by its
/// name and assembly, so the Views packages need no reference to Runic Translations: the generated code, compiled into
/// the application that references it, calls its parser and source-generated serializer.
/// </summary>
internal static class BridgeTextReferences
{
    internal const string TranslationReferenceName = "Runic.Translations.TranslationReference";

    internal static bool Is(Type type) => type == typeof(RunicTextReference) || IsTranslationReference(type);

    internal static bool IsTranslationReference(Type type) =>
        type.FullName == TranslationReferenceName && type.Assembly.GetName().Name == "Runic.Translations";
}
