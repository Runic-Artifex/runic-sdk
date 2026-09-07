using Runic.Application;
using Runic.Application.Desktop;
using Runic.Assets;
using Runic.Assets.Desktop;
using Runic.Desktop;
namespace RunicDesktopApp;
internal static class HostComposition
{
    public static IApplicationHost Create(IAssetSource assets) => new DesktopApplicationHost(new()
    {
        Title = "Runic Application Counter · Vue",
        Surface = new DesktopSurfaceOptions { ContentHandler = assets.ToDesktopContentHandler() },
    });
}
