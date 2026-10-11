namespace Runic.Application.Views;

// CommunityToolkit.Mvvm's metadata names for the Bridge contract.
internal static class CommunityToolkitConventions
{
    internal static readonly BridgeMvvmConvention Descriptor = new(
        Library: "CommunityToolkit.Mvvm",
        FrameworkBaseTypes: ["CommunityToolkit.Mvvm.ComponentModel.ObservableObject"],
        FrameworkNamespaces: ["CommunityToolkit"],
        TypedCommandContracts: [],
        InputCommandContracts: ["CommunityToolkit.Mvvm.Input.IAsyncRelayCommand`1", "CommunityToolkit.Mvvm.Input.IRelayCommand`1"],
        AsyncCommandTypes: ["CommunityToolkit.Mvvm.Input.IAsyncRelayCommand"],
        InteractionContracts: [],
        // [RelayCommand] turns Save, SaveAsync or OnSave into SaveCommand.
        CommandSourceAttribute: "CommunityToolkit.Mvvm.Input.RelayCommandAttribute",
        CommandSourceName: CommandName);

    private static string CommandName(string method)
    {
        if (method.Length > 2 && method.StartsWith("On", StringComparison.Ordinal) && char.IsUpper(method[2]))
            method = method[2..];
        if (method.Length > "Async".Length && method.EndsWith("Async", StringComparison.Ordinal))
            method = method[..^"Async".Length];
        return method;
    }
}
