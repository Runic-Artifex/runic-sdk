using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// The Views contract IR (<c>runic.views.contract-ir/1</c>, specs/application/contract-ir.md): what the model
/// assembly exposes to pages, in a language-neutral form that a client emitter reads. The generator adds each planned
/// View (<c>ContractIrView</c> in Program.cs), and after a generation without errors writes <c>contract.ir.json</c>
/// beside the generated C# in <c>obj</c>, where <see cref="BridgeGenerationCache"/> records it like the C#.
/// </summary>
internal static class ContractIr
{
    internal const string Format = "runic.views.contract-ir/1";
    internal const string FileName = "contract.ir.json";

    private static readonly List<JsonObject> Views = [];
    private static readonly Dictionary<string, JsonObject> Types = new(StringComparer.Ordinal);
    private static readonly Dictionary<(Type Type, string Shape), string> TypeIds = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        // The IR is data for emitters, not HTML: + < > & and non-ASCII text stay readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // The View member or command that each published ViewModel property is, for the shared clients.
    private static readonly Dictionary<(Type Model, PropertyInfo Property), JsonObject> Published = [];

    internal static void AddView(JsonObject view) => Views.Add(view);

    /// <summary>Records that <paramref name="property"/> of <paramref name="model"/> is the View member or command <paramref name="wire"/>.</summary>
    internal static void Publish(Type model, PropertyInfo property, bool command, string wire) =>
        Published[(model, property)] = new JsonObject { [command ? "command" : "member"] = wire };

    /// <summary>The View member or command of a published property, as a shared client member names it.</summary>
    internal static JsonObject PublishedAs(Type model, PropertyInfo property) => (JsonObject)Published[(model, property)].DeepClone();

    /// <summary>A value position: the root of a type graph, with the direction in which its values cross.</summary>
    internal static JsonObject Value(BridgeTypeGraph graph) => new()
    {
        ["direction"] = graph.Root.Direction switch
        {
            BridgeValueDirection.ToPage => "to-page",
            BridgeValueDirection.FromPage => "from-page",
            _ => "both",
        },
        ["type"] = TypeRef(graph.Root),
    };

    /// <summary>The untyped string argument of a legacy command, which has no input graph.</summary>
    internal static JsonObject UntypedString() => new()
    {
        ["direction"] = "from-page",
        ["type"] = new JsonObject { ["kind"] = "string" },
        ["untyped"] = true,
    };

    internal static JsonObject TypeRef(BridgeTypeNode node)
    {
        var result = new JsonObject();
        switch (node.Kind)
        {
            case BridgeWireKind.Enum or BridgeWireKind.Dto or BridgeWireKind.Union:
                result["kind"] = "named";
                result["ref"] = Named(node);
                break;
            case BridgeWireKind.Array or BridgeWireKind.List:
                result["kind"] = "list";
                result["element"] = TypeRef(node.Element!);
                break;
            case BridgeWireKind.StringDictionary:
                result["kind"] = "map";
                result["value"] = TypeRef(node.Value!);
                break;
            case BridgeWireKind.Custom:
                var codec = node.CustomCodec!;
                result["kind"] = "custom";
                result["codec"] = codec.CodecType.ToString();
                result["typescript"] = new JsonObject
                {
                    ["type"] = codec.TypeScriptType,
                    ["decode"] = codec.TypeScriptDecoderExpression,
                    ["encode"] = codec.TypeScriptEncoderExpression,
                };
                break;
            case BridgeWireKind.Json:
                result["kind"] = "json";
                // JSON null is a value of a JsonElement and of a nullable JsonNode; a JsonNode's null is its C# null.
                result["jsonNull"] = node.IsNullable || node.NonNullableType == typeof(JsonElement);
                break;
            default:
                result["kind"] = Scalar(node.Kind);
                break;
        }
        if (node.IsNullable) result["nullable"] = true;
        return result;
    }

