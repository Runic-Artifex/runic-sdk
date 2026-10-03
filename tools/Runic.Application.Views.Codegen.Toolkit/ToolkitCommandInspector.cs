using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Runic.Application.Views.Codegen.Toolkit;

/// <summary>
/// Describes a CommunityToolkit command from its public interface contract.
/// A property may expose an interface, a concrete command, or a derived
/// command, so generation must inspect every inherited interface rather than
/// depend on a factory implementation type.
/// </summary>
public static class ToolkitCommandInspector
{
    public static bool HasUnsupportedAotValidation(Type model) =>
        typeof(ObservableValidator).IsAssignableFrom(model);

    /// <summary>
    /// Returns the command input and execution shape for a Toolkit command.
    /// A <c>null</c> input denotes the non-generic command interfaces.
    /// </summary>
    public static ToolkitCommandContract? InspectContract(PropertyInfo command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var candidates = Enumerate(command.PropertyType).ToArray();

        // AsyncRelayCommand&lt;T&gt; also implements IRelayCommand&lt;T&gt; and the
        // non-generic interfaces. Check its most specific contract first.
        var asyncGeneric = candidates.FirstOrDefault(candidate => IsGeneric(candidate, typeof(IAsyncRelayCommand<>)));
        if (asyncGeneric is not null)
            return Create(command, asyncGeneric.GenericTypeArguments[0], isAsync: true);

        if (candidates.Any(candidate => candidate == typeof(IAsyncRelayCommand)))
            return Create(command, input: null, isAsync: true);

        var syncGeneric = candidates.FirstOrDefault(candidate => IsGeneric(candidate, typeof(IRelayCommand<>)));
        if (syncGeneric is not null)
            return Create(command, syncGeneric.GenericTypeArguments[0], isAsync: false);

        return candidates.Any(candidate => candidate == typeof(IRelayCommand))
            ? Create(command, input: null, isAsync: false)
            : null;
    }

    /// <summary>
    /// Preserves the original public lowering API for parameterless and string
    /// commands. New generation consumes <see cref="InspectContract"/> and
    /// supplies typed codecs for every supported generic input.
    /// </summary>
    public static (bool HasStringArgument, bool IsAsync, string Descriptor)? Inspect(PropertyInfo command, string modelType)
    {
        var contract = InspectContract(command);
        if (contract is null || (contract.Input is not null && contract.Input != typeof(string))) return null;

        var readArgument = contract.HasInput
            ? "ReadArgument: e => global::Runic.Application.Views.BridgeJson.ReadRequiredString(e.GetString())"
            : null;
        return (contract.HasInput, contract.IsAsync,
            DescriptorFor(command, modelType, contract, "string", readArgument));
    }

    /// <summary>
    /// Emits the Toolkit-specific execution portion of a descriptor. The main
    /// generator owns wire codecs and passes their named argument fragments.
    /// Toolkit cancellation is command-wide: <see cref="IAsyncRelayCommand.Cancel"/>
    /// cancels the command's current execution, rather than a bridge request
    /// independently, so callers must not advertise per-invocation isolation.
    /// </summary>
    public static string DescriptorFor(PropertyInfo command, string modelType,
        ToolkitCommandContract contract, string? inputType = null,
        string? readArgument = null, string? encodeArgument = null, string? subscribe = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelType);
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.HasInput && string.IsNullOrWhiteSpace(inputType))
            throw new ArgumentException("A typed Toolkit command needs its generated C# input type.", nameof(inputType));

        var name = contract.Name.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        var prefix = $"new global::Runic.Application.Views.CommandDescriptor<{modelType}>(\"{name}\", vm => (object)vm.{command.Name}";
        var extras = JoinNamedArguments(readArgument, encodeArgument, subscribe);
        if (!contract.IsAsync) return prefix + extras + ")";

        var commandType = contract.HasInput
            ? $"global::CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<{inputType}>"
            : "global::CommunityToolkit.Mvvm.Input.IAsyncRelayCommand";
        var typedCommand = $"({commandType})vm.{command.Name}";
        var argument = contract.HasInput ? $"({inputType})argument!" : "null";
        // Registering a pre-cancelled token invokes Cancel synchronously. Do
        // not then start a fresh Toolkit execution after that cancellation has
        // already happened.
        var execute = $"async (vm, token, argument) => {{ token.ThrowIfCancellationRequested(); var command = {typedCommand}; using var registration = token.Register(command.Cancel); await command.ExecuteAsync({argument}).ConfigureAwait(false); }}";
        return prefix + ", " + execute + extras + ")";
    }

    private static ToolkitCommandContract Create(PropertyInfo command, Type? input, bool isAsync)
    {
        if (!command.Name.EndsWith("Command", StringComparison.Ordinal))
            throw new NotSupportedException($"{command.Name}: Bridge commands must end with Command.");
        return new(command.Name[..^"Command".Length], input, isAsync);
    }

    private static bool IsGeneric(Type candidate, Type definition) =>
        candidate.IsGenericType && candidate.GetGenericTypeDefinition() == definition;

    private static IEnumerable<Type> Enumerate(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            yield return current;
            foreach (var candidate in current.GetInterfaces()) yield return candidate;
        }
    }

    private static string JoinNamedArguments(params string?[] values)
    {
        var named = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return named.Length == 0 ? string.Empty : ", " + string.Join(", ", named);
    }
}

/// <summary>Typed CommunityToolkit command shape shared with bridge generation.</summary>
public sealed record ToolkitCommandContract(string Name, Type? Input, bool IsAsync)
{
    public bool HasInput => Input is not null;
}
