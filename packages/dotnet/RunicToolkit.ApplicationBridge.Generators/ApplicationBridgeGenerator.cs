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

namespace RunicToolkit.ApplicationBridge.Generators;

/// <summary>Generates one closed, reflection-free Application Bridge contract.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class ApplicationBridgeGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidManifest = new(
        "RTKAB0001",
        "Invalid Application Bridge manifest",
        "The Application Bridge manifest is invalid: {0}",
        "RunicToolkit.ApplicationBridge",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor MissingSchema = new(
        "RTKAB0002",
        "Application Bridge schema is missing",
        "The committed schema '{0}' referenced by the Application Bridge manifest is missing",
        "RunicToolkit.ApplicationBridge",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor StaleSchema = new(
        "RTKAB0003",
        "Application Bridge schema is stale",
        "The committed schema '{0}' does not match its manifest fingerprint",
        "RunicToolkit.ApplicationBridge",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor UnsupportedSchema = new(
        "RTKAB0004",
        "Unsupported Application Bridge schema",
        "Schema '{0}' uses an unsupported construct at '{1}'",
        "RunicToolkit.ApplicationBridge",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<AdditionalText>> files = context.AdditionalTextsProvider
            .Where(static file =>
                file.Path.EndsWith("bridge.manifest.json", StringComparison.OrdinalIgnoreCase) ||
                file.Path.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase))
            .Collect();
        context.RegisterSourceOutput(files, static (productionContext, inputs) => Emit(productionContext, inputs));
    }

    private static void Emit(SourceProductionContext context, ImmutableArray<AdditionalText> files)
    {
        AdditionalText? manifestFile = files
            .Where(static file => file.Path.EndsWith("bridge.manifest.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static file => file.Path, StringComparer.Ordinal)
            .FirstOrDefault();
        if (manifestFile is null)
        {
            return;
        }

        SourceText? manifestText = manifestFile.GetText(context.CancellationToken);
        if (manifestText is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidManifest, Location.None, "the file could not be read"));
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(manifestText.ToString());
            ContractModel model = ParseManifest(document.RootElement, manifestText.ToString());
            var schemas = new List<SchemaModel>();
            foreach (SchemaEntry entry in model.Schemas)
            {
                AdditionalText? schemaFile = files.FirstOrDefault(file =>
                    Normalize(file.Path).EndsWith(Normalize(entry.File), StringComparison.OrdinalIgnoreCase));
                if (schemaFile is null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(MissingSchema, Location.None, entry.File));
                    return;
                }
                string schemaText = schemaFile.GetText(context.CancellationToken)?.ToString() ?? string.Empty;
                string actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schemaText))).ToLowerInvariant();
                if (!string.Equals(actual, entry.Fingerprint, StringComparison.Ordinal))
                {
                    context.ReportDiagnostic(Diagnostic.Create(StaleSchema, Location.None, entry.File));
                    return;
                }
                using JsonDocument schemaDocument = JsonDocument.Parse(schemaText);
                schemas.Add(ParseSchema(entry, schemaDocument.RootElement));
            }
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

    private static ContractModel ParseManifest(JsonElement root, string manifestText)
    {
        if (root.GetProperty("generatorFormatVersion").GetInt32() != 1)
        {
            throw new InvalidOperationException("unsupported generator format version");
        }
        JsonElement protocol = root.GetProperty("protocol");
        JsonElement csharp = root.GetProperty("csharp");
        var schemas = root.GetProperty("schemas").EnumerateArray()
            .Select(static item => new SchemaEntry(
                item.GetProperty("name").GetString()!,
                item.GetProperty("kind").GetString()!,
                item.GetProperty("file").GetString()!,
                item.GetProperty("sha256").GetString()!))
            .OrderBy(static item => item.Kind, StringComparer.Ordinal)
            .ThenBy(static item => item.Name, StringComparer.Ordinal)
            .ToImmutableArray();
        var commands = root.GetProperty("commands").EnumerateArray()
            .Select(static item => new CommandModel(
                item.GetProperty("tag").GetString()!,
                item.GetProperty("receipt").GetString()!,
                item.TryGetProperty("startsOperation", out JsonElement starts) && starts.GetBoolean(),
                item.TryGetProperty("advancesRevision", out JsonElement advances) && advances.GetBoolean()))
            .OrderBy(static item => item.Tag, StringComparer.Ordinal)
            .ToImmutableArray();
        return new ContractModel(
            protocol.GetProperty("identity").GetString()!,
            protocol.GetProperty("version").GetInt32(),
            csharp.GetProperty("namespace").GetString()!,
            csharp.GetProperty("contractName").GetString()!,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestText))).ToLowerInvariant(),
            schemas,
            commands);
    }

    private static SchemaModel ParseSchema(SchemaEntry entry, JsonElement root)
    {
        if (!root.TryGetProperty("type", out JsonElement type) || type.GetString() != "object")
        {
            throw new UnsupportedSchemaException(entry.Name, "$");
        }
        var nested = new List<ObjectModel>();
        ObjectModel rootObject = ParseObject(entry.Name, entry.Name, root, nested);
        return new SchemaModel(entry, rootObject, nested.ToImmutableArray());
    }

    private static ObjectModel ParseObject(
        string schema,
        string name,
        JsonElement element,
        List<ObjectModel> nested)
    {
        if (element.TryGetProperty("additionalProperties", out JsonElement additional) && additional.ValueKind != JsonValueKind.False)
        {
            throw new UnsupportedSchemaException(schema, name + ".additionalProperties");
        }
        var required = element.TryGetProperty("required", out JsonElement requiredElement)
            ? requiredElement.EnumerateArray().Select(static item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var properties = new List<PropertyModel>();
        if (element.TryGetProperty("properties", out JsonElement propertyElement))
        {
            foreach (JsonProperty property in propertyElement.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
            {
                string nestedName = name + Pascal(property.Name.TrimStart('_'));
                string type = ParseType(schema, nestedName, property.Value, nested);
                properties.Add(new PropertyModel(property.Name, Pascal(property.Name.TrimStart('_')), type, required.Contains(property.Name)));
            }
        }
        return new ObjectModel(name, properties.ToImmutableArray());
    }

    private static string ParseType(string schema, string name, JsonElement element, List<ObjectModel> nested)
    {
        if (element.TryGetProperty("$ref", out JsonElement reference))
        {
            string value = reference.GetString()!;
            if (value.EndsWith("/Uuid", StringComparison.Ordinal)) return "global::System.Guid";
            if (value.EndsWith("/Int", StringComparison.Ordinal) || value.EndsWith("/Revision", StringComparison.Ordinal)) return "long";
            throw new UnsupportedSchemaException(schema, name + ".$ref");
        }
        if (!element.TryGetProperty("type", out JsonElement typeElement))
        {
            throw new UnsupportedSchemaException(schema, name + ".type");
        }
        return typeElement.GetString() switch
        {
            "string" => "string",
            "boolean" => "bool",
            "integer" => "long",
            "number" => "double",
            "array" => ParseArray(schema, name, element, nested),
            "object" => ParseNested(schema, name, element, nested),
            "null" => "object?",
            _ => throw new UnsupportedSchemaException(schema, name + ".type"),
        };
    }

    private static string ParseArray(string schema, string name, JsonElement element, List<ObjectModel> nested)
    {
        if (!element.TryGetProperty("items", out JsonElement items))
        {
            throw new UnsupportedSchemaException(schema, name + ".items");
        }
        return $"global::System.Collections.Generic.IReadOnlyList<{ParseType(schema, name + "Item", items, nested)}>";
    }

    private static string ParseNested(string schema, string name, JsonElement element, List<ObjectModel> nested)
    {
        ObjectModel nestedObject = ParseObject(schema, name, element, nested);
        nested.Add(nestedObject);
        return name;
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
        foreach (SchemaModel schema in schemas)
        {
            RenderObject(source, schema.Root with { Name = TypeName(schema) });
            foreach (ObjectModel nested in schema.Nested.DistinctBy(static item => item.Name)) RenderObject(source, nested);
        }

        source.Append("public interface I").Append(contract.ContractName).AppendLine("BridgeHandler").AppendLine("{");
        foreach (CommandModel command in contract.Commands)
        {
            source.Append("    global::System.Threading.Tasks.ValueTask<")
                .Append(typeNames["receipt:" + command.Receipt]).Append("> ")
                .Append(Pascal(command.Tag)).AppendLine("Async(")
                .Append("        ").Append(typeNames["command:" + command.Tag]).AppendLine(" command,")
                .AppendLine("        global::RunicToolkit.ApplicationBridge.BridgeCommandContext context,")
                .AppendLine("        global::System.Threading.CancellationToken cancellationToken);");
        }
        source.AppendLine("}").AppendLine();

        source.Append("public sealed class ").Append(contract.ContractName)
            .AppendLine("BridgeDispatcher : global::RunicToolkit.ApplicationBridge.IApplicationBridgeDispatcher")
            .AppendLine("{")
            .Append("    private readonly I").Append(contract.ContractName).AppendLine("BridgeHandler _handler;")
            .Append("    public ").Append(contract.ContractName).Append("BridgeDispatcher(I")
            .Append(contract.ContractName).AppendLine("BridgeHandler handler) => _handler = handler ?? throw new global::System.ArgumentNullException(nameof(handler));")
            .Append("    public string ProtocolIdentity => \"").Append(Escape(contract.Protocol)).AppendLine("\";")
            .Append("    public int ProtocolVersion => ").Append(contract.Version.ToString(CultureInfo.InvariantCulture)).AppendLine(";")
            .Append("    public string ManifestFingerprint => \"").Append(contract.ManifestFingerprint).AppendLine("\";")
            .AppendLine("    public async global::System.Threading.Tasks.ValueTask<global::RunicToolkit.ApplicationBridge.BridgeDispatchResult> DispatchAsync(")
            .AppendLine("        global::System.Text.Json.JsonElement command,")
            .AppendLine("        global::RunicToolkit.ApplicationBridge.BridgeCommandContext context,")
            .AppendLine("        global::System.Threading.CancellationToken cancellationToken)")
            .AppendLine("    {")
            .AppendLine("        if (!command.TryGetProperty(\"_tag\", out global::System.Text.Json.JsonElement tagElement))")
            .AppendLine("            throw new global::System.Text.Json.JsonException(\"The command tag is missing.\");")
            .AppendLine("        return tagElement.GetString() switch")
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
            .AppendLine("    }");
        foreach (CommandModel command in contract.Commands)
        {
            string commandType = typeNames["command:" + command.Tag];
            string receiptType = typeNames["receipt:" + command.Receipt];
            source.Append("    private async global::System.Threading.Tasks.ValueTask<global::RunicToolkit.ApplicationBridge.BridgeDispatchResult> Dispatch")
                .Append(Pascal(command.Tag)).AppendLine("Async(")
                .AppendLine("        global::System.Text.Json.JsonElement payload,")
                .AppendLine("        global::RunicToolkit.ApplicationBridge.BridgeCommandContext context,")
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
                source.Append(", new global::RunicToolkit.ApplicationBridge.BridgeOperationId(receipt.OperationId)");
            }
            source.AppendLine(");").AppendLine("    }");
        }
        source.AppendLine("}").AppendLine();

        source.Append("public static class ").Append(contract.ContractName).AppendLine("BridgeEvents").AppendLine("{");
        foreach (SchemaModel eventSchema in schemas.Where(static item => item.Entry.Kind == "event"))
        {
            string eventType = TypeName(eventSchema);
            source.Append("    public static global::System.Threading.Tasks.ValueTask Publish")
                .Append(Pascal(eventSchema.Entry.Name)).AppendLine("Async(")
                .AppendLine("        this global::RunicToolkit.ApplicationBridge.IBridgeEventPublisher publisher,")
                .Append("        ").Append(eventType).AppendLine(" value,")
                .AppendLine("        bool advancesRevision = false,")
                .AppendLine("        global::RunicToolkit.ApplicationBridge.BridgeOperationId? operationId = null,")
                .AppendLine("        global::System.Threading.CancellationToken cancellationToken = default) =>")
                .Append("        publisher.PublishAsync(new global::RunicToolkit.ApplicationBridge.BridgeEventPayload(")
                .Append(contract.ContractName).Append("BridgeContractCodec.Encode").Append(eventType)
                .AppendLine("(value), advancesRevision, operationId), cancellationToken);");
        }
        source.AppendLine("}").AppendLine();

        var objects = new List<ObjectModel>();
        foreach (SchemaModel schema in schemas)
        {
            objects.Add(schema.Root with { Name = TypeName(schema) });
            objects.AddRange(schema.Nested);
        }
        source.Append("internal static class ").Append(contract.ContractName).AppendLine("BridgeContractCodec").AppendLine("{");
        foreach (ObjectModel model in objects.DistinctBy(static item => item.Name))
        {
            RenderDecode(source, model);
            RenderEncode(source, model);
        }
        source.AppendLine("}");
        return source.ToString();
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
                    .Append(element).Append(") ? ");
            }
            source.Append(DecodeExpression(property.Type, element));
            if (!property.Required) source.Append(" : null");
            source.AppendLine(",");
        }
        source.AppendLine("        };").AppendLine("    }");
    }

    private static string DecodeExpression(string type, string element)
    {
        if (type == "string") return element + ".GetString() ?? throw new global::System.Text.Json.JsonException(\"A required string was null.\")";
        if (type == "bool") return element + ".GetBoolean()";
        if (type == "long") return element + ".GetInt64()";
        if (type == "double") return element + ".GetDouble()";
        if (type == "global::System.Guid") return element + ".GetGuid()";
        const string prefix = "global::System.Collections.Generic.IReadOnlyList<";
        if (type.StartsWith(prefix, StringComparison.Ordinal))
        {
            string itemType = type.Substring(prefix.Length, type.Length - prefix.Length - 1);
            string item = DecodeExpression(itemType, "item");
            return "global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select(" + element + ".EnumerateArray(), static item => " + item + "))";
        }
        if (type == "object?") return "(object?)null";
        return "Decode" + type + "(" + element + ")";
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
            .AppendLine("        return document.RootElement.Clone();")
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
                source.Append("        if (value.").Append(property.Name).AppendLine(" is not null)").AppendLine("        {");
            }
            RenderWriteProperty(source, property, optional ? "            " : "        ");
            if (optional) source.AppendLine("        }");
        }
        source.AppendLine("        writer.WriteEndObject();").AppendLine("    }");
    }

    private static void RenderWriteProperty(StringBuilder source, PropertyModel property, string indent)
    {
        string access = "value." + property.Name;
        string unwrapped = property.Required || property.Type is "string" || property.Type.StartsWith("global::System.Collections.Generic.IReadOnlyList<", StringComparison.Ordinal) || (!IsValueType(property.Type) && property.Type != "object?")
            ? access
            : access + ".Value";
        if (property.Type == "string")
        {
            source.Append(indent).Append("writer.WriteString(\"").Append(Escape(property.JsonName)).Append("\", ").Append(unwrapped).AppendLine(");");
        }
        else if (property.Type == "bool")
        {
            source.Append(indent).Append("writer.WriteBoolean(\"").Append(Escape(property.JsonName)).Append("\", ").Append(unwrapped).AppendLine(");");
        }
        else if (property.Type is "long" or "double")
        {
            source.Append(indent).Append("writer.WriteNumber(\"").Append(Escape(property.JsonName)).Append("\", ").Append(unwrapped).AppendLine(");");
        }
        else if (property.Type == "global::System.Guid")
        {
            source.Append(indent).Append("writer.WriteString(\"").Append(Escape(property.JsonName)).Append("\", ").Append(unwrapped).AppendLine(");");
        }
        else if (property.Type.StartsWith("global::System.Collections.Generic.IReadOnlyList<", StringComparison.Ordinal))
        {
            string itemType = property.Type.Substring("global::System.Collections.Generic.IReadOnlyList<".Length, property.Type.Length - "global::System.Collections.Generic.IReadOnlyList<".Length - 1);
            source.Append(indent).Append("writer.WritePropertyName(\"").Append(Escape(property.JsonName)).AppendLine("\");")
                .Append(indent).AppendLine("writer.WriteStartArray();")
                .Append(indent).Append("foreach (").Append(itemType).Append(" item in ").Append(access).AppendLine(")")
                .Append(indent).AppendLine("{");
            RenderWriteValue(source, itemType, "item", indent + "    ");
            source.Append(indent).AppendLine("}").Append(indent).AppendLine("writer.WriteEndArray();");
        }
        else if (property.Type == "object?")
        {
            source.Append(indent).Append("writer.WriteNull(\"").Append(Escape(property.JsonName)).AppendLine("\");");
        }
        else
        {
            source.Append(indent).Append("writer.WritePropertyName(\"").Append(Escape(property.JsonName)).AppendLine("\");")
                .Append(indent).Append("Write").Append(property.Type).Append("(writer, ").Append(access).AppendLine(");");
        }
    }

    private static void RenderWriteValue(StringBuilder source, string type, string access, string indent)
    {
        if (type == "string" || type == "global::System.Guid") source.Append(indent).Append("writer.WriteStringValue(").Append(access).AppendLine(");");
        else if (type == "bool") source.Append(indent).Append("writer.WriteBooleanValue(").Append(access).AppendLine(");");
        else if (type is "long" or "double") source.Append(indent).Append("writer.WriteNumberValue(").Append(access).AppendLine(");");
        else source.Append(indent).Append("Write").Append(type).Append("(writer, ").Append(access).AppendLine(");");
    }

    private static bool IsValueType(string type) => type is "bool" or "long" or "double" or "global::System.Guid";

    private static void RenderObject(StringBuilder source, ObjectModel model)
    {
        source.Append("public sealed record ").Append(model.Name).AppendLine().AppendLine("{");
        foreach (PropertyModel property in model.Properties)
        {
            if (property.JsonName == "_tag")
            {
                source.AppendLine("    [global::System.Text.Json.Serialization.JsonPropertyName(\"_tag\")]");
            }
            string nullable = property.Required || property.Type.EndsWith('?') ? string.Empty : "?";
            source.Append("    public ").Append(property.Required ? "required " : string.Empty)
                .Append(property.Type).Append(nullable).Append(' ').Append(property.Name)
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

    private static string Normalize(string path) => path.Replace('\\', '/');
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    private static string Safe(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private sealed record ContractModel(string Protocol, int Version, string Namespace, string ContractName, string ManifestFingerprint, ImmutableArray<SchemaEntry> Schemas, ImmutableArray<CommandModel> Commands);
    private sealed record SchemaEntry(string Name, string Kind, string File, string Fingerprint);
    private sealed record CommandModel(string Tag, string Receipt, bool StartsOperation, bool AdvancesRevision);
    private sealed record SchemaModel(SchemaEntry Entry, ObjectModel Root, ImmutableArray<ObjectModel> Nested);
    private sealed record ObjectModel(string Name, ImmutableArray<PropertyModel> Properties);
    private sealed record PropertyModel(string JsonName, string Name, string Type, bool Required);
    private sealed class UnsupportedSchemaException(string schema, string path) : Exception
    {
        internal string Schema { get; } = schema;
        internal string Path { get; } = path;
    }
}
