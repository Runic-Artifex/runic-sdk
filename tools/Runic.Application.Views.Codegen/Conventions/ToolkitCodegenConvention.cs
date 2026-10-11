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
        ToolkitCommandInspector.InspectContract(command) is { } contract ? new Command(contract) : null;

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

    private sealed class Command(ToolkitCommandContract contract) : MvvmCommand
    {
        internal override bool IsAsync => contract.IsAsync;
        internal override Type? Input => contract.Input;
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
            var toolkitType = contract.Input is null ? null : plan.InputGraph?.RootCSharpType() ?? BridgeTypeGraph.CSharpType(contract.Input);
            var readArgument = contract.HasInput
                ? $"ReadArgument: e => {{ using var document = global::System.Text.Json.JsonDocument.Parse(e.GetString()); return {property.Name}InputCodec.Read(document.RootElement); }}" : null;
            var encodeArgument = contract.HasInput
                ? $"EncodeArgument: argument => global::Runic.Application.Views.BridgeWire.EncodeCanonical(writer => {property.Name}InputCodec.Write(writer, ({toolkitType})argument!))" : null;
            // IsRunning can change without CanExecuteChanged (for example with
            // concurrent executions), so publish on the command's own change.
            var subscribeRunning = contract.IsAsync
                ? $"Subscribe: (vm, changed) => new ToolkitRunningSubscription(vm.{property.Name}, changed)" : null;
            return ToolkitCommandInspector.DescriptorFor(property, modelType, contract, toolkitType, readArgument, encodeArgument, subscribeRunning);
        }

        internal override void AppendSnapshotState(StringBuilder cs, PropertyInfo command, string name, GeneratedCommandPlan plan)
        {
            base.AppendSnapshotState(cs, command, name, plan);
            if (contract.IsAsync)
                cs.AppendLine($"        writer.WriteBoolean(\"is{name}Executing\", ((global::CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.{command.Name}).IsRunning);");
        }
    }
}
