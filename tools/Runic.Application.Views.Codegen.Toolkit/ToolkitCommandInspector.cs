using System.CodeDom.Compiler;
using System.Reflection;
using System.Windows.Input;
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
            : IsGeneratedCancelCommand(command) ? Create(command, input: null, isAsync: false) : null;
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
        string? readArgument = null, string? encodeArgument = null, string? subscribe = null,
        string? resultType = null, string? encodeResult = null)
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
        if (contract.Result is not null)
        {
            if (string.IsNullOrWhiteSpace(resultType) || string.IsNullOrWhiteSpace(encodeResult))
                throw new ArgumentException("A Toolkit command with a result needs its generated C# result type and encoder.", nameof(resultType));
            // ExecuteAsync returns the task of the [RelayCommand] method.
            var executeResult = $"async (vm, token, argument) => {{ token.ThrowIfCancellationRequested(); var command = {typedCommand}; using var registration = token.Register(command.Cancel); var execution = command.ExecuteAsync({argument}); await execution.ConfigureAwait(false); var result = ((global::System.Threading.Tasks.Task<{resultType}>)execution).Result; return {encodeResult}; }}";
            return prefix + ", ExecuteResultAsync: " + executeResult + extras + ")";
        }
        var execute = $"async (vm, token, argument) => {{ token.ThrowIfCancellationRequested(); var command = {typedCommand}; using var registration = token.Register(command.Cancel); await command.ExecuteAsync({argument}).ConfigureAwait(false); }}";
        return prefix + ", " + execute + extras + ")";
    }

    /// <summary>
    /// The member named by <c>[RelayCommand(CanExecute = ...)]</c> on the method the command was generated from,
    /// or <see langword="null"/>.
    /// </summary>
    public static string? CanExecuteMember(PropertyInfo command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (GeneratorVersion(command) is null || command.DeclaringType is not { } owner
            || !command.Name.EndsWith("Command", StringComparison.Ordinal))
            return null;
        var name = command.Name[..^"Command".Length];
        return owner.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => CommandName(method.Name) == name)
            .Select(method => method.GetCustomAttribute<RelayCommandAttribute>(false)?.CanExecute)
            .FirstOrDefault(member => member is not null);
    }

    private static ToolkitCommandContract Create(PropertyInfo command, Type? input, bool isAsync)
    {
        if (!command.Name.EndsWith("Command", StringComparison.Ordinal))
            throw new NotSupportedException($"{command.Name}: Bridge commands must end with Command.");
        var name = command.Name[..^"Command".Length];
        // A [RelayCommand] method that returns Task<T>: the Toolkit's command
        // returns that task from ExecuteAsync, so its result can be read.
        var source = isAsync ? SourceMethod(command, name) : null;
        var result = source?.ReturnType is { IsGenericType: true } returned && returned.GetGenericTypeDefinition() == typeof(Task<>)
            ? returned.GenericTypeArguments[0] : null;
        return new(name, input, isAsync) { Result = result, Source = result is null ? null : source };
    }

    // The [RelayCommand] method a generated command property wraps.
    private static MethodInfo? SourceMethod(PropertyInfo command, string name) =>
        GeneratorVersion(command) is null || command.DeclaringType is not { } owner ? null
            : owner.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => CommandName(method.Name) == name)
                .FirstOrDefault(method => method.GetCustomAttribute<RelayCommandAttribute>(false) is not null);

    // IncludeCancelCommand emits ICommand, not IRelayCommand. Its public
    // generator metadata plus the attributed source and paired async property
    // identify the parameterless cancellation contract without accepting an
    // arbitrary ICommand whose input cannot be inferred. Keep executing the
    // generated property: Toolkit owns CanBeCanceled and command-wide Cancel.
    private static bool IsGeneratedCancelCommand(PropertyInfo command)
    {
        const string suffix = "CancelCommand";
        if (command.PropertyType != typeof(ICommand)
            || !command.Name.EndsWith(suffix, StringComparison.Ordinal)
            || command.GetMethod is not { IsPublic: true, IsStatic: false }
            || command.SetMethod is not null
            || command.DeclaringType is not { } owner
            || GeneratorVersion(command) is not { } version)
            return false;

        var name = command.Name[..^suffix.Length];
        var sibling = owner.GetProperty(name + "Command", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public);
        if (sibling is null || GeneratorVersion(sibling) != version
            || InspectContract(sibling) is not { IsAsync: true } contract)
            return false;

        return owner.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            // The name first: GetCustomAttribute resolves every attribute type
            // on the method, and only the command's own method is relevant.
            .Any(method => CommandName(method.Name) == name
                && method.GetCustomAttribute<RelayCommandAttribute>(false) is { IncludeCancelCommand: true }
                && typeof(Task).IsAssignableFrom(method.ReturnType)
                && method.GetParameters() is var parameters
                && parameters.Length == (contract.HasInput ? 2 : 1)
                && parameters[^1].ParameterType == typeof(CancellationToken)
                && (!contract.HasInput || parameters[0].ParameterType == contract.Input));
    }

    private static string? GeneratorVersion(PropertyInfo property) =>
        property.GetCustomAttribute<GeneratedCodeAttribute>(false) is
            { Tool: "CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", Version.Length: > 0 } generated
            ? generated.Version : null;

    private static string CommandName(string methodName)
    {
        if (methodName.Length > 2 && methodName.StartsWith("On", StringComparison.Ordinal) && char.IsUpper(methodName[2]))
            methodName = methodName[2..];
        if (methodName.Length > "Async".Length && methodName.EndsWith("Async", StringComparison.Ordinal))
            methodName = methodName[..^"Async".Length];
        return methodName;
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

    /// <summary>The <c>T</c> of an async command whose method returns <c>Task&lt;T&gt;</c>, or null.</summary>
    public Type? Result { get; init; }

    /// <summary>The <c>[RelayCommand]</c> method of a command with a <see cref="Result"/>.</summary>
    public MethodInfo? Source { get; init; }
}
