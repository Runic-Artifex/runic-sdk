using System.Reflection;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace Runic.Application.Views.Codegen.ReactiveUI;

/// <summary>Lowers compiled ReactiveUI commands to awaited observable invokers.</summary>
public static class ReactiveCommandInspector
{
    public static (bool HasStringArgument, bool IsAsync, string Descriptor)? Inspect(PropertyInfo command, string modelType)
    {
        var type = command.PropertyType;
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(ReactiveCommand<,>)
            || type.GenericTypeArguments[1] != typeof(RxVoid)) return null;
        var input = type.GenericTypeArguments[0];
        if (input != typeof(RxVoid) && input != typeof(string)) return null;
        var hasStringArgument = input == typeof(string);
        var name = command.Name[..^"Command".Length];
        var execution = hasStringArgument
            ? $"vm.{command.Name}.Execute((string)argument!)"
            : $"vm.{command.Name}.Execute()";
        var readArgument = hasStringArgument
            ? ", ReadArgument: e => global::Runic.Application.Views.BridgeJson.ReadRequiredString(e.GetString())"
            : "";
        var descriptor = $"new global::Runic.Application.Views.CommandDescriptor<{modelType}>(\"{name}\", vm => vm.{command.Name}, "
            + $"async (vm, token, argument) => {{ await global::ReactiveUI.Primitives.Signals.Signal.ToTask({execution}, token); }}{readArgument})";
        return (hasStringArgument, true, descriptor);
    }
}
