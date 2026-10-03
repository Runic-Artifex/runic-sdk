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

    internal static BridgeTypeGraph Discover(Type type, NullabilityInfo? nullability = null, string? rootPath = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        var builder = new Builder();
        var root = builder.Build(type, nullability, rootPath ?? type.Name, [], null);
        return new(root, builder.Nodes);
    }

    internal string TypeScriptType() => TypeScriptType(Root);

    /// <summary>The root's declared C# type, including nullable annotations.</summary>
    internal string RootCSharpType() => CSharpNodeType(Root);

    internal string EmitTypeScriptDecoder(string expression) => EmitTypeScriptDecoder(Root, expression);

    /// <summary>Emits the JSON-wire expression for a public TypeScript value.</summary>
    internal string EncodeTypeScript(string expression) => EncodeTypeScript(Root, expression);

    internal void AppendFingerprint(List<string> parts, string scope)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var visited = new HashSet<BridgeTypeNode>(ReferenceEqualityComparer.Instance);
        AppendFingerprint(parts, scope, Root, visited);
    }

    // Emits a self-contained direct codec class. The caller normally embeds it
    // in the generated bridge file and passes `Write`/`Read` to BridgeValueCodec.
    internal void AppendCSharpCodec(StringBuilder source, string className, string accessibility = "private")
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(className)) throw new ArgumentException("A codec class name is required.", nameof(className));
        source.AppendLine($"{accessibility} static class {className}");
        source.AppendLine("{");
        source.AppendLine($"    internal static void Write(global::System.Text.Json.Utf8JsonWriter writer, {CSharpNodeType(Root)} value) => Write{Root.Id}(writer, value);");
        source.AppendLine($"    internal static {CSharpNodeType(Root)} Read(global::System.Text.Json.JsonElement element) => Read{Root.Id}(element);");
        source.AppendLine();
        foreach (var node in Nodes.OrderBy(node => node.Id))
        {
            AppendWriter(source, node);
            source.AppendLine();
            AppendReader(source, node);
            source.AppendLine();
        }
        source.AppendLine("}");
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

    private static string TypeScriptType(BridgeTypeNode node)
    {
        var type = node.Kind switch
        {
            BridgeWireKind.Boolean => "boolean",
            BridgeWireKind.Int8 or BridgeWireKind.UInt8 or BridgeWireKind.Int16 or BridgeWireKind.UInt16 or
            BridgeWireKind.Int32 or BridgeWireKind.UInt32 or BridgeWireKind.Single or BridgeWireKind.Double => "number",
            BridgeWireKind.Int64 or BridgeWireKind.UInt64 or BridgeWireKind.BigInteger => "bigint",
            BridgeWireKind.Decimal => "string",
            BridgeWireKind.String or BridgeWireKind.Guid or BridgeWireKind.DateOnly or BridgeWireKind.TimeOnly or
            BridgeWireKind.DateTime or BridgeWireKind.DateTimeOffset or BridgeWireKind.TimeSpan or BridgeWireKind.Enum => "string",
            BridgeWireKind.Array or BridgeWireKind.List => $"readonly ({TypeScriptType(node.Element!)})[]",
            BridgeWireKind.StringDictionary => $"Readonly<Record<string, {TypeScriptType(node.Value!)}>>",
            BridgeWireKind.Dto => "{ " + string.Join("; ", node.Members.Select(member =>
                $"readonly [{Quote(member.WireName)}]: {TypeScriptType(member.Type)}")) + " }",
            BridgeWireKind.Union => string.Join(" | ", node.Cases.Select(@case =>
                $"({{ readonly $case: {Quote(@case.Discriminator)} }} & {TypeScriptType(@case.Type)})")),
            BridgeWireKind.Custom => node.CustomCodec!.TypeScriptType,
            _ => throw new InvalidOperationException($"No TypeScript form exists for {node.Kind}.")
        };
        return node.IsNullable ? $"{type} | null" : type;
    }

    // The decoder validates every scalar rather than trusting a cast. It is an
    // expression so Program can place it in generated route decoding directly.
    private static string EmitTypeScriptDecoder(BridgeTypeNode node, string expression)
    {
        var nonNull = node.Kind switch
        {
            BridgeWireKind.Boolean => $"bridgeWire.boolean({expression})",
            BridgeWireKind.Int8 => $"bridgeWire.integer({expression}, -128, 127)",
            BridgeWireKind.UInt8 => $"bridgeWire.integer({expression}, 0, 255)",
            BridgeWireKind.Int16 => $"bridgeWire.integer({expression}, -32768, 32767)",
            BridgeWireKind.UInt16 => $"bridgeWire.integer({expression}, 0, 65535)",
            BridgeWireKind.Int32 => $"bridgeWire.integer({expression}, -2147483648, 2147483647)",
            BridgeWireKind.UInt32 => $"bridgeWire.integer({expression}, 0, 4294967295)",
            BridgeWireKind.Single or BridgeWireKind.Double => $"bridgeWire.finiteNumber({expression})",
            BridgeWireKind.Int64 => $"bridgeWire.bigint({expression}, \"-9223372036854775808\", \"9223372036854775807\")",
            BridgeWireKind.UInt64 => $"bridgeWire.bigint({expression}, \"0\", \"18446744073709551615\")",
            BridgeWireKind.BigInteger => $"bridgeWire.bigint({expression})",
            BridgeWireKind.Decimal => $"bridgeWire.decimal({expression})",
            BridgeWireKind.String => $"bridgeWire.string({expression})",
            BridgeWireKind.Guid => $"bridgeWire.guid({expression})",
            BridgeWireKind.DateOnly => $"bridgeWire.dateOnly({expression})",
            BridgeWireKind.TimeOnly => $"bridgeWire.timeOnly({expression})",
            BridgeWireKind.DateTime => $"bridgeWire.dateTime({expression})",
            BridgeWireKind.DateTimeOffset => $"bridgeWire.dateTimeOffset({expression})",
            BridgeWireKind.TimeSpan => $"bridgeWire.duration({expression})",
            BridgeWireKind.Enum => $"bridgeWire.enumName({expression}, [{string.Join(", ", node.EnumCases.Select(@case => Quote(@case.WireName)))}])",
            BridgeWireKind.Array or BridgeWireKind.List => $"bridgeWire.array({expression}, item => {EmitTypeScriptDecoder(node.Element!, "item")})",
            BridgeWireKind.StringDictionary => $"bridgeWire.stringRecord({expression}, item => {EmitTypeScriptDecoder(node.Value!, "item")})",
            BridgeWireKind.Dto => $"bridgeWire.object({expression}, value => ({{ {string.Join(", ", node.Members.Select(member => "[" + Quote(member.WireName) + "]: " + EmitTypeScriptDecoder(member.Type, "value[" + Quote(member.WireName) + "]"))) }}}))",
            BridgeWireKind.Union => DecodeUnionTypeScript(node, expression),
            BridgeWireKind.Custom => node.CustomCodec!.TypeScriptDecoderExpression.Replace("$value", expression, StringComparison.Ordinal),
            _ => throw new InvalidOperationException($"No TypeScript decoder exists for {node.Kind}.")
        };
        return node.IsNullable ? $"({expression} === null ? null : {nonNull})" : nonNull;
    }

    private static string DecodeUnionTypeScript(BridgeTypeNode node, string expression)
    {
        var value = $"bridgeDecodedUnion{node.Id}";
        var cases = string.Join(" ", node.Cases.Select(@case =>
        {
            var fields = string.Join(", ", @case.Type.Members.Select(member =>
                "[" + Quote(member.WireName) + "]: " + EmitTypeScriptDecoder(member.Type, value + "[" + Quote(member.WireName) + "]")));
            return $"case {Quote(@case.Discriminator)}: return {{ \"$case\": {Quote(@case.Discriminator)}{(fields.Length == 0 ? string.Empty : ", " + fields)} }};";
        }));
        return $"(() => {{ const {value}: any = bridgeWire.union({expression}); switch ({value}.$case) {{ {cases} default: throw new RangeError(\"Unknown union case.\"); }} }})()";
    }

    private static string EncodeTypeScript(BridgeTypeNode node, string expression)
    {
        // Capture nullable paths once before encoding. Apart from avoiding a
        // repeated getter, this gives strict TypeScript a local it can narrow:
        // `baseline.value` and an optional DTO member cannot reliably remain
        // narrowed through a recursively generated expression.
        var value = node.IsNullable ? $"bridgeNullable{node.Id}" : expression;
        var nonNull = node.Kind switch
        {
            BridgeWireKind.Int64 or BridgeWireKind.UInt64 or BridgeWireKind.BigInteger => $"{value}.toString()",
            // TimeSpan is an invariant "c" string. Validate outbound values too:
            // generated clients otherwise accept malformed values until the route
            // rejects them, while snapshots are validated on the way in.
            BridgeWireKind.TimeSpan => $"bridgeWire.duration({value})",
            BridgeWireKind.DateOnly => $"bridgeWire.dateOnly({value})",
            BridgeWireKind.TimeOnly => $"bridgeWire.timeOnly({value})",
            BridgeWireKind.DateTime => $"bridgeWire.dateTime({value})",
            BridgeWireKind.DateTimeOffset => $"bridgeWire.dateTimeOffset({value})",
            BridgeWireKind.Array or BridgeWireKind.List => $"{value}.map(item => {EncodeTypeScript(node.Element!, "item")})",
            BridgeWireKind.StringDictionary => $"Object.fromEntries(Object.entries({value}).map(([key, item]) => [key, {EncodeTypeScript(node.Value!, "item")}]))",
            BridgeWireKind.Dto => "{ " + string.Join(", ", node.Members.Select(member => "[" + Quote(member.WireName) + "]: " + EncodeTypeScript(member.Type, value + "[" + Quote(member.WireName) + "]"))) + " }",
            BridgeWireKind.Union => EncodeUnionTypeScript(node, value),
            BridgeWireKind.Custom => node.CustomCodec!.TypeScriptEncoderExpression.Replace("$value", value, StringComparison.Ordinal),
            _ => value,
        };
        return node.IsNullable
            ? $"(() => {{ const {value} = {expression}; return {value} === null ? null : {nonNull}; }})()"
            : nonNull;
    }

    private static string EncodeUnionTypeScript(BridgeTypeNode node, string expression)
    {
        var unionValue = $"bridgeUnion{node.Id}";
        var cases = string.Join(" ", node.Cases.Select(@case =>
        {
            var fields = string.Join(", ", @case.Type.Members.Select(member =>
                "[" + Quote(member.WireName) + "]: " + EncodeTypeScript(member.Type, unionValue + "[" + Quote(member.WireName) + "]")));
            return $"case {Quote(@case.Discriminator)}: return {{ \"$case\": {Quote(@case.Discriminator)}{(fields.Length == 0 ? string.Empty : ", " + fields)} }};";
        }));
        return $"(() => {{ const {unionValue}: any = bridgeWire.encodeUnion({expression}); switch ({unionValue}.$case) {{ {cases} default: throw new RangeError(\"Unknown union case.\"); }} }})()";
    }

    private static void AppendWriter(StringBuilder source, BridgeTypeNode node)
    {
        source.AppendLine($"    private static void Write{node.Id}(global::System.Text.Json.Utf8JsonWriter writer, {CSharpNodeType(node)} value)");
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
            case BridgeWireKind.Single: case BridgeWireKind.Double: source.AppendLine($"        global::Runic.Application.Views.BridgeWire.WriteFiniteNumber(writer, {value});"); break;
            case BridgeWireKind.Int64: case BridgeWireKind.UInt64: case BridgeWireKind.BigInteger: source.AppendLine($"        global::Runic.Application.Views.BridgeWire.WriteIntegerString(writer, {value});"); break;
            case BridgeWireKind.Decimal: source.AppendLine($"        global::Runic.Application.Views.BridgeWire.WriteDecimal(writer, {value});"); break;
            case BridgeWireKind.String: source.AppendLine($"        writer.WriteStringValue({value});"); break;
            case BridgeWireKind.Guid: source.AppendLine($"        global::Runic.Application.Views.BridgeWire.WriteGuid(writer, {value});"); break;
            case BridgeWireKind.DateOnly: source.AppendLine($"        global::Runic.Application.Views.BridgeWire.WriteDateOnly(writer, {value});"); break;
            case BridgeWireKind.TimeOnly: source.AppendLine($"        global::Runic.Application.Views.BridgeWire.WriteTimeOnly(writer, {value});"); break;
            case BridgeWireKind.DateTime: source.AppendLine($"        global::Runic.Application.Views.BridgeWire.WriteDateTime(writer, {value});"); break;
            case BridgeWireKind.DateTimeOffset: source.AppendLine($"        global::Runic.Application.Views.BridgeWire.WriteDateTimeOffset(writer, {value});"); break;
            case BridgeWireKind.TimeSpan: source.AppendLine($"        global::Runic.Application.Views.BridgeWire.WriteTimeSpan(writer, {value});"); break;
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
                source.AppendLine("        foreach (var item in " + value + ") Write" + node.Element!.Id + "(writer, item);");
                source.AppendLine("        writer.WriteEndArray();");
                break;
            case BridgeWireKind.StringDictionary:
                source.AppendLine("        writer.WriteStartObject();");
                source.AppendLine("        foreach (var entry in global::System.Linq.Enumerable.OrderBy(" + value + ", entry => entry.Key, global::System.StringComparer.Ordinal))");
                source.AppendLine("        { writer.WritePropertyName(entry.Key); Write" + node.Value!.Id + "(writer, entry.Value); }");
                source.AppendLine("        writer.WriteEndObject();");
                break;
            case BridgeWireKind.Dto:
                source.AppendLine("        writer.WriteStartObject();");
                foreach (var member in node.Members)
                    source.AppendLine($"        writer.WritePropertyName({Quote(member.WireName)}); Write{member.Type.Id}(writer, {value}.{member.Property.Name});");
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
                        source.AppendLine($"                writer.WritePropertyName({Quote(member.WireName)}); Write{member.Type.Id}(writer, item{@case.Type.Id}.{member.Property.Name});");
                    source.AppendLine("                break;");
                }
                source.AppendLine("            default: throw new global::System.NotSupportedException(\"The value is not a declared bridge-union case.\");");
                source.AppendLine("        }");
                source.AppendLine("        writer.WriteEndObject();");
                break;
            case BridgeWireKind.Custom:
                source.AppendLine($"        {CSharpType(node.CustomCodec!.CodecType)}.Write(writer, {value});");
                break;
            default:
                source.AppendLine("        throw new global::System.NotSupportedException(\"The generated codec needs a closed-union writer.\");");
                break;
        }
        source.AppendLine("    }");
    }

    private static void AppendReader(StringBuilder source, BridgeTypeNode node)
    {
        source.AppendLine($"    private static {CSharpNodeType(node)} Read{node.Id}(global::System.Text.Json.JsonElement element)");
        source.AppendLine("    {");
        if (node.IsNullable)
        {
            source.AppendLine("        if (element.ValueKind is global::System.Text.Json.JsonValueKind.Null) return default!;");
        }
        switch (node.Kind)
        {
            case BridgeWireKind.Boolean: source.AppendLine("        return element.ValueKind is global::System.Text.Json.JsonValueKind.True ? true : element.ValueKind is global::System.Text.Json.JsonValueKind.False ? false : throw global::Runic.Application.Views.BridgeWire.Invalid(\"Expected a boolean.\");"); break;
            case BridgeWireKind.Int8: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadInt8(element);"); break;
            case BridgeWireKind.UInt8: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadUInt8(element);"); break;
            case BridgeWireKind.Int16: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadInt16(element);"); break;
            case BridgeWireKind.UInt16: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadUInt16(element);"); break;
            case BridgeWireKind.Int32: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadInt32(element);"); break;
            case BridgeWireKind.UInt32: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadUInt32(element);"); break;
            case BridgeWireKind.Single: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadSingle(element);"); break;
            case BridgeWireKind.Double: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadDouble(element);"); break;
            case BridgeWireKind.Int64: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadInt64String(element);"); break;
            case BridgeWireKind.UInt64: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadUInt64String(element);"); break;
            case BridgeWireKind.BigInteger: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadBigInteger(element);"); break;
            case BridgeWireKind.Decimal: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadDecimal(element);"); break;
            case BridgeWireKind.String: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadString(element);"); break;
            case BridgeWireKind.Guid: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadGuid(element);"); break;
            case BridgeWireKind.DateOnly: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadDateOnly(element);"); break;
            case BridgeWireKind.TimeOnly: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadTimeOnly(element);"); break;
            case BridgeWireKind.DateTime: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadDateTime(element);"); break;
            case BridgeWireKind.DateTimeOffset: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadDateTimeOffset(element);"); break;
            case BridgeWireKind.TimeSpan: source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadTimeSpan(element);"); break;
            case BridgeWireKind.Enum:
                source.AppendLine("        return global::Runic.Application.Views.BridgeWire.ReadString(element) switch");
                source.AppendLine("        {");
                foreach (var @case in node.EnumCases)
                    source.AppendLine($"            {Quote(@case.WireName)} => {CSharpType(node.NonNullableType)}.{@case.Field.Name},");
                source.AppendLine("            _ => throw global::Runic.Application.Views.BridgeWire.Invalid(\"Unknown bridge enum name.\"),");
                source.AppendLine("        };");
                break;
            case BridgeWireKind.Array:
            case BridgeWireKind.List:
                AppendArrayReader(source, node); break;
            case BridgeWireKind.StringDictionary:
                AppendDictionaryReader(source, node); break;
            case BridgeWireKind.Dto:
                AppendDtoReader(source, node); break;
            case BridgeWireKind.Union:
                source.AppendLine("        var discriminator = global::Runic.Application.Views.BridgeWire.ReadString(global::Runic.Application.Views.BridgeWire.RequiredProperty(element, \"$case\"));");
                source.AppendLine("        return discriminator switch");
                source.AppendLine("        {");
                foreach (var @case in node.Cases)
                    source.AppendLine($"            {Quote(@case.Discriminator)} => Read{@case.Type.Id}(element),");
                source.AppendLine("            _ => throw global::Runic.Application.Views.BridgeWire.Invalid(\"Unknown bridge-union case.\"),");
                source.AppendLine("        };");
                break;
            case BridgeWireKind.Custom:
                source.AppendLine($"        return {CSharpType(node.CustomCodec!.CodecType)}.Read(element);"); break;
            default:
                source.AppendLine("        throw new global::System.NotSupportedException(\"The generated codec needs a closed-union reader.\");"); break;
        }
        source.AppendLine("    }");
    }

    // Builds the declared collection type itself, so a codec's signature and
    // its reader agree for every collection that Builder accepts.
    private static void AppendArrayReader(StringBuilder source, BridgeTypeNode node)
    {
        var element = node.Element ?? throw new InvalidOperationException("A collection needs an element type.");
        var elementType = CSharpNodeType(element);
        source.AppendLine("        if (element.ValueKind is not global::System.Text.Json.JsonValueKind.Array) throw global::Runic.Application.Views.BridgeWire.Invalid(\"Expected an array.\");");
        source.AppendLine($"        var items = new global::System.Collections.Generic.List<{elementType}>(element.GetArrayLength());");
        source.AppendLine($"        foreach (var item in element.EnumerateArray()) items.Add(Read{element.Id}(item));");
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

    private static void AppendDictionaryReader(StringBuilder source, BridgeTypeNode node)
    {
        var value = node.Value ?? throw new InvalidOperationException("A dictionary node needs a value type.");
        source.AppendLine("        if (element.ValueKind is not global::System.Text.Json.JsonValueKind.Object) throw global::Runic.Application.Views.BridgeWire.Invalid(\"Expected an object.\");");
        source.AppendLine($"        var result = new global::System.Collections.Generic.Dictionary<string, {CSharpNodeType(value)}>(global::System.StringComparer.Ordinal);");
        source.AppendLine($"        foreach (var property in element.EnumerateObject()) result.Add(property.Name, Read{value.Id}(property.Value));");
        source.AppendLine("        return result;");
    }

    private static void AppendDtoReader(StringBuilder source, BridgeTypeNode node)
    {
        source.AppendLine("        if (element.ValueKind is not global::System.Text.Json.JsonValueKind.Object) throw global::Runic.Application.Views.BridgeWire.Invalid(\"Expected an object.\");");
        foreach (var member in node.Members)
            source.AppendLine($"        var {Local(member)} = global::Runic.Application.Views.BridgeWire.RequiredProperty(element, {Quote(member.WireName)});");
        if (node.Constructor is { } constructor)
        {
            var arguments = constructor.GetParameters().Select(parameter =>
            {
                var member = node.Members.Single(candidate => string.Equals(candidate.Property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
                return $"Read{member.Type.Id}({Local(member)})";
            });
            source.AppendLine($"        return new {CSharpType(node.NonNullableType)}({string.Join(", ", arguments)});");
        }
        else
        {
            source.AppendLine($"        return new {CSharpType(node.NonNullableType)} {{");
            foreach (var member in node.Members)
                source.AppendLine($"            {member.Property.Name} = Read{member.Type.Id}({Local(member)}),");
            source.AppendLine("        };");
        }
    }

    private static string Local(BridgeTypeMember member) => "value_" + member.Index.ToString(CultureInfo.InvariantCulture);
    private static string Quote(string text) => JsonSerializer.Serialize(text);
    private static string TypeIdentity(Type type) => type.FullName ?? type.Name;

    internal static string CSharpType(Type type)
    {
        if (type.IsArray) return CSharpType(type.GetElementType()!) + "[]";
        if (!type.IsGenericType) return "global::" + (type.FullName ?? type.Name).Replace('+', '.');
        var name = (type.GetGenericTypeDefinition().FullName ?? type.Name);
        name = name[..name.IndexOf('`')].Replace('+', '.');
        return "global::" + name + "<" + string.Join(", ", type.GetGenericArguments().Select(CSharpType)) + ">";
    }

    // The declared C# type with its nullable annotations, e.g.
    // ImmutableArray<string?>? or Point?. Codecs use it for signatures.
    private static string CSharpNodeType(BridgeTypeNode node)
    {
        var type = node.Kind switch
        {
            BridgeWireKind.Array => CSharpNodeType(node.Element!) + "[]",
            BridgeWireKind.List => GenericName(node.NonNullableType) + "<" + CSharpNodeType(node.Element!) + ">",
            BridgeWireKind.StringDictionary => GenericName(node.NonNullableType) + "<string, " + CSharpNodeType(node.Value!) + ">",
            _ => CSharpType(node.NonNullableType),
        };
        return node.IsNullable ? type + "?" : type;
    }

    private static string GenericName(Type type)
    {
        var generic = type.GetGenericTypeDefinition().FullName
            ?? throw new InvalidOperationException("A collection needs a generic type name.");
        return "global::" + generic[..generic.IndexOf('`')].Replace('+', '.');
    }

    private sealed class Builder
    {
        private readonly NullabilityInfoContext _nullability = new();
        private int _nextId;
        internal List<BridgeTypeNode> Nodes { get; } = [];

        internal BridgeTypeNode Build(Type declared, NullabilityInfo? nullability, string path, HashSet<Type> stack,
            RunicBridgeCodecAttribute? memberCodec, bool unionCase = false)
        {
            var nullable = !declared.IsValueType
                ? nullability?.ReadState == NullabilityState.Nullable
                : Nullable.GetUnderlyingType(declared) is not null;
            var type = Nullable.GetUnderlyingType(declared) ?? declared;
            if (stack.Contains(type)) throw new BridgeTypeGraphException(path, "Recursive DTO graphs are not a bridge contract.");
            var node = new BridgeTypeNode(_nextId++, type, nullable);
            Nodes.Add(node);
            if (TryScalar(type, out var scalar)) { node.Kind = scalar; return node; }
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
                if (memberCodec is not null || type.GetCustomAttribute<RunicBridgeCodecAttribute>(inherit: true) is not null)
                    throw new BridgeTypeGraphException(path, "A closed union cannot also use an opaque custom codec.");
                BuildUnion(node, union, path, stack);
                return node;
            }
            var custom = memberCodec ?? type.GetCustomAttribute<RunicBridgeCodecAttribute>(inherit: true);
            if (custom is not null)
            {
                node.Kind = BridgeWireKind.Custom;
                node.CustomCodec = CustomCodecDescription.Create(type, custom.CodecType, path);
                return node;
            }
            if (TryCollection(type, path, out var collection, out var element))
            {
                node.Kind = collection;
                stack.Add(type);
                // Array element annotations live in ElementType, not in
                // GenericTypeArguments: string?[] must keep its nullable items.
                node.Element = Build(element!, type.IsArray ? nullability?.ElementType : GenericNullability(nullability, 0),
                    path + "[]", stack, null);
                stack.Remove(type);
                return node;
            }
            if (TryStringDictionary(type, out var value))
            {
                node.Kind = BridgeWireKind.StringDictionary;
                stack.Add(type);
                node.Value = Build(value!, GenericNullability(nullability, 1), path + "{}", stack, null);
                stack.Remove(type);
                return node;
            }
            // A concrete collection outside the supported list would
            // otherwise be treated as a DTO and silently serialize as {}.
            if (UnsupportedCollection(type) is { } collectionError) throw new BridgeTypeGraphException(path, collectionError);
            if (!IsDto(type)) throw new BridgeTypeGraphException(path, $"{TypeIdentity(type)} is not an explicitly supported bridge value. Use a supported scalar, collection, public DTO, RunicUnion, or RunicBridgeCodec.");
            node.Kind = BridgeWireKind.Dto;
            stack.Add(type);
            var members = DataProperties(type, path).ToArray();
            // An empty union case is a valid tag-only case. Any other DTO
            // without readable properties would always cross the bridge as {}.
            if (members.Length == 0 && !unionCase)
                throw new BridgeTypeGraphException(path, $"{TypeIdentity(type)} has no public readable properties, so it would always serialize as {{}}. Add properties or a RunicBridgeCodec.");
            foreach (var property in members)
            {
                var info = _nullability.Create(property);
                node.Members.Add(new BridgeTypeMember(node.Members.Count, property, WireName(property),
                    Build(property.PropertyType, info, path + "." + WireName(property), stack,
                        property.GetCustomAttribute<RunicBridgeCodecAttribute>(inherit: true))));
            }
            node.Constructor = SelectConstructor(type, node.Members, path);
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
                if (type.GetCustomAttribute<RunicBridgeCodecAttribute>(inherit: true) is not null)
                    throw new BridgeTypeGraphException(path, $"{TypeIdentity(type)} cannot be both a union case and an opaque custom codec.");
                var tag = type.GetCustomAttribute<RunicUnionCaseAttribute>(inherit: false)?.Name;
                if (string.IsNullOrWhiteSpace(tag) || tag.Length > 128 || tag.Any(char.IsControl) || !seenTags.Add(tag))
                    throw new BridgeTypeGraphException(path, $"{TypeIdentity(type)} needs a unique RunicUnionCase discriminator of at most 128 printable characters.");
                // A case can contain a member whose declared type references the union root; this is a real cycle.
                var caseNode = Build(type, null, path + "." + tag, stack, null, unionCase: true);
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

        private static NullabilityInfo? GenericNullability(NullabilityInfo? parent, int index) =>
            parent is { GenericTypeArguments.Length: > 0 } && parent.GenericTypeArguments.Length > index
                ? parent.GenericTypeArguments[index] : null;

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

        private static IEnumerable<PropertyInfo> DataProperties(Type type, string path)
        {
            var hierarchy = new Stack<Type>();
            for (var current = type; current is not null && current != typeof(object) && !IsFrameworkBase(current); current = current.BaseType)
                hierarchy.Push(current);
            var names = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
            foreach (var current in hierarchy)
            foreach (var property in current.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public).Where(property => property.GetIndexParameters().Length == 0))
            {
                if (property.GetMethod is null || property.GetCustomAttribute<RunicIgnoreAttribute>(inherit: true) is not null) continue;
                var wire = WireName(property);
                if (names.TryGetValue(wire, out var other))
                    throw new BridgeTypeGraphException(path + "." + wire, $"Wire name collides between {other.DeclaringType?.Name}.{other.Name} and {property.DeclaringType?.Name}.{property.Name}; use RunicAlias or RunicIgnore.");
                names.Add(wire, property);
                yield return property;
            }
        }

        private static bool IsFrameworkBase(Type type) => type.FullName is "ReactiveUI.ReactiveObject" or "CommunityToolkit.Mvvm.ComponentModel.ObservableObject" ||
            type.Namespace?.StartsWith("System.", StringComparison.Ordinal) == true;

        private static string WireName(PropertyInfo property) =>
            property.GetCustomAttribute<RunicAliasAttribute>(inherit: true)?.Name
            ?? property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name
            ?? char.ToLowerInvariant(property.Name[0]) + property.Name[1..];

        private static ConstructorInfo? SelectConstructor(Type type, IReadOnlyList<BridgeTypeMember> members, string path)
        {
            var names = members.Select(member => member.Property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                .Where(candidate => candidate.GetParameters().Length == members.Count && candidate.GetParameters().All(parameter => parameter.Name is not null && names.Contains(parameter.Name)))
                .OrderByDescending(candidate => candidate.GetParameters().Length).FirstOrDefault();
            if (constructor is not null) return constructor;
            if (type.GetConstructor(Type.EmptyTypes) is not null && members.All(member => member.Writable)) return null;
            throw new BridgeTypeGraphException(path, $"{TypeIdentity(type)} needs a public constructor whose parameters map to every required property, or a public parameterless constructor with public setters.");
        }
    }
}

internal enum BridgeWireKind
{
    None, Boolean, Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Single, Double, Decimal,
    String, Guid, DateOnly, TimeOnly, DateTime, DateTimeOffset, TimeSpan, BigInteger, Enum, Array, List,
    StringDictionary, Dto, Union, Custom,
}

internal sealed class BridgeTypeNode(int id, Type nonNullableType, bool isNullable)
{
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
}

internal sealed record BridgeTypeMember(int Index, PropertyInfo Property, string WireName, BridgeTypeNode Type)
{
    internal bool Readable => Property.GetMethod?.IsPublic == true;
    internal bool Writable => Property.SetMethod?.IsPublic == true;
}

internal sealed record BridgeUnionCase(string Discriminator, BridgeTypeNode Type);
internal sealed record BridgeEnumCase(FieldInfo Field, string WireName);

internal sealed record CustomCodecDescription(Type CodecType, string TypeScriptType, string TypeScriptDecoderExpression, string TypeScriptEncoderExpression)
{
    internal static CustomCodecDescription Create(Type valueType, Type codecType, string path)
    {
        var contract = codecType.GetInterfaces().FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IRunicBridgeCodec<>) && type.GenericTypeArguments[0] == valueType)
            ?? throw new BridgeTypeGraphException(path, $"{BridgeTypeGraph.CSharpType(codecType)} must implement IRunicBridgeCodec<{BridgeTypeGraph.CSharpType(valueType)}>.");
        _ = contract;
        var shape = codecType.GetCustomAttribute<RunicCodecShapeAttribute>(inherit: false);
        if (shape is null || string.IsNullOrWhiteSpace(shape.TypeScriptType) || string.IsNullOrWhiteSpace(shape.DecoderExpression))
            throw new BridgeTypeGraphException(path, $"{BridgeTypeGraph.CSharpType(codecType)} must provide TypeScriptType and TypeScriptDecoderExpression metadata.");
        return new(codecType, shape.TypeScriptType, shape.DecoderExpression, shape.EncoderExpression);
    }
}

internal sealed class BridgeTypeGraphException(string path, string message) : NotSupportedException($"{path}: {message}")
{
    internal string Path { get; } = path;
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
