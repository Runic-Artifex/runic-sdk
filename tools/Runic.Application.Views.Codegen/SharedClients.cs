using System.Reflection;
using System.Text.Json.Nodes;
using Runic.Application.Views;

// [RunicSharedClient] (#839): one TypeScript client for the ViewModels that implement an interface, so one web
// component renders all of them. Discover finds the interfaces; once every ViewModel is planned, Check reports what only
// .NET can tell (an interface member that an implementing ViewModel does not publish, publishes as an interaction, or
// declares with another nullability), and the checked shared clients go to the contract IR. The emitter compares the
// client parts each implementing View contributes (RUNICBRIDGE036) and writes `{name}.ts` with `{Name}State`,
// `{Name}Client`, `{Name}PageReference` and `{name}Kinds`. Nothing changes for a ViewModel that implements no such
// interface.

/// <summary>A <c>[RunicSharedClient]</c> interface and the generated ViewModels that implement it.</summary>
internal sealed record SharedClientDeclaration(Type Interface, string Name, (Type Model, string Name)[] Implementers)
{
    internal string Module => char.ToLowerInvariant(Name[0]) + Name[1..];
    internal string ReferenceType => $"{Name}PageReference";
}

internal static class SharedClients
{
    private const string AttributeName = "Runic.Application.Views.RunicSharedClientAttribute";

    // What each Bridge member of an implementing ViewModel publishes: whether the page can set it, or why a shared
    // client cannot include it.
    private static readonly Dictionary<Type, Dictionary<PropertyInfo, (bool Settable, string? Unsupported)>> Surfaces = [];
    private static readonly Dictionary<Type, SharedClientDeclaration[]> ByModel = [];
    // The shared clients Check found without a problem, with the property that publishes each interface member in
    // every implementing ViewModel, for the contract IR.
    private static readonly List<(SharedClientDeclaration Declaration, (PropertyInfo Member, (Type Model, PropertyInfo Published)[] Implementations)[] Members)> Checked = [];

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

    /// <summary>
    /// Records a Bridge member of an implementing ViewModel: a member or command, with whether the page can set it, or
    /// an interaction or view request, which a shared client cannot include.
    /// </summary>
    internal static void Record(Type model, PropertyInfo property, bool settable = false, string? unsupported = null)
    {
        if (!ByModel.ContainsKey(model)) return;
        if (!Surfaces.TryGetValue(model, out var surface)) Surfaces.Add(model, surface = []);
        surface[property] = (settable, unsupported);
    }

    /// <summary>
    /// Checks the implementing ViewModels of every shared client. Each problem is reported; a shared client with one is
    /// left out of the contract IR.
    /// </summary>
    internal static void Check(IReadOnlyList<SharedClientDeclaration> declarations)
    {
        // A member of an interface that another shared interface extends is checked for both; it is reported once.
        var reportedMembers = new HashSet<(Type Model, Module Module, int Token)>();
        foreach (var declaration in declarations)
        {
            PropertyInfo[] members;
            try { members = Members(declaration.Interface); }
            catch (Exception error) when (Diagnostics.IsReportable(error)) { Diagnostics.Report(error); continue; }
            var collected = new List<(Type Model, Dictionary<PropertyInfo, PropertyInfo> Members)>();
            foreach (var (model, _) in declaration.Implementers)
            {
                try { collected.Add((model, Collect(declaration, model, members))); }
                catch (MemberProblem problem)
                {
                    if (reportedMembers.Add((model, problem.Member.Module, problem.Member.MetadataToken))) Diagnostics.Report(problem.Error, model);
                }
                catch (Exception error) when (Diagnostics.IsReportable(error)) { Diagnostics.Report(error, model); }
            }
            if (collected.Count != declaration.Implementers.Length) continue;
            Checked.Add((declaration, [.. members.Select(member =>
                (member, collected.Select(entry => (entry.Model, entry.Members[member])).ToArray()))]));
        }
    }

    /// <summary>
    /// The checked shared clients in the contract IR (specs/application/contract-ir.md). <paramref name="published"/>
    /// names the View member or command of a property that an implementing ViewModel publishes.
    /// </summary>
    internal static IEnumerable<JsonObject> ContractIr(Func<Type, PropertyInfo, JsonObject> published) => Checked.Select(entry =>
    {
        var (declaration, members) = entry;
        var item = new JsonObject { ["id"] = declaration.Interface.FullName, ["name"] = declaration.Name, ["interface"] = declaration.Interface.Name };
        if (XmlDocumentation.Summary(declaration.Interface) is { } doc) item["doc"] = doc;
        if (ClientModules.OwnerOfModule(declaration.Module) is { } owner) item["owner"] = owner.Assembly;
        item["views"] = new JsonArray([.. declaration.Implementers.Select(implementer => (JsonNode)implementer.Model.FullName!)]);
        item["members"] = new JsonArray([.. members.Select(member =>
        {
            var name = member.Member.Name;
            var result = new JsonObject { ["name"] = char.ToUpperInvariant(name[0]) + name[1..] };
            if (XmlDocumentation.Summary(member.Member) is { } memberDoc) result["doc"] = memberDoc;
            if (member.Member.SetMethod is not null) result["settable"] = true;
            result["published"] = new JsonObject(member.Implementations.Select(implementation =>
                KeyValuePair.Create(implementation.Model.FullName!, (JsonNode?)published(implementation.Model, implementation.Published))));
            return (JsonNode)result;
        })]);
        return item;
    });

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

    // The published property of each interface member in one implementing ViewModel.
    private static Dictionary<PropertyInfo, PropertyInfo> Collect(SharedClientDeclaration declaration, Type model, PropertyInfo[] members)
    {
        var recorded = Surfaces.GetValueOrDefault(model) ?? [];
        var nullability = new NullabilityInfoContext();
        var result = new Dictionary<PropertyInfo, PropertyInfo>();
        foreach (var member in members)
        {
            try { result.Add(member, CollectMember(declaration, model, member, recorded, nullability)); }
            catch (BridgeDiagnosticException error) { throw new MemberProblem(member, error); }
        }
        return result;
    }

    // A member's problem in one implementing ViewModel; Check reports it once for the shared clients that include it.
    private sealed class MemberProblem(PropertyInfo member, BridgeDiagnosticException error) : Exception(error.Message, error)
    {
        internal PropertyInfo Member { get; } = member;
        internal BridgeDiagnosticException Error { get; } = error;
    }

    private static PropertyInfo CollectMember(SharedClientDeclaration declaration, Type model,
        PropertyInfo member, Dictionary<PropertyInfo, (bool Settable, string? Unsupported)> recorded, NullabilityInfoContext nullability)
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
        var (settable, unsupported) = recorded[published];
        if (unsupported is not null)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.SharedClient,
                $"{model.Name}.{published.Name}: {source} of the shared client {declaration.Name} is {unsupported}", published);
        var expected = nullability.Create(member);
        var actual = nullability.Create(published);
        if (!SameNullability(expected, actual, member.SetMethod is not null))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.SharedClient,
                $"{model.Name}.{published.Name} is {Show(actual)}, but {source} of the shared client {declaration.Name} is {Show(expected)}. Declare the same nullability.",
                published);
        // The interface decides whether the shared client sets the member.
        if (member.SetMethod is not null && !settable)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.SharedClient,
                $"{model.Name}.{published.Name}: {source} of the shared client {declaration.Name} is settable, but the page cannot set {published.Name}. Make the interface property get-only.",
                published);
        return published;
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
}
