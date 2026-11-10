using System.Reflection;
using System.Text;
using System.Text.Json;
using Runic.Application.Views;
using static TypeScriptModuleBindings;

// [RunicSharedClient] (#839): one TypeScript client for the ViewModels that implement an interface, so one web
// component renders all of them. GenerateOne records the client parts each Bridge member of an implementing ViewModel
// contributes, with the ViewModel's own name replaced by Owner. Once every ViewModel is generated, Generate checks that
// the implementing ViewModels publish each property of the interface alike (RUNICBRIDGE036) and writes `{name}.ts` with
// `{Name}State`, `{Name}Client`, `{Name}PageReference` and `{name}Kinds`. Each implementing ViewModel's page reference
// extends `{Name}PageReference` and names it under `sharedPageReferences`, which the frameworks' ViewRegistry types read.
// Nothing changes for a ViewModel that implements no such interface.

/// <summary>A part of a generated client that one Bridge member contributes.</summary>
/// <param name="Key">What the part is, such as <c>state</c>, <c>setter</c> or <c>command</c>.</param>
/// <param name="Section">Where it goes: <c>state</c>, <c>client</c>, <c>queries</c> or <c>operations</c>.</param>
/// <param name="Declaration">The TypeScript declaration, with <see cref="SharedClients.Owner"/> for the ViewModel's own name.</param>
/// <param name="Comparable">The declaration without the names that do not change its type, such as parameter names.</param>
/// <param name="Optional">Shared only when every implementing ViewModel has it alike, such as a member's validation errors.</param>
/// <param name="Imports">The page reference types it names, with their modules.</param>
/// <param name="Unsupported">Why the member cannot be part of a shared client, if it cannot.</param>
internal sealed record SharedClientPart(string Key, string Section, string Declaration, string Comparable, string? Documentation,
    bool Optional = false, IReadOnlyList<(string Module, string Type)>? Imports = null, string? Unsupported = null);

/// <summary>A <c>[RunicSharedClient]</c> interface and the generated ViewModels that implement it.</summary>
internal sealed record SharedClientDeclaration(Type Interface, string Name, (Type Model, string Name)[] Implementers)
{
    internal string Module => char.ToLowerInvariant(Name[0]) + Name[1..];
    internal string ReferenceType => $"{Name}PageReference";
}

internal static class SharedClients
{
    /// <summary>Stands for the ViewModel's own name in a recorded declaration.</summary>
    internal const string Owner = "\u0006";
    // Stands for a parameter name in a comparable declaration.
    internal const string Parameter = "\u0007";
    private const string AttributeName = "Runic.Application.Views.RunicSharedClientAttribute";

    private static readonly Dictionary<Type, Dictionary<PropertyInfo, List<SharedClientPart>>> Surfaces = [];
    // The types of @runic-artifex/views that shared client parts can name.
    private static readonly string[] PublicTypes = ["ViewClient", "BridgeOutcome", "BridgeOperation", "BridgeStreamOperation",
        "BridgeOperationStartOptions", "RegionCollection", "RegionOutcome", "QueryCollection", "QueryCollectionState"];
    private static readonly Dictionary<Type, SharedClientDeclaration[]> ByModel = [];

