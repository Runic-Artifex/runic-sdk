using System.Reflection;

namespace Runic.Application.Views.Codegen.ReactiveUI;

/// <summary>Finds typed ReactiveUI Binding interactions without runtime loading tricks.</summary>
public static class ReactiveInteractionInspector
{
    public static ReactiveInteractionContract? InspectContract(PropertyInfo interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        var candidate = Enumerate(interaction.PropertyType).FirstOrDefault(type => type.IsGenericType
            && type.GetGenericTypeDefinition().FullName is "ReactiveUI.Binding.IInteraction`2"
                or "ReactiveUI.Binding.Reactive.IInteraction`2");
        if (candidate is null) return null;
        var arguments = candidate.GenericTypeArguments;
        var flavor = candidate.GetGenericTypeDefinition().FullName == "ReactiveUI.Binding.Reactive.IInteraction`2"
            ? ReactiveUiFlavor.SystemReactive : ReactiveUiFlavor.Default;
        return new(interaction.Name, arguments[0], arguments[1], flavor);
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

public enum ReactiveUiFlavor { Default, SystemReactive }

public sealed record ReactiveInteractionContract(string Name, Type Input, Type Output, ReactiveUiFlavor Flavor);
