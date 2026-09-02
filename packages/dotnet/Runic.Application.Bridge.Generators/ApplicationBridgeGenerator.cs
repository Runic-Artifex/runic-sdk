using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Runic.Application.Bridge.Generators;

/// <summary>Generates one closed, reflection-free Application Bridge contract.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class ApplicationBridgeGenerator : IIncrementalGenerator
{
    private static readonly HashSet<string> BuiltInErrors = new(StringComparer.Ordinal)
    {
        "CommandRejected",
        "OperationCancelled",
        "OperationFailed",
        "OperationTimedOut",
        "ProtocolDecodeError",
        "ProtocolVersionMismatch",
        "StaleRevision",
        "TransportClosed",
        "TransportUnavailable",
    };
    private static readonly DiagnosticDescriptor InvalidManifest = new(
        "RTKAB0001",
        "Invalid Application Bridge IR",
        "The Application Bridge IR is invalid: {0}",
        "Runic.Application.Bridge",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor UnsupportedSchema = new(
        "RTKAB0004",
        "Unsupported Application Bridge schema",
        "Schema '{0}' uses an unsupported construct at '{1}'",
        "Runic.Application.Bridge",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<AdditionalText?> ir = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith("bridge.ir.json", StringComparison.OrdinalIgnoreCase))
            .Collect()
            .Select(static (files, _) => files.OrderBy(static file => file.Path, StringComparer.Ordinal).FirstOrDefault());
        context.RegisterSourceOutput(ir, static (productionContext, input) => Emit(productionContext, input));
    }

    private static void Emit(SourceProductionContext context, AdditionalText? irFile)
    {
        if (irFile is null)
        {
            return;
        }

        SourceText? irText = irFile.GetText(context.CancellationToken);
        if (irText is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidManifest, Location.None, "the file could not be read"));
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(irText.ToString());
            (ContractModel model, List<SchemaModel> schemas) = ParseIr(document.RootElement);
            context.AddSource(
                $"{model.ContractName}.ApplicationBridge.g.cs",
                SourceText.From(Render(model, schemas), Encoding.UTF8));
        }
        catch (UnsupportedSchemaException exception)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                UnsupportedSchema,
                Location.None,
                exception.Schema,
                exception.Path));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidManifest, Location.None, Safe(exception.Message)));
        }
    }

    private static (ContractModel Contract, List<SchemaModel> Schemas) ParseIr(JsonElement root)
    {
        if (root.GetProperty("format").GetString() != "runic.application-bridge-ir" ||
            root.GetProperty("formatVersion").GetInt32() != 1)
        {
            throw new InvalidOperationException("unsupported Bridge IR format version");
        }
        JsonElement wire = root.GetProperty("wire");
        JsonElement protocol = wire.GetProperty("protocol");
        JsonElement csharp = root.GetProperty("csharp");
        var commands = wire.GetProperty("commands").EnumerateArray()
            .Select(static item => new CommandModel(
                item.GetProperty("name").GetString()!,
                item.GetProperty("receipt").GetString()!,
                item.GetProperty("startsOperation").GetBoolean(),
                item.GetProperty("cancellable").GetBoolean(),
                item.GetProperty("advancesRevision").GetBoolean()))
            .OrderBy(static item => item.Tag, StringComparer.Ordinal)
            .ToImmutableArray();
        JsonElement fingerprint = root.GetProperty("fingerprint");
        if (fingerprint.GetProperty("algorithm").GetString() != "sha256" ||
            fingerprint.GetProperty("scope").GetString() != "wire")
        {
            throw new InvalidOperationException("unsupported contract fingerprint declaration");
        }
        string contractFingerprint = fingerprint.GetProperty("value").GetString()!;
        if (contractFingerprint.Length != 64 || contractFingerprint.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidOperationException("invalid contract fingerprint");
        }
        string actualFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Canonicalize(wire)))).ToLowerInvariant();
        if (!string.Equals(contractFingerprint, actualFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the contract fingerprint does not match the canonical manifest body");
        }
        JsonProperty[] definitionProperties = wire.GetProperty("definitions").EnumerateObject()
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .ToArray();
        var entries = definitionProperties.Select(static property => SchemaEntry.Parse(property.Name)).ToImmutableArray();
        Dictionary<string, int> nameCounts = entries.GroupBy(static item => item.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        var typeNames = entries.ToDictionary(
            static item => item.Id,
            item => nameCounts[item.Name] == 1 ? Pascal(item.Name) : Pascal(item.Name) + Pascal(item.Kind),
            StringComparer.Ordinal);
        var definitions = definitionProperties.ToDictionary(
            static property => property.Name,
            static property => property.Value.Clone(),
            StringComparer.Ordinal);
        var schemas = new List<SchemaModel>();
        foreach (JsonProperty property in definitionProperties)
        {
            SchemaEntry entry = SchemaEntry.Parse(property.Name);
            if (property.Value.GetProperty("kind").GetString() == "object")
            {
                schemas.Add(ParseSchema(entry, property.Value, typeNames, definitions));
            }
        }
        var contract = new ContractModel(
            protocol.GetProperty("identity").GetString()!,
            protocol.GetProperty("version").GetInt32(),
            wire.TryGetProperty("initialize", out JsonElement initialize) ? initialize.GetString() : null,
            csharp.GetProperty("namespace").GetString()!,
            csharp.GetProperty("contractName").GetString()!,
            contractFingerprint,
            entries,
            commands);
        return (contract, schemas);
    }

    private static string Canonicalize(JsonElement root)
    {
        var source = new StringBuilder();
        WriteCanonicalJson(source, root, 0);
        source.Append('\n');
        return source.ToString();
    }

    private static void WriteCanonicalJson(StringBuilder source, JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                JsonProperty[] properties = element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToArray();
                source.Append('{');
                if (properties.Length > 0) source.Append('\n');
                for (int index = 0; index < properties.Length; index++)
                {
                    source.Append(' ', (depth + 1) * 2);
                    AppendJsonString(source, properties[index].Name);
                    source.Append(": ");
                    WriteCanonicalJson(source, properties[index].Value, depth + 1);
                    if (index < properties.Length - 1) source.Append(',');
                    source.Append('\n');
                }
                if (properties.Length > 0) source.Append(' ', depth * 2);
                source.Append('}');
                return;
            }
            case JsonValueKind.Array:
            {
                JsonElement[] values = element.EnumerateArray().ToArray();
                source.Append('[');
                if (values.Length > 0) source.Append('\n');
                for (int index = 0; index < values.Length; index++)
                {
                    source.Append(' ', (depth + 1) * 2);
                    WriteCanonicalJson(source, values[index], depth + 1);
                    if (index < values.Length - 1) source.Append(',');
                    source.Append('\n');
                }
                if (values.Length > 0) source.Append(' ', depth * 2);
                source.Append(']');
                return;
            }
            case JsonValueKind.String:
                AppendJsonString(source, element.GetString()!);
                return;
            default:
                source.Append(element.GetRawText());
                return;
        }
    }

    private static void AppendJsonString(StringBuilder source, string value)
    {
        source.Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '"': source.Append("\\\""); break;
                case '\\': source.Append("\\\\"); break;
                case '\b': source.Append("\\b"); break;
                case '\f': source.Append("\\f"); break;
                case '\n': source.Append("\\n"); break;
                case '\r': source.Append("\\r"); break;
                case '\t': source.Append("\\t"); break;
                default:
                    if (character < ' ')
                    {
                        source.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else source.Append(character);
                    break;
            }
        }
        source.Append('"');
    }

    private static SchemaModel ParseSchema(
        SchemaEntry entry,
        JsonElement root,
        Dictionary<string, string> typeNames,
        Dictionary<string, JsonElement> definitions)
    {
        if (root.GetProperty("kind").GetString() != "object")
        {
            throw new UnsupportedSchemaException(entry.Name, "$");
        }
        var nested = new List<ObjectModel>();
        ObjectModel rootObject = ParseObject(entry.Name, typeNames[entry.Id], root, nested, typeNames, definitions);
        return new SchemaModel(entry, rootObject, nested.ToImmutableArray());
    }

    private static ObjectModel ParseObject(
        string schema,
        string name,
        JsonElement element,
        List<ObjectModel> nested,
        Dictionary<string, string> typeNames,
        Dictionary<string, JsonElement> definitions)
    {
        var properties = new List<PropertyModel>();
        if (element.TryGetProperty("properties", out JsonElement propertyElement))
        {
            foreach (JsonProperty property in propertyElement.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
            {
                string nestedName = name + Pascal(property.Name.TrimStart('_'));
                JsonElement declaration = property.Value;
                TypeModel type = ParseType(schema, nestedName, declaration.GetProperty("type"), nested, typeNames, definitions);
                properties.Add(new PropertyModel(
                    property.Name,
                    Pascal(property.Name.TrimStart('_')),
                    type,
                    !declaration.GetProperty("optional").GetBoolean()));
            }
        }
        return new ObjectModel(name, properties.ToImmutableArray());
    }

    private static TypeModel ParseType(
        string schema,
        string name,
        JsonElement element,
        List<ObjectModel> nested,
        Dictionary<string, string> typeNames,
        Dictionary<string, JsonElement> definitions)
    {
        string kind = element.GetProperty("kind").GetString()!;
        Constraints constraints = ParseConstraints(element);
        switch (kind)
        {
            case "string" when constraints.Pattern == "^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$":
                return new("global::System.Guid", "guid", constraints, [], null, false);
            case "string": return new("string", "string", constraints, [], null, false);
            case "boolean": return new("bool", "boolean", constraints, [], null, false);
            case "integer": return new("long", "integer", constraints, [], null, false);
            case "number": return new("double", "number", constraints, [], null, false);
            case "null": return new("object", "null", constraints, ["null"], null, true);
            case "literal": return ParseLiteral(element, constraints);
            case "array":
            {
                TypeModel item = ParseType(schema, name + "Item", element.GetProperty("items"), nested, typeNames, definitions);
                return new($"global::System.Collections.Generic.IReadOnlyList<{item.DeclarationType}>", "array", constraints, [], item, false);
            }
            case "tuple":
            {
                var elements = element.GetProperty("elements").EnumerateArray()
                    .Select((item, index) => new TupleElementModel(
                        ParseType(schema, name + "Item" + (index + 1).ToString(CultureInfo.InvariantCulture), item.GetProperty("type"), nested, typeNames, definitions),
                        !item.GetProperty("optional").GetBoolean()))
                    .ToImmutableArray();
                TypeModel? rest = element.TryGetProperty("rest", out JsonElement restElement)
                    ? ParseType(schema, name + "RestItem", restElement, nested, typeNames, definitions)
                    : null;
                return new(name, "tuple", constraints, [], null, false, elements, rest, []);
            }
            case "object":
            {
                ObjectModel nestedObject = ParseObject(schema, name, element, nested, typeNames, definitions);
                nested.Add(nestedObject);
                return new(name, "object", constraints, [], null, false);
            }
            case "ref":
            {
                string id = element.GetProperty("name").GetString()!;
                if (!definitions.TryGetValue(id, out JsonElement target)) throw new UnsupportedSchemaException(schema, name + ".ref");
                if (id == "type:Uuid") return new("global::System.Guid", "guid", constraints, [], null, false);
                if (target.GetProperty("kind").GetString() == "object") return new(typeNames[id], "object", constraints, [], null, false);
                return ParseType(schema, name, target, nested, typeNames, definitions);
            }
            case "union": return ParseUnion(schema, name, element, nested, typeNames, definitions);
            case "record":
            {
                TypeModel value = ParseType(schema, name + "Value", element.GetProperty("values"), nested, typeNames, definitions);
                string? keyPattern = element.TryGetProperty("keyPattern", out JsonElement pattern) ? pattern.GetString() : null;
                return new(
                    $"global::System.Collections.Generic.IReadOnlyDictionary<string, {value.DeclarationType}>",
                    "record",
                    constraints with { Pattern = keyPattern },
                    [],
                    value,
                    false);
            }
            default: throw new UnsupportedSchemaException(schema, name + "." + kind);
        }
    }

    private static TypeModel ParseLiteral(JsonElement element, Constraints constraints)
    {
        JsonElement value = element.GetProperty("value");
        return value.ValueKind switch
        {
            JsonValueKind.String => new("string", "string", constraints, [value.GetRawText()], null, false),
            JsonValueKind.True or JsonValueKind.False => new("bool", "boolean", constraints, [value.GetRawText()], null, false),
            JsonValueKind.Number when value.TryGetInt64(out _) => new("long", "integer", constraints, [value.GetRawText()], null, false),
            JsonValueKind.Number => new("double", "number", constraints, [value.GetRawText()], null, false),
            JsonValueKind.Null => new("object", "null", constraints, ["null"], null, true),
            _ => throw new InvalidOperationException("invalid IR literal"),
        };
    }

    private static TypeModel ParseUnion(
        string schema,
        string name,
        JsonElement element,
        List<ObjectModel> nested,
        Dictionary<string, string> typeNames,
        Dictionary<string, JsonElement> definitions)
    {
        TypeModel[] members = element.GetProperty("members").EnumerateArray()
            .Select((member, index) => ParseType(schema, name + index.ToString(CultureInfo.InvariantCulture), member, nested, typeNames, definitions))
            .ToArray();
        TypeModel[] values = members.Where(static member => member.Kind != "null").ToArray();
        bool nullable = values.Length != members.Length;
        if (values.Length == 1) return values[0] with { Nullable = nullable || values[0].Nullable };
        if (values.Length > 0 && values.All(member => member.Kind == values[0].Kind && member.Literals.Length > 0))
        {
            return values[0] with {
                Literals = values.SelectMany(static member => member.Literals).ToImmutableArray(),
                Nullable = nullable,
            };
        }
        return new(name, "union", new(), [], null, nullable, [], null, values.ToImmutableArray());
    }

    private static Constraints ParseConstraints(JsonElement element)
    {
        if (!element.TryGetProperty("constraints", out JsonElement value)) return new();
        double? Number(string property) => value.TryGetProperty(property, out JsonElement item) ? item.GetDouble() : null;
        int? Integer(string property) => value.TryGetProperty(property, out JsonElement item) ? item.GetInt32() : null;
        return new(
            Number("minimum"),
            Number("maximum"),
            Number("exclusiveMinimum"),
            Number("exclusiveMaximum"),
            Number("multipleOf"),
            Integer("minLength"),
            Integer("maxLength"),
            value.TryGetProperty("pattern", out JsonElement pattern) ? pattern.GetString() : null,
            Integer("minItems"),
            Integer("maxItems"),
            value.TryGetProperty("uniqueItems", out JsonElement unique) && unique.GetBoolean());
    }

    private static string Render(ContractModel contract, IReadOnlyList<SchemaModel> schemas)
    {
        Dictionary<string, int> nameCounts = schemas.GroupBy(static item => item.Entry.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        string TypeName(SchemaModel schema) => nameCounts[schema.Entry.Name] == 1
            ? Pascal(schema.Entry.Name)
            : Pascal(schema.Entry.Name) + Pascal(schema.Entry.Kind);
        var typeNames = schemas.ToDictionary(
            static item => item.Entry.Kind + ":" + item.Entry.Name,
            TypeName,
            StringComparer.Ordinal);
        var source = new StringBuilder("// <auto-generated/>\n#nullable enable\n#pragma warning disable CS1591\n");
        source.Append("namespace ").Append(contract.Namespace).AppendLine(";").AppendLine();
        source.Append("public static class ").Append(contract.ContractName).AppendLine("BridgeContract")
            .AppendLine("{")
            .Append("    public const string ProtocolIdentity = \"").Append(Escape(contract.Protocol)).AppendLine("\";")
            .Append("    public const int ProtocolVersion = ").Append(contract.Version.ToString(CultureInfo.InvariantCulture)).AppendLine(";")
            .Append("    public const string Fingerprint = \"").Append(contract.ManifestFingerprint).AppendLine("\";")
            .AppendLine("}").AppendLine();
        foreach (SchemaModel schema in schemas)
        {
            RenderObject(source, schema.Root with { Name = TypeName(schema) });
            foreach (ObjectModel nested in schema.Nested.DistinctBy(static item => item.Name)) RenderObject(source, nested);
        }
        var objects = new List<ObjectModel>();
        foreach (SchemaModel schema in schemas)
        {
            objects.Add(schema.Root with { Name = TypeName(schema) });
            objects.AddRange(schema.Nested);
        }
        TypeModel[] complexTypes = objects
            .SelectMany(static item => item.Properties)
            .SelectMany(static property => ComplexTypes(property.Type))
            .DistinctBy(static item => item.CSharpType)
            .OrderBy(static item => item.CSharpType, StringComparer.Ordinal)
            .ToArray();
        SchemaModel[] errorSchemas = schemas
            .Where(static item => item.Entry.Kind == "error")
            .OrderBy(static item => item.Entry.Name, StringComparer.Ordinal)
            .ToArray();
        SchemaModel[] domainErrorSchemas = errorSchemas
            .Where(item => !BuiltInErrors.Contains(item.Entry.Name))
            .ToArray();
        foreach (TypeModel type in complexTypes)
        {
            if (type.Kind == "tuple") RenderTuple(source, type);
            else RenderUnion(source, type);
        }

        source.Append("public interface I").Append(contract.ContractName).AppendLine("BridgeHandler").AppendLine("{");
        foreach (CommandModel command in contract.Commands)
        {
            source.Append("    global::System.Threading.Tasks.ValueTask<")
                .Append(typeNames["receipt:" + command.Receipt]).Append("> ")
                .Append(Pascal(command.Tag)).AppendLine("Async(")
                .Append("        ").Append(typeNames["command:" + command.Tag]).AppendLine(" command,")
                .AppendLine("        global::Runic.Application.Bridge.BridgeCommandContext context,")
                .AppendLine("        global::System.Threading.CancellationToken cancellationToken);");
        }
        source.AppendLine("}").AppendLine();

        source.Append("public sealed class ").Append(contract.ContractName)
            .AppendLine("BridgeDispatcher : global::Runic.Application.Bridge.IApplicationBridgeDispatcher")
            .AppendLine("{")
            .Append("    private readonly I").Append(contract.ContractName).AppendLine("BridgeHandler _handler;")
            .Append("    public ").Append(contract.ContractName).Append("BridgeDispatcher(I")
            .Append(contract.ContractName).AppendLine("BridgeHandler handler) => _handler = handler ?? throw new global::System.ArgumentNullException(nameof(handler));")
            .Append("    public string ProtocolIdentity => \"").Append(Escape(contract.Protocol)).AppendLine("\";")
            .Append("    public int ProtocolVersion => ").Append(contract.Version.ToString(CultureInfo.InvariantCulture)).AppendLine(";")
            .Append("    public string ManifestFingerprint => \"").Append(contract.ManifestFingerprint).AppendLine("\";")
            .AppendLine("    public async global::System.Threading.Tasks.ValueTask<global::Runic.Application.Bridge.BridgeDispatchResult> DispatchAsync(")
            .AppendLine("        global::System.Text.Json.JsonElement command,")
            .AppendLine("        global::Runic.Application.Bridge.BridgeCommandContext context,")
            .AppendLine("        global::System.Threading.CancellationToken cancellationToken)")
            .AppendLine("    {")
            .AppendLine("        if (!command.TryGetProperty(\"_tag\", out global::System.Text.Json.JsonElement tagElement))")
            .AppendLine("            throw new global::System.Text.Json.JsonException(\"The command tag is missing.\");");
        if (contract.InitializeTag is not null)
        {
            source.Append("        if (context.IsInitialization && !global::System.String.Equals(tagElement.GetString(), \"")
                .Append(Escape(contract.InitializeTag)).AppendLine("\", global::System.StringComparison.Ordinal))")
                .AppendLine("            throw new global::System.Text.Json.JsonException(\"The initialization envelope contains the wrong command.\");");
        }
        source.AppendLine("        return tagElement.GetString() switch")
            .AppendLine("        {");
        foreach (CommandModel command in contract.Commands)
        {
            string commandType = typeNames["command:" + command.Tag];
            string receiptType = typeNames["receipt:" + command.Receipt];
            source.Append("            \"").Append(Escape(command.Tag)).Append("\" => await Dispatch")
                .Append(Pascal(command.Tag)).AppendLine("Async(command, context, cancellationToken).ConfigureAwait(false),");
        }
        source.AppendLine("            _ => throw new global::System.Text.Json.JsonException(\"The command tag is not declared by the contract.\"),")
            .AppendLine("        };")
            .AppendLine("    }")
            .AppendLine("    public global::System.Text.Json.JsonElement ValidateError(global::System.Text.Json.JsonElement error)")
            .AppendLine("    {")
            .AppendLine("        if (!error.TryGetProperty(\"_tag\", out global::System.Text.Json.JsonElement tagElement)")
            .AppendLine("            || tagElement.ValueKind != global::System.Text.Json.JsonValueKind.String)")
            .AppendLine("            throw new global::System.Text.Json.JsonException(\"The error tag is missing.\");")
            .AppendLine("        return tagElement.GetString() switch")
            .AppendLine("        {");
        foreach (SchemaModel errorSchema in errorSchemas)
        {
            string errorType = TypeName(errorSchema);
            source.Append("            \"").Append(Escape(errorSchema.Entry.Name)).Append("\" => ")
                .Append(contract.ContractName).Append("BridgeContractCodec.Encode").Append(errorType)
                .Append('(').Append(contract.ContractName).Append("BridgeContractCodec.Decode").Append(errorType)
                .AppendLine("(error)),");
        }
        source.AppendLine("            _ => throw new global::System.Text.Json.JsonException(\"The error tag is not declared by the contract.\"),")
            .AppendLine("        };")
            .AppendLine("    }");
        foreach (CommandModel command in contract.Commands)
        {
            string commandType = typeNames["command:" + command.Tag];
            string receiptType = typeNames["receipt:" + command.Receipt];
            source.Append("    private async global::System.Threading.Tasks.ValueTask<global::Runic.Application.Bridge.BridgeDispatchResult> Dispatch")
                .Append(Pascal(command.Tag)).AppendLine("Async(")
                .AppendLine("        global::System.Text.Json.JsonElement payload,")
                .AppendLine("        global::Runic.Application.Bridge.BridgeCommandContext context,")
                .AppendLine("        global::System.Threading.CancellationToken cancellationToken)")
                .AppendLine("    {")
                .Append("        ").Append(commandType).Append(" command = ")
                .Append(contract.ContractName).Append("BridgeContractCodec.Decode").Append(commandType)
                .AppendLine("(payload);")
                .Append("        ").Append(receiptType).Append(" receipt = await _handler.")
                .Append(Pascal(command.Tag)).AppendLine("Async(command, context, cancellationToken).ConfigureAwait(false);")
                .Append("        return new(").Append(contract.ContractName).Append("BridgeContractCodec.Encode")
                .Append(receiptType).Append("(receipt)")
                .Append(", ").Append(command.AdvancesRevision ? "true" : "false");
            if (command.StartsOperation)
            {
                source.Append(", new global::Runic.Application.Bridge.BridgeOperationId(receipt.OperationId), ")
                    .Append(command.Cancellable ? "true" : "false");
            }
            source.AppendLine(");").AppendLine("    }");
        }
        source.AppendLine("}").AppendLine();

        source.Append("public static class ").Append(contract.ContractName).AppendLine("BridgeErrors").AppendLine("{");
        foreach (SchemaModel errorSchema in domainErrorSchemas)
        {
            string errorType = TypeName(errorSchema);
            source.Append("    public static global::Runic.Application.Bridge.BridgeCommandFailureException ")
                .Append(Pascal(errorSchema.Entry.Name)).Append('(').Append(errorType).AppendLine(" value) =>")
                .Append("        new(").Append(contract.ContractName).Append("BridgeContractCodec.Encode")
                .Append(errorType).AppendLine("(value));");
        }
        source.AppendLine("}").AppendLine();

        source.Append("public static class ").Append(contract.ContractName).AppendLine("BridgeEvents").AppendLine("{");
        foreach (SchemaModel eventSchema in schemas.Where(static item => item.Entry.Kind == "event"))
        {
            string eventType = TypeName(eventSchema);
            source.Append("    public static global::System.Threading.Tasks.ValueTask Publish")
                .Append(Pascal(eventSchema.Entry.Name)).AppendLine("Async(")
                .AppendLine("        this global::Runic.Application.Bridge.IBridgeEventPublisher publisher,")
                .Append("        ").Append(eventType).AppendLine(" value,")
                .AppendLine("        bool advancesRevision = false,")
                .AppendLine("        global::Runic.Application.Bridge.BridgeOperationId? operationId = null,")
                .AppendLine("        global::System.Threading.CancellationToken cancellationToken = default) =>")
                .Append("        publisher.PublishAsync(new global::Runic.Application.Bridge.BridgeEventPayload(")
                .Append(contract.ContractName).Append("BridgeContractCodec.Encode").Append(eventType)
                .AppendLine("(value), advancesRevision, operationId), cancellationToken);");
        }
        source.AppendLine("}").AppendLine();

        source.Append("internal static class ").Append(contract.ContractName).AppendLine("BridgeContractCodec").AppendLine("{");
        foreach (ObjectModel model in objects.DistinctBy(static item => item.Name))
        {
            RenderDecode(source, model);
            RenderEncode(source, model);
        }
        foreach (TypeModel type in complexTypes)
        {
            if (type.Kind == "tuple") RenderTupleCodec(source, type);
            else RenderUnionCodec(source, type);
        }
        RenderValidationHelpers(source);
        source.AppendLine("}");
        return source.ToString();
    }

    private static IEnumerable<TypeModel> ComplexTypes(TypeModel type)
    {
        if (type.Kind is "tuple" or "union") yield return type;
        if (type.Item is not null)
        {
            foreach (TypeModel nested in ComplexTypes(type.Item)) yield return nested;
        }
        foreach (TupleElementModel element in type.Elements.IsDefault ? ImmutableArray<TupleElementModel>.Empty : type.Elements)
        {
            foreach (TypeModel nested in ComplexTypes(element.Type)) yield return nested;
        }
        if (type.Rest is not null)
        {
            foreach (TypeModel nested in ComplexTypes(type.Rest)) yield return nested;
        }
        foreach (TypeModel member in type.Members.IsDefault ? ImmutableArray<TypeModel>.Empty : type.Members)
        {
            foreach (TypeModel nested in ComplexTypes(member)) yield return nested;
        }
    }

    private static void RenderTuple(StringBuilder source, TypeModel type)
    {
        source.Append("public sealed record ").Append(type.CSharpType).AppendLine().AppendLine("{");
        for (int index = 0; index < type.Elements.Length; index++)
        {
            TupleElementModel element = type.Elements[index];
            string declaration = element.Required
                ? element.Type.DeclarationType
                : $"global::Runic.Application.Bridge.BridgeOptional<{element.Type.DeclarationType}>";
            source.Append("    public ").Append(element.Required ? "required " : string.Empty)
                .Append(declaration).Append(" Item").Append((index + 1).ToString(CultureInfo.InvariantCulture))
                .AppendLine(" { get; init; }");
        }
        if (type.Rest is not null)
        {
            source.Append("    public global::System.Collections.Generic.IReadOnlyList<")
                .Append(type.Rest.DeclarationType).Append("> Rest { get; init; } = global::System.Array.Empty<")
                .Append(type.Rest.DeclarationType).AppendLine(">();");
        }
        source.AppendLine("}").AppendLine();
    }

    private static void RenderUnion(StringBuilder source, TypeModel type)
    {
        source.Append("public sealed record ").Append(type.CSharpType).AppendLine().AppendLine("{")
            .Append("    private ").Append(type.CSharpType).AppendLine("(int @case, object value) { Case = @case; Value = value; }")
            .AppendLine("    public int Case { get; }")
            .AppendLine("    public object Value { get; }");
        Dictionary<string, int> counts = type.Members.GroupBy(static member => member.DeclarationType, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        for (int index = 0; index < type.Members.Length; index++)
        {
            TypeModel member = type.Members[index];
            string ordinal = (index + 1).ToString(CultureInfo.InvariantCulture);
            source.Append("    public static ").Append(type.CSharpType).Append(" FromCase").Append(ordinal)
                .Append('(').Append(member.DeclarationType).Append(" value) => new(")
                .Append(index.ToString(CultureInfo.InvariantCulture)).AppendLine(", value);");
            if (counts[member.DeclarationType] == 1)
            {
                source.Append("    public static implicit operator ").Append(type.CSharpType).Append('(')
                    .Append(member.DeclarationType).Append(" value) => FromCase").Append(ordinal).AppendLine("(value);");
            }
        }
        source.AppendLine("}").AppendLine();
    }

    private static void RenderDecode(StringBuilder source, ObjectModel model)
    {
        source.Append("    internal static ").Append(model.Name).Append(" Decode").Append(model.Name)
            .AppendLine("(global::System.Text.Json.JsonElement element)")
            .AppendLine("    {")
            .AppendLine("        if (element.ValueKind != global::System.Text.Json.JsonValueKind.Object)")
            .AppendLine("            throw new global::System.Text.Json.JsonException(\"The contract value must be an object.\");")
            .AppendLine("        var seen = new global::System.Collections.Generic.HashSet<string>(global::System.StringComparer.Ordinal);")
            .AppendLine("        foreach (global::System.Text.Json.JsonProperty property in element.EnumerateObject())")
            .AppendLine("        {")
            .AppendLine("            if (!seen.Add(property.Name)) throw new global::System.Text.Json.JsonException(\"A contract property was duplicated.\");")
            .AppendLine("            switch (property.Name)")
            .AppendLine("            {");
        foreach (PropertyModel property in model.Properties)
        {
            source.Append("                case \"").Append(Escape(property.JsonName)).AppendLine("\": break;");
        }
        source.AppendLine("                default: throw new global::System.Text.Json.JsonException(\"The contract value contains an unknown property.\");")
            .AppendLine("            }")
            .AppendLine("        }")
            .Append("        return new ").Append(model.Name).AppendLine().AppendLine("        {");
        foreach (PropertyModel property in model.Properties)
        {
            string element = property.Required
                ? $"element.GetProperty(\"{Escape(property.JsonName)}\")"
                : $"optional{property.Name}";
            source.Append("            ").Append(property.Name).Append(" = ");
            if (!property.Required)
            {
                source.Append("element.TryGetProperty(\"").Append(Escape(property.JsonName)).Append("\", out global::System.Text.Json.JsonElement ")
                    .Append(element).Append(") ? new global::Runic.Application.Bridge.BridgeOptional<")
                    .Append(property.Type.DeclarationType).Append(">(");
            }
            source.Append(DecodeExpression(property.Type, element));
            if (!property.Required) source.Append(") : default");
            source.AppendLine(",");
        }
        source.AppendLine("        };").AppendLine("    }");
    }

    private static string DecodeExpression(TypeModel type, string element)
    {
        if (type.Nullable)
        {
            return element + ".ValueKind == global::System.Text.Json.JsonValueKind.Null ? null : " +
                DecodeExpression(type with { Nullable = false }, element);
        }
        if (type.Kind == "string")
        {
            return "ValidateString(" + element + ".GetString() ?? throw new global::System.Text.Json.JsonException(\"A required string was null.\"), " +
                NullableInteger(type.Constraints.MinLength) + ", " + NullableInteger(type.Constraints.MaxLength) + ", " +
                NullableString(type.Constraints.Pattern) + ", " + LiteralArray(type.Literals, "string") + ")";
        }
        if (type.Kind == "boolean") return "ValidateBoolean(" + element + ".GetBoolean(), " + LiteralArray(type.Literals, "bool") + ")";
        if (type.Kind == "integer")
        {
            return "ValidateInteger(" + element + ".GetInt64(), " + Number(type.Constraints.Minimum) + ", " + Number(type.Constraints.Maximum) + ", " +
                Number(type.Constraints.ExclusiveMinimum) + ", " + Number(type.Constraints.ExclusiveMaximum) + ", " + Number(type.Constraints.MultipleOf) + ", " +
                LiteralArray(type.Literals, "long") + ")";
        }
        if (type.Kind == "number")
        {
            return "ValidateNumber(" + element + ".GetDouble(), " + Number(type.Constraints.Minimum) + ", " + Number(type.Constraints.Maximum) + ", " +
                Number(type.Constraints.ExclusiveMinimum) + ", " + Number(type.Constraints.ExclusiveMaximum) + ", " + Number(type.Constraints.MultipleOf) + ", " +
                LiteralArray(type.Literals, "double") + ")";
        }
        if (type.Kind == "guid") return element + ".GetGuid()";
        if (type.Kind == "array")
        {
            string item = DecodeExpression(type.Item!, "item");
            return "global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select(ValidateJsonArray(" + element +
                ", " + NullableInteger(type.Constraints.MinItems) + ", " + NullableInteger(type.Constraints.MaxItems) + ", " +
                (type.Constraints.UniqueItems ? "true" : "false") + ").EnumerateArray(), static item => " + item + "))";
        }
        if (type.Kind == "record")
        {
            return "DecodeRecord(" + element + ", " + NullableString(type.Constraints.Pattern) + ", static item => " +
                DecodeExpression(type.Item!, "item") + ")";
        }
        if (type.Kind == "null") return element + ".ValueKind == global::System.Text.Json.JsonValueKind.Null ? null : throw new global::System.Text.Json.JsonException(\"Expected null.\")";
        return "Decode" + type.CSharpType + "(" + element + ")";
    }

    private static void RenderEncode(StringBuilder source, ObjectModel model)
    {
        source.Append("    internal static global::System.Text.Json.JsonElement Encode").Append(model.Name)
            .Append('(').Append(model.Name).AppendLine(" value)")
            .AppendLine("    {")
            .AppendLine("        var buffer = new global::System.Buffers.ArrayBufferWriter<byte>();")
            .AppendLine("        using (var writer = new global::System.Text.Json.Utf8JsonWriter(buffer))")
            .AppendLine("        {")
            .Append("            Write").Append(model.Name).AppendLine("(writer, value);")
            .AppendLine("        }")
            .AppendLine("        using global::System.Text.Json.JsonDocument document = global::System.Text.Json.JsonDocument.Parse(buffer.WrittenMemory);")
            .AppendLine("        global::System.Text.Json.JsonElement encoded = document.RootElement.Clone();")
            .Append("        _ = Decode").Append(model.Name).AppendLine("(encoded);")
            .AppendLine("        return encoded;")
            .AppendLine("    }")
            .Append("    private static void Write").Append(model.Name)
            .Append("(global::System.Text.Json.Utf8JsonWriter writer, ").Append(model.Name).AppendLine(" value)")
            .AppendLine("    {")
            .AppendLine("        writer.WriteStartObject();");
        foreach (PropertyModel property in model.Properties)
        {
            bool optional = !property.Required;
            if (optional)
            {
                source.Append("        if (value.").Append(property.Name).AppendLine(".HasValue)").AppendLine("        {");
            }
            RenderWriteProperty(source, property, optional ? "            " : "        ");
            if (optional) source.AppendLine("        }");
        }
        source.AppendLine("        writer.WriteEndObject();").AppendLine("    }");
    }

    private static void RenderTupleCodec(StringBuilder source, TypeModel type)
    {
        int required = type.Elements.Count(static item => item.Required);
        string maximum = type.Rest is null
            ? type.Elements.Length.ToString(CultureInfo.InvariantCulture)
            : "null";
        source.Append("    private static ").Append(type.CSharpType).Append(" Decode").Append(type.CSharpType)
            .AppendLine("(global::System.Text.Json.JsonElement element)")
            .AppendLine("    {")
            .Append("        global::System.Text.Json.JsonElement[] items = ValidateTuple(element, ")
            .Append(required.ToString(CultureInfo.InvariantCulture)).Append(", ").Append(maximum).Append(", ")
            .Append(NullableInteger(type.Constraints.MinItems)).Append(", ").Append(NullableInteger(type.Constraints.MaxItems))
            .Append(", ").Append(type.Constraints.UniqueItems ? "true" : "false").AppendLine(");")
            .Append("        return new ").Append(type.CSharpType).AppendLine().AppendLine("        {");
        for (int index = 0; index < type.Elements.Length; index++)
        {
            TupleElementModel item = type.Elements[index];
            string ordinal = (index + 1).ToString(CultureInfo.InvariantCulture);
            source.Append("            Item").Append(ordinal).Append(" = ");
            if (item.Required)
            {
                source.Append(DecodeExpression(item.Type, $"items[{index.ToString(CultureInfo.InvariantCulture)}]"));
            }
            else
            {
                source.Append("items.Length > ").Append(index.ToString(CultureInfo.InvariantCulture))
                    .Append(" ? new global::Runic.Application.Bridge.BridgeOptional<")
                    .Append(item.Type.DeclarationType).Append(">(")
                    .Append(DecodeExpression(item.Type, $"items[{index.ToString(CultureInfo.InvariantCulture)}]"))
                    .Append(") : default");
            }
            source.AppendLine(",");
        }
        if (type.Rest is not null)
        {
            source.Append("            Rest = global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select(global::System.Linq.Enumerable.Skip(items, ")
                .Append(type.Elements.Length.ToString(CultureInfo.InvariantCulture)).Append("), static item => ")
                .Append(DecodeExpression(type.Rest, "item")).AppendLine(")),");
        }
        source.AppendLine("        };").AppendLine("    }")
            .Append("    private static void Write").Append(type.CSharpType).Append("(global::System.Text.Json.Utf8JsonWriter writer, ")
            .Append(type.CSharpType).AppendLine(" value)")
            .AppendLine("    {")
            .AppendLine("        var comparable = new global::System.Collections.Generic.List<object?>();");
        for (int index = 0; index < type.Elements.Length; index++)
        {
            TupleElementModel item = type.Elements[index];
            string property = "value.Item" + (index + 1).ToString(CultureInfo.InvariantCulture);
            if (item.Required) source.Append("        comparable.Add(").Append(property).AppendLine(");");
            else source.Append("        if (").Append(property).Append(".HasValue) comparable.Add(").Append(property).AppendLine(".Value);");
        }
        if (type.Rest is not null) source.AppendLine("        foreach (object? item in value.Rest) comparable.Add(item);");
        source.Append("        ValidateTupleValues(comparable, ").Append(required.ToString(CultureInfo.InvariantCulture))
            .Append(", ").Append(maximum).Append(", ").Append(NullableInteger(type.Constraints.MinItems)).Append(", ")
            .Append(NullableInteger(type.Constraints.MaxItems)).Append(", ").Append(type.Constraints.UniqueItems ? "true" : "false").AppendLine(");")
            .AppendLine("        writer.WriteStartArray();");
        for (int index = 0; index < type.Elements.Length; index++)
        {
            TupleElementModel item = type.Elements[index];
            string property = "value.Item" + (index + 1).ToString(CultureInfo.InvariantCulture);
            if (!item.Required) source.Append("        if (").Append(property).AppendLine(".HasValue)").AppendLine("        {");
            RenderWriteValue(source, item.Type, item.Required ? property : property + ".Value!", item.Required ? "        " : "            ");
            if (!item.Required) source.AppendLine("        }");
        }
        if (type.Rest is not null)
        {
            source.Append("        foreach (").Append(type.Rest.DeclarationType).AppendLine(" item in value.Rest)")
                .AppendLine("        {");
            RenderWriteValue(source, type.Rest, "item", "            ");
            source.AppendLine("        }");
        }
        source.AppendLine("        writer.WriteEndArray();").AppendLine("    }");
    }

    private static void RenderUnionCodec(StringBuilder source, TypeModel type)
    {
        source.Append("    private static ").Append(type.CSharpType).Append(" Decode").Append(type.CSharpType)
            .AppendLine("(global::System.Text.Json.JsonElement element)")
            .AppendLine("    {");
        for (int index = 0; index < type.Members.Length; index++)
        {
            source.AppendLine("        try")
                .AppendLine("        {")
                .Append("            return ").Append(type.CSharpType).Append(".FromCase")
                .Append((index + 1).ToString(CultureInfo.InvariantCulture)).Append('(')
                .Append(DecodeExpression(type.Members[index], "element")).AppendLine(");")
                .AppendLine("        }")
                .AppendLine("        catch (global::System.Exception exception) when (IsUnionMismatch(exception)) { }");
        }
        source.AppendLine("        throw new global::System.Text.Json.JsonException(\"The value did not match any declared union member.\");")
            .AppendLine("    }")
            .Append("    private static void Write").Append(type.CSharpType).Append("(global::System.Text.Json.Utf8JsonWriter writer, ")
            .Append(type.CSharpType).AppendLine(" value)")
            .AppendLine("    {")
            .AppendLine("        switch (value.Case)")
            .AppendLine("        {");
        for (int index = 0; index < type.Members.Length; index++)
        {
            TypeModel member = type.Members[index];
            source.Append("            case ").Append(index.ToString(CultureInfo.InvariantCulture)).AppendLine(":");
            RenderWriteValue(source, member, $"({member.DeclarationType})value.Value", "                ");
            source.AppendLine("                return;");
        }
        source.AppendLine("            default: throw new global::System.Text.Json.JsonException(\"The union case is invalid.\");")
            .AppendLine("        }")
            .AppendLine("    }");
    }

    private static void RenderWriteProperty(StringBuilder source, PropertyModel property, string indent)
    {
        TypeModel type = property.Type;
        string access = "value." + property.Name;
        string unwrapped = property.Required ? access : access + ".Value!";
        if (type.Nullable)
        {
            source.Append(indent).Append("writer.WritePropertyName(\"").Append(Escape(property.JsonName)).AppendLine("\");")
                .Append(indent).Append("if (").Append(unwrapped).AppendLine(" is null) writer.WriteNullValue();")
                .Append(indent).AppendLine("else")
                .Append(indent).AppendLine("{");
            RenderWriteValue(source, type with { Nullable = false }, unwrapped + "!", indent + "    ");
            source.Append(indent).AppendLine("}");
            return;
        }
        if (type.Kind == "string")
        {
            source.Append(indent).Append("writer.WriteString(\"").Append(Escape(property.JsonName)).Append("\", ")
                .Append(ValidateExpression(type, unwrapped)).AppendLine(");");
        }
        else if (type.Kind == "boolean")
        {
            source.Append(indent).Append("writer.WriteBoolean(\"").Append(Escape(property.JsonName)).Append("\", ")
                .Append(ValidateExpression(type, unwrapped)).AppendLine(");");
        }
        else if (type.Kind is "integer" or "number")
        {
            source.Append(indent).Append("writer.WriteNumber(\"").Append(Escape(property.JsonName)).Append("\", ")
                .Append(ValidateExpression(type, unwrapped)).AppendLine(");");
        }
        else if (type.Kind == "guid")
        {
            source.Append(indent).Append("writer.WriteString(\"").Append(Escape(property.JsonName)).Append("\", ").Append(unwrapped).AppendLine(");");
        }
        else if (type.Kind == "array")
        {
            source.Append(indent).Append("writer.WritePropertyName(\"").Append(Escape(property.JsonName)).AppendLine("\");")
                .Append(indent).AppendLine("writer.WriteStartArray();")
                .Append(indent).Append("foreach (").Append(type.Item!.DeclarationType).Append(" item in ")
                .Append(ValidateArrayExpression(type, unwrapped)).AppendLine(")")
                .Append(indent).AppendLine("{");
            RenderWriteValue(source, type.Item!, "item", indent + "    ");
            source.Append(indent).AppendLine("}").Append(indent).AppendLine("writer.WriteEndArray();");
        }
        else if (type.Kind == "record")
        {
            source.Append(indent).Append("writer.WritePropertyName(\"").Append(Escape(property.JsonName)).AppendLine("\");")
                .Append(indent).AppendLine("writer.WriteStartObject();")
                .Append(indent).Append("foreach (global::System.Collections.Generic.KeyValuePair<string, ")
                .Append(type.Item!.DeclarationType).Append("> item in ValidateRecord(").Append(unwrapped)
                .Append(", ").Append(NullableString(type.Constraints.Pattern)).AppendLine("))")
                .Append(indent).AppendLine("{")
                .Append(indent).AppendLine("    writer.WritePropertyName(item.Key);");
            RenderWriteValue(source, type.Item!, "item.Value", indent + "    ");
            source.Append(indent).AppendLine("}").Append(indent).AppendLine("writer.WriteEndObject();");
        }
        else if (type.Kind == "null")
        {
            source.Append(indent).Append("writer.WriteNull(\"").Append(Escape(property.JsonName)).AppendLine("\");");
        }
        else
        {
            source.Append(indent).Append("writer.WritePropertyName(\"").Append(Escape(property.JsonName)).AppendLine("\");")
                .Append(indent).Append("Write").Append(type.CSharpType).Append("(writer, ").Append(unwrapped).AppendLine(");");
        }
    }

    private static void RenderWriteValue(StringBuilder source, TypeModel type, string access, string indent)
    {
        if (type.Nullable)
        {
            source.Append(indent).Append("if (").Append(access).AppendLine(" is null) writer.WriteNullValue();")
                .Append(indent).AppendLine("else")
                .Append(indent).AppendLine("{");
            RenderWriteValue(source, type with { Nullable = false }, access + "!", indent + "    ");
            source.Append(indent).AppendLine("}");
            return;
        }
        if (type.Kind == "string" || type.Kind == "guid") source.Append(indent).Append("writer.WriteStringValue(").Append(ValidateExpression(type, access)).AppendLine(");");
        else if (type.Kind == "boolean") source.Append(indent).Append("writer.WriteBooleanValue(").Append(ValidateExpression(type, access)).AppendLine(");");
        else if (type.Kind is "integer" or "number") source.Append(indent).Append("writer.WriteNumberValue(").Append(ValidateExpression(type, access)).AppendLine(");");
        else if (type.Kind == "null") source.Append(indent).AppendLine("writer.WriteNullValue();");
        else if (type.Kind == "array")
        {
            source.Append(indent).AppendLine("writer.WriteStartArray();")
                .Append(indent).Append("foreach (").Append(type.Item!.DeclarationType).Append(" item in ")
                .Append(ValidateArrayExpression(type, access)).AppendLine(")")
                .Append(indent).AppendLine("{");
            RenderWriteValue(source, type.Item!, "item", indent + "    ");
            source.Append(indent).AppendLine("}").Append(indent).AppendLine("writer.WriteEndArray();");
        }
        else if (type.Kind == "record")
        {
            source.Append(indent).AppendLine("writer.WriteStartObject();")
                .Append(indent).Append("foreach (global::System.Collections.Generic.KeyValuePair<string, ")
                .Append(type.Item!.DeclarationType).Append("> item in ValidateRecord(").Append(access)
                .Append(", ").Append(NullableString(type.Constraints.Pattern)).AppendLine("))")
                .Append(indent).AppendLine("{")
                .Append(indent).AppendLine("    writer.WritePropertyName(item.Key);");
            RenderWriteValue(source, type.Item!, "item.Value", indent + "    ");
            source.Append(indent).AppendLine("}").Append(indent).AppendLine("writer.WriteEndObject();");
        }
        else source.Append(indent).Append("Write").Append(type.CSharpType).Append("(writer, ").Append(access).AppendLine(");");
    }

    private static string ValidateExpression(TypeModel type, string access) => type.Kind switch
    {
        "string" => "ValidateString(" + access + ", " + NullableInteger(type.Constraints.MinLength) + ", " +
            NullableInteger(type.Constraints.MaxLength) + ", " + NullableString(type.Constraints.Pattern) + ", " +
            LiteralArray(type.Literals, "string") + ")",
        "boolean" => "ValidateBoolean(" + access + ", " + LiteralArray(type.Literals, "bool") + ")",
        "integer" => "ValidateInteger(" + access + ", " + Number(type.Constraints.Minimum) + ", " + Number(type.Constraints.Maximum) + ", " +
            Number(type.Constraints.ExclusiveMinimum) + ", " + Number(type.Constraints.ExclusiveMaximum) + ", " + Number(type.Constraints.MultipleOf) + ", " +
            LiteralArray(type.Literals, "long") + ")",
        "number" => "ValidateNumber(" + access + ", " + Number(type.Constraints.Minimum) + ", " + Number(type.Constraints.Maximum) + ", " +
            Number(type.Constraints.ExclusiveMinimum) + ", " + Number(type.Constraints.ExclusiveMaximum) + ", " + Number(type.Constraints.MultipleOf) + ", " +
            LiteralArray(type.Literals, "double") + ")",
        _ => access,
    };

    private static string ValidateArrayExpression(TypeModel type, string access) =>
        "ValidateCollection(" + access + ", " + NullableInteger(type.Constraints.MinItems) + ", " +
        NullableInteger(type.Constraints.MaxItems) + ", " + (type.Constraints.UniqueItems ? "true" : "false") + ")";

    private static string NullableInteger(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";
    private static string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "null";
    private static string NullableString(string? value) => value is null ? "null" : "\"" + Escape(value) + "\"";
    private static string LiteralArray(ImmutableArray<string> values, string type) => values.Length == 0
        ? "null"
        : "new " + type + "[] { " + string.Join(", ", values) + " }";

    private static void RenderValidationHelpers(StringBuilder source)
    {
        source.AppendLine(
            """
                private static string ValidateString(string value, int? minimum, int? maximum, string? pattern, string[]? allowed)
                {
                    if ((minimum.HasValue && value.Length < minimum.Value) ||
                        (maximum.HasValue && value.Length > maximum.Value) ||
                        (pattern is not null && !global::System.Text.RegularExpressions.Regex.IsMatch(value, pattern, global::System.Text.RegularExpressions.RegexOptions.CultureInvariant | global::System.Text.RegularExpressions.RegexOptions.ECMAScript)) ||
                        (allowed is not null && !global::System.Array.Exists(allowed, item => global::System.StringComparer.Ordinal.Equals(item, value))))
                        throw new global::System.Text.Json.JsonException("A string violated its Application Bridge contract.");
                    return value;
                }
                private static bool ValidateBoolean(bool value, bool[]? allowed)
                {
                    if (allowed is not null && !global::System.Array.Exists(allowed, item => item == value))
                        throw new global::System.Text.Json.JsonException("A boolean violated its Application Bridge contract.");
                    return value;
                }
                private static long ValidateInteger(long value, double? minimum, double? maximum, double? exclusiveMinimum, double? exclusiveMaximum, double? multipleOf, long[]? allowed)
                {
                    ValidateNumber(value, minimum, maximum, exclusiveMinimum, exclusiveMaximum, multipleOf, null);
                    if (allowed is not null && !global::System.Array.Exists(allowed, item => item == value))
                        throw new global::System.Text.Json.JsonException("An integer violated its Application Bridge contract.");
                    return value;
                }
                private static double ValidateNumber(double value, double? minimum, double? maximum, double? exclusiveMinimum, double? exclusiveMaximum, double? multipleOf, double[]? allowed)
                {
                    if (!double.IsFinite(value) ||
                        (minimum.HasValue && value < minimum.Value) || (maximum.HasValue && value > maximum.Value) ||
                        (exclusiveMinimum.HasValue && value <= exclusiveMinimum.Value) || (exclusiveMaximum.HasValue && value >= exclusiveMaximum.Value) ||
                        (multipleOf.HasValue && global::System.Math.Abs(value / multipleOf.Value - global::System.Math.Round(value / multipleOf.Value)) > 1e-12) ||
                        (allowed is not null && !global::System.Array.Exists(allowed, item => item.Equals(value))))
                        throw new global::System.Text.Json.JsonException("A number violated its Application Bridge contract.");
                    return value;
                }
                private static T[] ValidateArray<T>(T[] value, int? minimum, int? maximum, bool unique)
                {
                    ValidateCollection(value, minimum, maximum, unique);
                    return value;
                }
                private static global::System.Collections.Generic.IReadOnlyList<T> ValidateCollection<T>(global::System.Collections.Generic.IReadOnlyList<T> value, int? minimum, int? maximum, bool unique)
                {
                    if ((minimum.HasValue && value.Count < minimum.Value) || (maximum.HasValue && value.Count > maximum.Value) ||
                        (unique && new global::System.Collections.Generic.HashSet<T>(value).Count != value.Count))
                        throw new global::System.Text.Json.JsonException("A collection violated its Application Bridge contract.");
                    return value;
                }
                private static global::System.Text.Json.JsonElement[] ValidateTuple(global::System.Text.Json.JsonElement element, int structuralMinimum, int? structuralMaximum, int? minimum, int? maximum, bool unique)
                {
                    if (element.ValueKind != global::System.Text.Json.JsonValueKind.Array)
                        throw new global::System.Text.Json.JsonException("A tuple must be an array.");
                    global::System.Text.Json.JsonElement[] values = global::System.Linq.Enumerable.ToArray(element.EnumerateArray());
                    if (values.Length < structuralMinimum || (structuralMaximum.HasValue && values.Length > structuralMaximum.Value) ||
                        (minimum.HasValue && values.Length < minimum.Value) || (maximum.HasValue && values.Length > maximum.Value) ||
                        (unique && new global::System.Collections.Generic.HashSet<string>(global::System.Linq.Enumerable.Select(values, static item => CanonicalJson(item)), global::System.StringComparer.Ordinal).Count != values.Length))
                        throw new global::System.Text.Json.JsonException("A tuple violated its Application Bridge contract.");
                    return values;
                }
                private static global::System.Text.Json.JsonElement ValidateJsonArray(global::System.Text.Json.JsonElement element, int? minimum, int? maximum, bool unique)
                {
                    if (element.ValueKind != global::System.Text.Json.JsonValueKind.Array)
                        throw new global::System.Text.Json.JsonException("A collection must be an array.");
                    global::System.Text.Json.JsonElement[] values = global::System.Linq.Enumerable.ToArray(element.EnumerateArray());
                    if ((minimum.HasValue && values.Length < minimum.Value) || (maximum.HasValue && values.Length > maximum.Value) ||
                        (unique && new global::System.Collections.Generic.HashSet<string>(global::System.Linq.Enumerable.Select(values, static item => CanonicalJson(item)), global::System.StringComparer.Ordinal).Count != values.Length))
                        throw new global::System.Text.Json.JsonException("A collection violated its Application Bridge contract.");
                    return element;
                }
                private static string CanonicalJson(global::System.Text.Json.JsonElement element)
                {
                    var buffer = new global::System.Buffers.ArrayBufferWriter<byte>();
                    using (var writer = new global::System.Text.Json.Utf8JsonWriter(buffer)) WriteCanonicalJson(writer, element);
                    return global::System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
                }
                private static void WriteCanonicalJson(global::System.Text.Json.Utf8JsonWriter writer, global::System.Text.Json.JsonElement element)
                {
                    switch (element.ValueKind)
                    {
                        case global::System.Text.Json.JsonValueKind.Object:
                            writer.WriteStartObject();
                            foreach (global::System.Text.Json.JsonProperty property in global::System.Linq.Enumerable.OrderBy(element.EnumerateObject(), static item => item.Name, global::System.StringComparer.Ordinal))
                            {
                                writer.WritePropertyName(property.Name);
                                WriteCanonicalJson(writer, property.Value);
                            }
                            writer.WriteEndObject();
                            return;
                        case global::System.Text.Json.JsonValueKind.Array:
                            writer.WriteStartArray();
                            foreach (global::System.Text.Json.JsonElement item in element.EnumerateArray()) WriteCanonicalJson(writer, item);
                            writer.WriteEndArray();
                            return;
                        case global::System.Text.Json.JsonValueKind.Number:
                            writer.WriteNumberValue(element.GetDouble());
                            return;
                        default:
                            element.WriteTo(writer);
                            return;
                    }
                }
                private static void ValidateTupleValues(global::System.Collections.Generic.IReadOnlyList<object?> values, int structuralMinimum, int? structuralMaximum, int? minimum, int? maximum, bool unique)
                {
                    if (values.Count < structuralMinimum || (structuralMaximum.HasValue && values.Count > structuralMaximum.Value) ||
                        (minimum.HasValue && values.Count < minimum.Value) || (maximum.HasValue && values.Count > maximum.Value) ||
                        (unique && new global::System.Collections.Generic.HashSet<object?>(values).Count != values.Count))
                        throw new global::System.Text.Json.JsonException("A tuple violated its Application Bridge contract.");
                }
                private static bool IsUnionMismatch(global::System.Exception exception) =>
                    exception is global::System.Text.Json.JsonException or global::System.InvalidOperationException or global::System.FormatException or global::System.OverflowException;
                private static global::System.Collections.Generic.IReadOnlyDictionary<string, T> DecodeRecord<T>(global::System.Text.Json.JsonElement element, string? keyPattern, global::System.Func<global::System.Text.Json.JsonElement, T> decode)
                {
                    if (element.ValueKind != global::System.Text.Json.JsonValueKind.Object)
                        throw new global::System.Text.Json.JsonException("A record must be an object.");
                    var result = new global::System.Collections.Generic.Dictionary<string, T>(global::System.StringComparer.Ordinal);
                    foreach (global::System.Text.Json.JsonProperty property in element.EnumerateObject())
                    {
                        ValidateRecordKey(property.Name, keyPattern);
                        if (!result.TryAdd(property.Name, decode(property.Value)))
                            throw new global::System.Text.Json.JsonException("A record key was duplicated.");
                    }
                    return result;
                }
                private static global::System.Collections.Generic.IReadOnlyDictionary<string, T> ValidateRecord<T>(global::System.Collections.Generic.IReadOnlyDictionary<string, T> value, string? keyPattern)
                {
                    foreach (string key in value.Keys) ValidateRecordKey(key, keyPattern);
                    return value;
                }
                private static void ValidateRecordKey(string key, string? pattern)
                {
                    if (pattern is not null && !global::System.Text.RegularExpressions.Regex.IsMatch(key, pattern, global::System.Text.RegularExpressions.RegexOptions.CultureInvariant | global::System.Text.RegularExpressions.RegexOptions.ECMAScript))
                        throw new global::System.Text.Json.JsonException("A record key violated its Application Bridge contract.");
                }
            """);
    }

    private static void RenderObject(StringBuilder source, ObjectModel model)
    {
        source.Append("public sealed record ").Append(model.Name).AppendLine().AppendLine("{");
        foreach (PropertyModel property in model.Properties)
        {
            if (property.JsonName == "_tag")
            {
                source.AppendLine("    [global::System.Text.Json.Serialization.JsonPropertyName(\"_tag\")]");
            }
            string declarationType = property.Required
                ? property.Type.DeclarationType
                : $"global::Runic.Application.Bridge.BridgeOptional<{property.Type.DeclarationType}>";
            source.Append("    public ").Append(property.Required ? "required " : string.Empty)
                .Append(declarationType).Append(' ').Append(property.Name)
                .AppendLine(" { get; init; }");
        }
        source.AppendLine("}").AppendLine();
    }

    private static string Pascal(string value)
    {
        var result = new StringBuilder(value.Length);
        bool upper = true;
        foreach (char character in value)
        {
            if (!char.IsLetterOrDigit(character)) { upper = true; continue; }
            result.Append(upper ? char.ToUpperInvariant(character) : character);
            upper = false;
        }
        return result.Length == 0 ? "Value" : result.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    private static string Safe(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private sealed record ContractModel(string Protocol, int Version, string? InitializeTag, string Namespace, string ContractName, string ManifestFingerprint, ImmutableArray<SchemaEntry> Schemas, ImmutableArray<CommandModel> Commands);
    private sealed record SchemaEntry(string Id, string Name, string Kind)
    {
        internal static SchemaEntry Parse(string id)
        {
            int separator = id.IndexOf(':');
            if (separator <= 0 || separator == id.Length - 1) throw new InvalidOperationException("invalid definition id");
            return new(id, id[(separator + 1)..], id[..separator]);
        }
    }
    private sealed record CommandModel(string Tag, string Receipt, bool StartsOperation, bool Cancellable, bool AdvancesRevision);
    private sealed record SchemaModel(SchemaEntry Entry, ObjectModel Root, ImmutableArray<ObjectModel> Nested);
    private sealed record ObjectModel(string Name, ImmutableArray<PropertyModel> Properties);
    private sealed record PropertyModel(string JsonName, string Name, TypeModel Type, bool Required);
    private sealed record TypeModel(
        string CSharpType,
        string Kind,
        Constraints Constraints,
        ImmutableArray<string> Literals,
        TypeModel? Item,
        bool Nullable,
        ImmutableArray<TupleElementModel> Elements = default,
        TypeModel? Rest = null,
        ImmutableArray<TypeModel> Members = default)
    {
        internal string DeclarationType => Nullable && !CSharpType.EndsWith('?')
            ? CSharpType + "?"
            : CSharpType;
    }
    private sealed record TupleElementModel(TypeModel Type, bool Required);
    private sealed record Constraints(
        double? Minimum = null,
        double? Maximum = null,
        double? ExclusiveMinimum = null,
        double? ExclusiveMaximum = null,
        double? MultipleOf = null,
        int? MinLength = null,
        int? MaxLength = null,
        string? Pattern = null,
        int? MinItems = null,
        int? MaxItems = null,
        bool UniqueItems = false);
    private sealed class UnsupportedSchemaException(string schema, string path) : Exception
    {
        internal string Schema { get; } = schema;
        internal string Path { get; } = path;
    }
}
