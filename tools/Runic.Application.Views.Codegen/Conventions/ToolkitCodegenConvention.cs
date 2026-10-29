using System.CodeDom.Compiler;
using System.Reflection;
using System.Text;
using Runic.Application.Views.Codegen.Toolkit;

/// <summary>
/// CommunityToolkit.Mvvm commands: IRelayCommand and IAsyncRelayCommand, typed
/// or not, plus the generated cancel command. Execution and IsRunning go
/// through the Toolkit API directly; there is no Runic runtime adapter.
/// </summary>
internal sealed class ToolkitCodegenConvention : MvvmCodegenConvention
{
    internal static readonly ToolkitCodegenConvention Instance = new();

    internal override Assembly PluginAssembly => typeof(ToolkitCommandInspector).Assembly;

    internal override string? UnsupportedAotReason(Type model) => ToolkitCommandInspector.HasUnsupportedAotValidation(model)
        ? $"{model.FullName} inherits CommunityToolkit.Mvvm.ObservableValidator. Its validation failed in this prototype's Native AOT probe; use a framework-dependent publish until an AOT-safe validator is verified."
        : null;

    internal override MvvmCommand? InspectCommand(PropertyInfo command) =>
        ToolkitCommandInspector.InspectContract(command) is { } contract
            ? new Command(contract, ToolkitCommandInspector.CanExecuteMember(command)) : null;

    internal override void AppendSupportTypes(StringBuilder cs, IEnumerable<GeneratedCommandPlan> plans)
    {
        if (!plans.Any(plan => plan.Command is Command { IsAsync: true })) return;
        cs.AppendLine("    private sealed class ToolkitRunningSubscription : global::System.IDisposable");
        cs.AppendLine("    {");
        cs.AppendLine("        private readonly global::System.ComponentModel.INotifyPropertyChanged _command;");
        cs.AppendLine("        private readonly global::System.ComponentModel.PropertyChangedEventHandler _handler;");
        cs.AppendLine("        internal ToolkitRunningSubscription(object command, global::System.Action changed)");
        cs.AppendLine("        {");
        cs.AppendLine("            _command = (global::System.ComponentModel.INotifyPropertyChanged)command;");
        cs.AppendLine("            _handler = (_, e) => { if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == \"IsRunning\") changed(); };");
        cs.AppendLine("            _command.PropertyChanged += _handler;");
        cs.AppendLine("        }");
        cs.AppendLine("        public void Dispose() => _command.PropertyChanged -= _handler;");
        cs.AppendLine("    }");
        cs.AppendLine();
    }

    private sealed class Command(ToolkitCommandContract contract, string? canExecute) : MvvmCommand
    {
        internal override bool IsAsync => contract.IsAsync;
        internal override string? AvailabilityMember => canExecute;
        internal override Type? Input => contract.Input;
        internal override Type? Result => contract.Result;

        // The method's Task<T> carries the result's annotations.
        internal override NullabilityInfo? ResultNullability(PropertyInfo command, NullabilityInfoContext context) =>
            contract.Source is { } source && context.Create(source.ReturnParameter).GenericTypeArguments is [var result] ? result : null;
        internal override Type[] ContractArguments => contract.Input is { } input ? [input] : [];

        // The Toolkit generator documents its properties in C# terms ("Gets an
        // ICommand instance that can be used to cancel SaveCommand"). Describe the
        // generated cancel command for TypeScript and drop any other such comment.
        internal override string? PropertyDocumentation(PropertyInfo command)
        {
            if (command.GetCustomAttribute<GeneratedCodeAttribute>(false) is not { Tool: { } tool }
                || !tool.StartsWith("CommunityToolkit.Mvvm.SourceGenerators.", StringComparison.Ordinal))
                return base.PropertyDocumentation(command);
            const string cancel = "CancelCommand";
            if (!command.Name.EndsWith(cancel, StringComparison.Ordinal) || contract.IsAsync) return null;
            var target = command.Name[..^cancel.Length];
            return $"Cancels the running `{char.ToLowerInvariant(target[0])}{target[1..]}` call.";
        }

        internal override string Descriptor(PropertyInfo property, string modelType, GeneratedCommandPlan plan)
        {
            // The command's parameter; a RunicCommandKey command's codec encodes its key instead.
            var toolkitType = contract.Input is null ? null : plan.ParameterCSharpType ?? BridgeTypeGraph.CSharpType(contract.Input);
            var keyType = plan.InputGraph?.RootCSharpType() ?? toolkitType;
            var readArgument = contract.HasInput
                ? $"ReadInput: input => {property.Name}InputCodec.Read(input)" : null;
            var encodeArgument = contract.HasInput
                ? $"EncodeArgument: argument => global::Runic.Application.Views.Generated.BridgeWire.EncodeCanonical(writer => {property.Name}InputCodec.Write(writer, ({keyType})argument!))" : null;
            // IsRunning can change without CanExecuteChanged (for example with
            // concurrent executions), so publish on the command's own change.
            var subscribeRunning = contract.IsAsync
                ? $"Subscribe: (vm, changed) => new ToolkitRunningSubscription(vm.{property.Name}, changed)" : null;
            var resultType = plan.ResultGraph?.RootCSharpType();
            var encodeResult = resultType is null ? null
                : $"global::Runic.Application.Views.Generated.BridgeOperationResult.Encode(() => global::Runic.Application.Views.Generated.BridgeWire.EncodeCanonical(writer => {property.Name}ResultCodec.Write(writer, result)))";
            return ToolkitCommandInspector.DescriptorFor(property, modelType, contract, toolkitType, readArgument, encodeArgument, subscribeRunning,
                resultType, encodeResult);
        }

        internal override IEnumerable<(string Member, string Value)> StateMembers(PropertyInfo command, string name, GeneratedCommandPlan plan) =>
            contract.IsAsync
                ? [.. base.StateMembers(command, name, plan), ($"is{name}Executing", $"((global::CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.{command.Name}).IsRunning")]
                : base.StateMembers(command, name, plan);
    }
}
