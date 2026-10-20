using System.Reflection;
using System.Text;
using Runic.Application.Views;

/// <summary>
/// One MVVM library's contribution to bridge generation. The generator itself
/// understands neutral concepts only: a command with an optional input and
/// result, its execution state, an interaction, generator options and AOT
/// support. Each library's module recognises its own types through its
/// generator plug-in and emits the C# that calls its own API.
/// </summary>
internal abstract class MvvmCodegenConvention
{
    /// <summary>The plug-in assembly whose code shapes the generated output.</summary>
    internal abstract Assembly PluginAssembly { get; }

    /// <summary>Generator command-line options this library reads.</summary>
    internal virtual IReadOnlyList<string> Options => [];

    internal virtual void ApplyOption(string option, string value) =>
        throw new ArgumentException($"Unsupported option '{option}'.");

    /// <summary>Option values that change the output and so key the generation cache.</summary>
    internal virtual IEnumerable<string> CacheArguments => [];

    /// <summary>Why <paramref name="model"/> cannot be published with Native AOT, or null.</summary>
    internal virtual string? UnsupportedAotReason(Type model) => null;

    /// <summary>
    /// The library command <paramref name="command"/> exposes, or null. Shape
    /// errors are reported as <see cref="NotSupportedException"/>.
    /// </summary>
    internal abstract MvvmCommand? InspectCommand(PropertyInfo command);

    /// <summary>The library interaction <paramref name="property"/> exposes, or null.</summary>
    internal virtual MvvmInteraction? InspectInteraction(PropertyInfo property) => null;

    /// <summary>Appends private helper types the bridge's commands of this library need.</summary>
    internal virtual void AppendSupportTypes(StringBuilder cs, IEnumerable<GeneratedCommandPlan> plans) { }
}

/// <summary>A command recognised by an MVVM library convention.</summary>
internal abstract class MvvmCommand
{
    internal abstract bool IsAsync { get; }

    /// <summary>The argument type, or null for a command without an argument.</summary>
    internal abstract Type? Input { get; }

    /// <summary>The result type, or null for a command without a result value.</summary>
    internal virtual Type? Result => null;

    /// <summary>The contract's generic arguments, which carry the nullability annotations of <see cref="Input"/> and <see cref="Result"/>.</summary>
    internal abstract Type[] ContractArguments { get; }

    /// <summary>The nullable annotations of <see cref="Result"/>, or null when it has none.</summary>
    internal virtual NullabilityInfo? ResultNullability(PropertyInfo command, NullabilityInfoContext context) =>
        ContractNullability.Argument(command, context, ContractArguments, 1);

    /// <summary>True when the argument is a string the legacy client may send unencoded.</summary>
    internal virtual bool HasStringArgument => false;

    /// <summary>True when the snapshot carries is&lt;Name&gt;Executing.</summary>
    internal virtual bool HasExecutionState => IsAsync;

    /// <summary>
    /// The ViewModel property the command's availability is declared as, such as the Toolkit's
    /// <c>[RelayCommand(CanExecute = nameof(CanSave))]</c>, or null. A command without an argument publishes that
    /// availability as <c>can&lt;Name&gt;</c> state, so a property of the same wire name is the same value.
    /// </summary>
    internal virtual string? AvailabilityMember => null;

    /// <summary>True when the command honours <see cref="RunicCommandResultAttribute"/>.</summary>
    internal virtual bool SelectsResultCardinality => false;

    internal virtual RunicCommandResultCardinality Cardinality => RunicCommandResultCardinality.Single;

    /// <summary>
    /// The TSDoc of a command property without a source method. A library may replace the
    /// comment its source generator wrote, which describes C# objects a TypeScript caller never sees.
    /// </summary>
    internal virtual string? PropertyDocumentation(PropertyInfo command) => XmlDocumentation.Summary(command);

    /// <summary>The <c>CommandDescriptor</c> constructor call, without a failure encoder.</summary>
    internal abstract string Descriptor(PropertyInfo property, string modelType, GeneratedCommandPlan plan);

    /// <summary>
    /// Object-initializer assignments for the <c>CommandDescriptor</c>, such as <c>ObserveFailures</c>, or
    /// <see langword="null"/>.
    /// </summary>
    internal virtual string? DescriptorInitializer(PropertyInfo property, GeneratedCommandPlan plan) => null;

    /// <summary>Appends the command's availability and execution state to the snapshot writer.</summary>
    internal virtual void AppendSnapshotState(StringBuilder cs, PropertyInfo command, string name, GeneratedCommandPlan plan)
    {
        if (!plan.HasArgument) AppendCanExecute(cs, command, name);
    }

    /// <summary>Availability through <see cref="System.Windows.Input.ICommand"/>, for a command without an argument.</summary>
    internal static void AppendCanExecute(StringBuilder cs, PropertyInfo command, string name) =>
        cs.AppendLine($"        writer.WriteBoolean(\"can{name}\", ((global::System.Windows.Input.ICommand)vm.{command.Name}).CanExecute(null));");
}

/// <summary>An interaction recognised by an MVVM library convention.</summary>
internal abstract class MvvmInteraction
{
    internal abstract Type Input { get; }
    internal abstract Type Output { get; }

    /// <summary>The descriptor expression for the bridge's <c>interactions:</c> argument.</summary>
    internal abstract string Descriptor(GeneratedInteractionPlan plan, string modelType, string publicName,
        string inputCodec, string outputCodec);
}

internal static class MvvmCodegenConventions
{
    // A command is matched by the first module that recognises it. Every module
    // still inspects it, so a shape error from any of them is reported.
    internal static readonly MvvmCodegenConvention[] All = [ToolkitCodegenConvention.Instance, ReactiveUICodegenConvention.Instance];

    internal static MvvmCodegenConvention? ForOption(string option) =>
        All.FirstOrDefault(convention => convention.Options.Contains(option, StringComparer.Ordinal));

    internal static string? UnsupportedAotReason(Type model) =>
        All.Select(convention => convention.UnsupportedAotReason(model)).FirstOrDefault(reason => reason is not null);

    internal static MvvmInteraction? InspectInteraction(PropertyInfo property) =>
        All.Select(convention => convention.InspectInteraction(property)).FirstOrDefault(interaction => interaction is not null);
}
