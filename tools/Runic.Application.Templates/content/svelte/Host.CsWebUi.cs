using Runic.Application;
using Runic.Application.CsWebUi;
using Runic.Assets;
namespace RunicDesktopApp;
internal static class HostComposition
{
    public static IApplicationHost Create(IAssetSource assets) => new CsWebUiApplicationHost(new()
    {
        Title = "Runic Application Counter · Svelte", Assets = assets,
    });
}
