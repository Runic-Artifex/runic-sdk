using System.Reflection;
using System.Text;
using System.Text.Json;
using Runic.Application.Views;
using System.Text.Json.Serialization;
using System.ComponentModel;

// Describes the data members that BridgeDataSubscriptions observes, as D records of the Bridge's table (S4) and the
// arms that read them. The runtime follows only these direct accesses, so nested DTO/list changes stay AOT-safe.
internal static class BridgeDataSubscriptionEmitter
{
    /// <param name="properties">The ViewModel's data members with their property ids, which their Get arms read.</param>
    /// <param name="developmentOnly">Whether a member is development-only: its records and arms, its children's too, are marked.</param>
    internal static void AppendTable(BridgeTableBuilder table, IEnumerable<(PropertyInfo Property, int Id, BridgeTypeGraph Graph)> properties,
        Func<PropertyInfo, bool> developmentOnly)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(developmentOnly);
        var next = 0;
        foreach (var (property, id, graph) in properties)
        {
            using var scope = table.DevelopmentOnly(developmentOnly(property));
            AppendMember(table, ref next, "-", WireName(property), property.Name, null, graph.Root, ValidationProperty(property), id);
        }
    }

    // A member's record, then its children's. A ViewModel member reads its property; a child reads its owner with its
    // ReadData arm, or is its owner ($items of a nested collection).
    private static void AppendMember(BridgeTableBuilder table, ref int next, string parent, string name, string propertyName,
        string? read, BridgeTypeNode node, string? validationProperty, int? property = null)
    {
        var id = next++;
        var flags = new System.Text.StringBuilder();
        if (property is null && read is null) flags.Append('I');
        else if (property is null) table.Return("ReadData", id, read!);
        if (node.Kind is BridgeWireKind.Array or BridgeWireKind.List) flags.Append('L');
        else if (node.Kind is BridgeWireKind.StringDictionary)
        {
            flags.Append('Y');
            var entries = $"(global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.KeyValuePair<string, {BridgeTypeGraph.CSharpType(node.Value!.Type)}>>)value";
            table.Return("Enumerate", id, $"global::System.Linq.Enumerable.Select({entries}, static entry => (object?)entry.Value)");
            table.Return("EnumerateValidation", id, $"global::Runic.Application.Views.Generated.BridgeValidation.EnumerateDictionary({entries})");
        }
        if (name == "$items") flags.Append('T');
        if (validationProperty is not null)
        {
            flags.Append('V');
            table.Return("ValidationProperty", id, validationProperty);
        }
        table.Record("D", Id(id), parent, name, propertyName, flags.Length == 0 ? "-" : flags.ToString(), property is { } value ? Id(value) : "-");
        foreach (var child in Children(node))
            AppendMember(table, ref next, Id(id), child.Name, child.PropertyName, child.Read, child.Node, child.ValidationProperty);
    }

    private static string Id(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // translation-text/1 (RunicValidationOptions.TextFor): a member with validation attributes names its property, read
    // with a constant name so that a trimmed application keeps its metadata and attributes. DeclaredOnly on the
    // declaring type finds the property itself where it hides an inherited one with new, for which a lookup on the
    // owner throws AmbiguousMatchException. C# makes a base type at least as accessible as the type deriving from it.
    private static string? ValidationProperty(PropertyInfo property) =>
        property.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.ValidationAttribute), true).Length == 0 ? null
            : $"typeof({BridgeTypeGraph.CSharpType(property.DeclaringType!)}).GetProperty({Quote(property.Name)}, " +
              "global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.DeclaredOnly)";

    private static List<Child> Children(BridgeTypeNode node)
    {
        if (node.Kind is BridgeWireKind.Array or BridgeWireKind.List)
            return CollectionElementChildren(node.Element!);
        if (node.Kind is BridgeWireKind.StringDictionary)
            return CollectionElementChildren(node.Value!);
        if (node.Kind is BridgeWireKind.Dto)
            return node.Members.Where(member => typeof(INotifyPropertyChanged).IsAssignableFrom(node.NonNullableType) ||
                typeof(INotifyDataErrorInfo).IsAssignableFrom(node.NonNullableType) || ReportsPending(node) || MayNotify(member.Type, []))
                .Select(member => new Child(member.WireName, member.Property.Name,
                $"(({BridgeTypeGraph.CSharpType(node.NonNullableType)})owner).{member.Property.Name}", member.Type,
                ValidationProperty(member.Property))).ToList();
        if (node.Kind is BridgeWireKind.Union)
            return node.Cases.SelectMany(@case => @case.Type.Members.Select(member => new Child(member.WireName, member.Property.Name,
                $"owner is {BridgeTypeGraph.CSharpType(@case.Type.Type)} current ? current.{member.Property.Name} : null", member.Type,
                ValidationProperty(member.Property)))).ToList();
        return [];
    }

    private static bool MayNotify(BridgeTypeNode node, HashSet<BridgeTypeNode> visited)
    {
        if (!visited.Add(node)) return false;
        if (node.Kind is BridgeWireKind.Array or BridgeWireKind.List or BridgeWireKind.StringDictionary ||
            node.Kind is BridgeWireKind.Dto && !node.NonNullableType.IsSealed ||
            typeof(INotifyPropertyChanged).IsAssignableFrom(node.NonNullableType) ||
            typeof(INotifyDataErrorInfo).IsAssignableFrom(node.NonNullableType) || ReportsPending(node)) return true;
        return node.Members.Any(member => MayNotify(member.Type, visited)) ||
            node.Cases.Any(@case => MayNotify(@case.Type, visited));
    }

    // C3: an object that reports validation in progress is observed, and its members resolve its pending paths.
    private static bool ReportsPending(BridgeTypeNode node) =>
        typeof(Runic.Application.Views.IRunicPendingValidation).IsAssignableFrom(node.NonNullableType);

    // A collection member's metadata describes one element, not the
    // collection itself. For nested collections, retain that intermediate
    // element as an owner with a self-reading edge. Otherwise List<List<T>>
    // would try to read T members from the inner List, and inner collection
    // mutations (including collections of scalar values) would be invisible.
    private static List<Child> CollectionElementChildren(BridgeTypeNode element) =>
        element.Kind is BridgeWireKind.Array or BridgeWireKind.List or BridgeWireKind.StringDictionary
            ? [new Child("$items", "$items", null, element)]
            : Children(element);

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static string WireName(PropertyInfo property) =>
        property.GetCustomAttribute<RunicAliasAttribute>(true)?.Name ??
        property.GetCustomAttribute<JsonPropertyNameAttribute>(true)?.Name ??
        char.ToLowerInvariant(property.Name[0]) + property.Name[1..];

    // Read reads the member from its owner, or is null for the owner itself.
    private sealed record Child(string Name, string PropertyName, string? Read, BridgeTypeNode Node, string? ValidationProperty = null);
}
