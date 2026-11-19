namespace Runic.Desktop.Internal;

// The one rule of Runic Desktop's WebContentRules that the linked WebUiRemoteDebugging needs (sdk#1221): Development is
// DOTNET_ENVIRONMENT, else ASPNETCORE_ENVIRONMENT, as for Bridge failure detail.
internal static class WebContentRules
{
    internal static bool IsDevelopment(Func<string, string?> read) =>
        Runic.Application.Views.BridgeFailureDetails.IsDevelopment(read);
}
