using System.Reflection;
using Runic.Application.Views;

/// <summary>
/// One ViewModel's Bridge contract, discovered and validated (<c>PlanView</c> in Program.cs): its members, commands,
/// interactions and view requests with their value graphs, and what the client offers for each. The C# emission
/// (<c>EmitView</c>) and the contract IR (<see cref="ContractIr"/>), from which the emitter writes the TypeScript, read
/// it; planning writes no output.
/// </summary>
internal sealed class ViewPlan
{
    internal required Type Model { get; init; }
    /// <summary>The public name, such as <c>Counter</c> for <c>CounterViewModel</c>.</summary>
    internal required string ShortName { get; init; }
    /// <summary>The route prefix and module name, the public name with a lowercase first letter.</summary>
    internal required string Prefix { get; init; }
    internal required (Type Model, string Name)[] KnownModels { get; init; }
    internal required IReadOnlyDictionary<Type, List<Type>>? ViewTypes { get; init; }
    /// <summary>
    /// The View's fingerprint (<c>runic.views.fingerprint/1</c>), a hash of its contract IR, which <see cref="Seal"/>
    /// sets once the IR is written.
    /// </summary>
    internal string ContractFingerprint { get; private set; } = "";
    /// <summary>
    /// The reflection shape hash of the model's assembly (<c>BridgeContractShape</c>), which the generated Bridge passes
    /// on for Hot Reload to recompute.
    /// </summary>
    internal required string ContractShape { get; init; }
    internal required NullabilityInfoContext Nullability { get; init; }

    internal required GeneratedInteractionPlan[] Interactions { get; set; }
    internal required GeneratedViewRequestPlan[] Requests { get; set; }
    internal required PropertyInfo[] Commands { get; init; }
    /// <summary>The state properties in declaration order.</summary>
    internal required PropertyInfo[] Properties { get; init; }
    internal required IReadOnlyList<SharedClientDeclaration> SharedClients { get; init; }

    internal required Dictionary<PropertyInfo, (Type Model, string Name)[]> ContentProperties { get; init; }
    internal required Dictionary<PropertyInfo, (Type Model, string Name)[]> ContentCollections { get; init; }
    internal required HashSet<PropertyInfo> RegionSlots { get; init; }
    internal required HashSet<PropertyInfo> SelectionSlots { get; init; }
    internal required Dictionary<PropertyInfo, (Type Model, string Name)[]> RegionCollections { get; init; }
    internal required KeyValuePair<PropertyInfo, (Type Model, string Name)[]>[] ContentBindings { get; init; }
    internal required Dictionary<PropertyInfo, BridgeTypeGraph> ValueProperties { get; init; }
    internal required Dictionary<PropertyInfo, (BridgeTypeGraph Rows, Type Key)> QueryCollections { get; init; }
    /// <summary>Keyed collections and their key row property.</summary>
    internal required Dictionary<PropertyInfo, PropertyInfo> IncrementalCollections { get; init; }
    internal required Dictionary<PropertyInfo, MethodInfo> RowEditors { get; init; }
    internal required PropertyInfo[] TextFields { get; init; }
    internal required PropertyInfo[] TextMembers { get; init; }
    internal required bool HasContent { get; init; }

    internal required Dictionary<PropertyInfo, GeneratedCommandPlan> CommandPlans { get; init; }
    internal required Dictionary<PropertyInfo, MethodInfo?> CommandSources { get; init; }
    internal required string[] ScopedCommands { get; init; }

    internal required bool HasErrors { get; init; }
    internal required string[] ValidationContextFlavors { get; init; }
    internal required bool HasValidation { get; init; }
    internal required PropertyInfo[] CheckedProperties { get; init; }
    internal required Dictionary<PropertyInfo, List<BridgeFieldLeaf>> NestedPaths { get; init; }
    internal required Dictionary<PropertyInfo, List<BridgeFieldLeaf>> RowPaths { get; init; }
    internal required bool HasFieldPaths { get; init; }
    internal required bool UsesUploads { get; init; }
    /// <summary>Interactions and view requests are attached to the presentations of a WindowContentSession.</summary>
    internal required bool Presented { get; init; }

    /// <summary>
    /// Sets the View's fingerprint and the contracts of its interactions and view requests, which contain it, with the
    /// IR's UpperCamelCase name (ContractIr.Seal).
    /// </summary>
    internal void Seal(string fingerprint)
    {
        ContractFingerprint = fingerprint;
        Interactions = [.. Interactions.Select(plan => plan with
        {
            ContractId = ContractIr.InteractionContract(fingerprint, "interaction", UpperFirst(plan.Property.Name)),
        })];
        Requests = [.. Requests.Select(plan => plan with
        {
            ContractId = ContractIr.InteractionContract(fingerprint, "request", UpperFirst(plan.Property.Name)),
        })];
    }

    private static string UpperFirst(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Whether the page sets the property: a public setter, unless the property holds a RunicBlob (blobs/1).</summary>
    internal static bool IsPageSettable(PropertyInfo property, IReadOnlyDictionary<PropertyInfo, BridgeTypeGraph> valueProperties) =>
        property.SetMethod?.IsPublic == true
        && !(valueProperties.TryGetValue(property, out var graph) && graph.Nodes.Any(node => node.Kind == BridgeWireKind.Blob));

    internal bool PageSettable(PropertyInfo property) => IsPageSettable(property, ValueProperties);

    /// <summary>
    /// D072.13: a development-only state property or command (<c>[RunicDevelopmentOnly]</c>), which a trimmed or
    /// NativeAOT publish leaves out.
    /// </summary>
    internal bool DevelopmentOnly(PropertyInfo member) => CommandPlans.ContainsKey(member)
        ? BridgeCommandSources.IsDevelopmentOnly(Model, member)
        : member.GetCustomAttribute<RunicDevelopmentOnlyAttribute>(true) is not null;

    /// <summary>
    /// Whether presented content may be null. A region slot presents its Current, whose nullability (always nullable)
    /// is the slot's wire and TypeScript nullability.
    /// </summary>
    // DeclaredOnly: NavigationRegion<T>.Current hides the non-generic base's object-typed Current.
    internal bool IsNullableContent(PropertyInfo property) => (RegionSlots.Contains(property)
        ? Nullability.Create(property.PropertyType.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!)
        : Nullability.Create(property)).ReadState == NullabilityState.Nullable;
}
