using System.Reflection;
using Runic.Assets;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// The frontend that the build embedded with <c>RunicApplicationFrontendDelivery=Embedded</c> (I-027, D039.10): the
/// <c>Runic.Application.FrontendArchive</c> runtime option names the manifest resource of the entry assembly. The
/// Desktop, WPF and CS-WebUI hosts serve it in place of <c>www/</c>; Runic.Application.Views.CsWebUi compiles this
/// file too.
/// </summary>
internal static partial class FrontendArchive
{
    internal const string RuntimeOption = "Runic.Application.FrontendArchive";

    // Read once per process, when a window first needs it. A missing or invalid resource fails each open.
    private static readonly Lazy<AssetArchiveSource?> Embedded = new(LoadEmbedded);

    /// <summary>The build's archive, or <see langword="null"/> when the build embedded none.</summary>
    internal static IAssetSource? FromBuild => AppContext.GetData(RuntimeOption) is string { Length: > 0 } ? Embedded.Value : null;

    private static AssetArchiveSource? LoadEmbedded()
    {
        if (AppContext.GetData(RuntimeOption) is not string { Length: > 0 } resource) return null;
        var assembly = Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException($"The {RuntimeOption} runtime option names '{resource}', but the process has no entry assembly to read it from.");
        return AssetArchive.ReadEmbedded(assembly, resource);
    }
}
