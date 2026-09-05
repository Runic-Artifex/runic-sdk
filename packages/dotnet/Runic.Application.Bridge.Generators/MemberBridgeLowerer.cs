using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Runic.Application.Bridge.Generators;

/// <summary>The shared semantic lowerer used by source generation and the artifact inspector.</summary>
public sealed class MemberBridgeLowerer
{
    internal const string Prefix = "Runic.Application.Bridge.";
    internal const string UuidPattern = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";
    private readonly Compilation _compilation;
    internal readonly Dictionary<string, INamedTypeSymbol> Types = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, IMethodSymbol> Constructors = new(StringComparer.Ordinal);
    internal readonly List<Member> Members = [];
    internal readonly JsonObject Definitions = new();
    internal readonly List<Diagnostic> Diagnostics = [];
    private readonly Dictionary<string, ISymbol> _tags = new(StringComparer.Ordinal);
    internal string Registrar => "Runic.Application.Bridge.Generated.Module_" + Hash(_compilation.AssemblyName!)[..16];
    private static readonly DiagnosticDescriptor Invalid = new("RTKAB2001", "Invalid member-based bridge", "{0}", "Runic.Application.Bridge", DiagnosticSeverity.Error, true);

    /// <summary>Creates a semantic bridge lowerer for one compilation.</summary>
    public MemberBridgeLowerer(Compilation compilation) => _compilation = compilation;

    /// <summary>Gets source-located contract diagnostics.</summary>
    public IReadOnlyList<Diagnostic> Errors => Diagnostics;

