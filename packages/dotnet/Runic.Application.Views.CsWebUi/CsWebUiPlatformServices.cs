using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Runic.Application.Views;
using Runic.Platform;
using Runic.Platform.Runtime;

namespace Runic.Application.Views.CsWebUi;

/// <summary>Reaches the CS-WebUI window of a Runic Window opened by <see cref="RunicCsWebUiHost"/>.</summary>
public static class CsWebUiPlatformServices
{
    /// <summary>Gets the CS-WebUI window that presents a Runic Window, without naming its ViewModel type.</summary>
    /// <param name="window">The Window, on any host. It must have opened; it may have closed since.</param>
    /// <returns>
    /// The <see cref="CsWebUiBridgeWindow{TViewModel}.Window"/> of <see cref="IRunicWindow.Host"/>, or
    /// <see langword="null"/> for a Window on another host, such as Runic Desktop.
    /// </returns>
    /// <remarks>
    /// The counterpart of <c>DesktopPlatformServices.GetDesktopWindow</c> on Runic Desktop, for code that holds an
    /// <see cref="IRunicWindow"/>, such as a navigation service, and needs WebUI's own window API. The window
    /// belongs to the host: do not close or dispose it; close the Runic Window instead.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The Window has not opened.</exception>
    public static WebUiWindow? GetWebUiWindow(IRunicWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Host is ICsWebUiNativeWindowSource source ? source.NativeWindow : null;
    }
}

