using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Runic.Application.Views.Codegen.Toolkit;

/// <summary>Lowers compiled CommunityToolkit commands to Bridge descriptor source.</summary>
public static class ToolkitCommandInspector
{
    public static bool HasUnsupportedAotValidation(Type model) =>
        typeof(ObservableValidator).IsAssignableFrom(model);

    public static (bool HasStringArgument, bool IsAsync, string Descriptor)? Inspect(PropertyInfo command, string modelType)
    {
        var type = command.PropertyType;
        var name = command.Name[..^"Command".Length];
        var prefix = $"new global::Runic.Application.Views.CommandDescriptor<{modelType}>(\"{name}\", vm => vm.{command.Name}";
        if (typeof(IAsyncRelayCommand).IsAssignableFrom(type) && !type.IsGenericType)
            return (false, true, prefix + $", async (vm, token, _) => {{ using var registration = token.Register(vm.{command.Name}.Cancel); await vm.{command.Name}.ExecuteAsync(null); }})");
        if (typeof(IRelayCommand).IsAssignableFrom(type) && !type.IsGenericType
            && !typeof(IAsyncRelayCommand).IsAssignableFrom(type))
            return (false, false, prefix + ")");
        var stringRelay = !typeof(IAsyncRelayCommand).IsAssignableFrom(type)
            && type.GetInterfaces().Append(type).Any(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IRelayCommand<>)
                && candidate.GenericTypeArguments[0] == typeof(string));
        return stringRelay
            ? (true, false, prefix + ", ReadArgument: e => global::Runic.Application.Views.BridgeJson.ReadRequiredString(e.GetString()))")
            : null;
    }
}
