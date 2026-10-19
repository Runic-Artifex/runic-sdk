using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Runic.Platform;

namespace Runic.Application.Views.CsWebUi;

/// <summary>Registers window platform services for CS-WebUI applications.</summary>
public static class CsWebUiPlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers every Runic platform service that Runic Desktop's <c>AddRunicPlatformServices</c> registers:
    /// <see cref="IWindowPlatformServices"/>, <see cref="IFileDialogs"/>, <see cref="ITextClipboard"/> and
    /// <see cref="IDesktopFileLauncher"/> per window scope, and one <see cref="IDesktopSettings"/>,
    /// <see cref="IDesktopNotifications"/>, <see cref="IDesktopStatusIcon"/>, <see cref="IDesktopLoginStartup"/>,
    /// <see cref="IDesktopInhibition"/>, <see cref="IDesktopSecretStore"/>, <see cref="IDesktopTrash"/>,
    /// <see cref="IDesktopFileTransfers"/>, <see cref="IDesktopVolumes"/>, <see cref="IDesktopApplications"/> and <see cref="IDesktopThumbnails"/> for the application. CS-WebUI windows have no native owner, so every window
    /// operation and the capability snapshot report <see cref="PlatformUnavailableReason.OwnerUnavailable"/>. This
    /// package selects no native provider, so every application service reports
    /// <see cref="PlatformUnavailableReason.ProviderNotConfigured"/>.
    /// </summary>
    /// <remarks>
    /// ViewModels written against these services resolve and run on CS-WebUI and check the result or the capability
    /// snapshot; on Runic Desktop, whose <c>AddRunicPlatformServices</c> registers the same services, the same
    /// ViewModels get native dialogs, the clipboard, the desktop's appearance preferences, notifications, the status
    /// icon, login startup, inhibition, secret storage, the trash, file transfers, the application database and thumbnails. The application services need no window, so a CS-WebUI
    /// application can use them by registering one from a platform provider, such as
    /// <c>WindowsPlatformProvider.CreateSettings()</c>, <c>PortalApplication.CreateUnparentedInhibition()</c>,
    /// <c>PortalPlatformProvider.CreateSecretStore()</c> or <c>PortalPlatformProvider.CreateApplications()</c>, or the managed file transfers from
    /// <c>Runic.Platform.Runtime.LocalFileTransfers.Create()</c>, before calling this method. Existing registrations of
    /// these service types are kept.
    /// </remarks>
    public static IServiceCollection AddRunicPlatformServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(static _ => DesktopSettings.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopNotifications.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopStatusIcons.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopLoginStartup.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopInhibition.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopSecretStore.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopTrash.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopFileTransfers.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopVolumes.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopApplications.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopThumbnails.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddScoped(static _ => WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable));
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().FileDialogs);
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().Clipboard);
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().FileLauncher);
        return services;
    }
}
