using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Runic.Platform;

namespace Runic.Application.Views.CsWebUi;

/// <summary>Registers window platform services for CS-WebUI applications.</summary>
public static class CsWebUiPlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IWindowPlatformServices"/>, <see cref="IFileDialogs"/>, <see cref="ITextClipboard"/> and
    /// <see cref="IDesktopFileLauncher"/> per window scope. CS-WebUI windows have no verified native owner, so every
    /// operation reports <see cref="PlatformUnavailableReason.OwnerUnavailable"/>. Also registers one
    /// <see cref="IDesktopSettings"/> for the application, which reports
    /// <see cref="PlatformUnavailableReason.ProviderNotConfigured"/>.
    /// </summary>
    /// <remarks>
    /// ViewModels written against these services resolve and run on CS-WebUI and check the result or the capability
    /// snapshot; on Runic Desktop, whose <c>AddRunicPlatformServices</c> registers the same services, the same
    /// ViewModels get native dialogs, the clipboard and the desktop's appearance preferences. To read the
    /// preferences on CS-WebUI, register an <see cref="IDesktopSettings"/> from a platform provider, such as
    /// <c>WindowsPlatformProvider.CreateSettings()</c>, before calling this method. Existing registrations of these
    /// service types are kept.
    /// </remarks>
    public static IServiceCollection AddRunicPlatformServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(static _ => DesktopSettings.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddScoped(static _ => WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable));
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().FileDialogs);
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().Clipboard);
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().FileLauncher);
        return services;
    }
}