    private static string Scalar(BridgeWireKind kind) => kind switch
    {
        BridgeWireKind.Boolean => "boolean",
        BridgeWireKind.Int8 => "int8",
        BridgeWireKind.UInt8 => "uint8",
        BridgeWireKind.Int16 => "int16",
        BridgeWireKind.UInt16 => "uint16",
        BridgeWireKind.Int32 => "int32",
        BridgeWireKind.UInt32 => "uint32",
        BridgeWireKind.Int64 => "int64",
        BridgeWireKind.UInt64 => "uint64",
        BridgeWireKind.BigInteger => "big-integer",
        BridgeWireKind.Single => "float32",
        BridgeWireKind.Double => "float64",
        BridgeWireKind.Decimal => "decimal",
        BridgeWireKind.String => "string",
        BridgeWireKind.Guid => "uuid",
        BridgeWireKind.DateOnly => "local-date",
        BridgeWireKind.TimeOnly => "local-time",
        BridgeWireKind.DateTime => "date-time",
        BridgeWireKind.DateTimeOffset => "offset-date-time",
        BridgeWireKind.TimeSpan => "duration",
        BridgeWireKind.TextReference => "text-reference",
        BridgeWireKind.Blob => "blob",
        BridgeWireKind.Upload => "upload",
        _ => throw new InvalidOperationException($"The contract IR has no value kind for {kind}."),
    };

    // One definition per .NET type and member shape (TypeNames.Shape), as the emitter declares one TypeScript type each:
    // the type's name, then name#2, name#3 for further shapes of one type, in the order the IR first reaches them.
    private static string Named(BridgeTypeNode node)
    {
        var key = (node.NonNullableType, TypeNames.Shape(node));
        if (TypeIds.TryGetValue(key, out var id)) return id;
        var source = node.NonNullableType.ToString();
        id = source;
        for (var suffix = 2; Types.ContainsKey(id); suffix++) id = $"{source}#{suffix}";
        TypeIds.Add(key, id);
        var definition = new JsonObject();
        Types.Add(id, definition);
        definition["kind"] = node.Kind switch { BridgeWireKind.Enum => "enum", BridgeWireKind.Dto => "object", _ => "union" };
        definition["name"] = NameHints(node, source);
        if (XmlDocumentation.Summary(node.NonNullableType) is { } doc) definition["doc"] = doc;
        switch (node.Kind)
        {
            case BridgeWireKind.Enum:
                definition["cases"] = new JsonArray([.. node.EnumCases.Select(@case =>
                {
                    var item = new JsonObject
                    {
                        ["wire"] = @case.WireName,
                        ["value"] = Convert.ToString(@case.Field.GetRawConstantValue(), CultureInfo.InvariantCulture),
                    };
                    if (XmlDocumentation.Summary(@case.Field) is { } caseDoc) item["doc"] = caseDoc;
                    return (JsonNode)item;
                })]);
                break;
            case BridgeWireKind.Dto:
                definition["members"] = new JsonArray([.. node.Members.Select(member =>
                {
                    var item = new JsonObject { ["wire"] = member.WireName, ["type"] = TypeRef(member.Type) };
                    if (member.Optional) item["optional"] = true;
                    if (member.Absent) item["absent"] = true;
                    if (XmlDocumentation.Summary(member.Property) is { } memberDoc) item["doc"] = memberDoc;
                    return (JsonNode)item;
                })]);
                break;
            case BridgeWireKind.Union:
                definition["cases"] = new JsonArray([.. node.Cases.Select(@case => (JsonNode)new JsonObject
                {
                    ["tag"] = @case.Discriminator,
                    // A case with members is a named object; a tag-only case has none.
                    ["object"] = @case.Type.Members.Count == 0 ? null : Named(@case.Type),
                })]);
                break;
        }
        return id;
    }

    // The names a target language names the type from: the base name, the base name with the use's nullable
    // annotations (two shapes of one generic type), and the qualifiers, innermost first.
    private static JsonObject NameHints(BridgeTypeNode node, string source)
    {
        var hints = new JsonObject
        {
            ["source"] = source,
            ["base"] = TypeNames.BaseName(node.NonNullableType, null),
        };
        var annotated = TypeNames.BaseName(node.NonNullableType, node.Nullability);
        if (annotated != hints["base"]!.GetValue<string>()) hints["annotatedBase"] = annotated;
        hints["qualifiers"] = new JsonArray([.. TypeNames.Qualifiers(node.NonNullableType).Select(value => (JsonNode)value)]);
        return hints;
    }

    internal const string FingerprintFormat = "runic.views.fingerprint/1";

