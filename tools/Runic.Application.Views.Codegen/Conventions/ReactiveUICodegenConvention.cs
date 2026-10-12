using System.Reflection;
using System.Text;
using Runic.Application.Views;
using Runic.Application.Views.Codegen.ReactiveUI;

/// <summary>
/// ReactiveUI commands and interactions. Generated code calls the
/// Runic.Application.Views.ReactiveUI adapter (or its System.Reactive flavour), so
/// the Bridge runtime itself stays ReactiveUI-free.
/// </summary>
internal sealed class ReactiveUICodegenConvention : MvvmCodegenConvention
{
    internal static readonly ReactiveUICodegenConvention Instance = new();

    private const string FlavorOption = "--reactiveui-flavor";

    /// <summary>The flavour the application declared; a System.Reactive contract selects its own.</summary>
    internal ReactiveUiFlavor Flavor { get; private set; } = ReactiveUiFlavor.Default;

    internal override Assembly PluginAssembly => typeof(ReactiveCommandInspector).Assembly;

    internal override IReadOnlyList<string> Options => [FlavorOption];

    internal override void ApplyOption(string option, string value)
    {
        if (option != FlavorOption) base.ApplyOption(option, value);
        Flavor = value switch
        {
            "primitives" => ReactiveUiFlavor.Default,
            "reactive" => ReactiveUiFlavor.SystemReactive,
            _ => throw new ArgumentException($"Unsupported ReactiveUI flavor '{value}'. Expected 'primitives' or 'reactive'."),
        };
    }

    internal override IEnumerable<string> CacheArguments => [$"reactiveui={Flavor}"];

    internal override MvvmCommand? InspectCommand(PropertyInfo command) =>
        ReactiveCommandInspector.InspectContract(command, Flavor) is { } contract ? new Command(contract) : null;

    internal override MvvmInteraction? InspectInteraction(PropertyInfo property) =>
        ReactiveInteractionInspector.InspectContract(property) is { } contract ? new Interaction(contract) : null;

    private static string Helper(ReactiveUiFlavor flavor) => flavor is ReactiveUiFlavor.SystemReactive
        ? "global::Runic.Application.Views.ReactiveUI.Reactive.ReactiveCommandExecution"
        : "global::Runic.Application.Views.ReactiveUI.ReactiveCommandExecution";

    private static string Void(Type input) => input.FullName == "System.Reactive.Unit"
        ? "default(global::System.Reactive.Unit)"
        : "global::ReactiveUI.Primitives.RxVoid.Default";

    private sealed class Command(ReactiveCommandContract contract) : MvvmCommand
    {
        internal override bool IsAsync => true;
        internal override Type? Input => contract.HasInput ? contract.Input : null;
        internal override Type? Result => contract.HasResult ? contract.Result : null;
        internal override Type[] ContractArguments => [contract.Input, contract.Result];
        internal override bool HasStringArgument => contract.HasInput && contract.Input == typeof(string);
        internal override bool HasExecutionState => true;
        internal override bool SelectsResultCardinality => true;
        internal override BridgeCommandResultCardinality Cardinality => contract.Cardinality;

        private string CommandType(GeneratedCommandPlan plan) =>
            $"global::ReactiveUI.IReactiveCommand<{plan.InputGraph?.RootCSharpType() ?? BridgeTypeGraph.CSharpType(contract.Input)}, {plan.ResultGraph?.RootCSharpType() ?? BridgeTypeGraph.CSharpType(contract.Result)}>";

