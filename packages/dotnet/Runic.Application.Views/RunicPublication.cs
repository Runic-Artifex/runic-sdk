using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Runic.Application.Views;

/// <summary>Which public properties of a ViewModel class its Bridge publishes; see <see cref="RunicPublicationAttribute"/>.</summary>
public enum RunicPublicationMode
{
    /// <summary>
    /// The default: every public instance property that the ViewModel class declares, except indexers and
    /// <see cref="RunicIgnoreAttribute"/> ones.
    /// </summary>
    Implicit = 0,

    /// <summary>
    /// Only the properties marked <see cref="RunicIncludeAttribute"/>, commands included, and the properties that implement
    /// a member of a <see cref="RunicSharedClientAttribute"/> interface the ViewModel implements. A
    /// <see cref="RunicIgnoreAttribute"/> property is never published.
    /// </summary>
    Explicit = 1,
}

/// <summary>
/// Sets which public properties the Bridge publishes for the ViewModels declared in the annotated assembly, or for the
/// annotated ViewModel class and the classes deriving from it.
/// </summary>
/// <remarks>
/// <para>
/// Without the attribute a ViewModel publishes every public instance property its class declares, except
/// <see cref="RunicIgnoreAttribute"/> ones (<see cref="RunicPublicationMode.Implicit"/>). With
/// <c>[assembly: RunicPublication(RunicPublicationMode.Explicit)]</c> or the attribute on the class, it publishes only
/// <see cref="RunicIncludeAttribute"/> properties and the members of its <see cref="RunicSharedClientAttribute"/>
/// interfaces, so a member added later stays on .NET until it is marked.
/// </para>
/// <para>
/// A declaration on the class, or the nearest one on a base class, wins over the assembly's: a ViewModel in an explicit
/// assembly can return to <see cref="RunicPublicationMode.Implicit"/>, and the reverse. The assembly is the one that
/// declares the ViewModel. Changing the mode changes the generated contract, and the frontend's types with it.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RunicPublicationAttribute(RunicPublicationMode mode) : Attribute
{
    /// <summary>Which properties the Bridge publishes.</summary>
    public RunicPublicationMode Mode { get; } = Enum.IsDefined(mode)
        ? mode
        : throw new ArgumentOutOfRangeException(nameof(mode), mode, "Use RunicPublicationMode.Implicit or RunicPublicationMode.Explicit.");
}

// One publication rule (#1320) for the code generator, the analyzer's mirror of it and the Hot Reload contract
// fingerprint (BridgeContractShape), so a member the generated contract omits is never in a snapshot. Attributes are
// matched by name and before a property's signature is resolved, as in BridgeDtoContract.
internal static class BridgePublication
{
    internal const string PublicationAttribute = "Runic.Application.Views.RunicPublicationAttribute";
    internal const string IncludeAttribute = "Runic.Application.Views.RunicIncludeAttribute";
    private const string SharedClientAttribute = "Runic.Application.Views.RunicSharedClientAttribute";

    /// <summary>
    /// The declared mode of <paramref name="model"/> as its Int32 value: the class's, or the nearest base class's, then
    /// the declaring assembly's, else <see cref="RunicPublicationMode.Implicit"/>. The generator reports a value that is
    /// not a <see cref="RunicPublicationMode"/>.
    /// </summary>
    internal static int DeclaredMode(Type model)
    {
        for (var current = model; current is not null && current != typeof(object); current = current.BaseType)
            if (Mode(BridgeAttributeNames.Find(current, PublicationAttribute)) is { } declared) return declared;
        return Mode(BridgeAttributeNames.Of(model.Assembly).FirstOrDefault(entry => entry.Name == PublicationAttribute))
            ?? (int)RunicPublicationMode.Implicit;
    }

    private static int? Mode(BridgeAttributeNames.Entry? attribute) =>
        attribute?.FixedArguments?.Invoke() is [var value] ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : null;

    internal static bool IsExplicit(Type model) => DeclaredMode(model) == (int)RunicPublicationMode.Explicit;

    // [RunicInclude] on the property or on a base property it overrides, as with [RunicIgnore].
    internal static bool IsIncluded(PropertyInfo property) =>
        BridgeDtoContract.InheritanceChain(property).Any(declared => BridgeAttributeNames.Has(declared, IncludeAttribute));

    /// <summary>
    /// The public instance properties of <paramref name="model"/> that its Bridge publishes, in reflection order: those
    /// the class declares, not <see cref="RunicIgnoreAttribute"/> ones and, in explicit mode, only the included ones.
    /// Indexers are not removed here: resolving a property's signature may need an assembly the generator cannot load,
    /// so callers check them after this name-only selection.
    /// </summary>
    [RequiresUnreferencedCode("Reflects over a ViewModel's properties and interfaces; used only by code generation and Hot Reload.")]
    internal static IEnumerable<PropertyInfo> Candidates(Type model)
    {
        var declared = model.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public)
            .Where(property => !BridgeDtoContract.IsRunicIgnored(property));
        if (!IsExplicit(model)) return declared;
        var shared = SharedClientGetters(model);
        return declared.Where(property => IsIncluded(property)
            || property.GetMethod is { } getter && shared.Contains((getter.Module, getter.MetadataToken)));
    }

    /// <summary>
    /// The getters that implement a property of a <see cref="RunicSharedClientAttribute"/> interface of
    /// <paramref name="model"/>, or of an interface it extends; in explicit mode they count as included.
    /// </summary>
    [RequiresUnreferencedCode("Reflects over a ViewModel's interfaces; used only by code generation and Hot Reload.")]
    internal static HashSet<(Module Module, int Token)> SharedClientGetters(Type model)
    {
        var getters = new HashSet<(Module, int)>();
        foreach (var contract in model.GetInterfaces())
        {
            if (!BridgeAttributeNames.Has(contract.IsGenericType ? contract.GetGenericTypeDefinition() : contract, SharedClientAttribute)) continue;
            foreach (var member in contract.GetInterfaces().Prepend(contract))
            {
                var map = model.GetInterfaceMap(member);
                for (var index = 0; index < map.InterfaceMethods.Length; index++)
                    if (map.InterfaceMethods[index].IsSpecialName && map.InterfaceMethods[index].Name.StartsWith("get_", StringComparison.Ordinal))
                        getters.Add((map.TargetMethods[index].Module, map.TargetMethods[index].MetadataToken));
            }
        }
        return getters;
    }
}