    /// <summary>
    /// The View's fingerprint (<c>runic.views.fingerprint/1</c>, specs/application/contract-ir.md, "Fingerprint"): the
    /// uppercase hexadecimal SHA-256 of the RFC 8785 canonical form of the View's wire projection, in which each named
    /// type the View reaches is written in place of its reference. Call it once every type of the View is recorded.
    /// </summary>
    internal static string Fingerprint(JsonObject view)
    {
        var projection = new JsonObject { ["format"] = FingerprintFormat, ["view"] = ProjectView(view) };
        var text = new System.Text.StringBuilder();
        WriteCanonical(text, projection);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// Completes a View of the IR with its <paramref name="fingerprint"/> and the contracts derived from it: an
    /// interaction's <c>{fingerprint}:interaction:{name}</c> and a view request's <c>{fingerprint}:request:{name}</c>.
    /// </summary>
    internal static void Seal(JsonObject view, string fingerprint)
    {
        view["fingerprint"] = fingerprint;
        foreach (var (member, kind) in new[] { ("interactions", "interaction"), ("requests", "request") })
            foreach (var item in view[member]?.AsArray() ?? [])
                item!["contract"] = InteractionContract(fingerprint, kind, item["name"]!.GetValue<string>());
    }

    /// <summary>The contract of an interaction (<c>interaction</c>) or view request (<c>request</c>) of a View.</summary>
    internal static string InteractionContract(string fingerprint, string kind, string name) => $"{fingerprint}:{kind}:{name}";

    // The members of the projection's View, of its state members, commands, interactions and view requests, of named
    // types and of their parts that are left out: identities, documentation, names that only the client uses, constraints
    // and the contracts that contain the fingerprint.
    private static readonly string[] ViewOmitted = ["id", "name", "fingerprint", "doc", "owner", "sharedClients"];
    private static readonly string[] MemberOmitted = ["name", "doc", "declaringType", "constraints", "defaultKey"];
    private static readonly string[] CommandOmitted = ["doc", "parameter"];
    private static readonly string[] InteractionOmitted = ["doc", "contract"];
    private static readonly string[] DefinitionOmitted = ["name", "doc"];
    private static readonly string[] ValuePositions = ["value", "rows", "input", "result", "failure", "output"];

    private static JsonObject ProjectView(JsonObject view)
    {
        var result = Without(view, ViewOmitted);
        // The kinds are a set on the wire; their order only chooses the client's default, and a unit that imports a
        // library's clients may list the library's kinds in another order than the library does.
        result["kinds"] = new JsonArray([.. result["kinds"]!.AsArray().Select(kind => kind!.GetValue<string>())
            .Order(StringComparer.Ordinal).Select(kind => (JsonNode)kind)]);
        foreach (var item in result["members"]?.AsArray() ?? [])
        {
            var member = (JsonObject)item!;
            foreach (var name in MemberOmitted) member.Remove(name);
            foreach (var page in member["pages"]?.AsArray() ?? []) ((JsonObject)page!).Remove("name");
            foreach (var path in member["fieldPaths"]?.AsArray() ?? []) ((JsonObject)path!).Remove("constraints");
            ProjectPositions(member);
        }
        foreach (var item in result["commands"]?.AsArray() ?? [])
        {
            foreach (var name in CommandOmitted) ((JsonObject)item!).Remove(name);
            ProjectPositions((JsonObject)item!);
        }
        foreach (var section in new[] { "interactions", "requests" })
            foreach (var item in result[section]?.AsArray() ?? [])
            {
                foreach (var name in InteractionOmitted) ((JsonObject)item!).Remove(name);
                ProjectPositions((JsonObject)item!);
            }
        return result;
    }

    // A value position {direction, type} keeps its direction; its type is projected.
    private static void ProjectPositions(JsonObject owner)
    {
        foreach (var name in ValuePositions)
            if (owner[name] is JsonObject position && position["type"] is JsonObject type)
                position["type"] = ProjectType(type);
    }

    private static JsonObject ProjectType(JsonObject type)
    {
        var result = (JsonObject)type.DeepClone();
        switch (result["kind"]!.GetValue<string>())
        {
            case "named":
                var id = result["ref"]!.GetValue<string>();
                result.Remove("ref");
                result["definition"] = ProjectDefinition(id);
                break;
            case "list":
                result["element"] = ProjectType((JsonObject)result["element"]!);
                break;
            case "map":
                result["value"] = ProjectType((JsonObject)result["value"]!);
                break;
        }
        return result;
    }

    // A named type, written in place of its reference. Producers reject recursive types, so the expansion ends.
    private static JsonObject ProjectDefinition(string id)
    {
        var result = Without(Types[id], DefinitionOmitted);
        switch (result["kind"]!.GetValue<string>())
        {
            case "enum":
                foreach (var item in result["cases"]!.AsArray()) ((JsonObject)item!).Remove("doc");
                break;
            case "object":
                foreach (var item in result["members"]!.AsArray())
                {
                    var member = (JsonObject)item!;
                    member.Remove("doc");
                    member["type"] = ProjectType((JsonObject)member["type"]!);
                }
                break;
            case "union":
                foreach (var item in result["cases"]!.AsArray())
                    if (item!["object"] is JsonValue reference) item["object"] = ProjectDefinition(reference.GetValue<string>());
                break;
        }
        return result;
    }

    private static JsonObject Without(JsonObject source, string[] omitted)
    {
        var result = (JsonObject)source.DeepClone();
        foreach (var name in omitted) result.Remove(name);
        return result;
    }

    // RFC 8785 for the projection, which holds objects, arrays, strings, booleans and null: members sorted by their
    // names' UTF-16 code units, no whitespace, and strings escaped as ECMAScript's JSON.stringify escapes them.
    private static void WriteCanonical(System.Text.StringBuilder text, JsonNode? node)
    {
        switch (node)
        {
            case null:
                text.Append("null");
                break;
            case JsonObject value:
                text.Append('{');
                var first = true;
                foreach (var (name, member) in value.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    if (!first) text.Append(',');
                    first = false;
                    WriteString(text, name);
                    text.Append(':');
                    WriteCanonical(text, member);
                }
                text.Append('}');
                break;
            case JsonArray value:
                text.Append('[');
                for (var index = 0; index < value.Count; index++)
                {
                    if (index > 0) text.Append(',');
                    WriteCanonical(text, value[index]);
                }
                text.Append(']');
                break;
            case JsonValue value when value.TryGetValue<string>(out var stringValue):
                WriteString(text, stringValue);
                break;
            case JsonValue value when value.TryGetValue<bool>(out var boolean):
                text.Append(boolean ? "true" : "false");
                break;
            default:
                throw new InvalidOperationException($"The {FingerprintFormat} projection holds a number: {node.ToJsonString()}.");
        }
    }

    private static void WriteString(System.Text.StringBuilder text, string value)
    {
        text.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                case < ' ': text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: text.Append(character); break;
            }
        }
        text.Append('"');
    }

    /// <summary>
    /// The IR document of the generation so far. <paramref name="sourceMap"/> is the development source map's text,
    /// whose locations become <c>sources</c>.
    /// </summary>
    internal static string Serialize(string unit, bool clientLibrary, IReadOnlyList<ClientModuleMapping> clientModules,
        IEnumerable<JsonObject> sharedClients, string sourceMap)
    {
        var producer = new JsonObject { ["language"] = "csharp", ["tool"] = "Runic.Application.Views.Codegen", ["unit"] = unit };
        if (clientLibrary) producer["clientLibrary"] = true;
        var document = new JsonObject { ["format"] = Format, ["producer"] = producer };
        if (clientModules.Count > 0)
            document["clientModules"] = new JsonArray([.. clientModules.Select(mapping => (JsonNode)new JsonObject
            {
                ["unit"] = mapping.Assembly,
                ["specifier"] = mapping.Specifier,
            })]);
        document["views"] = new JsonArray([.. Views.Select(view => view.DeepClone())]);
        var shared = new JsonArray([.. sharedClients]);
        if (shared.Count > 0) document["sharedClients"] = shared;
        document["types"] = new JsonObject(Types.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => KeyValuePair.Create(entry.Key, (JsonNode?)entry.Value.DeepClone())));
        var locations = new JsonObject();
        var map = JsonNode.Parse(sourceMap)!.AsObject();
        foreach (var section in new[] { "viewModels", "members" })
            foreach (var entry in map[section]?.AsArray() ?? [])
                if (entry?["location"] is JsonObject location)
                    locations[entry["id"]!.GetValue<string>()] = new JsonObject
                    {
                        ["file"] = location["file"]!.DeepClone(),
                        ["line"] = location["line"]!.DeepClone(),
                        ["column"] = location["column"]!.DeepClone(),
                    };
        document["sources"] = locations;
        return document.ToJsonString(JsonOptions) + "\n";
    }

    /// <summary>
    /// Writes the IR to <paramref name="directory"/>, only when its content changed, as the development source map is.
    /// The emitter writes the TypeScript from it, so an unwritable file is an error (RUNICBRIDGE001).
    /// </summary>
    internal static void Write(string directory, string content) =>
        GeneratedFile.WriteIfChanged(Path.Combine(directory, FileName), content);

    /// <summary>Removes the IR of an earlier generation, for a project that no longer generates Bridges.</summary>
    internal static void Remove(string directory)
    {
        try
        {
            File.Delete(Path.Combine(directory, FileName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
