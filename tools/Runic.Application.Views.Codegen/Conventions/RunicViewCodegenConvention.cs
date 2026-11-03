using System.Reflection;
using Runic.Application.Views;

/// <summary>
/// The flavor-neutral View members of Runic.Application.Views: a
/// <c>ViewQuestion&lt;TInput, TOutput&gt;</c> is an interaction whose descriptor
/// the runtime itself provides, so a CommunityToolkit-only project needs no
/// adapter. (A <c>ViewRequest&lt;T&gt;</c> is not an interaction; the generator
/// recognises it directly, see <see cref="InteractionCodeEmitter.DiscoverRequests"/>.)
/// </summary>
internal sealed class RunicViewCodegenConvention : MvvmCodegenConvention
{
    internal static readonly RunicViewCodegenConvention Instance = new();

    internal override Assembly PluginAssembly => typeof(ViewRequestDelivery).Assembly;

    internal override MvvmCommand? InspectCommand(PropertyInfo command) => null;

    internal override MvvmInteraction? InspectInteraction(PropertyInfo property) =>
        property.PropertyType is { IsGenericType: true } type
            && type.GetGenericTypeDefinition().FullName == RunicViewConventions.ViewQuestionContract
            ? new Question(type.GenericTypeArguments[0], type.GenericTypeArguments[1]) : null;

    private sealed class Question(Type input, Type output) : MvvmInteraction
    {
        internal override Type Input => input;
        internal override Type Output => output;
        internal override string ShapeDiagnostic => BridgeDiagnosticCodes.ViewMember;
        internal override string Kind => "view question";

        internal override string Descriptor(GeneratedInteractionPlan plan, string modelType, string publicName,
            string inputCodec, string outputCodec) =>
            $"global::Runic.Application.Views.Generated.ViewQuestionDescriptor.Create<{modelType}, {plan.Input.RootCSharpType()}, {plan.Output.RootCSharpType()}>(\"{publicName}\", \"{plan.ContractId}\", vm => vm.{plan.Property.Name}, input => global::Runic.Application.Views.Generated.BridgeWire.EncodeCanonical(writer => {inputCodec}.Write(writer, input)), element => {outputCodec}.Read(element))";
    }
}
