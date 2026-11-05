using System.Reflection;
using System.Text;
using System.Text.Json;
using Runic.Application.Views;
using System.Text.Json.Serialization;
using System.ComponentModel;

// Emits the delegate graph consumed by BridgeDataSubscriptions. The runtime
// follows only these direct accesses, so nested DTO/list changes stay AOT-safe.
internal static class BridgeDataSubscriptionEmitter
{
    internal static void AppendMetadata(StringBuilder source, string modelType,
        IEnumerable<KeyValuePair<PropertyInfo, BridgeTypeGraph>> properties)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.AppendLine("        [");
        foreach (var (property, graph) in properties)
        {
            var getter = $"static owner => (({modelType})owner).{property.Name}";
            AppendMember(source, WireName(property), property.Name, getter, graph.Root, 12, ValidationProperty(property));
            source.AppendLine(",");
        }
        source.AppendLine("        ]");
    }

    private static void AppendMember(StringBuilder source, string name, string propertyName, string getter, BridgeTypeNode node, int indent,
        string? validationProperty = null)
    {
        var pad = new string(' ', indent);
        source.Append($"{pad}new global::Runic.Application.Views.Generated.BridgeDataSubscriptionMember({Quote(name)}, {getter}");
        var children = Children(node);
        if (children.Count > 0)
        {
            source.Append(", children: [");
            source.AppendLine();
            for (var index = 0; index < children.Count; index++)
            {
                AppendMember(source, children[index].Name, children[index].PropertyName, children[index].Getter, children[index].Node, indent + 4,
                    children[index].ValidationProperty);
                source.AppendLine(index == children.Count - 1 ? string.Empty : ",");
            }
            source.Append($"{pad}]");
        }
        if (node.Kind is BridgeWireKind.Array or BridgeWireKind.List)
        {
            source.Append(", enumerateChildren: static value => (global::System.Collections.IEnumerable)value");
            source.Append(", enumerateValidationChildren: static value => global::Runic.Application.Views.Generated.BridgeValidation.EnumerateIndexed((global::System.Collections.IEnumerable)value)");
        }
        else if (node.Kind is BridgeWireKind.StringDictionary)
        {
            var valueType = BridgeTypeGraph.CSharpType(node.Value!.Type);
            source.Append($", enumerateChildren: static value => global::System.Linq.Enumerable.Select((global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.KeyValuePair<string, {valueType}>>)value, static entry => (object?)entry.Value)");
            source.Append($", enumerateValidationChildren: static value => global::Runic.Application.Views.Generated.BridgeValidation.EnumerateDictionary((global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.KeyValuePair<string, {valueType}>>)value)");
        }
        if (name == "$items") source.Append(", isPathTransparent: true");
        source.Append($", propertyName: {Quote(propertyName)})");
        if (validationProperty is not null) source.Append($" {{ ValidationProperty = {validationProperty} }}");
    }

    // translation-text/1 (RunicValidationOptions.TextFor): a member with validation attributes names its property, read
    // with a constant name so that a trimmed application keeps its metadata and attributes. DeclaredOnly on the
    // declaring type finds the property itself where it hides an inherited one with new, for which a lookup on the
    // owner throws AmbiguousMatchException. C# makes a base type at least as accessible as the type deriving from it.
    private static string? ValidationProperty(PropertyInfo property) =>
        property.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.ValidationAttribute), true).Length == 0 ? null
            : $"static () => typeof({BridgeTypeGraph.CSharpType(property.DeclaringType!)}).GetProperty({Quote(property.Name)}, " +
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
                $"static owner => (({BridgeTypeGraph.CSharpType(node.NonNullableType)})owner).{member.Property.Name}", member.Type,
                ValidationProperty(member.Property))).ToList();
        if (node.Kind is BridgeWireKind.Union)
            return node.Cases.SelectMany(@case => @case.Type.Members.Select(member => new Child(member.WireName, member.Property.Name,
                $"static owner => owner is {BridgeTypeGraph.CSharpType(@case.Type.Type)} current ? current.{member.Property.Name} : null", member.Type,
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
            ? [new Child("$items", "$items", "static owner => owner", element)]
            : Children(element);

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static string WireName(PropertyInfo property) =>
        property.GetCustomAttribute<RunicAliasAttribute>(true)?.Name ??
        property.GetCustomAttribute<JsonPropertyNameAttribute>(true)?.Name ??
        char.ToLowerInvariant(property.Name[0]) + property.Name[1..];

    private sealed record Child(string Name, string PropertyName, string Getter, BridgeTypeNode Node, string? ValidationProperty = null);
}
