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

    /// <summary>
    /// The flavour of the Runic ReactiveUI adapter the project references, which the build passes. A command's type
    /// does not reveal it: both distributions share ReactiveUI.Core's IReactiveCommand. Interactions take theirs from
    /// their own interface (ReactiveUI.Binding or ReactiveUI.Binding.Reactive).
    /// </summary>
    internal ReactiveUiFlavor Flavor { get; private set; } = ReactiveUiFlavor.Default;

    // "none": the build found no Runic ReactiveUI adapter, so a ReactiveUI member has no helpers to call.
    private bool _noAdapter;

    // The build named the adapter's flavor, so an interaction of the other flavor is a mix. A direct
    // invocation without the option keeps the primitives default and checks nothing.
    private bool _declared;

    internal override Assembly PluginAssembly => typeof(ReactiveCommandInspector).Assembly;

    internal override IReadOnlyList<string> Options => [FlavorOption];

    internal override void ApplyOption(string option, string value)
    {
        if (option != FlavorOption) base.ApplyOption(option, value);
        Flavor = value switch
        {
            "primitives" or "none" => ReactiveUiFlavor.Default,
            "reactive" => ReactiveUiFlavor.SystemReactive,
            _ => throw new ArgumentException($"Unsupported ReactiveUI flavor '{value}'. Expected 'primitives', 'reactive' or 'none'."),
        };
        _noAdapter = value == "none";
        _declared = true;
    }

    internal override IEnumerable<string> CacheArguments => [$"reactiveui={(_noAdapter ? "none" : Flavor.ToString())}"];

    internal override MvvmCommand? InspectCommand(PropertyInfo command)
    {
        if (ReactiveCommandInspector.InspectContract(command, Flavor) is not { } contract) return null;
        if (_noAdapter)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ReactiveUiAdapter,
                $"{command.ReflectedType?.Name}.{command.Name} is a ReactiveUI command, but the project references no Runic ReactiveUI adapter, whose helpers the generated Bridge calls. Reference Runic.Application.Views.ReactiveUI, or Runic.Application.Views.ReactiveUI.Reactive for ReactiveUI's System.Reactive flavor.",
                command);
        return new Command(contract);
    }

    internal override MvvmInteraction? InspectInteraction(PropertyInfo property)
    {
        if (ReactiveInteractionInspector.InspectContract(property) is not { } contract) return null;
        if (_noAdapter)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ReactiveUiAdapter,
                $"{property.ReflectedType?.Name}.{property.Name} is a ReactiveUI interaction, but the project references no Runic ReactiveUI adapter, whose helpers the generated Bridge calls. Reference {AdapterFor(contract.Flavor)}.",
                property);
        if (_declared && contract.Flavor != Flavor)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ReactiveUiAdapter,
                $"{property.ReflectedType?.Name}.{property.Name} is a {FlavorName(contract.Flavor)} ReactiveUI interaction, but the project references {AdapterFor(Flavor)}, which uses {FlavorName(Flavor)}. Use one ReactiveUI flavor: reference {AdapterFor(contract.Flavor)} instead, or declare the interaction with the {FlavorName(Flavor)} ReactiveUI.Binding interface.",
                property);
        return new Interaction(contract);
    }

    private static string AdapterFor(ReactiveUiFlavor flavor) => flavor is ReactiveUiFlavor.SystemReactive
        ? "Runic.Application.Views.ReactiveUI.Reactive" : "Runic.Application.Views.ReactiveUI";

    private static string FlavorName(ReactiveUiFlavor flavor) => flavor is ReactiveUiFlavor.SystemReactive
        ? "System.Reactive" : "ReactiveUI.Primitives";

    private static string Helper(ReactiveUiFlavor flavor) => flavor is ReactiveUiFlavor.SystemReactive
        ? "global::Runic.Application.Views.ReactiveUI.Reactive.Generated.ReactiveCommandExecution"
        : "global::Runic.Application.Views.ReactiveUI.Generated.ReactiveCommandExecution";

    private static string Void(Type input) => input.FullName == "System.Reactive.Unit"
        ? "default(global::System.Reactive.Unit)"
        : "global::ReactiveUI.Primitives.RxVoid.Default";

    // The class of BridgeSupportFiles.Commands.
    private const string BridgeCommands = "RunicBridgeCommands";

    // Awaits a command's execution for its Bridge (runic-sdk#1109). It names no MVVM library, so it is shared by
    // every command whose adapter returns a task.
    private const string CommandsFile = """
        // <auto-generated />
        #nullable enable
        namespace Runic.Application.Views.Generated.Bridges;

        // Awaits this assembly's command executions for their Bridges: one state machine for every command instead of an
        // async method per command. A Bridge's Members passes the task of its adapter's execution and, for a result, its
        // id, whose EncodeResult encodes the value of the completed Task<T>.
        internal static class RunicBridgeCommands
        {
            internal static async global::System.Threading.Tasks.Task<global::Runic.Application.Views.Generated.BridgeOperationResult> Result(
                global::System.Threading.Tasks.Task execution, RunicBridgeMembers members, int id)
            {
                await execution.ConfigureAwait(false);
                return members.EncodeResult(id, execution);
            }

            internal static async global::System.Threading.Tasks.Task<global::Runic.Application.Views.Generated.BridgeOperationResult> Completion(
                global::System.Threading.Tasks.Task execution)
            {
                await execution.ConfigureAwait(false);
                return global::Runic.Application.Views.Generated.BridgeOperationResult.None;
            }
        }

        """;

    internal override void UseSupportFiles(IEnumerable<GeneratedCommandPlan> plans)
    {
        // A stream command keeps its own lambda: the runtime does not await its delegate's call (BridgeRuntime.Operations).
        if (plans.Any(plan => plan.Command is Command { Streams: false }))
            BridgeSupportFiles.Use(BridgeSupportFiles.Commands, CommandsFile);
    }

    private sealed class Command(ReactiveCommandContract contract) : MvvmCommand
    {
        // RunicCommandResult(Stream) on a command with a result; a command without one completes.
        internal bool Streams => contract.HasResult && contract.Cardinality is RunicCommandResultCardinality.Stream;
        internal override bool IsAsync => true;
        internal override Type? Input => contract.HasInput ? contract.Input : null;
        internal override Type? Result => contract.HasResult ? contract.Result : null;
        internal override Type[] ContractArguments => [contract.Input, contract.Result];
        internal override bool HasStringArgument => contract.HasInput && contract.Input == typeof(string);
        internal override bool HasExecutionState => true;
        internal override bool SelectsResultCardinality => true;
        internal override RunicCommandResultCardinality Cardinality => contract.Cardinality;

        private string CommandType(GeneratedCommandPlan plan) =>
            $"global::ReactiveUI.IReactiveCommand<{plan.ParameterCSharpType ?? BridgeTypeGraph.CSharpType(contract.Input)}, {plan.ResultGraph?.RootCSharpType() ?? BridgeTypeGraph.CSharpType(contract.Result)}>";

        internal override void Describe(BridgeCommandTable command, PropertyInfo property, GeneratedCommandPlan plan)
        {
            // The command's parameter; a RunicCommandKey command's codec encodes its key instead.
            var inputType = plan.ParameterCSharpType ?? BridgeTypeGraph.CSharpType(contract.Input);
            var keyType = plan.InputGraph?.RootCSharpType() ?? inputType;
            var typed = $"({CommandType(plan)})vm.{property.Name}";
            var helper = Helper(contract.Flavor);
            var input = contract.HasInput ? $"({inputType})argument!" : Void(contract.Input);
            if (contract.HasInput)
            {
                command.Return("ReadInput", $"{property.Name}InputCodec.Read(input)");
                command.Return("EncodeArgument", $"global::Runic.Application.Views.Generated.BridgeWire.EncodeCanonical(writer => {property.Name}InputCodec.Write(writer, ({keyType})argument!))");
            }
            command.Return("CanExecute", $"{helper}.CanExecute({typed}, {input})");
            command.Return("Subscribe", $"{helper}.Observe({typed}, changed)");
            // While a Bridge presents the ViewModel, it logs the command's ThrownExceptions that no Bridge call
            // reported (W250-042), so the ViewModel needs no subscription of its own.
            command.Return("ObserveFailures", $"{helper}.ObserveFailures({typed}, logger, \"{property.ReflectedType?.Name}.{property.Name}\")");
            if (Streams)
            {
                // An arm of the async ExecuteStream: the runtime does not await this delegate's call, so its exceptions
                // stay the returned task's, as with the async lambda before.
                command.Return("ExecuteStream", $"{helper}.ExecuteStream({typed}, {input}, execution.Stream!, result => global::Runic.Application.Views.Generated.BridgeWire.EncodeCanonical(writer => {property.Name}ResultCodec.Write(writer, result)), token)");
                return;
            }
            // The assembly's generated RunicBridgeCommands awaits the adapter's task and encodes its value
            // (EncodeResult). The runtime awaits ExecuteResultAsync, so an exception thrown before the task exists is
            // observed as the task's failure was.
            if (contract.HasResult)
            {
                var executionMethod = contract.Cardinality is RunicCommandResultCardinality.Last ? "ExecuteLast" : "Execute";
                var resultType = plan.ResultGraph?.RootCSharpType() ?? BridgeTypeGraph.CSharpType(contract.Result);
                command.Return("ExecuteResult", $"{BridgeCommands}.Result({helper}.{executionMethod}({typed}, {input}, token), this, id)");
                command.Arm("EncodeResult", $"""
                    var result = ((global::System.Threading.Tasks.Task<{resultType}>)execution).Result;
                    return global::Runic.Application.Views.Generated.BridgeOperationResult.Encode(() => global::Runic.Application.Views.Generated.BridgeWire.EncodeCanonical(writer => {property.Name}ResultCodec.Write(writer, result)));
                    """);
            }
            else command.Return("ExecuteResult", $"{BridgeCommands}.Completion({helper}.ExecuteCompletion({typed}, {input}, token))");
        }

        // Availability and execution state come from the command's observables.
        internal override IEnumerable<(string Member, string Value)> StateMembers(PropertyInfo command, string name, GeneratedCommandPlan plan)
        {
            var commandType = CommandType(plan);
            var helper = Helper(contract.Flavor);
            if (!plan.HasArgument)
                yield return ($"can{name}", $"{helper}.CanExecute(({commandType})vm.{command.Name}, {Void(contract.Input)})");
            yield return ($"is{name}Executing", $"{helper}.IsExecuting(({commandType})vm.{command.Name})");
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
                ? "global::Runic.Application.Views.ReactiveUI.Reactive.Generated"
                : "global::Runic.Application.Views.ReactiveUI.Generated";
            return $"{adapterNamespace}.ReactiveInteractionDescriptor.Create<{plan.Input.RootCSharpType()}, {plan.Output.RootCSharpType()}>(\"{publicName}\", \"{plan.ContractId}\", static vm => {GeneratedModel.Access(modelType)}.{plan.Property.Name}, input => global::Runic.Application.Views.Generated.BridgeWire.EncodeCanonical(writer => {inputCodec}.Write(writer, input)), element => {outputCodec}.Read(element){(plan.Timeout is { } timeout ? $", timeout: {timeout}" : "")})";
        }
    }
}