/// <summary>Registers window platform services for CS-WebUI applications.</summary>
public static class CsWebUiPlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers every Runic platform service that Runic Desktop's <c>AddRunicPlatformServices</c> registers:
    /// <see cref="IWindowPlatformServices"/>, <see cref="IFileDialogs"/>, <see cref="ITextClipboard"/>, <see cref="IDesktopClipboard"/>,
    /// <see cref="IDesktopFileLauncher"/> and <see cref="IDesktopMessageBoxes"/> per window scope, and one
    /// <see cref="IApplicationPlatformServices"/>, <see cref="IDesktopSettings"/>,
    /// <see cref="IDesktopNotifications"/>, <see cref="IDesktopStatusIcon"/>, <see cref="IDesktopGlobalShortcuts"/>, <see cref="IDesktopLoginStartup"/>,
    /// <see cref="IDesktopInhibition"/>, <see cref="IDesktopSecretStore"/>, <see cref="IDesktopTrash"/>,
    /// <see cref="IDesktopFileTransfers"/>, <see cref="IDesktopVolumes"/>, <see cref="IDesktopNetworkStatus"/>, <see cref="IDesktopPowerEvents"/>, <see cref="IDesktopRecentDocuments"/>, <see cref="IDesktopApplications"/> and <see cref="IDesktopThumbnails"/> for the application. CS-WebUI windows have no native owner, so every window
    /// operation and the capability snapshot report <see cref="PlatformUnavailableReason.OwnerUnavailable"/>. This
    /// package selects no native provider, so every application service reports
    /// <see cref="PlatformUnavailableReason.ProviderNotConfigured"/>.
    /// </summary>
    /// <remarks>
    /// ViewModels written against these services resolve and run on CS-WebUI and check the result or the capability
    /// snapshot; on Runic Desktop, whose <c>AddRunicPlatformServices</c> registers the same services, the same
    /// ViewModels get native dialogs, the clipboard, the desktop's appearance preferences, notifications, the status
    /// icon, global shortcuts, login startup, inhibition, secret storage, the trash, file transfers, the application database and thumbnails. The application services need no window, so a CS-WebUI
    /// application can use them by registering one from a platform provider, such as
    /// <c>WindowsPlatformProvider.CreateSettings()</c>, <c>WindowsPlatformProvider.CreateGlobalShortcuts()</c>, <c>new PortalApplication().CreateInhibition()</c>,
    /// <c>new PortalApplication().CreateSecretStore()</c> or <c>MacOSPlatformProvider.CreateApplications()</c>, or the managed file transfers from
    /// <c>Runic.Platform.Runtime.LocalFileTransfers.Create()</c>, before calling this method. Existing registrations of
    /// these service types are kept.
    /// </remarks>
    public static IServiceCollection AddRunicPlatformServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(static _ => ApplicationPlatformServices.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopSettings.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopNotifications.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopStatusIcon.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopGlobalShortcuts.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopLoginStartup.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopInhibition.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopSecretStore.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopTrash.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopFileTransfers.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopVolumes.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopNetworkStatus.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopPowerEvents.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopRecentDocuments.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopApplications.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddSingleton(static _ => DesktopThumbnails.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        services.TryAddScoped(static _ => WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable));
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().FileDialogs);
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().Clipboard);
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().Clipboard as IDesktopClipboard
            ?? (IDesktopClipboard)WindowPlatformServices.Unavailable(PlatformUnavailableReason.BackendUnavailable).Clipboard);
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().FileLauncher);
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().MessageBoxes);
        return services;
    }

    /// <summary>
    /// Registers the same services as <see cref="AddRunicPlatformServices(IServiceCollection)"/>, with file dialogs
    /// without an owner window: each window's <see cref="IFileDialogs"/> shows <paramref name="unownedFileDialogs"/>
    /// for <see cref="OwnerPolicy.PreferOwner"/>.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <param name="unownedFileDialogs">
    /// The platform's dialogs without a parent window: <c>WindowsPlatformProvider.CreateUnownedFileDialogs()</c>
    /// (Runic.Platform.Windows), <c>MacOSPlatformProvider.CreateUnownedFileDialogs()</c> (Runic.Platform.MacOS) or
    /// <c>new PortalApplication("org.example.Notes").CreateUnparentedFileDialogs()</c> (Runic.Platform.Linux.Portal).
    /// </param>
    /// <returns><paramref name="services"/>.</returns>
    /// <remarks>
    /// <para>
    /// CS-WebUI windows have no native owner, so the dialogs open on their own, in front of the application, as
    /// Runic Desktop's do for a window without native dispatch. A request with <see cref="OwnerPolicy.RequireOwner"/>,
    /// the default of the options, still reports <see cref="PlatformUnavailableReason.OwnerUnavailable"/>: ask for
    /// <c>new OpenDirectoryOptions(OwnerPolicy.PreferOwner)</c> where a dialog of its own is acceptable. The capability
    /// snapshot reports <see cref="PlatformCapabilityIds.DialogsUnowned"/> and the file capabilities available, and
    /// <see cref="PlatformCapabilityIds.DialogsOwned"/> unavailable (<see cref="PlatformUnavailableReason.OwnerUnavailable"/>),
    /// so a ViewModel that runs on both hosts can tell them apart. Every other window service reports
    /// <see cref="PlatformUnavailableReason.OwnerUnavailable"/>, as before.
    /// </para>
    /// <para>
    /// Each window's dialogs show one at a time: a second request while one is shown reports
    /// <see cref="PlatformFailureCode.ResourceBusy"/>. Closing the window, which disposes its service scope, closes a
    /// shown dialog, releases the leases the window still holds and reports
    /// <see cref="PlatformUnavailableReason.OwnerClosed"/> from then on. On macOS the panels need the main thread, which
    /// <see cref="RunicCsWebUiHost.Run(IServiceProvider, Func{RunicCsWebUiHost, Task{int}})"/> serves while the application
    /// runs; a host created with its constructor leaves the main thread to the application.
    /// </para>
    /// <para>
    /// A dialog opens only for a Window whose page no other computer can reach, because the user at this computer
    /// answers it. Each request checks the Window: <see cref="RunicCsWebUiHost"/> must have shown it itself (no
    /// <see cref="RunicCsWebUiHostOptions.ShowWindow"/>, which may only start the server for another device, and not
    /// under <c>RUNIC_APPLICATION_SERVE_ONLY=1</c>), and its server must listen on loopback only, which
    /// <c>WebUiWindow.SetPublic(true)</c> ends. Otherwise the dialogs report
    /// <see cref="PlatformUnavailableReason.OwnerUnavailable"/>, as every other window service does. An existing
    /// <see cref="IWindowPlatformServices"/> registration is kept, and ignores these dialogs.
    /// </para>
    /// </remarks>
    /// <example><code>
    /// if (OperatingSystem.IsWindowsVersionAtLeast(8))
    ///     services.AddRunicPlatformServices(WindowsPlatformProvider.CreateUnownedFileDialogs());
    /// else if (OperatingSystem.IsMacOS())
    ///     services.AddRunicPlatformServices(MacOSPlatformProvider.CreateUnownedFileDialogs());
    /// else if (OperatingSystem.IsLinux())
    ///     services.AddRunicPlatformServices(new PortalApplication().CreateUnparentedFileDialogs());
    /// else
    ///     services.AddRunicPlatformServices();
    /// </code></example>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="unownedFileDialogs"/> is
    /// <see langword="null"/>.</exception>
    public static IServiceCollection AddRunicPlatformServices(this IServiceCollection services, IPickerBackend unownedFileDialogs)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(unownedFileDialogs);
        // The host shows the window and attaches it (CsWebUiWindowLocality); the dialogs then ask it before each request.
        services.TryAddSingleton<CsWebUiUnownedDialogs>();
        services.TryAddScoped<CsWebUiWindowLocality>();
        services.TryAddScoped<IWindowPlatformServices>(provider =>
            new UnownedWindowPlatformServices(unownedFileDialogs, provider.GetRequiredService<CsWebUiWindowLocality>()));
        return services.AddRunicPlatformServices();
    }

    // A CS-WebUI window's services with unowned file dialogs: the dialogs for OwnerPolicy.PreferOwner while the window is
    // local, every other service unavailable for the missing owner. Disposing the window's scope closes a shown dialog and
    // releases its leases.
    internal sealed class UnownedWindowPlatformServices : IWindowPlatformServices, IFileDialogs, IAsyncDisposable
    {
        private static readonly IWindowPlatformServices NoOwner = WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        private readonly PresentationLifetime _lifetime = new(static () => false);
        private readonly PresentationFiles _files;
        private readonly CsWebUiWindowLocality _locality;

        public UnownedWindowPlatformServices(IPickerBackend unowned, CsWebUiWindowLocality locality)
        {
            _files = new PresentationFiles(_lifetime, backend: null, unowned);
            _locality = locality;
        }

        public IFileDialogs FileDialogs => this;
        public ITextClipboard Clipboard => NoOwner.Clipboard;
        public IDesktopFileLauncher FileLauncher => NoOwner.FileLauncher;
        public IDesktopMonitors Monitors => NoOwner.Monitors;
        public IDesktopMessageBoxes MessageBoxes => NoOwner.MessageBoxes;

        // The window's dialogs: unowned ones while it is local, and those of a closed window, which report it closed.
        private IFileDialogs Dialogs => _lifetime.IsClosing || _locality.IsLocal ? _files : NoOwner.FileDialogs;

        public CapabilitySnapshot GetSnapshot() => new(_lifetime.Generation, ReferenceEquals(Dialogs, _files)
            ? NoOwner.GetSnapshot().Statuses.SetItems(_files.GetSnapshot().Statuses)
            : NoOwner.GetSnapshot().Statuses);

        public ValueTask<PickerResult<IDirectoryLease>> OpenDirectoryAsync(OpenDirectoryOptions options, CancellationToken cancellationToken = default) =>
            Dialogs.OpenDirectoryAsync(options, cancellationToken);

        public ValueTask<PickerResult<IDirectoryLeaseList>> OpenDirectoriesAsync(OpenDirectoryOptions options, CancellationToken cancellationToken = default) =>
            Dialogs.OpenDirectoriesAsync(options, cancellationToken);

        public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default) =>
            Dialogs.OpenFileAsync(options, cancellationToken);

        public ValueTask<PickerResult<IReadFileLeaseList>> OpenFilesAsync(OpenFileOptions options, CancellationToken cancellationToken = default) =>
            Dialogs.OpenFilesAsync(options, cancellationToken);

        public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default) =>
            Dialogs.SaveFileAsync(options, cancellationToken);

        public ValueTask<PickerResult<IDocumentFileLease>> OpenDocumentAsync(OpenFileOptions options, CancellationToken cancellationToken = default) =>
            Dialogs.OpenDocumentAsync(options, cancellationToken);

        public ValueTask<PickerResult<IDocumentFileLease>> SaveDocumentAsync(SaveFileOptions options, CancellationToken cancellationToken = default) =>
            Dialogs.SaveDocumentAsync(options, cancellationToken);

        public ValueTask<PlatformResult<PlatformUnit>> SetBadgeAsync(DesktopBadge? badge, CancellationToken cancellationToken = default) =>
            NoOwner.SetBadgeAsync(badge, cancellationToken);

        public ValueTask<PlatformResult<PlatformUnit>> SetProgressAsync(DesktopProgress? progress, CancellationToken cancellationToken = default) =>
            NoOwner.SetProgressAsync(progress, cancellationToken);

        public ValueTask DisposeAsync() => _lifetime.DisposeAsync();
    }
}
