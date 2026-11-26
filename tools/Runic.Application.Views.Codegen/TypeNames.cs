using System.Text;
using System.Text.Json;

/// <summary>
/// The language-neutral names and identities of the named types (enums, DTOs, unions) of a contract: the identity
/// that tells two uses of one .NET type apart (<see cref="Shape"/>), the base name a target language names the type
/// from, and the qualifiers that tell colliding names apart. <see cref="ContractIr"/> writes them as the contract IR's
/// name hints, from which the emitter names the TypeScript declarations.
/// </summary>
internal static class TypeNames
{
    internal static bool IsNamed(BridgeTypeNode node) =>
        node.Kind is BridgeWireKind.Enum or BridgeWireKind.Dto or BridgeWireKind.Union;

    // A named node's included members and their nullability, down to the named
    // types they contain. The same DTO can have different exclusions when
    // ViewModels declared in different assemblies use different policies.
    internal static string Shape(BridgeTypeNode node) => node.Kind switch
    {
        BridgeWireKind.Dto => string.Join(",", node.Members.Select(member => JsonSerializer.Serialize(member.WireName) + ":" + Use(member.Type))),
        BridgeWireKind.Union => string.Join(",", node.Cases.Select(@case => Shape(@case.Type))),
        _ => "",
    };

    private static string Use(BridgeTypeNode node) => (node.IsNullable ? "?" : "")
        + (node.Element is { } element ? "[" + Use(element) + "]" : "")
        + (node.Value is { } value ? "{" + Use(value) + "}" : "")
        + (IsNamed(node) ? "(" + Shape(node) + ")" : "");

    // Declaring types, then namespace segments, innermost first.
    internal static List<string> Qualifiers(Type type)
    {
        var result = new List<string>();
        for (var outer = type.DeclaringType; outer is not null; outer = outer.DeclaringType) result.Add(BaseName(outer, null));
        if (type.Namespace is { } ns)
            result.AddRange(ns.Split('.').Reverse().Where(segment => segment.Length > 0)
                .Select(segment => Identifier(char.ToUpperInvariant(segment[0]) + segment[1..])));
        return result;
    }

    // Page<NoteRow> -> PageOfNoteRow, Box<string[]> -> BoxOfArrayOfString,
    // and with annotations Box<string?> -> BoxOfNullableOfString like
    // Box<int?> -> BoxOfNullableOfInt32. The result is always a TypeScript
    // identifier.
    internal static string BaseName(Type type, TypeNullability? nullability)
    {
        if (type.IsArray) return "ArrayOf" + ArgumentName(type.GetElementType()!, nullability?.Element);
        if (type.IsByRef || type.IsPointer) return BaseName(type.GetElementType()!, nullability);
        if (Nullable.GetUnderlyingType(type) is { } underlying) return "NullableOf" + BaseName(underlying, nullability);
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0) name = name[..tick];
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
            name += "Of" + string.Join("And", type.GenericTypeArguments.Select((argument, index) =>
                ArgumentName(argument, nullability?.Argument(index))));
        return Identifier(name);
    }

    private static string ArgumentName(Type type, TypeNullability? nullability) =>
        (nullability?.IsNullable == true && !type.IsValueType ? "NullableOf" : "") + BaseName(type, nullability);

    private static string Identifier(string name)
    {
        var result = new StringBuilder(name.Length);
        foreach (var character in name)
            if (char.IsLetterOrDigit(character) || character is '_' or '$') result.Append(character);
        if (result.Length == 0 || char.IsDigit(result[0])) result.Insert(0, '_');
        return result.ToString();
    }
}
