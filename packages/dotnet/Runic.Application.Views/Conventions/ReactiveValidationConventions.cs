using System.Reflection;

namespace Runic.Application.Views;

// ReactiveUI.Validation's IValidatableViewModel, matched by name (no Runic package references ReactiveUI.Validation):
// RunicArtifex.ReactiveUI.Validation (namespace root ReactiveUI.Validation) and its System.Reactive flavor
// (ReactiveUI.Validation.Reactive). A ViewModel or DTO that implements one has the validation member, read and
// observed through its ValidationContext (D033, D053.3), whether or not it implements INotifyDataErrorInfo.
internal static class ReactiveValidationConventions
{
    internal const string InterfaceName = "IValidatableViewModel";
    internal const string ContextProperty = "ValidationContext";

    // The flavor's namespace root by the full name of its IValidatableViewModel.
    private static readonly Dictionary<string, string> Flavors = new(StringComparer.Ordinal)
    {
        ["ReactiveUI.Validation.Abstractions.IValidatableViewModel"] = "ReactiveUI.Validation",
        ["ReactiveUI.Validation.Reactive.Abstractions.IValidatableViewModel"] = "ReactiveUI.Validation.Reactive",
    };

    // The namespace roots of the flavors whose IValidatableViewModel the type is or implements.
    internal static IEnumerable<string> FlavorsOf(Type type) => Interfaces(type)
        .Select(candidate => candidate.FullName is { } name && Flavors.TryGetValue(name, out var flavor) ? flavor : null)
        .OfType<string>();

    internal static bool IsValidatable(Type type) => FlavorsOf(type).Any();

    // The interfaces named IValidatableViewModel that the type is or implements and that are no known flavor's.
    internal static IEnumerable<Type> Unrecognized(Type type) => Interfaces(type)
        .Where(candidate => candidate.Name == InterfaceName && (candidate.FullName is not { } name || !Flavors.ContainsKey(name)));

    // The ValidationContext property of a type that implements a known IValidatableViewModel: the validation projection
    // represents it, so it is not part of the contract.
    internal static bool IsContextProperty(PropertyInfo property) =>
        property.Name == ContextProperty && property.DeclaringType is { } owner && IsValidatable(owner);

    private static Type[] Interfaces(Type type)
    {
        Type[] interfaces;
        try { interfaces = type.GetInterfaces(); }
        catch (Exception error) when (error is TypeLoadException or FileNotFoundException or ReflectionTypeLoadException)
        {
            interfaces = [];
        }
        return type.IsInterface ? [type, .. interfaces] : interfaces;
    }
}