    /// <summary>Lowers an application contract and its permitted project modules to canonical IR.</summary>
    public string? Inspect(IEnumerable<string>? projectAssemblies = null)
    {
        Discover();
        JsonObject local = Module();
        var modules = new List<JsonObject> { local };
        var allowed = projectAssemblies?.ToHashSet(StringComparer.Ordinal);
        foreach (IAssemblySymbol assembly in _compilation.SourceModule.ReferencedAssemblySymbols.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            if (allowed is not null && !allowed.Contains(assembly.Name)) continue;
            if (assembly.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == Prefix + "ApplicationBridgeContractAttribute"))
                Error(_compilation.Assembly, $"Referenced project '{assembly.Name}' declares an ApplicationBridgeContract root; roots belong only in the entry project.");
            int moduleCount = 0;
            foreach (AttributeData attribute in assembly.GetAttributes().Where(a => a.AttributeClass?.ToDisplayString() == Prefix + "BridgeModuleAttribute"))
            {
                try
                {
                    var module = JsonNode.Parse((string)attribute.ConstructorArguments[0].Value!)!.AsObject();
                    if (module["version"]?.GetValue<int>() != 1 || module["assembly"]?.GetValue<string>() != assembly.Name)
                        throw new InvalidOperationException("Conflicting bridge module metadata.");
                    if (++moduleCount > 1) throw new InvalidOperationException("Duplicate bridge module metadata.");
                    modules.Add(module);
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException)
                { Error(_compilation.Assembly, e.Message); }
            }
        }
        AttributeData[] roots = _compilation.Assembly.GetAttributes().Where(a => a.AttributeClass?.ToDisplayString() == Prefix + "ApplicationBridgeContractAttribute").ToArray();
        if (roots.Length == 0) return null;
        if (roots.Length != 1) { Error(_compilation.Assembly, "Declare exactly one ApplicationBridgeContract root."); return null; }
        AttributeData root = roots[0];
        string identity = (string)root.ConstructorArguments[0].Value!;
        int version = (int)root.ConstructorArguments[1].Value!;
        string name = Named(root, "ContractName") as string ?? "Application";
        if (string.IsNullOrWhiteSpace(identity) || version < 1 || !SyntaxFacts.IsValidIdentifier(name))
            Error(_compilation.Assembly, "The contract requires a nonblank identity, positive version, and valid ContractName.");
        JsonObject definitions = new();
        JsonArray commands = new(), events = new(), errors = new();
        JsonArray bindings = new();
        var snapshot = new List<JsonObject>();
        var tags = new HashSet<string>(StringComparer.Ordinal);
        var definitionOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonObject module in modules.OrderBy(m => m["assembly"]!.GetValue<string>(), StringComparer.Ordinal))
        {
            string owner = module["assembly"]!.GetValue<string>();
            bindings.Add((JsonNode)module.DeepClone());
            foreach (var pair in module["definitions"]!.AsObject())
            {
                if (definitions.TryGetPropertyValue(pair.Key, out var previous))
                {
                    if (Canonical(previous!) != Canonical(pair.Value!) || definitionOwners[pair.Key] != module["types"]?[pair.Key]?.GetValue<string>())
                        Error(_compilation.Assembly, $"Conflicting definition '{pair.Key}' in module '{owner}'. Use BridgeName to disambiguate.");
                }
                else { definitions.Add(pair.Key, pair.Value!.DeepClone()); definitionOwners[pair.Key] = module["types"]?[pair.Key]?.GetValue<string>() ?? owner; }
            }
            foreach (JsonObject member in module["members"]!.AsArray().OfType<JsonObject>())
            {
                if (member["kind"]!.GetValue<string>() == "snapshot") snapshot.Add(member);
                else
                {
                    string tag = member["tag"]!.GetValue<string>();
                    if (!tags.Add(tag)) Error(_compilation.Assembly, $"Duplicate command tag '{tag}' in module '{owner}'.");
                    commands.Add((JsonNode)new JsonObject { ["name"] = tag, ["receipt"] = member["receipt"]!.DeepClone(), ["startsOperation"] = member["startsOperation"]!.DeepClone(), ["cancellable"] = member["cancellable"]!.DeepClone(), ["advancesRevision"] = member["advancesRevision"]!.DeepClone() });
                }
            }
            foreach (string kind in new[] { "events", "errors" })
                foreach (JsonNode? entry in module[kind]!.AsArray())
                {
                    string tag = entry!.GetValue<string>();
                    if (!tags.Add(tag)) Error(_compilation.Assembly, $"Duplicate payload tag '{tag}' in module '{owner}'.");
                    (kind == "events" ? events : errors).Add((JsonNode?)JsonValue.Create(tag));
                }
        }
        if (snapshot.Count != 1) Error(_compilation.Assembly, $"The application requires exactly one BridgeSnapshot; found {snapshot.Count}.");
        foreach (string tag in BuiltInErrors)
        {
            if (tags.Contains(tag)) Error(_compilation.Assembly, $"'{tag}' is a built-in bridge error.");
            definitions["error:" + tag] = BuiltInError(tag); errors.Add((JsonNode?)JsonValue.Create(tag));
        }
        if (Diagnostics.Count != 0) return null;
        var wire = new JsonObject {
            ["protocol"] = new JsonObject { ["identity"] = identity, ["version"] = version },
            ["envelopeVersion"] = 1,
            ["initialization"] = new JsonObject { ["kind"] = "snapshot", ["payload"] = "empty-object" },
            ["canonicalEncoding"] = "runic-json-v1",
            ["limits"] = new JsonObject { ["maxFrameBytes"] = 262144, ["maxDepth"] = 32, ["maxStringBytes"] = 65536, ["maxCollectionItems"] = 4096, ["maxPendingCommands"] = 64 },
            ["snapshot"] = snapshot[0]["schema"]!.DeepClone(), ["definitions"] = definitions,
            ["commands"] = Sort(commands, n => n["name"]!.GetValue<string>()), ["events"] = Sort(events, n => n.GetValue<string>()), ["errors"] = Sort(errors, n => n.GetValue<string>())
        };
        return Canonical(new JsonObject { ["format"] = "runic.application-bridge-ir", ["formatVersion"] = 1, ["authority"] = "csharp",
            ["fingerprint"] = new JsonObject { ["algorithm"] = "sha256", ["scope"] = "wire", ["value"] = Hash(Canonical(wire)) },
            ["wire"] = wire, ["csharp"] = new JsonObject { ["namespace"] = "Runic.Application.Generated", ["contractName"] = name },
            ["bindings"] = new JsonObject { ["modules"] = bindings }, ["documentation"] = new JsonObject() });
    }

    internal void Discover()
    {
        if (Members.Count != 0 || Types.Count != 0) return;
        foreach (INamedTypeSymbol type in AllTypes(_compilation.Assembly.GlobalNamespace).OrderBy(t => t.ToDisplayString(), StringComparer.Ordinal))
        {
            foreach (ISymbol member in type.GetMembers().OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                AttributeData? command = Attr(member, "BridgeCommand");
                bool snapshot = Attr(member, "BridgeSnapshot") is not null;
                if (command is null && !snapshot) continue;
                if (type.IsStatic || type.IsAbstract || type.TypeParameters.Length != 0 || type.ContainingType is not null ||
                    !type.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax d && d.Modifiers.Any(SyntaxKind.PartialKeyword)))
                { Error(member, "Bridge parts must be concrete, nongeneric, top-level partial classes."); continue; }
                if (!type.InstanceConstructors.Any(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)) Error(member, "Bridge parts require an accessible constructor for dependency injection.");
                if (member.IsStatic || (command is not null && snapshot)) { Error(member, "Bridge members must be instance members with one bridge role."); continue; }
                ITypeSymbol? result = null, request = null;
                if (member is IPropertySymbol property && snapshot && property.GetMethod is not null && !property.IsIndexer)
                    result = property.Type;
                else if (member is IMethodSymbol method && !method.IsGenericMethod && method.MethodKind == MethodKind.Ordinary && !method.ReturnsByRef && !method.ReturnsByRefReadonly)
                {
                    result = Unwrap(method.ReturnType);
                    var infrastructure = new HashSet<string>(StringComparer.Ordinal);
                    foreach (IParameterSymbol parameter in method.Parameters)
                    {
                        string full = parameter.Type.ToDisplayString();
                        if (parameter.RefKind != RefKind.None || parameter.IsParams) Error(parameter, "Bridge parameters cannot use ref, out, in, or params.");
                        if (full == "System.Threading.CancellationToken" || full == Prefix + (snapshot ? "BridgeSnapshotContext" : "BridgeCommandContext"))
                        { if (!infrastructure.Add(full)) Error(parameter, "An infrastructure parameter may appear only once."); }
                        else if (!snapshot && request is null) request = parameter.Type;
                        else Error(parameter, snapshot ? "Snapshot providers accept only BridgeSnapshotContext and CancellationToken." : "A command must have exactly one application DTO parameter.");
                    }
                }
                else Error(member, "BridgeSnapshot requires a readable property or provider; BridgeCommand requires a non-generic method.");
                if (result is not INamedTypeSymbol receipt || receipt.SpecialType != SpecialType.None || receipt.TypeKind is not (TypeKind.Class or TypeKind.Struct) || (!snapshot && request is null))
                { Error(member, "Bridge methods require a DTO result (T, Task<T>, or ValueTask<T>) and commands require exactly one DTO request."); continue; }
                string resultId = AddType(receipt, snapshot ? "type" : "receipt", member);
                string? requestId = request is INamedTypeSymbol requestType ? AddType(requestType, "command", member) : null;
                bool starts = command is not null && Named(command, "StartsOperation") is true;
                bool cancellable = command is not null && Named(command, "Cancellable") is true;
                if (cancellable && !starts) Error(member, "Cancellable commands must start an operation.");
                if (starts && !Properties(receipt).Any(p => JsonName(p) == "operationId" && p.Type.ToDisplayString() == "System.Guid"))
                    Error(member, "Operation receipts require a Guid OperationId property.");
                Members.Add(new(member, snapshot, requestId, resultId, starts, cancellable, command is not null && Named(command, "AdvancesRevision") is true));
            }
            foreach (string role in new[] { "Event", "Error" })
                if (Attr(type, "Bridge" + role) is not null) AddType(type, role.ToLowerInvariant(), type);
        }
    }

    internal JsonObject Module() => new() {
        ["version"] = 1, ["assembly"] = _compilation.AssemblyName, ["registrar"] = Registrar,
        ["definitions"] = Definitions.DeepClone(),
        ["types"] = new JsonObject(Types.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key, JsonValue.Create(p.Value.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))))),
        ["members"] = new JsonArray(Members.Select(m => (JsonNode)new JsonObject {
            ["kind"] = m.Snapshot ? "snapshot" : "command", ["owner"] = m.Symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), ["member"] = m.Symbol.Name,
            ["adapter"] = m.Adapter, ["tag"] = m.RequestId?[8..], ["receipt"] = m.Snapshot ? null : m.ResultId[8..], ["schema"] = m.ResultId,
            ["startsOperation"] = m.StartsOperation, ["cancellable"] = m.Cancellable, ["advancesRevision"] = m.AdvancesRevision }).ToArray()),
        ["events"] = new JsonArray(Types.Keys.Where(k => k.StartsWith("event:", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(k => (JsonNode?)JsonValue.Create(k[6..])).ToArray()),
        ["errors"] = new JsonArray(Types.Keys.Where(k => k.StartsWith("error:", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(k => (JsonNode?)JsonValue.Create(k[6..])).ToArray())
    };

    private string AddType(INamedTypeSymbol type, string role, ISymbol source)
    {
        string name = role == "type" ? StableName(type) : Tag(type);
        string id = role + ":" + name;
        if (Types.TryGetValue(id, out var existing))
        { if (!SymbolEqualityComparer.Default.Equals(existing, type)) Error(source, $"Duplicate definition '{id}'."); return id; }
        if (!SyntaxFacts.IsValidIdentifier(name)) Error(source, "BridgeTag and BridgeName must be valid identifiers in V1.");
        if (role != "type")
        {
            if (_tags.TryGetValue(name, out var tagged) && !SymbolEqualityComparer.Default.Equals(tagged, type)) Error(source, $"Duplicate wire tag '{name}'.");
            _tags[name] = type;
        }
        Types[id] = type;
        Definitions[id] = new JsonObject { ["kind"] = "null" };
        if ((!type.IsRecord && !(type.IsSealed && type.TypeKind == TypeKind.Class)) || type.IsGenericType || type.IsAbstract ||
            type.BaseType is { SpecialType: not SpecialType.System_Object } parent && parent.ToDisplayString() != "System.ValueType")
            Error(source, $"'{type}' must be an immutable record or sealed init-only class without inheritance.");
        if (AttrFull(type, "System.Text.Json.Serialization.JsonConverterAttribute") is not null) Error(type, "Custom JSON converters are not portable.");
        JsonObject props = new();
        if (role != "type") props["_tag"] = Property(new JsonObject { ["kind"] = "literal", ["value"] = name });
        var properties = Properties(type).ToArray();
        foreach (IPropertySymbol property in properties)
        {
            if (property.SetMethod is not null && !property.SetMethod.IsInitOnly) Error(property, "Mutable bridge DTO setters are not supported.");
            if (property.GetMethod?.DeclaredAccessibility != Accessibility.Public) Error(property, "Wire properties need public getters.");
            foreach (AttributeData a in property.GetAttributes())
                if (a.AttributeClass?.ContainingNamespace.ToDisplayString() == "System.Text.Json.Serialization" && a.AttributeClass.Name != "JsonPropertyNameAttribute")
                    Error(property, "JsonPropertyName is the only supported JSON property override.");
            string json = JsonName(property);
            if (props.ContainsKey(json) || json == "_tag") { Error(property, $"Duplicate or reserved wire property '{json}'."); continue; }
            ITypeSymbol propertyType = property.Type;
            bool optional = propertyType is INamedTypeSymbol p && p.OriginalDefinition.ToDisplayString() == Prefix + "BridgeOptional<T>";
            if (optional) propertyType = ((INamedTypeSymbol)propertyType).TypeArguments[0];
            props[json] = Property(Lower(propertyType, property), optional);
        }
        IMethodSymbol? constructor = type.InstanceConstructors.Where(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal && !c.IsStatic)
            .Where(c => c.Parameters.All(p => properties.Any(prop => string.Equals(prop.Name, p.Name, StringComparison.OrdinalIgnoreCase) && SymbolEqualityComparer.IncludeNullability.Equals(prop.Type, p.Type))))
            .OrderByDescending(c => c.Parameters.Length).FirstOrDefault();
        if (constructor is null || properties.Any(p => p.SetMethod is null && !constructor.Parameters.Any(arg => string.Equals(arg.Name, p.Name, StringComparison.OrdinalIgnoreCase))))
            Error(type, "The DTO needs an accessible constructor for its immutable properties.");
        else Constructors[id] = constructor;
        Definitions[id] = new JsonObject { ["kind"] = "object", ["properties"] = props };
        return id;
    }

    private JsonObject Lower(ITypeSymbol type, IPropertySymbol source, bool constraints = true)
    {
        if (type.NullableAnnotation == NullableAnnotation.Annotated || type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            ITypeSymbol value = type is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T ? n.TypeArguments[0] : type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            return Union([new JsonObject { ["kind"] = "null" }, Lower(value, source, constraints)]);
        }
        if (type is INamedTypeSymbol named && AttrFull(named, "System.Text.Json.Serialization.JsonConverterAttribute") is not null)
            Error(source, "Custom JSON converters are not portable.");
        JsonObject node;
        switch (type.SpecialType)
        {
            case SpecialType.System_String: node = new() { ["kind"] = "string" }; break;
            case SpecialType.System_Boolean: node = new() { ["kind"] = "boolean" }; break;
            case SpecialType.System_Byte: case SpecialType.System_SByte: case SpecialType.System_Int16: case SpecialType.System_UInt16: case SpecialType.System_Int32: case SpecialType.System_UInt32:
                node = new() { ["kind"] = "integer" }; break;
            case SpecialType.System_Int64: case SpecialType.System_UInt64:
                if (!PropertyAttributes(source).Any(a => a.AttributeClass?.ToDisplayString() == Prefix + "BridgeSafeIntegerAttribute")) Error(source, "64-bit integers require BridgeSafeInteger.");
                node = new() { ["kind"] = "integer" }; break;
            case SpecialType.System_Double: node = new() { ["kind"] = "number" }; break;
            default:
                if (type.ToDisplayString() == "System.Guid") node = new() { ["kind"] = "string", ["format"] = "uuid" };
                else if (type is IArrayTypeSymbol array) node = new() { ["kind"] = "array", ["items"] = Lower(array.ElementType, source, false) };
                else if (type is INamedTypeSymbol collection && collection.IsGenericType && collection.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.IReadOnlyList<T>" or "System.Collections.Generic.IReadOnlyCollection<T>")
                    node = new() { ["kind"] = "array", ["items"] = Lower(collection.TypeArguments[0], source, false) };
                else if (type is INamedTypeSymbol e && e.TypeKind == TypeKind.Enum)
                {
                    if (AttrFull(e, "System.FlagsAttribute") is not null) Error(source, "Flags enums are not supported.");
                    var values = e.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).Select(f => AttrFull(f, "System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute")?.ConstructorArguments[0].Value as string ?? f.Name).ToArray();
                    if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) Error(source, "Enum wire names must be unique.");
                    node = Union(values.Select(v => new JsonObject { ["kind"] = "literal", ["value"] = v }));
                }
                else if (type is INamedTypeSymbol dto && !dto.ContainingNamespace.ToDisplayString().StartsWith("System", StringComparison.Ordinal))
                    node = new() { ["kind"] = "ref", ["name"] = AddType(dto, "type", source) };
                else { Error(source, $"'{type}' is not a portable V1 wire type."); node = new() { ["kind"] = "null" }; }
                break;
        }
        if (node["kind"]?.GetValue<string>() == "integer")
        {
            (long min, long max) = type.SpecialType switch {
                SpecialType.System_Byte => (byte.MinValue, byte.MaxValue), SpecialType.System_SByte => (sbyte.MinValue, sbyte.MaxValue),
                SpecialType.System_Int16 => (short.MinValue, short.MaxValue), SpecialType.System_UInt16 => (ushort.MinValue, ushort.MaxValue),
                SpecialType.System_Int32 => (int.MinValue, int.MaxValue), SpecialType.System_UInt32 => (uint.MinValue, uint.MaxValue),
                SpecialType.System_UInt64 => (0, 9007199254740991L), _ => (-9007199254740991L, 9007199254740991L) };
            node["constraints"] = new JsonObject { ["minimum"] = min, ["maximum"] = max };
        }
        if (constraints) ApplyConstraints(node, source);
        return node;
    }

    private void ApplyConstraints(JsonObject node, IPropertySymbol source)
    {
        JsonObject constraints = node["constraints"]?.DeepClone().AsObject() ?? new();
        string kind = node["kind"]!.GetValue<string>();
        foreach (AttributeData a in PropertyAttributes(source))
        {
            if (a.AttributeClass?.ContainingNamespace.ToDisplayString() != Prefix.TrimEnd('.')) continue;
            string name = a.AttributeClass.Name;
            double Number(int i) => Convert.ToDouble(a.ConstructorArguments[i].Value, System.Globalization.CultureInfo.InvariantCulture);
            string? key = name switch { "BridgeMinimumAttribute" => "minimum", "BridgeMaximumAttribute" => "maximum", "BridgeExclusiveMinimumAttribute" => "exclusiveMinimum", "BridgeExclusiveMaximumAttribute" => "exclusiveMaximum", "BridgeMultipleOfAttribute" => "multipleOf", _ => null };
            if (key is not null)
            {
                double value = Number(0);
                if (kind is not ("integer" or "number") || !double.IsFinite(value) || key == "multipleOf" && value <= 0) Error(source, "Numeric constraints require finite bounds and a positive multiple on a numeric property.");
                else constraints[key] = key == "minimum" && constraints[key] is not null ? Math.Max(Convert.ToDouble(constraints[key]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture), value) : key == "maximum" && constraints[key] is not null ? Math.Min(Convert.ToDouble(constraints[key]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture), value) : value;
            }
            else if (name == "BridgeSafeIntegerAttribute") { /* Integer bounds are intrinsic to the lowered type. */ }
            else if (name is "BridgeStringLengthAttribute" or "BridgeCollectionLengthAttribute")
            {
                bool text = name == "BridgeStringLengthAttribute";
                if (kind != (text ? "string" : "array") || Number(0) < 0 || Number(1) < Number(0)) Error(source, "Length constraints require valid bounds on a string or collection.");
                else { constraints[text ? "minLength" : "minItems"] = Number(0); if (Number(1) < int.MaxValue) constraints[text ? "maxLength" : "maxItems"] = Number(1); }
            }
            else if (name == "BridgePatternAttribute")
            {
                string pattern = (string)a.ConstructorArguments[0].Value!;
                if (kind != "string" || pattern.Contains("(?", StringComparison.Ordinal) || pattern.Contains("\\p", StringComparison.Ordinal) || pattern.Contains("\\k", StringComparison.Ordinal)) Error(source, "Patterns must use the portable regular-expression subset.");
                else { try { _ = new System.Text.RegularExpressions.Regex(pattern); constraints["pattern"] = pattern; } catch (ArgumentException) { Error(source, "Invalid bridge pattern."); } }
            }
            else if (name == "BridgeUniqueAttribute") { if (kind != "array") Error(source, "Uniqueness requires a collection."); else constraints["uniqueItems"] = true; }
        }
        if (constraints["minimum"] is not null && constraints["maximum"] is not null && double.Parse(constraints["minimum"]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) > double.Parse(constraints["maximum"]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture)) Error(source, "The numeric bounds have an empty intersection.");
        if (constraints.Count != 0) node["constraints"] = constraints;
    }

    private static IEnumerable<AttributeData> PropertyAttributes(IPropertySymbol source) => source.GetAttributes().Concat(source.ContainingType.InstanceConstructors.SelectMany(c => c.Parameters).Where(p => string.Equals(p.Name, source.Name, StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.GetAttributes()));
    internal static IEnumerable<IPropertySymbol> Properties(INamedTypeSymbol type) => type.GetMembers().OfType<IPropertySymbol>().Where(p => !p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public);
    internal static string JsonName(IPropertySymbol p) => AttrFull(p, "System.Text.Json.Serialization.JsonPropertyNameAttribute")?.ConstructorArguments[0].Value as string ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name);
    internal static string StableName(INamedTypeSymbol t) => Attr(t, "BridgeName")?.ConstructorArguments[0].Value as string ?? t.Name;
    internal static string Tag(INamedTypeSymbol t) => Attr(t, "BridgeTag")?.ConstructorArguments[0].Value as string ?? t.Name;
    internal static AttributeData? Attr(ISymbol s, string name) => AttrFull(s, Prefix + name + "Attribute");
    internal static AttributeData? AttrFull(ISymbol s, string name) => s.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == name);
    internal static object? Named(AttributeData a, string key) => a.NamedArguments.FirstOrDefault(p => p.Key == key).Value.Value;
    internal static ITypeSymbol Unwrap(ITypeSymbol type) => type is INamedTypeSymbol n && n.IsGenericType && n.OriginalDefinition.ToDisplayString() is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>" ? n.TypeArguments[0] : type;
    internal static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns) => ns.GetTypeMembers().Concat(ns.GetNamespaceMembers().SelectMany(AllTypes));
    internal void Error(ISymbol source, string message) => Diagnostics.Add(Diagnostic.Create(Invalid, source.Locations.FirstOrDefault(l => l.IsInSource) ?? _compilation.Assembly.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == Prefix + "ApplicationBridgeContractAttribute")?.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? Location.None, message));
    internal static JsonObject Property(JsonObject type, bool optional = false) => new() { ["type"] = type, ["optional"] = optional };
    internal static JsonObject Union(IEnumerable<JsonObject> members) => new() { ["kind"] = "union", ["members"] = new JsonArray(members.OrderBy(Canonical, StringComparer.Ordinal).Select(m => (JsonNode)m).ToArray()) };
    internal static JsonArray Sort(JsonArray items, Func<JsonNode, string> key) => new(items.Select(n => n!).OrderBy(key, StringComparer.Ordinal).Select(n => n.DeepClone()).ToArray());
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static string Canonical(JsonNode node) => ApplicationBridgeGenerator.Canonicalize(JsonDocument.Parse(node.ToJsonString()).RootElement);
    internal static readonly string[] BuiltInErrors = ["CommandRejected", "OperationCancelled", "OperationFailed", "OperationTimedOut", "ProtocolDecodeError", "ProtocolVersionMismatch", "StaleRevision", "TransportClosed", "TransportUnavailable"];
    internal static JsonObject BuiltInError(string tag) => new() { ["kind"] = "object", ["properties"] = new JsonObject {
        ["_tag"] = Property(new() { ["kind"] = "literal", ["value"] = tag }), ["message"] = Property(new() { ["kind"] = "string" }), ["retryable"] = Property(new() { ["kind"] = "boolean" }) } };
    internal sealed record Member(ISymbol Symbol, bool Snapshot, string? RequestId, string ResultId, bool StartsOperation, bool Cancellable, bool AdvancesRevision)
    { internal string Adapter => "__Runic_" + Hash((Snapshot ? "snapshot" : RequestId) + Symbol.Name)[..12]; }
}
