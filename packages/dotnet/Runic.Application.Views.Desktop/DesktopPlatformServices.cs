using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Desktop;
using Runic.Platform;
#if RUNIC_PORTABLE_PLATFORMS
using Runic.Platform.Linux;
using Runic.Platform.Linux.Gtk4;
using Runic.Platform.Linux.Portal;
using Runic.Platform.MacOS;
#endif
using Runic.Platform.Runtime;
using Runic.Platform.Windows;

namespace Runic.Application.Views.Desktop;

/// <summary>The native provider that <see cref="DesktopPlatformServices"/> selects for a Desktop window.</summary>
public enum DesktopPlatformProvider
{
    /// <summary>No provider: the window has no native owner, or the process runs no toolkit Runic recognizes.</summary>
    None,
    /// <summary>Win32 dialogs, clipboard and shell handoff from <c>Runic.Platform.Windows</c>.</summary>
    Windows,
    /// <summary>AppKit dialogs, clipboard and file handoff from <c>Runic.Platform.MacOS</c>.</summary>
    MacOS,
    /// <summary>xdg-desktop-portal dialogs and handoff parented through GTK 3, and the GTK 3 clipboard, from <c>Runic.Platform.Linux</c>.</summary>
    LinuxGtk3,
    /// <summary>xdg-desktop-portal dialogs and handoff parented through GTK 4, and the GTK 4 clipboard, from <c>Runic.Platform.Linux.Gtk4</c>.</summary>
    LinuxGtk4,
}

/// <summary>Selects the native platform-service provider for the running Desktop backend.</summary>
/// <remarks>
/// Applications that open windows with <c>OpenWindowAsync</c> on <see cref="RunicDesktopHost"/> call
/// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection)"/> instead and take
/// <see cref="IWindowPlatformServices"/> or <see cref="IFileDialogs"/> in a ViewModel constructor. The explicit
/// providers, such as <c>WindowsPlatformProvider</c>, remain available for custom composition.
/// </remarks>
public static partial class DesktopPlatformServices
{
    /// <summary>Gets the provider for the operating system and the Linux toolkit this process has started.</summary>
    /// <remarks>
    /// On Linux the toolkit is the one the embedded Desktop window host claimed for the process, so a GTK 4 process
    /// never selects the GTK 3 provider. Before any embedded window starts a toolkit, Linux reports
    /// <see cref="DesktopPlatformProvider.None"/>.
    /// </remarks>
    public static DesktopPlatformProvider CurrentProvider => Select(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(),
        OperatingSystem.IsLinux(),
        gtk3Claimed: !LinuxDesktopRuntime.CanUse(LinuxEmbeddedBackend.Gtk4WebKit6),
        gtk4Claimed: !LinuxDesktopRuntime.CanUse(LinuxEmbeddedBackend.Gtk3WebKit41));

    // A process claims at most one Linux toolkit; a claim makes the other one unusable.
    internal static DesktopPlatformProvider Select(bool windows, bool macOS, bool linux, bool gtk3Claimed, bool gtk4Claimed) =>
        windows ? DesktopPlatformProvider.Windows
        : macOS ? DesktopPlatformProvider.MacOS
        : !linux ? DesktopPlatformProvider.None
        : gtk4Claimed ? DesktopPlatformProvider.LinuxGtk4
        : gtk3Claimed ? DesktopPlatformProvider.LinuxGtk3
        : DesktopPlatformProvider.None;

    /// <summary>Creates platform services for an open Desktop window, using <see cref="CurrentProvider"/>.</summary>
    /// <param name="owner">The window's native owner.</param>
    /// <returns>Services the caller disposes before the window's event loop stops; disposal releases acquired file access.</returns>
    /// <remarks>
    /// A window without native dispatch, such as an installed browser after an embedded-window fallback, gets services
    /// that report <see cref="PlatformUnavailableReason.OwnerUnavailable"/>; a native window whose toolkit has no
    /// provider reports <see cref="PlatformUnavailableReason.ProviderNotConfigured"/>.
    /// </remarks>
    public static DesktopWindowPlatformServices ForWindow(DesktopNativeOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var services = new DesktopWindowPlatformServices();
        services.Bind(owner);
        return services;
    }

    /// <summary>Creates the desktop's appearance preferences from the provider for the operating system.</summary>
    /// <returns>Application-scoped preferences that the caller disposes at shutdown.</returns>
    /// <remarks>
    /// Windows reads its settings, macOS reads AppKit once the Desktop event loop runs, and Linux reads
    /// xdg-desktop-portal with either toolkit, without loading GTK. Elsewhere, or where the Windows provider does not
    /// support the running version, every read reports <see cref="PlatformUnavailableReason.ProviderNotConfigured"/>.
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection)"/> registers one instance for
    /// the application.
    /// </remarks>
    public static IDesktopSettings CreateSettings()
    {
        IDesktopSettings? settings = null;
        if (OperatingSystem.IsWindows()) settings = Optional(CreateWindowsSettings);
#if RUNIC_PORTABLE_PLATFORMS
        else if (OperatingSystem.IsMacOS()) settings = CreateMacOSSettings();
        else if (OperatingSystem.IsLinux()) settings = CreatePortalSettings();
#endif
        return settings ?? DesktopSettings.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
    }

    /// <summary>Creates the desktop's notifications from the provider for the operating system.</summary>
    /// <param name="applicationId">
    /// The application's identity: on Windows its AppUserModelID, which the application registers with the shell; on
    /// Linux the reverse-DNS ID of its installed desktop entry (<c>org.example.App</c> for
    /// <c>org.example.App.desktop</c>). macOS uses the bundle identifier and ignores it. <see langword="null"/> on Linux
    /// lets the portal infer the identity from the desktop session, which suits development; on Windows it leaves
    /// notifications unconfigured.
    /// </param>
    /// <returns>Application-scoped notifications that the caller disposes at shutdown.</returns>
    /// <remarks>
    /// Windows sends toasts, macOS UserNotifications once the Desktop event loop runs, and Linux xdg-desktop-portal
    /// notifications with either toolkit, without loading GTK. Creating them sends nothing and connects to nothing; the
    /// first request does. Without an <paramref name="applicationId"/> on Windows, elsewhere, or where the Windows
    /// provider does not support the running version, every request reports
    /// <see cref="PlatformUnavailableReason.ProviderNotConfigured"/>.
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection, string)"/>
    /// registers one instance for the application.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="applicationId"/> is not a valid ID on this operating system.</exception>
    public static IDesktopNotifications CreateNotifications(string? applicationId = null) => CreateNotifications(applicationId, null);

    internal static IDesktopNotifications CreateNotifications(string? applicationId, ILogger? logger)
    {
        if (applicationId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        IDesktopNotifications? notifications = null;
        if (OperatingSystem.IsWindows())
            notifications = applicationId is null ? null : Optional(() => OperatingSystem.IsWindows() ? CreateWindowsNotifications(applicationId) : null);
#if RUNIC_PORTABLE_PLATFORMS
        else if (OperatingSystem.IsMacOS()) notifications = CreateMacOSNotifications();
        else if (OperatingSystem.IsLinux()) notifications = CreatePortalNotifications(applicationId, logger);
#endif
        return notifications ?? DesktopNotifications.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
    }

    /// <summary>Creates the application's status icon from the provider for the operating system.</summary>
    /// <param name="applicationId">On Linux the reverse-DNS ID of the installed desktop entry, used as the icon's ID;
    /// <see langword="null"/> uses the process name.</param>
    /// <returns>A status icon that the caller disposes at shutdown.</returns>
    /// <remarks>
    /// Linux registers a StatusNotifierItem with the desktop's status area (Plasma, Waybar's tray, or GNOME with the
    /// AppIndicator extension) without loading GTK. Windows adds a notification-area icon with <c>Shell_NotifyIcon</c>
    /// from RGBA images, ignoring theme names, and adds it again when Explorer restarts. Creating it connects to
    /// nothing; the first <see cref="IDesktopStatusIcon.ShowAsync"/> does. macOS shows an <c>NSStatusItem</c> in the
    /// menu bar once the Desktop event loop runs, and reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>
    /// without that loop; it ignores <paramref name="applicationId"/>.
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection)"/> registers
    /// one instance for the application.
    /// </remarks>
    public static IDesktopStatusIcon CreateStatusIcon(string? applicationId = null) => CreateStatusIcon(applicationId, null);

    internal static IDesktopStatusIcon CreateStatusIcon(string? applicationId, ILogger? logger)
    {
        IDesktopStatusIcon? icon = null;
        if (OperatingSystem.IsWindows()) icon = Optional(CreateWindowsStatusIcon);
#if RUNIC_PORTABLE_PLATFORMS
        else if (OperatingSystem.IsLinux()) icon = CreatePortalStatusIcon(applicationId, logger);
        else if (OperatingSystem.IsMacOS()) icon = CreateMacOSStatusIcon();
#endif
        return icon ?? DesktopStatusIcon.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
    }

