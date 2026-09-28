using System.Reflection;
using System.Text;

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
            AppendMember(source, property.Name, getter, graph.Root, 12, root: true);
            source.AppendLine(",");
        }
        source.AppendLine("        ]");
    }

    private static void AppendMember(StringBuilder source, string name, string getter, BridgeTypeNode node, int indent, bool root = false)
    {
        var pad = new string(' ', indent);
        source.Append($"{pad}new global::Runic.Application.Views.BridgeDataSubscriptionMember(\"{name}\", {getter}");
        var children = Children(node);
        if (children.Count > 0)
        {
            source.Append(", children: [");
            source.AppendLine();
            for (var index = 0; index < children.Count; index++)
            {
                AppendMember(source, children[index].Name, children[index].Getter, children[index].Node, indent + 4);
                source.AppendLine(index == children.Count - 1 ? string.Empty : ",");
            }
            source.Append($"{pad}]");
        }
        if (node.Kind is BridgeWireKind.Array or BridgeWireKind.List)
            source.Append(", enumerateChildren: static value => (global::System.Collections.IEnumerable)value");
        else if (node.Kind is BridgeWireKind.StringDictionary)
            source.Append($", enumerateChildren: static value => global::System.Linq.Enumerable.Select((global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.KeyValuePair<string, {BridgeTypeGraph.CSharpType(node.Value!.Type)}>>)value, static entry => (object?)entry.Value)");
        source.Append(")");
    }

    private static List<Child> Children(BridgeTypeNode node)
    {
        if (node.Kind is BridgeWireKind.Array or BridgeWireKind.List) return Children(node.Element!);
        if (node.Kind is BridgeWireKind.StringDictionary) return Children(node.Value!);
        if (node.Kind is BridgeWireKind.Dto)
            return node.Members.Select(member => new Child(member.WireName,
                $"static owner => (({BridgeTypeGraph.CSharpType(node.Type)})owner).{member.Property.Name}", member.Type)).ToList();
        if (node.Kind is BridgeWireKind.Union)
            return node.Cases.SelectMany(@case => @case.Type.Members.Select(member => new Child(member.WireName,
                $"static owner => owner is {BridgeTypeGraph.CSharpType(@case.Type.Type)} current ? current.{member.Property.Name} : null", member.Type))).ToList();
        return [];
    }

    private sealed record Child(string Name, string Getter, BridgeTypeNode Node);
}