    /// <summary>The shared clients of the generated ViewModels; reports invalid declarations.</summary>
    internal static SharedClientDeclaration[] Discover((Type Model, string Name)[] models, IEnumerable<(string Kind, MemberInfo Member)> kinds)
    {
        var implementers = new Dictionary<Type, List<(Type Model, string Name)>>();
        var invalid = new HashSet<Type>();
        foreach (var entry in models.OrderBy(entry => entry.Name, StringComparer.Ordinal))
            foreach (var contract in entry.Model.GetInterfaces())
            {
                var definition = contract.IsGenericType ? contract.GetGenericTypeDefinition() : contract;
                if (!BridgeAttributeNames.Has(definition, AttributeName)) continue;
                if (contract.IsGenericType)
                {
                    if (invalid.Add(definition))
                        Diagnostics.Report(new BridgeDiagnosticException(BridgeDiagnosticCodes.SharedClient,
                            $"{definition.Name.Split('`')[0]}: a [RunicSharedClient] interface cannot be generic, because its members differ by type argument. Declare one non-generic interface per shared client.",
                            definition));
                    continue;
                }
                if (!implementers.TryGetValue(contract, out var list)) implementers.Add(contract, list = []);
                list.Add(entry);
            }
        var declarations = new List<SharedClientDeclaration>();
        foreach (var (contract, list) in implementers)
        {
            try
            {
                declarations.Add(new SharedClientDeclaration(contract, NameOf(contract), [.. list]));
            }
            catch (Exception error) when (Diagnostics.IsReportable(error))
            {
                Diagnostics.Report(error);
            }
        }
        var ordered = declarations.OrderBy(declaration => declaration.Name, StringComparer.Ordinal).ToArray();
        foreach (var group in ordered.GroupBy(declaration => declaration.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            Diagnostics.Report(new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
                $"[RunicSharedClient] names must be unique: {string.Join(" and ", group.Select(declaration => declaration.Interface.FullName))} both use '{group.Key}'. Set Name on one of them.",
                group.Last().Interface));
        // An implementing ViewModel's module imports {Name}PageReference, and a module declares or imports the page
        // reference of each kind it presents, so the name must not be one of a kind's.
        var kindReferences = kinds.GroupBy(kind => char.ToUpperInvariant(kind.Kind[0]) + kind.Kind[1..] + "PageReference", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var declaration in ordered)
            if (kindReferences.TryGetValue(declaration.ReferenceType, out var kind))
                Diagnostics.Report(new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
                    $"{declaration.Interface.Name}: the shared client {declaration.Name} declares {declaration.ReferenceType}, which is also the page reference of the kind '{kind.Kind}' of {kind.Member.Name}. "
                    + $"Name the shared client with [RunicSharedClient(Name = \"…\")] on {declaration.Interface.Name}.",
                    declaration.Interface));
        foreach (var declaration in ordered)
            foreach (var (model, _) in declaration.Implementers)
                ByModel[model] = [.. ByModel.GetValueOrDefault(model) ?? [], declaration];
        return ordered;
    }

    private static string NameOf(Type contract)
    {
        var attribute = BridgeAttributeNames.Find(contract, AttributeName)!;
        var name = attribute.NamedArguments().GetValueOrDefault("Name") as string;
        if (name is null)
            name = contract.Name.Length > 1 && contract.Name[0] == 'I' && char.IsUpper(contract.Name[1]) ? contract.Name[1..] : contract.Name;
        if (name.Length == 0 || !char.IsLetter(name[0]) || !name.All(char.IsLetterOrDigit))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.SharedClient,
                $"{contract.Name}: the shared client name '{name}' must contain only letters and digits and start with a letter.", contract);
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>The shared clients a ViewModel implements, ordered by name.</summary>
    internal static IReadOnlyList<SharedClientDeclaration> For(Type model) => ByModel.GetValueOrDefault(model) ?? [];

    /// <summary>Records a part of an implementing ViewModel's client.</summary>
    internal static void Record(Type model, PropertyInfo property, SharedClientPart part)
    {
        if (!ByModel.ContainsKey(model)) return;
        if (!Surfaces.TryGetValue(model, out var surface)) Surfaces.Add(model, surface = []);
        if (!surface.TryGetValue(property, out var parts)) surface.Add(property, parts = []);
        parts.Add(part);
    }

