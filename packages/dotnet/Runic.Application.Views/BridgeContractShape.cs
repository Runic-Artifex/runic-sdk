using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;

namespace Runic.Application.Views;

/// <summary>
/// The generated Bridge contract that a Debug process can reconstruct from its
/// compiled model and presentation types. This deliberately follows only the
/// supported code-generator surface; it is not a general-purpose .NET ABI hash.
/// </summary>
public static class BridgeContractShape
{
    /// <summary>
    /// Computes the contract fingerprint embedded into generated Bridges. A
    /// fingerprint covers every model discovered by the current generator
    /// convention because a model can appear in another model's content union
    /// or DI composition.
    /// </summary>
    public static string Compute([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var views = DiscoverViews(model.Assembly);
        var models = DiscoverModels(model, views);
        var nullability = new NullabilityInfoContext();
        var parts = new List<string> { "runic-bridge-contract-v2" };

        foreach (var knownModel in models.OrderBy(TypeIdentity, StringComparer.Ordinal))
        {
            parts.Add($"model:{TypeIdentity(knownModel)}:public-name:{PublicName(knownModel)}");
            AppendModelMembers(parts, knownModel, models, nullability);
            AppendCompositionShape(parts, knownModel, models);
        }

        foreach (var view in views.OrderBy(view => TypeIdentity(view.ViewType), StringComparer.Ordinal))
            parts.Add($"view:{TypeIdentity(view.ViewType)}:model:{TypeIdentity(view.ModelType)}:contract:{view.Contract ?? "default"}");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))));
    }

    private static void AppendModelMembers(List<string> parts, Type model, IReadOnlyList<Type> models,
        NullabilityInfoContext nullability)
    {
        var properties = PublicDeclaredProperties(model)
            .Where(property => !HasAttribute(property, nameof(RunicIgnoreAttribute)))
            .OrderBy(property => property.MetadataToken);

        foreach (var property in properties)
        {
            var kind = typeof(ICommand).IsAssignableFrom(property.PropertyType) ? "command" : "state";
            var access = property.SetMethod?.IsPublic == true ? "write" : "read";
            parts.Add($"member:{TypeIdentity(model)}:{property.Name}:{TypeIdentity(property.PropertyType)}:{access}:{kind}");

            if (kind == "command") continue;

            var contentModels = ContentModels(property, model, models);
            if (contentModels.Count > 0)
            {
                var contract = ContractFor(property) ?? "default";
                parts.Add($"content:{TypeIdentity(model)}:{property.Name}:nullable:{IsReadNullable(property, nullability)}:contract:{contract}");
                foreach (var contentModel in contentModels.OrderBy(TypeIdentity, StringComparer.Ordinal))
                    parts.Add($"content-target:{TypeIdentity(model)}:{property.Name}:{TypeIdentity(contentModel)}:{PublicName(contentModel)}");
                continue;
            }

            // Only nullable strings alter the generated ordinary-state and
            // setter TypeScript types. The generator presently ignores other
            // CLR nullable annotations.
            if (property.PropertyType == typeof(string))
                parts.Add($"string-nullability:{TypeIdentity(model)}:{property.Name}:{IsReadNullable(property, nullability)}");

            if (TryListItem(property.PropertyType, out var item))
                AppendListCodec(parts, model, property, item!, nullability);
        }

        // Observable validation changes every emitted state property by adding
        // its accompanying error array.
        parts.Add($"validation-errors:{TypeIdentity(model)}:{typeof(INotifyDataErrorInfo).IsAssignableFrom(model)}");
    }

    private static void AppendListCodec(List<string> parts, Type model, PropertyInfo property, Type item,
        NullabilityInfoContext nullability)
    {
        parts.Add($"list-codec:{TypeIdentity(model)}:{property.Name}:{TypeIdentity(item)}");
        foreach (var member in PublicDeclaredProperties(item).OrderBy(member => member.MetadataToken))
        {
            // This is deliberately the same supported primitive surface used by
            // the code generator's generated Utf8JsonWriter calls.
            var writer = member.PropertyType == typeof(int) ? "number"
                : member.PropertyType == typeof(bool) ? "boolean"
                : member.PropertyType == typeof(string) ? "string"
                : "unsupported";
            var readable = member.GetMethod?.IsPublic == true;
            parts.Add($"list-member:{TypeIdentity(item)}:{member.Name}:{TypeIdentity(member.PropertyType)}:{readable}:{writer}" +
                (member.PropertyType == typeof(string) ? $":nullable:{IsReadNullable(member, nullability)}" : string.Empty));
        }
    }

    private static void AppendCompositionShape(List<string> parts, Type model, IReadOnlyList<Type> models)
    {
        var hasContent = PublicDeclaredProperties(model)
            .Where(property => !HasAttribute(property, nameof(RunicIgnoreAttribute)))
            .Any(property => ContentModels(property, model, models).Count > 0);
        parts.Add($"composition:{TypeIdentity(model)}:content:{hasContent}");
    }

    private static IReadOnlyList<Type> DiscoverModels(Type requestedModel, IReadOnlyList<PresentationView> views)
    {
        return views.Select(view => view.ModelType).Append(requestedModel).Distinct().ToArray();
    }

    private static IReadOnlyList<PresentationView> DiscoverViews(Assembly assembly) =>
        LoadableTypes(assembly)
            .Where(type => !type.IsAbstract && type.IsClass && type.IsPublic && !type.IsNested)
            .Select(type => (ViewType: type, ModelType: ViewModelFor(type)))
            .Where(candidate => candidate.ModelType is not null)
            .Select(candidate => new PresentationView(candidate.ViewType, candidate.ModelType!, ContractFor(candidate.ViewType)))
            .ToArray();

    private static Type? ViewModelFor(Type view)
    {
        for (var current = view.BaseType; current is not null; current = current.BaseType)
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(RunicView<>))
                return current.GenericTypeArguments[0];
        return null;
    }

    private static IReadOnlyList<Type> ContentModels(PropertyInfo property, Type owner, IReadOnlyList<Type> models) =>
        property.PropertyType == typeof(object)
            ? []
            : models.Where(model => model != owner && property.PropertyType.IsAssignableFrom(model)).ToArray();

    private static IEnumerable<PropertyInfo> PublicDeclaredProperties(Type type) =>
        type.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0);

    private static bool TryListItem(Type type, out Type? item)
    {
        item = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
            ? type.GenericTypeArguments[0]
            : null;
        return item is { IsClass: true, IsPublic: true } && item != typeof(string);
    }

    private static bool IsReadNullable(PropertyInfo property, NullabilityInfoContext nullability) =>
        nullability.Create(property).ReadState == NullabilityState.Nullable;

    private static bool HasAttribute(MemberInfo member, string attributeName) =>
        member.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == $"{typeof(RunicIgnoreAttribute).Namespace}.{attributeName}");

    private static string? ContractFor(MemberInfo member)
    {
        var attribute = member.CustomAttributes.FirstOrDefault(candidate =>
            candidate.AttributeType.FullName == typeof(RunicViewContractAttribute).FullName);
        return attribute?.ConstructorArguments.SingleOrDefault().Value as string;
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { return error.Types.OfType<Type>(); }
    }

    private static string PublicName(Type model) => model.Name.EndsWith("ViewModel", StringComparison.Ordinal)
        ? model.Name[..^"ViewModel".Length]
        : model.Name;

    private static string TypeIdentity(Type type) => type.FullName ?? type.Name;

    private sealed record PresentationView(Type ViewType, Type ModelType, string? Contract);
}
