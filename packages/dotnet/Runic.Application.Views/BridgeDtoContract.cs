using System.Reflection;
using System.Text.Json.Serialization;

namespace Runic.Application.Views;

// One exclusion rule for the build-time codecs, their subscription/validation
// metadata, and the reflection shape used by Hot Reload. The opt-in belongs to
// the model assembly, not to each DTO's assembly.
//
// Attributes are matched by name (see BridgeAttributeNames) and before the
// property's signature is resolved: an excluded property may have a type that
// the code generator cannot load, such as a WPF ImageSource.
internal static class BridgeDtoContract
{
    private const string RunicIgnore = "Runic.Application.Views.RunicIgnoreAttribute";
    private const string RunicBridgeJsonIgnore = "Runic.Application.Views.RunicBridgeJsonIgnoreAttribute";
    private const string JsonIgnore = "System.Text.Json.Serialization.JsonIgnoreAttribute";

    internal static bool HonorsJsonIgnore(Assembly modelAssembly) =>
        BridgeAttributeNames.Has(modelAssembly, RunicBridgeJsonIgnore);

    // [RunicIgnore] on the property or on a base property it overrides: an
    // attribute on a property is inherited by its overrides.
    internal static bool IsRunicIgnored(PropertyInfo property) =>
        InheritanceChain(property).Any(declared => BridgeAttributeNames.Has(declared, RunicIgnore));

    internal static bool IsIgnored(PropertyInfo property, bool honorJsonIgnore) =>
        IsRunicIgnored(property) ||
        honorJsonIgnore && InheritanceChain(property).FirstOrDefault(declared => BridgeAttributeNames.Has(declared, JsonIgnore)) is { } annotated
            && annotated.GetCustomAttribute<JsonIgnoreAttribute>(inherit: false)?.Condition == JsonIgnoreCondition.Always;

    /// <summary>
    /// The property, then each base property it overrides, nearest first: the
    /// properties whose attributes <c>GetCustomAttribute(inherit: true)</c>
    /// reads. Reflection finds a base property by its signature, which resolves
    /// the property type; this matches the accessors' virtual slot instead.
    /// </summary>
    internal static IEnumerable<PropertyInfo> InheritanceChain(PropertyInfo property)
    {
        for (PropertyInfo? current = property; current is not null; current = Overridden(current))
            yield return current;
    }

    private static PropertyInfo? Overridden(PropertyInfo property)
    {
        var accessor = property.GetMethod ?? property.SetMethod;
        if (accessor is not { IsVirtual: true } || accessor.DeclaringType is not { IsInterface: false } declaring) return null;
        var root = accessor.GetBaseDefinition();
        if (SameMethod(root, accessor)) return null;
        for (var type = declaring.BaseType; type is not null; type = type.BaseType)
            foreach (var candidate in type.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (candidate.Name == property.Name
                    && (property.GetMethod is null ? candidate.SetMethod : candidate.GetMethod) is { IsVirtual: true } other
                    && SameMethod(other.GetBaseDefinition(), root))
                    return candidate;
        return null;
    }

    private static bool SameMethod(MethodInfo left, MethodInfo right) =>
        left.MetadataToken == right.MetadataToken && left.Module.Equals(right.Module);
}