    /// <summary>
    /// Checks the implementing ViewModels of every shared client and adds its module. Each problem is reported; a shared
    /// client with one is not written.
    /// </summary>
    internal static void Generate(string directory, IReadOnlyList<SharedClientDeclaration> declarations,
        IReadOnlyDictionary<Type, List<Type>> viewTypes, Func<string, string?, string> pageKind, Func<MemberInfo, string?> contractFor)
    {
        // A member of an interface that another shared interface extends is checked for both; it is reported once.
        var reportedMembers = new HashSet<(Type Model, Module Module, int Token)>();
        foreach (var declaration in declarations)
        {
            PropertyInfo[] members;
            try { members = Members(declaration.Interface); }
            catch (Exception error) when (Diagnostics.IsReportable(error)) { Diagnostics.Report(error); continue; }
            var collected = new List<(Type Model, Dictionary<PropertyInfo, (PropertyInfo Published, List<SharedClientPart> Parts)> Members)>();
            foreach (var (model, _) in declaration.Implementers)
            {
                try { collected.Add((model, Collect(declaration, model, members))); }
                catch (MemberProblem problem)
                {
                    if (reportedMembers.Add((model, problem.Member.Module, problem.Member.MetadataToken))) Diagnostics.Report(problem.Error, model);
                }
                catch (Exception error) when (Diagnostics.IsReportable(error)) { Diagnostics.Report(error, model); }
            }
            // The ViewModels that do implement every member are compared too, so one build reports each one's first problem.
            var shared = Compare(declaration, members, collected, reportedMembers);
            if (shared is null || collected.Count != declaration.Implementers.Length) continue;
            var kinds = declaration.Implementers.SelectMany(entry => viewTypes.TryGetValue(entry.Model, out var views)
                ? views.Select(view => pageKind(entry.Name, contractFor(view))) : [pageKind(entry.Name, null)]).ToArray();
            TypeScriptModules.Add(Path.Combine(directory, $"{declaration.Module}.ts"), Emit(declaration, shared, kinds), declaration.Interface);
        }
    }