        internal override string Descriptor(PropertyInfo property, string modelType, GeneratedCommandPlan plan)
        {
            var inputType = plan.InputGraph?.RootCSharpType() ?? BridgeTypeGraph.CSharpType(contract.Input);
            var typed = $"({CommandType(plan)})vm.{property.Name}";
            var helper = Helper(contract.Flavor);
            var input = contract.HasInput ? $"({inputType})argument!" : Void(contract.Input);
            var read = contract.HasInput
                ? $", ReadArgument: e => {{ using var document = global::System.Text.Json.JsonDocument.Parse(e.GetString()); return {property.Name}InputCodec.Read(document.RootElement); }}"
                : "";
            var canonical = contract.HasInput
                ? $", EncodeArgument: argument => global::Runic.Application.Views.BridgeWire.EncodeCanonical(writer => {property.Name}InputCodec.Write(writer, ({inputType})argument!))"
                : "";
            var canExecute = $", CanExecute: (vm, argument) => {helper}.CanExecute({typed}, {input})";
            var subscribe = $", Subscribe: (vm, changed) => {helper}.Observe({typed}, changed)";
            if (contract.HasResult && contract.Cardinality is BridgeCommandResultCardinality.Stream)
            {
                var streamExecute = $"async (vm, execution, token, argument) => await {helper}.ExecuteStream({typed}, {input}, execution.Stream!, result => global::Runic.Application.Views.BridgeWire.EncodeCanonical(writer => {property.Name}ResultCodec.Write(writer, result)), token).ConfigureAwait(false)";
                return $"new global::Runic.Application.Views.CommandDescriptor<{modelType}>(\"{contract.Name}\", vm => (object)vm.{property.Name}, CreateStream: () => new global::Runic.Application.Views.BridgeOperationStream(), ExecuteStreamAsync: {streamExecute}{read}{canonical}{canExecute}{subscribe})";
            }
            var executionMethod = contract.Cardinality is BridgeCommandResultCardinality.Last ? "ExecuteLast" : "Execute";
            var execute = contract.HasResult
                ? $"async (vm, token, argument) => {{ var result = await {helper}.{executionMethod}({typed}, {input}, token).ConfigureAwait(false); return global::Runic.Application.Views.BridgeOperationResult.Encode(() => global::Runic.Application.Views.BridgeWire.EncodeCanonical(writer => {property.Name}ResultCodec.Write(writer, result))); }}"
                : $"async (vm, token, argument) => {{ await {helper}.ExecuteCompletion({typed}, {input}, token).ConfigureAwait(false); return global::Runic.Application.Views.BridgeOperationResult.None; }}";
            return $"new global::Runic.Application.Views.CommandDescriptor<{modelType}>(\"{contract.Name}\", vm => (object)vm.{property.Name}, ExecuteResultAsync: {execute}{read}{canonical}{canExecute}{subscribe})";
        }

        // While a Bridge presents the ViewModel, it logs the command's ThrownExceptions that no Bridge call
        // reported (W250-042), so the ViewModel needs no subscription of its own.
        internal override string? DescriptorInitializer(PropertyInfo property, GeneratedCommandPlan plan) =>
            $"ObserveFailures = (vm, logger) => {Helper(contract.Flavor)}.ObserveFailures(({CommandType(plan)})vm.{property.Name}, logger, \"{property.ReflectedType?.Name}.{property.Name}\")";

        // Availability and execution state come from the command's observables.
        internal override void AppendSnapshotState(StringBuilder cs, PropertyInfo command, string name, GeneratedCommandPlan plan)
        {
            var commandType = CommandType(plan);
            var helper = Helper(contract.Flavor);
            if (!plan.HasArgument)
                cs.AppendLine($"        writer.WriteBoolean(\"can{name}\", {helper}.CanExecute(({commandType})vm.{command.Name}, {Void(contract.Input)}));");
            cs.AppendLine($"        writer.WriteBoolean(\"is{name}Executing\", {helper}.IsExecuting(({commandType})vm.{command.Name}));");
        }
    }

    private sealed class Interaction(ReactiveInteractionContract contract) : MvvmInteraction
    {
        internal override Type Input => contract.Input;
        internal override Type Output => contract.Output;

        internal override string Descriptor(GeneratedInteractionPlan plan, string modelType, string publicName,
            string inputCodec, string outputCodec)
        {
            var adapterNamespace = contract.Flavor is ReactiveUiFlavor.SystemReactive
                ? "global::Runic.Application.Views.ReactiveUI.Reactive"
                : "global::Runic.Application.Views.ReactiveUI";
            return $"{adapterNamespace}.ReactiveInteractionDescriptor.Create<{modelType}, {plan.Input.RootCSharpType()}, {plan.Output.RootCSharpType()}>(\"{publicName}\", \"{plan.ContractId}\", vm => vm.{plan.Property.Name}, input => global::Runic.Application.Views.BridgeWire.EncodeCanonical(writer => {inputCodec}.Write(writer, input)), element => {outputCodec}.Read(element))";
        }
    }
}