#if RUNIC_PORTABLE_PLATFORMS
    [SupportedOSPlatform("linux")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopStatusIcon CreatePortalStatusIcon(string? applicationId, ILogger? logger) =>
        new PortalApplication(applicationId, logger is null ? null : diagnostic => DesktopLog.PortalDiagnostic(logger, diagnostic))
            .CreateStatusIcon();

    [SupportedOSPlatform("macos")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopStatusIcon CreateMacOSStatusIcon() => MacOSPlatformProvider.CreateStatusIcon();

    [SupportedOSPlatform("linux")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopLoginStartup CreatePortalLoginStartup(string? applicationId, ILogger? logger) =>
        new PortalApplication(applicationId, logger is null ? null : diagnostic => DesktopLog.PortalDiagnostic(logger, diagnostic))
            .CreateLoginStartup();

    [SupportedOSPlatform("macos")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopLoginStartup CreateMacOSLoginStartup() => MacOSPlatformProvider.CreateLoginStartup();
#endif

    /// <summary>Creates login startup for the application from the provider for the operating system.</summary>
    /// <param name="applicationId">
    /// The application's ID: on Linux the reverse-DNS ID of its installed desktop entry, which names the autostart
    /// entry; on Windows the name of its Run value. <see langword="null"/> leaves login startup unconfigured, except
    /// inside a Flatpak or Snap sandbox, where the desktop portal knows the application. macOS uses the bundle and
    /// ignores it.
    /// </param>
    /// <remarks>
    /// Nothing is registered until <see cref="IDesktopLoginStartup.EnableAsync"/>, which an application calls only
    /// when the user asks. Linux writes an XDG autostart entry, or asks the Background portal inside a sandbox;
    /// Windows writes the user's Run key. macOS 13 and later registers the application bundle as a login item with
    /// <c>SMAppService</c>, which starts it without the request's arguments; a login item waiting for the user's
    /// approval reports <see cref="PlatformFailureCode.PermissionDenied"/>, and a process outside an application
    /// bundle, or an earlier macOS, reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>.
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection)"/> registers
    /// one instance for the application.
    /// </remarks>
    public static IDesktopLoginStartup CreateLoginStartup(string? applicationId = null) => CreateLoginStartup(applicationId, null);

    internal static IDesktopLoginStartup CreateLoginStartup(string? applicationId, ILogger? logger)
    {
        IDesktopLoginStartup? startup = null;
        if (OperatingSystem.IsWindows())
            startup = applicationId is null ? null : Optional(() => OperatingSystem.IsWindows() ? CreateWindowsLoginStartup(applicationId) : null);
#if RUNIC_PORTABLE_PLATFORMS
        else if (OperatingSystem.IsLinux())
            startup = CreatePortalLoginStartup(applicationId, logger);
        else if (OperatingSystem.IsMacOS()) startup = CreateMacOSLoginStartup();
#endif
        return startup ?? DesktopLoginStartup.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
    }

    /// <summary>Creates idle-power inhibition for the application from the provider for the operating system.</summary>
    /// <param name="applicationId">On Linux the reverse-DNS ID of the installed desktop entry, which identifies the
    /// application to the portal; <see langword="null"/> lets the portal infer it. Windows and macOS ignore it.</param>
    /// <returns>Application-scoped inhibition; each lease it acquires is released by disposing the lease.</returns>
    /// <remarks>
    /// Nothing is inhibited until <see cref="IDesktopInhibition.AcquireAsync"/>. Windows creates a power request and
    /// macOS an IOKit power assertion; neither needs a window or the Desktop event loop. Linux asks the
    /// xdg-desktop-portal Inhibit interface with either toolkit, without loading GTK, and sends no parent window, so
    /// the request belongs to the application rather than to a window and works for a host without a native owner.
    /// Elsewhere, or where the Windows provider does not support the running version, every acquisition reports
    /// <see cref="PlatformUnavailableReason.ProviderNotConfigured"/>.
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection)"/> registers
    /// one instance for the application.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="applicationId"/> is not a valid ID on this operating system.</exception>
    public static IDesktopInhibition CreateInhibition(string? applicationId = null) => CreateInhibition(applicationId, null);

    internal static IDesktopInhibition CreateInhibition(string? applicationId, ILogger? logger)
    {
        IDesktopInhibition? inhibition = null;
        if (OperatingSystem.IsWindows()) inhibition = Optional(CreateWindowsInhibition);
#if RUNIC_PORTABLE_PLATFORMS
        else if (OperatingSystem.IsMacOS()) inhibition = CreateMacOSInhibition();
        else if (OperatingSystem.IsLinux()) inhibition = CreatePortalInhibition(applicationId, logger);
#endif
        return inhibition ?? DesktopInhibition.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
    }

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopInhibition CreateWindowsInhibition() => WindowsPlatformProvider.CreateInhibition();

#if RUNIC_PORTABLE_PLATFORMS
    [SupportedOSPlatform("macos")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopInhibition CreateMacOSInhibition() => MacOSPlatformProvider.CreateInhibition();

    [SupportedOSPlatform("linux")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopInhibition CreatePortalInhibition(string? applicationId, ILogger? logger) =>
        new PortalApplication(applicationId, logger is null ? null : diagnostic => DesktopLog.PortalDiagnostic(logger, diagnostic))
            .CreateInhibition();
#endif

    /// <summary>
    /// Creates the requests to open files and URLs that the application registered with the desktop, from the
    /// provider for the operating system.
    /// </summary>
    /// <param name="options">The URL schemes and files the application registered.</param>
    /// <param name="instance">The running instance from <c>ApplicationInstance.ClaimAsync</c>, whose later launches
    /// carry files and URLs; <see langword="null"/> without single instance.</param>
    /// <param name="launch">This process's launch, usually <see cref="ApplicationLaunch.FromProcess"/>.</param>
    /// <returns>Open requests to dispose when the application ends.</returns>
    /// <remarks>
    /// Linux and Windows start the application with the files or URL as arguments, so requests come from
    /// <paramref name="launch"/> and from the later launches the instance receives. macOS sends open events to the
    /// running application instead, and the arguments of launches from a terminal also count. On macOS, create it on
    /// the main thread before the first window or status icon, as <c>MacOSPlatformProvider.CreateOpenRequests</c>
    /// requires. It is not registered by
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection)"/>, because
    /// it needs the instance and must exist before the first window; register the instance this returns.
    /// </remarks>
    public static IDesktopOpenRequests CreateOpenRequests(DesktopOpenOptions options, IApplicationInstance? instance = null, ApplicationLaunch? launch = null)
    {
        ArgumentNullException.ThrowIfNull(options);
#if RUNIC_PORTABLE_PLATFORMS
        if (OperatingSystem.IsMacOS()) return CreateMacOSOpenRequests(options, instance, launch);
#endif
        return ApplicationInstance.CreateOpenRequests(options, instance, launch);
    }

#if RUNIC_PORTABLE_PLATFORMS
    [SupportedOSPlatform("macos")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopOpenRequests CreateMacOSOpenRequests(DesktopOpenOptions options, IApplicationInstance? instance, ApplicationLaunch? launch) =>
        MacOSPlatformProvider.CreateOpenRequests(options, instance, launch);
#endif

    /// <summary>
    /// Creates the registration of the application's URL schemes and file types with the desktop, for the current
    /// user, from the provider for the operating system.
    /// </summary>
    /// <param name="applicationId">
    /// The application's ID, as for <see cref="CreateLoginStartup(string?)"/>: on Linux the reverse-DNS ID that names
    /// the desktop entry the registration writes; on Windows the prefix of the file types' ProgIDs and the name under
    /// which Settings lists the application. macOS registers the running bundle and ignores it.
    /// </param>
    /// <remarks>
    /// Nothing is written until <see cref="IDesktopOpenRegistration.RegisterAsync"/>, which an application calls when
    /// the user agrees; it never makes the application the default. macOS checks that the bundle's <c>Info.plist</c>
    /// declares each item and registers the bundle with Launch Services; Linux writes a desktop entry and MIME
    /// definitions in <c>XDG_DATA_HOME</c> (<c>PortalApplication.CreateOpenRegistration</c>); Windows writes the user's
    /// <c>HKEY_CURRENT_USER\Software\Classes</c> keys and lists the application in Settings, Default apps. A packaged
    /// or sandboxed process, whose package
    /// declares what it opens, reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>; elsewhere every
    /// request reports <see cref="PlatformUnavailableReason.ProviderNotConfigured"/>.
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection, string)"/>
    /// registers one instance for the application.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="applicationId"/> is empty or not a valid ID on this operating system.</exception>
    public static IDesktopOpenRegistration CreateOpenRegistration(string applicationId)
    {
        ValidateApplicationId(applicationId);
        return CreateOpenRegistration(applicationId, null);
    }

    internal static IDesktopOpenRegistration CreateOpenRegistration(string applicationId, ILogger? logger)
    {
        IDesktopOpenRegistration? registration = null;
        if (OperatingSystem.IsWindows()) registration = Optional(() => OperatingSystem.IsWindows() ? CreateWindowsOpenRegistration(applicationId) : null);
#if RUNIC_PORTABLE_PLATFORMS
        else if (OperatingSystem.IsMacOS()) registration = CreateMacOSOpenRegistration();
        else if (OperatingSystem.IsLinux()) registration = CreatePortalOpenRegistration(applicationId, logger);
#endif
        return registration ?? DesktopOpenRegistrations.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
    }

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopOpenRegistration CreateWindowsOpenRegistration(string applicationId) => WindowsPlatformProvider.CreateOpenRegistration(applicationId);

#if RUNIC_PORTABLE_PLATFORMS
    [SupportedOSPlatform("macos")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopOpenRegistration CreateMacOSOpenRegistration() => MacOSPlatformProvider.CreateOpenRegistration();

    [SupportedOSPlatform("linux")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopOpenRegistration CreatePortalOpenRegistration(string applicationId, ILogger? logger) =>
        new PortalApplication(applicationId, logger is null ? null : diagnostic => DesktopLog.PortalDiagnostic(logger, diagnostic)).CreateOpenRegistration();

    [SupportedOSPlatform("linux")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<PlatformResult<IAsyncDisposable>> ExportPortalApplicationAsync(string applicationId, IApplicationInstance instance,
        CancellationToken cancellationToken) => new PortalApplication(applicationId).ExportApplicationAsync(instance, cancellationToken);
#endif

    /// <summary>
    /// Serves the desktop's D-Bus activation of the application (<c>org.freedesktop.Application</c>) on Linux, so the
    /// desktop delivers activations and opened files to the running instance without starting a process.
    /// </summary>
    /// <param name="applicationId">The reverse-DNS ID of the installed desktop entry, which names the application's bus
    /// name and object path.</param>
    /// <param name="instance">The running instance from <c>ApplicationInstance.ClaimAsync</c>: each activation arrives as
    /// one of its launches, and open requests created for it take the files and URLs.</param>
    /// <param name="cancellationToken">Cancels before the bus name is owned.</param>
    /// <returns>The export to dispose when the application ends, which releases the bus name, as
    /// <c>PortalApplication.ExportApplicationAsync</c> describes; every other operating system reports
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="applicationId"/> is empty or not a valid ID on this operating system.</exception>
    public static ValueTask<PlatformResult<IAsyncDisposable>> ExportApplicationAsync(string applicationId, IApplicationInstance instance,
        CancellationToken cancellationToken = default)
    {
        ValidateApplicationId(applicationId);
        ArgumentNullException.ThrowIfNull(instance);
        cancellationToken.ThrowIfCancellationRequested();
#if RUNIC_PORTABLE_PLATFORMS
        if (OperatingSystem.IsLinux()) return ExportPortalApplicationAsync(applicationId, instance, cancellationToken);
#endif
        return ValueTask.FromResult<PlatformResult<IAsyncDisposable>>(new PlatformResult<IAsyncDisposable>.Unavailable(
            OperatingSystem.IsLinux() ? PlatformUnavailableReason.ProviderNotConfigured : PlatformUnavailableReason.BackendUnavailable));
    }

    /// <summary>This process's launch, with the files and URLs the operating system activated it with.</summary>
    /// <param name="arguments">The process's arguments, as <c>Main</c> receives them.</param>
    /// <returns>The launch to pass to <c>ApplicationInstance.ClaimAsync</c> and <see cref="CreateOpenRequests"/>.</returns>
    /// <remarks>
    /// The same as <see cref="ApplicationLaunch.FromProcess"/>, except in an MSIX package on Windows, where the
    /// activation's protocol URI or files (several at once in one process) become arguments too
    /// (<c>WindowsPlatformProvider.GetLaunch</c>).
    /// </remarks>
    public static ApplicationLaunch GetLaunch(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return OperatingSystem.IsWindows() ? GetWindowsLaunch(arguments) : ApplicationLaunch.FromProcess(arguments);
    }

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ApplicationLaunch GetWindowsLaunch(IEnumerable<string> arguments) => WindowsPlatformProvider.GetLaunch(arguments);

    /// <summary>Creates file bookmarks, which keep access to files across launches, from the provider for the operating
    /// system.</summary>
    /// <returns>File bookmarks; they hold no native resources between requests and need no disposal.</returns>
    /// <remarks>
    /// macOS creates security-scoped bookmarks inside the App Sandbox and ordinary bookmarks outside it; Windows and
    /// Linux, where a path keeps its access, use the path itself. It needs no window.
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection)"/> registers
    /// one instance for the application.
    /// </remarks>
    public static IDesktopFileBookmarks CreateFileBookmarks()
    {
#if RUNIC_PORTABLE_PLATFORMS
        if (OperatingSystem.IsMacOS()) return CreateMacOSFileBookmarks();
#endif
        return new PathFileBookmarks();
    }

#if RUNIC_PORTABLE_PLATFORMS
    [SupportedOSPlatform("macos")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopFileBookmarks CreateMacOSFileBookmarks() => MacOSPlatformProvider.CreateFileBookmarks();
#endif

    /// <summary>Creates secret storage in the user's credential store from the provider for the operating system.</summary>
    /// <returns>A secret store; it holds no native resources between requests and needs no disposal.</returns>
    /// <remarks>
    /// Linux uses the desktop's Secret Service, such as GNOME Keyring or KeePassXC, with either toolkit and without
    /// loading GTK; Windows uses Credential Manager and macOS the user's login keychain. It needs no window and connects
    /// to nothing until the first request. Elsewhere every request reports
    /// <see cref="PlatformUnavailableReason.ProviderNotConfigured"/>.
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection)"/> registers
    /// one instance for the application.
    /// </remarks>
    public static IDesktopSecretStore CreateSecretStore() => CreateSecretStore((ILogger?)null);

    /// <summary>Creates secret storage for the application identified by <paramref name="applicationId"/>.</summary>
    /// <param name="applicationId">
    /// The application's identity, as for <see cref="CreateNotifications(string?)"/>: on Linux the reverse-DNS ID of its
    /// installed desktop entry, which the provider's portal requests and diagnostics carry; on Windows its
    /// AppUserModelID. The Windows and macOS stores do not use it today, and Linux stores secrets the same way with or
    /// without it, so secrets stored through <see cref="CreateSecretStore()"/> stay readable.
    /// </param>
    /// <returns>A secret store; it holds no native resources between requests and needs no disposal.</returns>
    /// <remarks>
    /// Otherwise the same as <see cref="CreateSecretStore()"/>, which
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection, string)"/>
    /// also registers; to register this one, add it to the services before calling that method.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="applicationId"/> is empty or not a valid ID on this operating system.</exception>
    public static IDesktopSecretStore CreateSecretStore(string applicationId)
    {
        ValidateApplicationId(applicationId);
        return CreateSecretStore(applicationId, null);
    }

    internal static IDesktopSecretStore CreateSecretStore(ILogger? logger) => CreateSecretStore(null, logger);

    internal static IDesktopSecretStore CreateSecretStore(string? applicationId, ILogger? logger)
    {
        IDesktopSecretStore? store = null;
        if (OperatingSystem.IsWindows()) store = Optional(CreateWindowsSecretStore);
#if RUNIC_PORTABLE_PLATFORMS
        else if (OperatingSystem.IsLinux()) store = CreatePortalSecretStore(applicationId, logger);
        else if (OperatingSystem.IsMacOS()) store = CreateMacOSSecretStore();
#endif
        return store ?? DesktopSecretStore.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
    }

    /// <summary>Creates the desktop's application database: content types, installed applications, default applications
    /// and icons.</summary>
    /// <returns>An application database; it holds no native resources between requests and needs no disposal.</returns>
    /// <remarks>
    /// Linux reads the freedesktop.org desktop entries, shared MIME-info database, mimeapps.list and icon themes
    /// directly, with either toolkit and without loading GTK or GIO. Windows uses the shell's file and protocol
    /// associations, where only the user can change a default; macOS uses Launch Services through
    /// <c>NSWorkspace</c> and uniform type identifiers. It needs no window or event loop. Elsewhere every request
    /// reports <see cref="PlatformUnavailableReason.ProviderNotConfigured"/>.
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection)"/> registers
    /// one instance for the application.
    /// </remarks>
    public static IDesktopApplications CreateApplications() => CreateApplications((ILogger?)null);

    /// <summary>Creates the desktop's application database for the application identified by <paramref name="applicationId"/>.</summary>
    /// <param name="applicationId">
    /// The application's identity, as for <see cref="CreateNotifications(string?)"/>: on Linux the reverse-DNS ID of its
    /// installed desktop entry, which the portal registers when a request first connects to it, as notifications do; on
    /// Windows its AppUserModelID. The Windows and macOS providers do not use it today.
    /// </param>
    /// <returns>An application database; it holds no native resources between requests and needs no disposal.</returns>
    /// <remarks>
    /// Otherwise the same as <see cref="CreateApplications()"/>, which
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection, string)"/>
    /// also registers; to register this one, add it to the services before calling that method. On Linux a portal
    /// request then reports <c>portal-identity-registration-failed</c> when no matching desktop entry is installed;
    /// during development use <see cref="CreateApplications()"/>.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="applicationId"/> is empty or not a valid ID on this operating system.</exception>
    public static IDesktopApplications CreateApplications(string applicationId)
    {
        ValidateApplicationId(applicationId);
        return CreateApplications(applicationId, null);
    }

    internal static IDesktopApplications CreateApplications(ILogger? logger) => CreateApplications(null, logger);

    internal static IDesktopApplications CreateApplications(string? applicationId, ILogger? logger)
    {
        IDesktopApplications? applications = null;
        if (OperatingSystem.IsWindows()) applications = Optional(CreateWindowsApplications);
#if RUNIC_PORTABLE_PLATFORMS
        else if (OperatingSystem.IsLinux()) applications = CreatePortalApplications(applicationId, logger);
        else if (OperatingSystem.IsMacOS()) applications = CreateMacOSApplications();
#endif
        return applications ?? DesktopApplications.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
    }

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopApplications CreateWindowsApplications() => WindowsPlatformProvider.CreateApplications();

#if RUNIC_PORTABLE_PLATFORMS
    [SupportedOSPlatform("linux")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopApplications CreatePortalApplications(string? applicationId, ILogger? logger) =>
        new PortalApplication(applicationId, logger is null ? null : diagnostic => DesktopLog.PortalDiagnostic(logger, diagnostic)).CreateApplications();

    [SupportedOSPlatform("macos")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopApplications CreateMacOSApplications() => MacOSPlatformProvider.CreateApplications();
#endif

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopSecretStore CreateWindowsSecretStore() => WindowsPlatformProvider.CreateSecretStore();

#if RUNIC_PORTABLE_PLATFORMS
    [SupportedOSPlatform("linux")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopSecretStore CreatePortalSecretStore(string? applicationId, ILogger? logger) =>
        new PortalApplication(applicationId, logger is null ? null : diagnostic => DesktopLog.PortalDiagnostic(logger, diagnostic)).CreateSecretStore();

    [SupportedOSPlatform("macos")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopSecretStore CreateMacOSSecretStore() => MacOSPlatformProvider.CreateSecretStore();
#endif

    // The checks of AddRunicPlatformServices(services, applicationId) for the running operating system.
    internal static void ValidateApplicationId(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        if (OperatingSystem.IsWindows() && (applicationId.Length > 128 || applicationId.Contains('\0')))
            throw new ArgumentException("Supply the AppUserModelID the application registers, at most 128 characters.", nameof(applicationId));
#if RUNIC_PORTABLE_PLATFORMS
        if (OperatingSystem.IsLinux()) ValidatePortalApplicationId(applicationId);
#endif
    }

    /// <summary>Creates the user's trash from the provider for the operating system.</summary>
    /// <returns>A trash; it holds no native resources between requests and needs no disposal.</returns>
    /// <remarks>
    /// Linux follows the freedesktop.org Trash specification, with the Trash portal inside Flatpak or Snap, with either
    /// toolkit and without loading GTK; Windows uses the Recycle Bin and macOS the Trash. It needs no window. Elsewhere
    /// every request reports <see cref="PlatformUnavailableReason.ProviderNotConfigured"/>.
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection)"/> registers
    /// one instance for the application.
    /// </remarks>
    public static IDesktopTrash CreateTrash() => CreateTrash(null);

    internal static IDesktopTrash CreateTrash(ILogger? logger)
    {
        IDesktopTrash? trash = null;
        if (OperatingSystem.IsWindows()) trash = Optional(CreateWindowsTrash);
#if RUNIC_PORTABLE_PLATFORMS
        else if (OperatingSystem.IsLinux()) trash = CreatePortalTrash(logger);
        else if (OperatingSystem.IsMacOS()) trash = CreateMacOSTrash();
#endif
        return trash ?? DesktopTrash.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
    }

    /// <summary>Creates file copy and move with progress, cancellation and conflict decisions.</summary>
    /// <returns>File transfers; they hold no native resources between requests and need no disposal.</returns>
    /// <remarks>
    /// Every operating system uses the managed engine from <see cref="LocalFileTransfers.Create"/>. It needs no window.
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection)"/> registers
    /// one instance for the application.
    /// </remarks>
    public static IDesktopFileTransfers CreateFileTransfers() => LocalFileTransfers.Create();

    /// <summary>Creates thumbnails of files from the provider for the operating system.</summary>
    /// <returns>Thumbnails; they need no disposal.</returns>
    /// <remarks>
    /// Linux reads the freedesktop.org thumbnail cache and, on a miss, asks the session's
    /// <c>org.freedesktop.thumbnails.Thumbnailer1</c> service, such as Tumbler, with either toolkit and without loading
    /// GTK; GNOME and KDE run no such service, so there only thumbnails their file managers cached are returned. Windows
    /// uses the shell's thumbnail providers and macOS Quick Look. It needs no window. Elsewhere every request reports
    /// <see cref="PlatformUnavailableReason.ProviderNotConfigured"/>.
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection)"/> registers
    /// one instance for the application.
    /// </remarks>
    public static IDesktopThumbnails CreateThumbnails() => CreateThumbnails(null);

    internal static IDesktopThumbnails CreateThumbnails(ILogger? logger)
    {
        IDesktopThumbnails? thumbnails = null;
        if (OperatingSystem.IsWindows()) thumbnails = Optional(CreateWindowsThumbnails);
#if RUNIC_PORTABLE_PLATFORMS
        else if (OperatingSystem.IsLinux()) thumbnails = CreatePortalThumbnails(logger);
        else if (OperatingSystem.IsMacOS()) thumbnails = CreateMacOSThumbnails();
#endif
        return thumbnails ?? DesktopThumbnails.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
    }

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopThumbnails CreateWindowsThumbnails() => WindowsPlatformProvider.CreateThumbnails();

#if RUNIC_PORTABLE_PLATFORMS
    [SupportedOSPlatform("linux")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopThumbnails CreatePortalThumbnails(ILogger? logger) =>
        new PortalApplication(null, logger is null ? null : diagnostic => DesktopLog.PortalDiagnostic(logger, diagnostic)).CreateThumbnails();

    [SupportedOSPlatform("macos")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopThumbnails CreateMacOSThumbnails() => MacOSPlatformProvider.CreateThumbnails();
#endif

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopTrash CreateWindowsTrash() => WindowsPlatformProvider.CreateTrash();

#if RUNIC_PORTABLE_PLATFORMS
    [SupportedOSPlatform("linux")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopTrash CreatePortalTrash(ILogger? logger) =>
        new PortalApplication(null, logger is null ? null : diagnostic => DesktopLog.PortalDiagnostic(logger, diagnostic)).CreateTrash();

    [SupportedOSPlatform("macos")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopTrash CreateMacOSTrash() => MacOSPlatformProvider.CreateTrash();
#endif

    /// <summary>Creates the desktop's storage volumes from the provider for the operating system.</summary>
    /// <returns>Application-scoped volumes that the caller disposes at shutdown.</returns>
    /// <remarks>
    /// Linux lists, mounts, unmounts and ejects volumes through UDisks2 on the system bus, with either toolkit and
    /// without loading GTK; UDisks2 asks Polkit for authorization and may show a prompt. It needs no window and
    /// connects to nothing until the first request. Windows lists volumes through the volume management API and ejects
    /// a drive as "Safely Remove Hardware" does; it mounts volumes itself, so mounting and unmounting report
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>. macOS uses Disk Arbitration, as Finder does.
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection)"/> registers
    /// one instance for the application.
    /// </remarks>
    public static IDesktopVolumes CreateVolumes()
    {
        IDesktopVolumes? volumes = null;
        if (OperatingSystem.IsWindows()) volumes = Optional(CreateWindowsVolumes);
#if RUNIC_PORTABLE_PLATFORMS
        else if (OperatingSystem.IsLinux()) volumes = CreatePortalVolumes();
        else if (OperatingSystem.IsMacOS()) volumes = CreateMacOSVolumes();
#endif
        return volumes ?? DesktopVolumes.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
    }

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopVolumes CreateWindowsVolumes() => WindowsPlatformProvider.CreateVolumes();

    /// <summary>Creates the computer's network status for the operating system: online, metered and captive portal.</summary>
    /// <returns>Application-scoped network status that the caller disposes at shutdown.</returns>
    /// <remarks>
    /// Windows reads the Network List Manager's connectivity hint (Windows 10 version 2004 and later), whose check also
    /// finds captive portals; macOS follows Network.framework's path monitor, which has no captive-portal check, so its
    /// connectivity is <see cref="DesktopNetworkConnectivity.Unknown"/> while a network is available. Elsewhere the
    /// status reports <see cref="PlatformUnavailableReason.ProviderNotConfigured"/>. It needs no window.
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection)"/> registers
    /// one instance for the application.
    /// </remarks>
    public static IDesktopNetworkStatus CreateNetworkStatus()
    {
        IDesktopNetworkStatus? status = null;
        if (OperatingSystem.IsWindows()) status = Optional(CreateWindowsNetworkStatus);
#if RUNIC_PORTABLE_PLATFORMS
        else if (OperatingSystem.IsMacOS()) status = CreateMacOSNetworkStatus();
#endif
        return status ?? DesktopNetworkStatus.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
    }

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopNetworkStatus CreateWindowsNetworkStatus() => WindowsPlatformProvider.CreateNetworkStatus();

#if RUNIC_PORTABLE_PLATFORMS
    [SupportedOSPlatform("macos")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopNetworkStatus CreateMacOSNetworkStatus() => MacOSPlatformProvider.CreateNetworkStatus();
#endif

#if RUNIC_PORTABLE_PLATFORMS
    [SupportedOSPlatform("linux")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopVolumes CreatePortalVolumes() => PortalApplication.Inferred.CreateVolumes();

    [SupportedOSPlatform("macos")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopVolumes CreateMacOSVolumes() => MacOSPlatformProvider.CreateVolumes();
#endif

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopStatusIcon CreateWindowsStatusIcon() => WindowsPlatformProvider.CreateStatusIcon();

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopLoginStartup CreateWindowsLoginStartup(string applicationId) =>
        WindowsPlatformProvider.CreateLoginStartup(applicationId);

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopNotifications CreateWindowsNotifications(string applicationId) =>
        WindowsPlatformProvider.CreateNotifications(applicationId);

#if RUNIC_PORTABLE_PLATFORMS
    [SupportedOSPlatform("macos")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopNotifications CreateMacOSNotifications() => MacOSPlatformProvider.CreateNotifications();

    [SupportedOSPlatform("linux")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopNotifications CreatePortalNotifications(string? applicationId, ILogger? logger) =>
        new PortalApplication(applicationId, logger is null ? null : diagnostic => DesktopLog.PortalDiagnostic(logger, diagnostic))
            .CreateNotifications();

    // Validates a Linux desktop ID without creating anything that connects.
    [SupportedOSPlatform("linux")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ValidatePortalApplicationId(string applicationId) => _ = new PortalApplication(applicationId);
#endif

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopSettings CreateWindowsSettings() => WindowsPlatformProvider.CreateSettings();

#if RUNIC_PORTABLE_PLATFORMS
    [SupportedOSPlatform("macos")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopSettings CreateMacOSSettings() => MacOSPlatformProvider.CreateSettings();

    [SupportedOSPlatform("linux")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopSettings CreatePortalSettings() => PortalApplication.Inferred.CreateSettings();
#endif

    internal static IWindowPlatformServices Create(DesktopNativeOwner owner, out DesktopPlatformProvider provider)
    {
        provider = DesktopPlatformProvider.None;
        if (!owner.IsAvailable) return WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        provider = CurrentProvider;
        // Each provider is created in its own method, so the JIT loads only the selected provider's assembly:
        // a GTK 4 process does not even load the GTK 3 provider. The -windows assembly references only the
        // Windows provider, so elsewhere its providers report ProviderNotConfigured.
        NativeServices? native = null;
        if (provider == DesktopPlatformProvider.Windows && OperatingSystem.IsWindows()) native = CreateWindows(owner);
#if RUNIC_PORTABLE_PLATFORMS
        else if (provider == DesktopPlatformProvider.MacOS && OperatingSystem.IsMacOS()) native = CreateMacOS(owner);
        else if (provider == DesktopPlatformProvider.LinuxGtk3 && OperatingSystem.IsLinux()) native = CreateGtk3(owner);
        else if (provider == DesktopPlatformProvider.LinuxGtk4 && OperatingSystem.IsLinux()) native = CreateGtk4(owner);
#endif
        if (native is not { } selected) return WindowPlatformServices.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        return new BoundWindowPlatformServices(owner, selected);
    }

    /// <summary>Asks the desktop to bring an open Desktop window to the front, for example for a second launch.</summary>
    /// <param name="owner">The window's native owner.</param>
    /// <param name="context">The activation token or startup ID that lets the desktop grant focus, such as
    /// <see cref="ApplicationLaunch.PlatformContext"/> of a forwarded launch.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <remarks>
    /// The desktop decides whether to grant focus; without a context many Wayland compositors only mark the window as
    /// wanting attention. Linux presents GTK 3 and GTK 4 windows. Windows restores a minimized window or shows a hidden one, and brings it to
    /// the foreground when the desktop allows it, as it does for the running instance after
    /// <see cref="Runic.Platform.Runtime.ApplicationInstance.ClaimAsync(string, ApplicationLaunch, CancellationToken)"/> forwards a launch; when
    /// Windows refuses, the taskbar button flashes and the result is <see cref="PlatformFailureCode.PermissionDenied"/>.
    /// macOS orders the window front, also a hidden or minimized one, and activates the application, cooperatively on
    /// macOS 14 and later, where a forwarding launch yields to it, and otherwise as the user's request; when macOS
    /// keeps another application active, the result is <see cref="PlatformFailureCode.PermissionDenied"/>. It needs no
    /// context. A window without native dispatch, such as an installed browser, reports
    /// <see cref="PlatformUnavailableReason.OwnerUnavailable"/>, and a closed one
    /// <see cref="PlatformUnavailableReason.OwnerClosed"/>.
    /// </remarks>
    public static async ValueTask<PlatformResult<PlatformUnit>> PresentAsync(DesktopNativeOwner owner, DesktopActivationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.Window.IsOpen) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        if (!owner.IsAvailable) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        try
        {
            var provider = CurrentProvider;
            if (provider == DesktopPlatformProvider.Windows && OperatingSystem.IsWindows())
            {
                try { return await PresentWindows(owner, cancellationToken).ConfigureAwait(false); }
                catch (PlatformNotSupportedException) { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured); }
            }
#if RUNIC_PORTABLE_PLATFORMS
            if (provider == DesktopPlatformProvider.LinuxGtk3 && OperatingSystem.IsLinux()) { await PresentGtk3(owner, context, cancellationToken).ConfigureAwait(false); return Presented; }
            if (provider == DesktopPlatformProvider.LinuxGtk4 && OperatingSystem.IsLinux())
            { await PresentGtk4(owner, context, cancellationToken).ConfigureAwait(false); return Presented; }
            if (provider == DesktopPlatformProvider.MacOS && OperatingSystem.IsMacOS())
                return await PresentMacOS(owner, cancellationToken).ConfigureAwait(false);
#endif
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        }
        catch (OwnerClosedException) { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
    }

    /// <summary>Asks the desktop to bring an open Runic Window to the front, for example for a second launch.</summary>
    /// <param name="window">The open Window, on any host.</param>
    /// <param name="context">The activation token or startup ID that lets the desktop grant focus, such as
    /// <see cref="ApplicationLaunch.PlatformContext"/> of a forwarded launch.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <remarks>
    /// A Window on <see cref="RunicDesktopHost"/> is presented as
    /// <see cref="PresentAsync(DesktopNativeOwner, DesktopActivationContext?, CancellationToken)"/> presents its
    /// <see cref="DesktopBridgeWindow{TViewModel}.NativeOwner"/>. A Window without a native owner reports
    /// <see cref="PlatformUnavailableReason.OwnerUnavailable"/>: one served under
    /// <c>RUNIC_APPLICATION_SERVE_ONLY=1</c>, which has no presentation, or one on another host.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The Window has not opened.</exception>
    // The overloads differ in their first parameter, which no call omits, so the optional ones cannot make a call
    // ambiguous or bind to the other overload.
    public static ValueTask<PlatformResult<PlatformUnit>> PresentAsync(IRunicWindow window, DesktopActivationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Host is IDesktopNativeOwnerSource { NativeOwnerOrNull: { } owner })
            return PresentAsync(owner, context, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PlatformResult<PlatformUnit>>(
            new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable));
    }

    /// <summary>Gets the Desktop window that presents an open Runic Window, without naming its ViewModel type.</summary>
    /// <param name="window">The Window, on any host. It must have opened; it may have closed since.</param>
    /// <returns>
    /// The <see cref="DesktopBridgeWindow{TViewModel}.Window"/> of <see cref="IRunicWindow.Host"/>: the embedded window, or
    /// the installed browser after a fallback (<see cref="DesktopWindow.Browser"/> tells which). <see langword="null"/> for a
    /// Window on another host, such as CS-WebUI, and for one served under <c>RUNIC_APPLICATION_SERVE_ONLY=1</c>, which has
    /// no presentation.
    /// </returns>
    /// <remarks>
    /// For example, to hide the window, subscribe to <see cref="DesktopWindow.Closed"/> or read
    /// <see cref="DesktopWindow.Capabilities"/>. After the window closes it is the closed presentation until the Window is
    /// disposed. <see cref="RunicDesktopHost.GetSurface"/> returns the surface the same way.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The Window has not opened.</exception>
    public static DesktopWindow? GetWindow(IRunicWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Host is IDesktopNativeOwnerSource source ? source.WindowOrNull : null;
    }

    private static readonly PlatformResult<PlatformUnit> Presented = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());

    /// <summary>Asks the screen reader to announce a message for an open Runic Window, such as the result of an action.</summary>
    /// <param name="window">The open Window, on any host.</param>
    /// <param name="message">The text to speak, in the page's language.</param>
    /// <param name="priority">
    /// <see cref="DesktopAnnouncementPriority.Polite"/>, the default, waits for what the screen reader is saying;
    /// <see cref="DesktopAnnouncementPriority.Assertive"/> interrupts it.
    /// </param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <remarks>
    /// The embedded window announces through the platform's accessibility API, as
    /// <see cref="DesktopWindow.AnnounceAsync"/> describes; WebKitGTK reports no ARIA live region to Orca, so on Linux
    /// this is how a page's status reaches a screen reader. A Window without an embedded window reports
    /// <see cref="PlatformUnavailableReason.OwnerUnavailable"/>: an installed browser after a fallback, a Window served
    /// under <c>RUNIC_APPLICATION_SERVE_ONLY=1</c>, or one on another host; a live region in the page reaches the screen
    /// reader there. A closed window reports <see cref="PlatformUnavailableReason.OwnerClosed"/>, and a custom window
    /// host without <see cref="DesktopWindowCapabilities.Announcements"/>
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>. A ViewModel takes
    /// <see cref="IWindowPlatformServices"/> and calls its <see cref="IWindowPlatformServices.AnnounceAsync"/>, which
    /// works on every host, or takes <see cref="DesktopWindowPlatformServices"/> and calls its
    /// <see cref="DesktopWindowPlatformServices.AnnounceAsync"/>.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="message"/> is empty or white space.</exception>
    /// <exception cref="InvalidOperationException">The Window has not opened.</exception>
    public static ValueTask<PlatformResult<PlatformUnit>> AnnounceAsync(IRunicWindow window, string message,
        DesktopAnnouncementPriority priority = DesktopAnnouncementPriority.Polite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var presentation = window.Host is IDesktopNativeOwnerSource { NativeOwnerOrNull: { } owner } ? owner.Window : null;
        return AnnounceAsync(presentation, message, priority, cancellationToken);
    }

    internal static async ValueTask<PlatformResult<PlatformUnit>> AnnounceAsync(DesktopWindow? window, string message,
        DesktopAnnouncementPriority priority, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        cancellationToken.ThrowIfCancellationRequested();
        if (window is null || window.Browser != BrowserKind.Embedded)
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        if (!window.IsOpen) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.Announcements))
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        try
        {
            await window.AnnounceAsync(message, priority, cancellationToken).ConfigureAwait(false);
            return Presented;
        }
        catch (ObjectDisposedException) { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (Exception error) when (error is NotSupportedException or DesktopException { Code: "window-capability-unavailable" })
        {
            // A runtime older than the API, such as GTK 4.12 or ATK before 2.46, or a window closing meanwhile.
            return window.IsOpen
                ? new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable)
                : new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        }
    }

    // The monitors capability: the monitors' own status while the window is open.
    internal static KeyValuePair<string, CapabilityStatus> MonitorStatus(IDesktopMonitors monitors) =>
        new(PlatformCapabilityIds.Monitors, DesktopServiceStatus.Get(monitors));

    private static readonly CapabilityStatus MenuAvailable = new CapabilityStatus.Available();
    private static readonly CapabilityStatus MenuBackendUnavailable = new CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    private static readonly CapabilityStatus MenuOwnerClosed = new CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerClosed);

    // The menu capabilities of a window: a native bar where it is placed in the window or a global bar, and context
    // menus where the window has them. A window of another host, such as WPF, has neither.
    internal static IEnumerable<KeyValuePair<string, CapabilityStatus>> MenuStatuses(DesktopWindow? window)
    {
        if (window is not null && !window.IsOpen)
        {
            yield return new(PlatformCapabilityIds.MenuBar, MenuOwnerClosed);
            yield return new(PlatformCapabilityIds.ContextMenu, MenuOwnerClosed);
            yield break;
        }
        yield return new(PlatformCapabilityIds.MenuBar,
            window?.MenuBarPlacement is DesktopMenuBarPlacement.Window or DesktopMenuBarPlacement.Global ? MenuAvailable : MenuBackendUnavailable);
        yield return new(PlatformCapabilityIds.ContextMenu,
            window?.Capabilities.HasFlag(DesktopWindowCapabilities.ContextMenu) == true ? MenuAvailable : MenuBackendUnavailable);
    }

    // IWindowPlatformServices.ShowContextMenuAsync for a Desktop window: its native context menu, or the reason it has none.
    internal static async ValueTask<PlatformResult<DesktopMenuItem?>> ShowContextMenuAsync(DesktopWindow? window,
        ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions? options, CancellationToken cancellationToken)
    {
        // The unavailable services check the arguments as every implementation does.
        if (window is null || window.Browser != BrowserKind.Embedded)
            return await Unavailable(PlatformUnavailableReason.OwnerUnavailable).ConfigureAwait(false);
        if (!window.IsOpen) return await Unavailable(PlatformUnavailableReason.OwnerClosed).ConfigureAwait(false);
        if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.ContextMenu))
            return await Unavailable(PlatformUnavailableReason.BackendUnavailable).ConfigureAwait(false);
        try
        {
            return new PlatformResult<DesktopMenuItem?>.Success(
                await window.ShowContextMenuAsync(items, options, cancellationToken).ConfigureAwait(false));
        }
        catch (ObjectDisposedException) { return new PlatformResult<DesktopMenuItem?>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (Exception error) when (error is NotSupportedException or DesktopException { Code: "window-capability-unavailable" })
        {
            return new PlatformResult<DesktopMenuItem?>.Unavailable(window.IsOpen
                ? PlatformUnavailableReason.BackendUnavailable
                : PlatformUnavailableReason.OwnerClosed);
        }

        ValueTask<PlatformResult<DesktopMenuItem?>> Unavailable(PlatformUnavailableReason reason) =>
            WindowPlatformServices.Unavailable(reason).ShowContextMenuAsync(items, options, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<PlatformResult<PlatformUnit>> PresentWindows(DesktopNativeOwner owner, CancellationToken cancellationToken) =>
        WindowsPlatformProvider.PresentAsync(owner, cancellationToken);

    // Runic.Application.Views.Wpf presents a WPF window with the same Windows presentation, on the window's thread.
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("windows8.0")]
    internal static PlatformResult<PlatformUnit> PresentWindowsHandle(nint handle) => WindowsWindowPresentation.Present(handle);

#if RUNIC_PORTABLE_PLATFORMS
    [SupportedOSPlatform("linux")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask PresentGtk3(DesktopNativeOwner owner, DesktopActivationContext? context, CancellationToken cancellationToken) =>
        new Gtk3PortalWindowOwner(owner).PresentAsync(context, cancellationToken);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("linux")]
    private static ValueTask PresentGtk4(DesktopNativeOwner owner, DesktopActivationContext? context, CancellationToken cancellationToken) =>
        new Gtk4PortalWindowOwner(owner).PresentAsync(context, cancellationToken);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("macos")]
    private static ValueTask<PlatformResult<PlatformUnit>> PresentMacOS(DesktopNativeOwner owner, CancellationToken cancellationToken) =>
        MacWindowPresenter.PresentAsync(owner, cancellationToken);
#endif

    // Runic.Application.Views.Wpf binds the services of a WPF window, which has no DesktopNativeOwner, to the Windows provider.
    [SupportedOSPlatform("windows")]
    internal static IWindowPlatformServices CreateForWindowsOwner(INativeWindowOwner owner, out DesktopPlatformProvider provider)
    {
        provider = DesktopPlatformProvider.Windows;
        return new BoundWindowPlatformServices(owner, CreateWindows(owner), Runic.Desktop.Internal.WindowsMonitors.Shared);
    }

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeServices CreateWindows(INativeWindowOwner owner) => new(
        WindowsPlatformProvider.CreateFileDialogs(owner),
        WindowsPlatformProvider.CreateTextClipboard(owner),
        Optional(() => WindowsPlatformProvider.CreateFileLauncher(owner)),
        Optional(() => WindowsPlatformProvider.CreateMessageBoxes(owner)));

#if RUNIC_PORTABLE_PLATFORMS
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("macos")]
    private static NativeServices CreateMacOS(DesktopNativeOwner owner) => new(
        MacOSPlatformProvider.CreateFileDialogs(owner),
        MacOSPlatformProvider.CreateTextClipboard(owner),
        MacOSPlatformProvider.CreateFileLauncher(owner),
        MacOSPlatformProvider.CreateMessageBoxes(owner));

    [SupportedOSPlatform("linux")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeServices CreateGtk3(DesktopNativeOwner owner) => new(
        LinuxPlatformProvider.CreateFileDialogs(owner),
        LinuxPlatformProvider.CreateTextClipboard(owner),
        LinuxPlatformProvider.CreateFileLauncher(owner));

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("linux")]
    private static NativeServices CreateGtk4(DesktopNativeOwner owner)
    {
        var portalOwner = Gtk4PlatformProvider.CreatePortalWindowOwner(owner);
        return new(PortalApplication.Inferred.CreateFileDialogs(portalOwner), Gtk4PlatformProvider.CreateTextClipboard(owner),
            PortalApplication.Inferred.CreateFileLauncher(portalOwner));
    }
#endif

    // A provider that requires a newer OS version than the one running reports the service unconfigured.
    private static T? Optional<T>(Func<T?> create) where T : class
    {
        try { return create(); }
        catch (PlatformNotSupportedException) { return null; }
    }

    // A provider without message boxes leaves them null, which reports ProviderNotConfigured.
    internal readonly record struct NativeServices(IPickerBackend Dialogs, ITextClipboard? Clipboard, IDesktopFileLauncher? Launcher,
        IDesktopMessageBoxes? MessageBoxes = null);

    private sealed class BoundWindowPlatformServices : IWindowPlatformServices, IAsyncDisposable
    {
        private readonly PresentationLifetime _lifetime;
        private readonly PresentationFiles _files;
        private readonly PresentationClipboard _clipboard;
        private readonly PresentationFileLauncher _launcher;
        private readonly PresentationMessageBoxes _messageBoxes;

        public BoundWindowPlatformServices(INativeWindowOwner owner, NativeServices native, IDesktopMonitors? monitors = null)
        {
            // A Desktop window's services take the monitors of its host instead (DesktopWindowPlatformServices.Monitors).
            Monitors = monitors ?? DesktopMonitors.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            _lifetime = new PresentationLifetime(() => owner.IsAvailable, owner.Generation);
            _files = new PresentationFiles(_lifetime, native.Dialogs);
            _clipboard = new PresentationClipboard(_lifetime, native.Clipboard);
            _launcher = new PresentationFileLauncher(_lifetime, native.Launcher);
            _messageBoxes = new PresentationMessageBoxes(_lifetime, native.MessageBoxes);
        }

        public IFileDialogs FileDialogs => _files;
        public ITextClipboard Clipboard => _clipboard;
        public IDesktopFileLauncher FileLauncher => _launcher;
        public IDesktopMessageBoxes MessageBoxes => _messageBoxes;

        // A WPF window lists the monitors of Windows, which need no Desktop window.
        public IDesktopMonitors Monitors { get; }

        public CapabilitySnapshot GetSnapshot() => new(_lifetime.Generation,
            _files.GetSnapshot().Statuses.SetItems(_clipboard.GetSnapshot().Statuses).SetItems(_launcher.GetSnapshot().Statuses)
                .SetItems(_messageBoxes.GetSnapshot().Statuses).SetItems(MenuStatuses(window: null)).SetItem(MonitorStatus(Monitors).Key, MonitorStatus(Monitors).Value));

        public async ValueTask DisposeAsync()
        {
            // Drains platform operations, then releases file access and provider resources. A provider resource that
            // must be released on the window's native thread cannot be once the window has gone; that is not a
            // failure of closing the window.
            try { await _lifetime.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) when (IsOwnerClosed(error)) { }
        }

        private static bool IsOwnerClosed(Exception error) => error switch
        {
            OwnerClosedException => true,
            AggregateException aggregate => aggregate.InnerExceptions.All(IsOwnerClosed),
            _ => false,
        };
    }
}

/// <summary>The platform services of one Desktop window, bound to its native owner once the window opens.</summary>
/// <remarks>
/// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices(IServiceCollection)"/> registers one instance per window
/// scope. Before the window opens every service reports <see cref="PlatformUnavailableReason.OwnerUnavailable"/>;
/// <c>OpenWindowAsync</c> on <see cref="RunicDesktopHost"/> binds it to the window as soon as the presentation opens, and closing the window
/// stops it. After that every service reports <see cref="PlatformUnavailableReason.OwnerClosed"/>.
/// </remarks>
public sealed class DesktopWindowPlatformServices : IWindowPlatformServices, IFileDialogs, ITextClipboard, IDesktopFileLauncher, IAsyncDisposable
{
    private static readonly IWindowPlatformServices NotOpen = WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
    private static readonly IWindowPlatformServices Closed = WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerClosed);
    private readonly object _gate = new();
    private IWindowPlatformServices _current = NotOpen;
    private IWindowPlatformServices? _bound;
    private DesktopWindow? _window;
    private Func<string, DesktopAnnouncementPriority, CancellationToken, ValueTask<PlatformResult<PlatformUnit>>>? _announcer;
    private Task? _disposal;
    private DesktopPlatformProvider _provider;

    internal DesktopWindowPlatformServices()
    {
        Monitors = new WindowMonitors(this);
        MessageBoxes = new WindowMessageBoxes(this);
    }

    /// <summary>Gets the provider selected when the window opened, or <see cref="DesktopPlatformProvider.None"/> before that.</summary>
    public DesktopPlatformProvider Provider { get { lock (_gate) return _provider; } }

    /// <summary>Gets whether the window has opened and the services are bound to it, including after it closes.</summary>
    internal bool IsBound { get { lock (_gate) return _bound is not null; } }

    /// <inheritdoc />
    public IFileDialogs FileDialogs => this;
    /// <inheritdoc />
    public ITextClipboard Clipboard => this;
    /// <inheritdoc />
    public IDesktopFileLauncher FileLauncher => this;

    private IWindowPlatformServices Current => Volatile.Read(ref _current);

    internal void Bind(DesktopNativeOwner owner)
    {
        lock (_gate)
        {
            if (_bound is not null) throw new InvalidOperationException("The platform services are already bound to a window.");
            if (_disposal is not null) return;
            _bound = DesktopPlatformServices.Create(owner, out _provider);
            _window = owner.Window;
            Volatile.Write(ref _current, _bound);
        }
    }

    // A window of another Windows host, such as WPF, without a Desktop window: announcements go to the host's own
    // announcer, which checks its window, or report the owner unavailable without one.
    [SupportedOSPlatform("windows")]
    internal void BindWindows(INativeWindowOwner owner,
        Func<string, DesktopAnnouncementPriority, CancellationToken, ValueTask<PlatformResult<PlatformUnit>>>? announcer = null)
    {
        lock (_gate)
        {
            if (_bound is not null) throw new InvalidOperationException("The platform services are already bound to a window.");
            if (_disposal is not null) return;
            _bound = DesktopPlatformServices.CreateForWindowsOwner(owner, out _provider);
            _announcer = announcer;
            Volatile.Write(ref _current, _bound);
        }
    }

    /// <inheritdoc />
    /// <remarks>The menu capabilities follow the Desktop window, whose menus need no platform provider.</remarks>
    public CapabilitySnapshot GetSnapshot()
    {
        DesktopWindow? window;
        IWindowPlatformServices current;
        lock (_gate)
        {
            current = Current;
            window = _disposal is null && _bound is not null ? _window : null;
        }
        var snapshot = current.GetSnapshot();
        if (window is null) return snapshot;
        var monitors = window.IsOpen
            ? DesktopPlatformServices.MonitorStatus(window.Monitors)
            : new(PlatformCapabilityIds.Monitors, new CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerClosed));
        return new(snapshot.Generation, snapshot.Statuses.SetItems(DesktopPlatformServices.MenuStatuses(window)).SetItem(monitors.Key, monitors.Value));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The monitors of the window's host once the window opens: <see cref="DesktopHost.Monitors"/> for a Desktop window,
    /// and those of Windows for a WPF window. Before that they report <see cref="PlatformUnavailableReason.OwnerUnavailable"/>,
    /// and after these services stop <see cref="PlatformUnavailableReason.OwnerClosed"/>. The instance stays the same, so a
    /// ViewModel can keep it from its constructor; a watch follows the monitors it had when it started, so start one after
    /// the window opened.
    /// </remarks>
    public IDesktopMonitors Monitors { get; }

    // The monitors behind Monitors now.
    private IDesktopMonitors CurrentMonitors
    {
        get
        {
            lock (_gate)
            {
                if (_disposal is not null || _bound is null) return Current.Monitors;
                return _window is { } window ? window.Monitors : _bound.Monitors;
            }
        }
    }

    private sealed class WindowMonitors(DesktopWindowPlatformServices services) : IDesktopMonitors, IDesktopServiceStatus
    {
        public CapabilityStatus Status => DesktopServiceStatus.Get(services.CurrentMonitors);

        public ValueTask<PlatformResult<ImmutableArray<DesktopMonitor>>> GetMonitorsAsync(CancellationToken cancellationToken = default) =>
            services.CurrentMonitors.GetMonitorsAsync(cancellationToken);

        public IAsyncEnumerable<PlatformResult<ImmutableArray<DesktopMonitor>>> WatchAsync(CancellationToken cancellationToken = default) =>
            services.CurrentMonitors.WatchAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Task dialogs on Windows, also for a WPF window, and sheets on macOS, once the window opens. Before that they report
    /// <see cref="PlatformUnavailableReason.OwnerUnavailable"/>, and after these services stop
    /// <see cref="PlatformUnavailableReason.OwnerClosed"/>; stopping closes a box that is shown. The instance stays the
    /// same, so a ViewModel can keep it from its constructor.
    /// </remarks>
    public IDesktopMessageBoxes MessageBoxes { get; }

    private sealed class WindowMessageBoxes(DesktopWindowPlatformServices services) : IDesktopMessageBoxes, IDesktopServiceStatus
    {
        public CapabilityStatus Status => DesktopServiceStatus.Get(services.Current.MessageBoxes);

        public ValueTask<PlatformResult<DesktopMessageBoxButton?>> ShowAsync(DesktopMessageBox messageBox, CancellationToken cancellationToken = default) =>
            services.Current.MessageBoxes.ShowAsync(messageBox, cancellationToken);
    }

    /// <summary>Asks the screen reader to announce a message for the window, such as the result of an action.</summary>
    /// <param name="message">The text to speak, in the page's language.</param>
    /// <param name="priority">Whether the message waits for what the screen reader is saying, the default, or interrupts it.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <remarks>
    /// Announces as <see cref="DesktopPlatformServices.AnnounceAsync(IRunicWindow, string, DesktopAnnouncementPriority, CancellationToken)"/>
    /// does, with the same results. Before the window opens it reports <see cref="PlatformUnavailableReason.OwnerUnavailable"/>,
    /// and after these services stop <see cref="PlatformUnavailableReason.OwnerClosed"/>. The services of a WPF window,
    /// from <c>WpfPlatformServices.ForWindow</c>, announce through the window's UI Automation peer.
    /// <see cref="IWindowPlatformServices.AnnounceAsync"/> announces the same way, with
    /// <see cref="PlatformAnnouncementPriority"/>.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="message"/> is empty or white space.</exception>
    public ValueTask<PlatformResult<PlatformUnit>> AnnounceAsync(string message,
        DesktopAnnouncementPriority priority = DesktopAnnouncementPriority.Polite, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        DesktopWindow? window;
        Func<string, DesktopAnnouncementPriority, CancellationToken, ValueTask<PlatformResult<PlatformUnit>>>? announcer;
        lock (_gate)
        {
            if (_disposal is not null)
                return ValueTask.FromResult<PlatformResult<PlatformUnit>>(
                    new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed));
            window = _window;
            announcer = _announcer;
        }
        if (announcer is null) return DesktopPlatformServices.AnnounceAsync(window, message, priority, cancellationToken);
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        cancellationToken.ThrowIfCancellationRequested();
        return announcer(message, priority, cancellationToken);
    }

    // Explicit, so a caller of the concrete type keeps binding to the overload above.
    ValueTask<PlatformResult<PlatformUnit>> IWindowPlatformServices.AnnounceAsync(string message, PlatformAnnouncementPriority priority,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var desktopPriority = priority switch
        {
            PlatformAnnouncementPriority.Polite => DesktopAnnouncementPriority.Polite,
            PlatformAnnouncementPriority.Assertive => DesktopAnnouncementPriority.Assertive,
            _ => throw new ArgumentOutOfRangeException(nameof(priority)),
        };
        return AnnounceAsync(message, desktopPriority, cancellationToken);
    }

    // Explicit, as the default member is: the window's native context menu once it opens.
    ValueTask<PlatformResult<DesktopMenuItem?>> IWindowPlatformServices.ShowContextMenuAsync(ImmutableArray<DesktopMenuEntry> items,
        DesktopContextMenuOptions? options, CancellationToken cancellationToken)
    {
        DesktopWindow? window;
        lock (_gate)
        {
            if (_disposal is not null || _bound is null) return Current.ShowContextMenuAsync(items, options, cancellationToken);
            window = _window;
        }
        // A window of another host, such as WPF, shows no Desktop context menu.
        return window is null
            ? WindowPlatformServices.Unavailable(PlatformUnavailableReason.BackendUnavailable).ShowContextMenuAsync(items, options, cancellationToken)
            : DesktopPlatformServices.ShowContextMenuAsync(window, items, options, cancellationToken);
    }

    /// <summary>Stops the services: waits for platform operations, then releases file access the window still owns.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is null)
            {
                var bound = _bound;
                Volatile.Write(ref _current, Closed);
                _disposal = bound is IAsyncDisposable disposable ? disposable.DisposeAsync().AsTask() : Task.CompletedTask;
            }
            return new(_disposal);
        }
    }

    ValueTask<PickerResult<IDirectoryLease>> IFileDialogs.OpenDirectoryAsync(OpenDirectoryOptions options, CancellationToken cancellationToken) =>
        Current.FileDialogs.OpenDirectoryAsync(options, cancellationToken);
    ValueTask<PickerResult<IDirectoryLeaseList>> IFileDialogs.OpenDirectoriesAsync(OpenDirectoryOptions options, CancellationToken cancellationToken) =>
        Current.FileDialogs.OpenDirectoriesAsync(options, cancellationToken);
    ValueTask<PickerResult<IReadFileLease>> IFileDialogs.OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken) =>
        Current.FileDialogs.OpenFileAsync(options, cancellationToken);
    ValueTask<PickerResult<IReadFileLeaseList>> IFileDialogs.OpenFilesAsync(OpenFileOptions options, CancellationToken cancellationToken) =>
        Current.FileDialogs.OpenFilesAsync(options, cancellationToken);
    ValueTask<PickerResult<ISaveFileLease>> IFileDialogs.SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken) =>
        Current.FileDialogs.SaveFileAsync(options, cancellationToken);
    ValueTask<PlatformResult<string?>> ITextClipboard.ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken) =>
        Current.Clipboard.ReadTextAsync(maximumCharacters, cancellationToken);
    ValueTask<PlatformResult<PlatformUnit>> ITextClipboard.WriteTextAsync(string text, CancellationToken cancellationToken) =>
        Current.Clipboard.WriteTextAsync(text, cancellationToken);
    ValueTask<PlatformResult<PlatformUnit>> IDesktopFileLauncher.LaunchAsync(string path, DesktopFileOperation operation, CancellationToken cancellationToken) =>
        Current.FileLauncher.LaunchAsync(path, operation, cancellationToken);
    ValueTask<PlatformResult<PlatformUnit>> IDesktopFileLauncher.LaunchUriAsync(Uri uri, CancellationToken cancellationToken) =>
        Current.FileLauncher.LaunchUriAsync(uri, cancellationToken);
}