    // The interface's properties and those of the interfaces it extends; a name declared twice is ambiguous.
    private static PropertyInfo[] Members(Type contract)
    {
        var members = new[] { contract }.Concat(contract.GetInterfaces())
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(property => property.GetIndexParameters().Length == 0).OrderBy(property => property.MetadataToken))
            .ToArray();
        if (members.GroupBy(member => member.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.SharedClient,
                $"{contract.Name}: {twice.Key} is declared by {string.Join(" and ", twice.Select(member => member.DeclaringType!.Name))}, so the shared client cannot tell which one it is. Rename one of them.",
                twice.Last());
        foreach (var member in members.Where(member => member.GetMethod is null))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.SharedClient,
                $"{member.DeclaringType!.Name}.{member.Name}: a member of a shared client needs a getter.", member);
        return members;
    }

    // The published property and the client parts of each interface member in one implementing ViewModel.
    private static Dictionary<PropertyInfo, (PropertyInfo Published, List<SharedClientPart> Parts)> Collect(
        SharedClientDeclaration declaration, Type model, PropertyInfo[] members)
    {
        var recorded = Surfaces.GetValueOrDefault(model) ?? [];
        var nullability = new NullabilityInfoContext();
        var result = new Dictionary<PropertyInfo, (PropertyInfo, List<SharedClientPart>)>();
        foreach (var member in members)
        {
            try { result.Add(member, CollectMember(declaration, model, member, recorded, nullability)); }
            catch (BridgeDiagnosticException error) { throw new MemberProblem(member, error); }
        }
        return result;
    }

    // A member's problem in one implementing ViewModel; Generate reports it once for the shared clients that include it.
    private sealed class MemberProblem(PropertyInfo member, BridgeDiagnosticException error) : Exception(error.Message, error)
    {
        internal PropertyInfo Member { get; } = member;
        internal BridgeDiagnosticException Error { get; } = error;
    }

    private static (PropertyInfo Published, List<SharedClientPart> Parts) CollectMember(SharedClientDeclaration declaration, Type model,
        PropertyInfo member, Dictionary<PropertyInfo, List<SharedClientPart>> recorded, NullabilityInfoContext nullability)
    {
        var source = $"{member.DeclaringType!.Name}.{member.Name}";
        var map = model.GetInterfaceMap(member.DeclaringType!);
        var index = Array.FindIndex(map.InterfaceMethods, method => method.MetadataToken == member.GetMethod!.MetadataToken);
        var target = index < 0 ? null : map.TargetMethods[index];
        var published = target is null ? null : recorded.Keys.FirstOrDefault(property =>
            property.GetMethod is { } getter && getter.MetadataToken == target.MetadataToken && getter.Module == target.Module);
        if (published is null)
        {
            var declared = target is null ? null : model.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(property => property.GetMethod is { } getter && getter.MetadataToken == target.MetadataToken && getter.Module == target.Module);
            var reason = target is null ? "does not implement it"
                : target.DeclaringType!.IsInterface ? "uses the interface's default implementation, which the Bridge does not publish"
                : target.DeclaringType != model ? $"inherits it from {target.DeclaringType.Name}, and the Bridge publishes only properties the ViewModel class declares"
                : !target.IsPublic ? "implements it explicitly, which the Bridge does not publish; implement it with a public property"
                : declared is not null && BridgeDtoContract.IsRunicIgnored(declared) ? $"marks {declared.Name} [RunicIgnore]"
                : declared is not null ? $"publishes {declared.Name} under another member of its client, such as a command's availability"
                : "does not publish it";
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.SharedClient,
                $"{model.Name} implements {source} of the shared client {declaration.Name}, but {reason}.",
                (MemberInfo?)declared ?? model);
        }
        var parts = recorded[published];
        if (parts.FirstOrDefault(part => part.Unsupported is not null) is { } unsupported)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.SharedClient,
                $"{model.Name}.{published.Name}: {source} of the shared client {declaration.Name} is {unsupported.Unsupported}", published);
        var expected = nullability.Create(member);
        var actual = nullability.Create(published);
        if (!SameNullability(expected, actual, member.SetMethod is not null))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.SharedClient,
                $"{model.Name}.{published.Name} is {Show(actual)}, but {source} of the shared client {declaration.Name} is {Show(expected)}. Declare the same nullability.",
                published);
        // The interface decides whether the shared client sets the member.
        var kept = parts.Where(part => part.Key != "setter" || member.SetMethod is not null).ToList();
        if (member.SetMethod is not null && !kept.Any(part => part.Key == "setter"))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.SharedClient,
                $"{model.Name}.{published.Name}: {source} of the shared client {declaration.Name} is settable, but the page cannot set {published.Name}. Make the interface property get-only.",
                published);
        return (published, kept);
    }

    // Unknown (an oblivious context) matches either state.
    private static bool SameNullability(NullabilityInfo expected, NullabilityInfo actual, bool settable)
    {
        static bool Same(NullabilityState left, NullabilityState right) =>
            left == right || left == NullabilityState.Unknown || right == NullabilityState.Unknown;
        if (!Same(expected.ReadState, actual.ReadState) || (settable && !Same(expected.WriteState, actual.WriteState))) return false;
        if ((expected.ElementType is null) != (actual.ElementType is null)
            || expected.GenericTypeArguments.Length != actual.GenericTypeArguments.Length) return true;
        if (expected.ElementType is not null && !SameNullability(expected.ElementType, actual.ElementType!, false)) return false;
        return expected.GenericTypeArguments.Zip(actual.GenericTypeArguments).All(pair => SameNullability(pair.First, pair.Second, false));
    }

    // The type as C# declares it, such as string? or List<int>.
    private static string Show(NullabilityInfo info)
    {
        var type = Nullable.GetUnderlyingType(info.Type) ?? info.Type;
        var name = info.ElementType is { } element ? Show(element) + "[]"
            : type.IsGenericType ? $"{type.Name.Split('`')[0]}<{string.Join(", ", info.GenericTypeArguments.Select(Show))}>"
            : Keyword(type) ?? type.Name;
        return info.ReadState == NullabilityState.Nullable ? name + "?" : name;
    }

    private static string? Keyword(Type type) => Type.GetTypeCode(type) switch
    {
        _ when type == typeof(object) => "object",
        _ when type.IsEnum => null,
        TypeCode.Boolean => "bool",
        TypeCode.Byte => "byte",
        TypeCode.SByte => "sbyte",
        TypeCode.Int16 => "short",
        TypeCode.UInt16 => "ushort",
        TypeCode.Int32 => "int",
        TypeCode.UInt32 => "uint",
        TypeCode.Int64 => "long",
        TypeCode.UInt64 => "ulong",
        TypeCode.Single => "float",
        TypeCode.Double => "double",
        TypeCode.Decimal => "decimal",
        TypeCode.Char => "char",
        TypeCode.String => "string",
        _ => null,
    };

    // The parts every implementing ViewModel publishes alike, or null after reporting the ones that differ. For each part,
    // the declaration most implementing ViewModels have is the expected one, so the report names the ViewModels that
    // differ from it.
    private static List<(PropertyInfo Member, SharedClientPart Part)>? Compare(SharedClientDeclaration declaration, PropertyInfo[] members,
        List<(Type Model, Dictionary<PropertyInfo, (PropertyInfo Published, List<SharedClientPart> Parts)> Members)> collected,
        HashSet<(Type Model, Module Module, int Token)> reportedMembers)
    {
        var shared = new List<(PropertyInfo, SharedClientPart)>();
        var reported = new HashSet<Type>();
        foreach (var member in members)
        {
            var keys = collected.SelectMany(entry => entry.Members[member].Parts.Select(part => part.Key)).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var key in keys)
            {
                var entries = collected.Select(entry => (entry.Model, entry.Members[member].Published,
                    Part: entry.Members[member].Parts.FirstOrDefault(part => part.Key == key))).ToArray();
                var groups = entries.GroupBy(entry => entry.Part?.Comparable).ToArray();
                if (groups.Length == 1)
                {
                    // Parameter names may differ; the most common one names the shared parameter.
                    shared.Add((member, entries.GroupBy(entry => entry.Part!.Declaration, StringComparer.Ordinal)
                        .OrderByDescending(group => group.Count()).First().First().Part!));
                    continue;
                }
                if (entries.Any(entry => entry.Part?.Optional == true)) continue;
                var expected = groups.OrderByDescending(group => group.Count()).First();
                var expectedPart = expected.First().Part;
                foreach (var entry in entries.Where(entry => entry.Part?.Comparable != expected.Key))
                {
                    if (!reported.Add(entry.Model) || !reportedMembers.Add((entry.Model, member.Module, member.MetadataToken))) continue;
                    var others = expected.Select(other => other.Model.Name).ToArray();
                    Diagnostics.Report(new BridgeDiagnosticException(BridgeDiagnosticCodes.SharedClient,
                        $"{entry.Model.Name}.{entry.Published.Name}: every ViewModel that implements {member.DeclaringType!.Name}.{member.Name} of the shared client {declaration.Name} must publish it alike, but {entry.Model.Name}'s client "
                        + $"{Describe(entry.Part, declaration.Name, plural: false)} where "
                        + (others.Length == 1 ? $"{others[0]}'s client " : $"{string.Join(", ", others[..^1])} and {others[^1]}'s clients ")
                        + $"{Describe(expectedPart, declaration.Name, plural: others.Length > 1)}. "
                        + "Check the member's type and attributes, such as [JsonPropertyName], [RunicCommandInput], [RunicCommandKey] and [RunicFailure].",
                        entry.Published), entry.Model);
                }
            }
        }
        return reported.Count == 0 ? shared : null;
    }

    private static string Describe(SharedClientPart? part, string name, bool plural) => (plural ? "have" : "has") + (part is null ? " no such member"
        : $" `{TypeScriptNamedTypes.Display(part.Declaration.Replace(Owner, name, StringComparison.Ordinal)).Replace("\u0003", "", StringComparison.Ordinal)}`");

    private static string Emit(SharedClientDeclaration declaration, List<(PropertyInfo Member, SharedClientPart Part)> shared, string[] kinds)
    {
        var name = declaration.Name;
        var contract = declaration.Interface.Name;
        var implementers = string.Join(", ", declaration.Implementers.Select(entry => entry.Model.Name));
        string Resolve(string text) => text.Replace(Owner, name, StringComparison.Ordinal);
        string? Documentation(PropertyInfo member, SharedClientPart part) => part.Key switch
        {
            "state" or "command" or "query" => XmlDocumentation.Summary(member) ?? part.Documentation,
            "setter" => (XmlDocumentation.Summary(member) is { } summary ? summary + "\n\n" : "")
                + $"Calls the public C# setter of `{member.Name}` of the implementing ViewModel.",
            _ => part.Documentation,
        };
        void Append(StringBuilder text, string indent, string section)
        {
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (member, part) in shared.Where(entry => entry.Part.Section == section))
            {
                // A member shared by two commands, such as upload(), is declared once.
                if (!written.Add(part.Declaration)) continue;
                XmlDocumentation.Append(text, indent, Documentation(member, part));
                text.AppendLine(indent + Resolve(part.Declaration));
            }
        }

        var body = new StringBuilder();
        XmlDocumentation.Append(body, "", $"The state that every ViewModel implementing `{contract}` publishes (`[RunicSharedClient]`).");
        body.AppendLine($"export interface {name}State {{");
        Append(body, "  ", "state");
        body.AppendLine("}");
        body.AppendLine();
        if (shared.Any(entry => entry.Part.Section == "operations"))
        {
            Append(body, "", "operations");
            body.AppendLine();
        }
        var summary = XmlDocumentation.Summary(declaration.Interface);
        XmlDocumentation.Append(body, "", (summary is null ? "" : summary + "\n\n")
            + $"A connected ViewModel that implements `{contract}`: {implementers}. Their own clients have these members too.");
        body.AppendLine($"export interface {name}Client extends {Ref("ViewClient")}<{name}State> {{");
        if (shared.Any(entry => entry.Part.Section == "queries"))
        {
            body.AppendLine("  /** The rows of each query collection, which this presentation loads for its own range (`query-collections/1`). */");
            body.AppendLine("  readonly queries: {");
            Append(body, "    ", "queries");
            body.AppendLine("  };");
        }
        Append(body, "  ", "client");
        body.AppendLine("}");
        body.AppendLine();
        var kindUnion = string.Join(" | ", kinds.Select(kind => JsonSerializer.Serialize(kind)));
        XmlDocumentation.Append(body, "", $"The page reference of any View whose ViewModel implements `{contract}`. `connect()` returns the shared client;\n"
            + $"each implementing ViewModel's own page reference extends this one, and a `ViewRegistry` accepts a component for it at each of its kinds.");
        body.AppendLine($"export interface {declaration.ReferenceType} {{");
        body.AppendLine($"  readonly kind: {kindUnion};");
        body.AppendLine($"  connect(): Promise<{name}Client>;");
        body.AppendLine("}");
        body.AppendLine();
        XmlDocumentation.Append(body, "", $"Every kind of `{declaration.ReferenceType}`, for example to register one component for each with `registryEntries`.");
        body.AppendLine($"export const {declaration.Module}Kinds: readonly {declaration.ReferenceType}[\"kind\"][] = [{string.Join(", ", kinds.Select(kind => JsonSerializer.Serialize(kind)))}];");

        var text = body.ToString();
        var publicImports = PublicTypes.Where(own => text.Contains(Ref(own), StringComparison.Ordinal)).Select(own => Import(own));
        var module = new StringBuilder();
        module.AppendLine(GeneratedOutput.Header);
        module.AppendLine($"import type {{ {string.Join(", ", publicImports)} }} from \"@runic-artifex/views\";");
        foreach (var group in shared.SelectMany(entry => entry.Part.Imports ?? []).Distinct().GroupBy(entry => entry.Module, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
            module.AppendLine($"import type {{ {string.Join(", ", group.Select(entry => entry.Type).Order(StringComparer.Ordinal))} }} from \"./{group.Key}.js\";");
        module.AppendLine(TypeScriptModules.NamedTypeImports);
        module.AppendLine();
        module.Append(text);
        return module.ToString();
    }
}
