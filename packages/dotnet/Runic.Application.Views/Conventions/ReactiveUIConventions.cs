namespace Runic.Application.Views;

// ReactiveUI's metadata names for the Bridge contract. The Default (ReactiveUI
// Primitives) and System.Reactive flavours have parallel contracts.
internal static class ReactiveUIConventions
{
    internal static readonly BridgeMvvmConvention Descriptor = new(
        Library: "ReactiveUI",
        // A DTO that derives from ReactiveUI.Validation's ReactiveValidationObject (either flavor) keeps HasErrors and
        // its ValidationContext out of its contract: the validation projection represents them.
        FrameworkBaseTypes: ["ReactiveUI.ReactiveObject", "ReactiveUI.Validation.Helpers.ReactiveValidationObject",
            "ReactiveUI.Validation.Reactive.Helpers.ReactiveValidationObject"],
        FrameworkNamespaces: ["ReactiveUI"],
        TypedCommandContracts: ["ReactiveUI.IReactiveCommand`2", "ReactiveUI.Reactive.IReactiveCommand`2"],
        InputCommandContracts: [],
        AsyncCommandTypes: [],
        InteractionContracts: ["ReactiveUI.Binding.IInteraction`2", "ReactiveUI.Binding.Reactive.IInteraction`2"]);
}