/// <summary>Registers Desktop platform services for dependency injection.</summary>
public static class DesktopPlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers every Runic platform service: one <see cref="DesktopWindowPlatformServices"/> per window scope, exposed
    /// as <see cref="IWindowPlatformServices"/>, <see cref="IFileDialogs"/>, <see cref="ITextClipboard"/> and
    /// <see cref="IDesktopFileLauncher"/>, <see cref="IDesktopMonitors"/> and <see cref="IDesktopMessageBoxes"/>, and one <see cref="IDesktopSettings"/>, <see cref="IDesktopNotifications"/>,
    /// <see cref="IDesktopStatusIcon"/>, <see cref="IDesktopLoginStartup"/>, <see cref="IDesktopInhibition"/>,
    /// <see cref="IDesktopSecretStore"/>, <see cref="IDesktopTrash"/>, <see cref="IDesktopFileTransfers"/>,
    /// <see cref="IDesktopVolumes"/>, <see cref="IDesktopNetworkStatus"/>, <see cref="IDesktopApplications"/>, <see cref="IDesktopThumbnails"/> and
    /// <see cref="IDesktopFileBookmarks"/> for the application, without an application ID.
    /// </summary>
    /// <remarks>
    /// A ViewModel resolved in the scope that <c>OpenWindowAsync</c> on <see cref="RunicDesktopHost"/> creates can take any of these services in
    /// its constructor. The window services need the window's native owner: they bind to it when the window opens, using
    /// <see cref="DesktopPlatformServices.CurrentProvider"/>, and stop when the window closes. A window without a native owner,
    /// such as an installed browser after an embedded-window fallback or a window served under
    /// <c>RUNIC_APPLICATION_SERVE_ONLY=1</c>, and any scope other than a window's, get window services that report
    /// <see cref="PlatformUnavailableReason.OwnerUnavailable"/>.
    /// The application services need no window, so they work the same with or without a native owner: they are
    /// singletons created by <see cref="DesktopPlatformServices.CreateSettings"/>,
    /// <see cref="DesktopPlatformServices.CreateNotifications(string?)"/>, <see cref="DesktopPlatformServices.CreateStatusIcon(string?)"/>,
    /// <see cref="DesktopPlatformServices.CreateLoginStartup(string?)"/>, <see cref="DesktopPlatformServices.CreateInhibition(string?)"/>,
    /// <see cref="DesktopPlatformServices.CreateSecretStore()"/>, <see cref="DesktopPlatformServices.CreateTrash()"/>,
    /// <see cref="DesktopPlatformServices.CreateFileTransfers()"/>, <see cref="DesktopPlatformServices.CreateVolumes"/>, <see cref="DesktopPlatformServices.CreateNetworkStatus"/>, <see cref="DesktopPlatformServices.CreateApplications()"/>
    /// and <see cref="DesktopPlatformServices.CreateThumbnails()"/> when first resolved, and disposed with the service provider. Without an application ID, Linux notifications
    /// use the identity the desktop portal infers and Windows notifications report
    /// <see cref="PlatformUnavailableReason.ProviderNotConfigured"/>; pass the ID to
    /// <see cref="AddRunicPlatformServices(IServiceCollection, string)"/> instead. Existing registrations of these
    /// service types are kept. Privileged actions need declared actions and their own provider package, so the
    /// application registers <see cref="IDesktopPrivilegedActions"/> itself.
    /// </remarks>
    public static IServiceCollection AddRunicPlatformServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddPlatformServices(applicationId: null);
    }

    /// <summary>
    /// Registers the same services as <see cref="AddRunicPlatformServices(IServiceCollection)"/>, with notifications,
    /// the status icon, login startup and inhibition for the application identified by <paramref name="applicationId"/>,
    /// and its <see cref="IDesktopOpenRegistration"/> (<see cref="DesktopPlatformServices.CreateOpenRegistration(string)"/>).
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <param name="applicationId">
    /// The application's identity, the same string on every operating system: on Windows its AppUserModelID, which the
    /// application's installer registers with the shell (a process installed from an MSIX package uses its package's
    /// AppUserModelID instead); on Linux the reverse-DNS ID of its installed desktop entry,
    /// such as <c>org.example.App</c> for <c>org.example.App.desktop</c>. macOS uses the bundle identifier instead.
    /// </param>
    /// <remarks>
    /// The ID is checked here for the running operating system. On Linux the portal registers it when the first
    /// notification request connects, and reports <c>portal-identity-registration-failed</c> when no matching
    /// desktop entry is installed; during development, use the overload without an ID. Portal diagnostics are logged
    /// as event 2003 through the provider's <c>ILoggerFactory</c>.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="applicationId"/> is empty or not a valid ID on this operating system.</exception>
    public static IServiceCollection AddRunicPlatformServices(this IServiceCollection services, string applicationId)
    {
        ArgumentNullException.ThrowIfNull(services);
        DesktopPlatformServices.ValidateApplicationId(applicationId);
        return services.AddPlatformServices(applicationId);
    }

    private static IServiceCollection AddPlatformServices(this IServiceCollection services, string? applicationId)
    {
        services.TryAddSingleton(static _ => DesktopPlatformServices.CreateSettings());
        services.TryAddSingleton(provider => DesktopPlatformServices.CreateNotifications(applicationId,
            provider.GetService<ILoggerFactory>()?.CreateLogger(DesktopBridgeTransport.LogCategory)));
        services.TryAddSingleton(provider => DesktopPlatformServices.CreateLoginStartup(applicationId,
            provider.GetService<ILoggerFactory>()?.CreateLogger(DesktopBridgeTransport.LogCategory)));
        services.TryAddSingleton(provider => DesktopPlatformServices.CreateStatusIcon(applicationId,
            provider.GetService<ILoggerFactory>()?.CreateLogger(DesktopBridgeTransport.LogCategory)));
        services.TryAddSingleton(provider => DesktopPlatformServices.CreateInhibition(applicationId,
            provider.GetService<ILoggerFactory>()?.CreateLogger(DesktopBridgeTransport.LogCategory)));
        services.TryAddSingleton(static provider => DesktopPlatformServices.CreateSecretStore(
            provider.GetService<ILoggerFactory>()?.CreateLogger(DesktopBridgeTransport.LogCategory)));
        services.TryAddSingleton(static provider => DesktopPlatformServices.CreateTrash(
            provider.GetService<ILoggerFactory>()?.CreateLogger(DesktopBridgeTransport.LogCategory)));
        services.TryAddSingleton(static _ => DesktopPlatformServices.CreateFileTransfers());
        services.TryAddSingleton(static _ => DesktopPlatformServices.CreateFileBookmarks());
        if (applicationId is not null)
            services.TryAddSingleton(provider => DesktopPlatformServices.CreateOpenRegistration(applicationId,
                provider.GetService<ILoggerFactory>()?.CreateLogger(DesktopBridgeTransport.LogCategory)));
        services.TryAddSingleton(static _ => DesktopPlatformServices.CreateVolumes());
        services.TryAddSingleton(static _ => DesktopPlatformServices.CreateNetworkStatus());
        services.TryAddSingleton(static provider => DesktopPlatformServices.CreateApplications(
            provider.GetService<ILoggerFactory>()?.CreateLogger(DesktopBridgeTransport.LogCategory)));
        services.TryAddSingleton(static provider => DesktopPlatformServices.CreateThumbnails(
            provider.GetService<ILoggerFactory>()?.CreateLogger(DesktopBridgeTransport.LogCategory)));
        services.TryAddScoped(static _ => new DesktopWindowPlatformServices());
        services.TryAddScoped<IWindowPlatformServices>(static provider => provider.GetRequiredService<DesktopWindowPlatformServices>());
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().FileDialogs);
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().Clipboard);
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().FileLauncher);
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().Monitors);
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().MessageBoxes);
        return services;
    }
}
