using System.Reflection;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;

namespace Runic.Application.Views.Codegen.ReactiveUI;

/// <summary>
/// Describes ReactiveUI commands from their public contract. Applications can
/// expose an interface, base command, concrete command, or combined command;
/// generation must not depend on the factory that created it.
/// </summary>
public static class ReactiveCommandInspector
{
    /// <summary>Returns the typed ReactiveUI command contract implemented by a property.</summary>
    public static ReactiveCommandContract? InspectContract(PropertyInfo command,
        ReactiveUiFlavor declaredFlavor = ReactiveUiFlavor.Default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contract = Enumerate(command.PropertyType).FirstOrDefault(candidate => candidate.IsGenericType
            && candidate.GetGenericTypeDefinition().FullName is "ReactiveUI.IReactiveCommand`2"
                or "ReactiveUI.Reactive.IReactiveCommand`2");
        if (contract is null) return null;
        var arguments = contract.GenericTypeArguments;
        var flavor = contract.GetGenericTypeDefinition().FullName == "ReactiveUI.Reactive.IReactiveCommand`2"
            ? ReactiveUiFlavor.SystemReactive : declaredFlavor;
        var cardinality = command.CustomAttributes.FirstOrDefault(attribute =>
            attribute.AttributeType.FullName == "Runic.Application.Views.RunicCommandResultAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as int? ?? 0;
        if (ReactiveCommandContract.IsVoid(arguments[1]) && cardinality != 0)
            throw new NotSupportedException($"{command.Name}: RxVoid commands cannot select a result cardinality.");
        return new(command.Name[..^"Command".Length], arguments[0], arguments[1], flavor,
            (BridgeCommandResultCardinality)cardinality);
    }

    /// <summary>
    /// Retains the original RxVoid/string lowering for existing generated
    /// bridges. The typed generator path uses <see cref="InspectContract"/>
    /// together with its generated input and result codecs.
    /// </summary>
    public static (bool HasStringArgument, bool IsAsync, string Descriptor)? Inspect(PropertyInfo command, string modelType)
    {
        var contract = InspectContract(command);
        if (contract is null || contract.Result != typeof(RxVoid)
            || (contract.Input != typeof(RxVoid) && contract.Input != typeof(string))) return null;
        var hasStringArgument = contract.Input == typeof(string);
        var execution = hasStringArgument
            ? $"global::Runic.Application.Views.ReactiveUI.ReactiveCommandExecution.Execute((global::ReactiveUI.IReactiveCommand<string, global::ReactiveUI.Primitives.RxVoid>)vm.{command.Name}, (string)argument!, token)"
            : $"global::Runic.Application.Views.ReactiveUI.ReactiveCommandExecution.Execute((global::ReactiveUI.IReactiveCommand<global::ReactiveUI.Primitives.RxVoid, global::ReactiveUI.Primitives.RxVoid>)vm.{command.Name}, global::ReactiveUI.Primitives.RxVoid.Default, token)";
        var readArgument = hasStringArgument
            ? ", ReadArgument: e => global::Runic.Application.Views.BridgeJson.ReadRequiredString(e.GetString())"
            : "";
        var descriptor = $"new global::Runic.Application.Views.CommandDescriptor<{modelType}>(\"{contract.Name}\", vm => (global::System.Windows.Input.ICommand)(object)vm.{command.Name}, "
            + $"async (vm, token, argument) => {{ await {execution}.ConfigureAwait(false); }}{readArgument})";
        return (hasStringArgument, true, descriptor);
    }

    private static IEnumerable<Type> Enumerate(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            yield return current;
            foreach (var candidate in current.GetInterfaces()) yield return candidate;
        }
    }
}

/// <summary>Typed command shape shared by source generation and diagnostics.</summary>
public sealed record ReactiveCommandContract(string Name, Type Input, Type Result, ReactiveUiFlavor Flavor,
    BridgeCommandResultCardinality Cardinality)
{
    public bool HasInput => !IsVoid(Input);
    public bool HasResult => !IsVoid(Result);
    internal static bool IsVoid(Type type) => type == typeof(RxVoid) || type.FullName == "System.Reactive.Unit";
}
