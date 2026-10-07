using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using Runic.Application.Views;

namespace Runic.Application.Testing;

// Maps ViewModel members to the names the generator uses for routes and wire fields.
internal static class RunicMembers
{
    internal static PropertyInfo Property(LambdaExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var body = expression.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs } unary)
            body = unary.Operand;
        if (body is MemberExpression { Member: PropertyInfo property, Expression: ParameterExpression }) return property;
        throw new ArgumentException($"Select a ViewModel property, such as vm => vm.Title, not '{expression}'.", nameof(expression));
    }

    internal static string WireName(PropertyInfo property) =>
        property.GetCustomAttribute<RunicAliasAttribute>(true)?.Name
        ?? property.GetCustomAttribute<JsonPropertyNameAttribute>(true)?.Name
        ?? LowerFirst(property.Name);

    // A generated command route is named after the property without "Command".
    internal static string CommandName(PropertyInfo property) =>
        property.Name.EndsWith("Command", StringComparison.Ordinal) && property.Name.Length > "Command".Length
            ? property.Name[..^"Command".Length]
            : throw new ArgumentException($"{property.DeclaringType?.Name}.{property.Name} is not a Bridge command; its name must end with Command.");

    internal static string PublicName(Type model) => model.Name.EndsWith("ViewModel", StringComparison.Ordinal)
        ? model.Name[..^"ViewModel".Length] : model.Name;

    // The generated root route: the lower-camel public name.
    internal static string RouteOf(Type model) => LowerFirst(PublicName(model));

    internal static string LowerFirst(string value) => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    // `[RunicCollection]` fields of a ViewModel and the wire name of each row key.
    internal static IReadOnlyDictionary<string, string> CollectionKeys(Type model)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in model.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetCustomAttribute<RunicCollectionAttribute>(true) is not { } collection) continue;
            var item = ItemType(property.PropertyType);
            var key = item?.GetProperty(collection.KeyProperty, BindingFlags.Instance | BindingFlags.Public);
            if (key is not null) keys[WireName(property)] = WireName(key);
        }
        return keys;
    }

    internal static string CollectionKey(Type model, string field) =>
        CollectionKeys(model).TryGetValue(field, out var key)
            ? key : throw new InvalidOperationException($"{model.Name}.{field} is not a [RunicCollection] with a key property.");

    // A row key as .NET writes it in a delta frame.
    internal static string KeyOf(System.Text.Json.JsonElement item, string key)
    {
        var value = item.GetProperty(key);
        return value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString()! : value.GetRawText();
    }

    private static Type? ItemType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        return (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? type : null)
            ?.GetGenericArguments()[0]
            ?? type.GetInterfaces().FirstOrDefault(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
    }
}
