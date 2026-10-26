using System.Runtime.Versioning;
using Runic.Platform.Runtime;

namespace Runic.Platform.MacOS;

/// <summary>The macOS provider of the application-scoped Runic.Platform services; no native initialization occurs
/// until used.</summary>
/// <remarks>
/// Every factory throws <see cref="PlatformNotSupportedException"/> on another operating system. The services take no
/// application ID: macOS identifies the application by its bundle. Window services (file dialogs, the clipboard and file
/// handoff) belong to a window: Runic's hosts create them for each window, such as <c>DesktopPlatformServices.ForWindow</c> of
/// Runic.Application.Views.Desktop. The privileged actions (<see cref="MacPrivilegedActions"/>) report unavailable instead,
/// because generated code registers them on every platform.
/// </remarks>
[SupportedOSPlatform("macos")]
public static class MacOSPlatformProvider
{
    /// <summary>Creates operation-scoped idle power inhibition.</summary>
    public static IDesktopInhibition CreateInhibition()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacDesktopInhibition(); }

    /// <summary>Creates appearance preferences. The AppKit main loop must be running before use.</summary>
    public static IDesktopSettings CreateSettings()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacDesktopSettings(); }
    /// <summary>Creates the menu-bar status icon with a menu, an <c>NSStatusItem</c>.</summary>
    /// <remarks>It needs no window; the Desktop event loop (<c>DesktopEventLoop.Run</c>) must service the main thread, or
    /// requests report <see cref="PlatformUnavailableReason.BackendUnavailable"/> after five seconds.</remarks>
    public static IDesktopStatusIcon CreateStatusIcon()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacStatusIcon(); }
    /// <summary>Creates the application's global shortcuts, Carbon hot keys (<c>RegisterEventHotKey</c>).</summary>
    /// <remarks>
    /// They need no Accessibility permission. The Desktop event loop (<c>DesktopEventLoop.Run</c>) must service the main
    /// thread, or requests report <see cref="PlatformUnavailableReason.BackendUnavailable"/> after five seconds. The
    /// application chooses the keys, so <see cref="IDesktopGlobalShortcuts.ConfigureAsync"/> reports
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>. Letters and punctuation are the keys that type them on
    /// the current keyboard layout; after the user switches input source the keys are registered again and
    /// <see cref="IDesktopGlobalShortcuts.BindingsChanged"/> reports a changed outcome. A combination another application
    /// holds reports <see cref="DesktopGlobalShortcutStatus.Conflict"/>; the standard menus' shortcuts such as ⌘Q, and a
    /// combination <c>RegisterEventHotKey</c> rejects, report <see cref="DesktopGlobalShortcutStatus.Refused"/>.
    /// </remarks>
    public static IDesktopGlobalShortcuts CreateGlobalShortcuts()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacGlobalShortcuts(); }
    /// <summary>Creates login startup for the application bundle, through <c>SMAppService.mainAppService</c>.</summary>
    /// <remarks>macOS 13 and later; a process outside an application bundle, or an earlier macOS, reports
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>. macOS starts the bundle without the request's arguments.</remarks>
    public static IDesktopLoginStartup CreateLoginStartup()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacLoginStartup(); }
    /// <summary>Creates UserNotifications for a bundled application. The AppKit main loop must run before use.</summary>
    public static IDesktopNotifications CreateNotifications()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacDesktopNotifications(); }
    /// <summary>
    /// Creates the requests to open files and URLs: the open-documents and get-URL events the system sends for the
    /// types and schemes in the bundle's <c>Info.plist</c>, and the files and URLs among the arguments of
    /// <paramref name="launch"/> and of the later launches <paramref name="instance"/> receives.
    /// </summary>
    /// <param name="options">The URL schemes and files the application registered.</param>
    /// <param name="instance">The running instance, or <see langword="null"/> without single instance.</param>
    /// <param name="launch">This process's launch, or <see langword="null"/>.</param>
    /// <remarks>
    /// <para>One instance per process; the events arrive while the Desktop event loop runs. Under Runic Desktop's
    /// <c>DesktopEventLoop.Run</c>, Runic's application delegate receives the open events and universal links from
    /// before the application's code runs and keeps them (up to 64) until open requests exist, so call this on any
    /// thread at any time, also after the first window or status icon: the launch's own open event, which the system
    /// sends as the application finishes launching, is then the first request after the launch's arguments. Disposing
    /// the instance makes the delegate keep later events for the next one.</para>
    /// <para>Without that event loop, call it on the main thread before the first window or status icon, because the
    /// system sends the open event of the launch itself as the application finishes launching.</para>
    /// <para>With <see cref="DesktopOpenOptions.WebLinkHosts"/>, universal links to those hosts are requests too: AppKit
    /// hands them to the application delegate (<c>application:continueUserActivity:restorationHandler:</c>). Under the
    /// Desktop event loop that is Runic Desktop's delegate. Without it, Runic sets one that answers only that and
    /// <c>applicationSupportsSecureRestorableState:</c> (YES); an application that sets its own delegate, before or
    /// after, gets the method added to the delegate's class where it has none. The bundle needs the Associated Domains
    /// entitlement (<c>applinks:</c> each host, which a provisioning profile grants) and each host an
    /// <c>apple-app-site-association</c> file.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Another instance is alive; or, without the Desktop event loop, not on
    /// the main thread or after the application finished launching.</exception>
    public static IDesktopOpenRequests CreateOpenRequests(DesktopOpenOptions options, IApplicationInstance? instance = null, ApplicationLaunch? launch = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        return new MacOpenRequests(options, instance, launch);
    }

    /// <summary>Creates the registration of the running application bundle with Launch Services.</summary>
    /// <remarks>
    /// Launch Services takes URL schemes and file types only from the bundle's <c>Info.plist</c>
    /// (<c>CFBundleURLTypes</c>, <c>CFBundleDocumentTypes</c>): <see cref="IDesktopOpenRegistration.RegisterAsync"/>
    /// checks that the bundle declares every item, <see cref="PlatformFailureCode.InvalidData"/> otherwise, and
    /// registers the bundle (<c>LSRegisterURL</c>), as launching it from Finder would. <see cref="DesktopOpenRegistration.DisplayName"/>,
    /// <see cref="DesktopOpenRegistration.Executable"/> and the file types' descriptions and icons come from the bundle.
    /// <see cref="IDesktopOpenRegistration.IsRegisteredAsync"/> asks Launch Services whether it lists the bundle for each
    /// scheme and type. A registered bundle stays registered until it is deleted, so
    /// <see cref="IDesktopOpenRegistration.UnregisterAsync"/> and every request of a process outside an application
    /// bundle report <see cref="PlatformUnavailableReason.BackendUnavailable"/>. It needs no window and no AppKit event loop.
    /// </remarks>
    public static IDesktopOpenRegistration CreateOpenRegistration()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacOpenRegistration(); }

    /// <summary>Creates file bookmarks: security-scoped bookmarks inside the App Sandbox, ordinary <c>NSURL</c> bookmarks
    /// outside it.</summary>
    /// <remarks>
    /// A sandboxed application keeps access to a file the user opened or chose by storing its bookmark; resolving the
    /// bookmark starts security-scoped access (<c>startAccessingSecurityScopedResource</c>) until the access is disposed.
    /// The sandboxed bundle needs the <c>com.apple.security.files.bookmarks.app-scope</c> entitlement. A bookmark created
    /// outside the sandbox resolves inside it only to files the sandbox already lets the application read.
    /// <see cref="IDesktopFileBookmarks.OpenDocumentAsync"/> opens a bookmark as a document whose lease keeps that access
    /// until it is disposed; in the sandbox its writes stage in the item replacement directory. It needs no window and
    /// no AppKit event loop.
    /// </remarks>
    public static IDesktopFileBookmarks CreateFileBookmarks()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacFileBookmarks(); }

    /// <summary>Creates secret storage in the user's default keychain, as generic passwords.</summary>
    /// <remarks>Each secret is an item with the secret's service and account, labelled with the service. The Keychain
    /// may ask the user to unlock it or to allow this application; a request waits for the answer, which cancellation
    /// does not interrupt.</remarks>
    public static IDesktopSecretStore CreateSecretStore()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacSecretStore(); }
    /// <summary>Creates secret storage in the keychain that <paramref name="options"/> choose, as generic passwords.</summary>
    /// <remarks>
    /// <para>
    /// With <see cref="DesktopSecretStoreOptions.UseDataProtectionKeychain"/> the items are in the data-protection
    /// keychain (<c>kSecUseDataProtectionKeychain</c>). A shipped application signs its bundle with a team ID and the
    /// <c>keychain-access-groups</c> entitlement. Without it macOS may answer a read with nothing stored and refuses a
    /// write with <see cref="PlatformFailureCode.PermissionDenied"/> and <c>OSStatus -34018</c>. Items of the login keychain are not
    /// moved there: read them with a store without the option and store them again with this one.
    /// </para>
    /// <para>
    /// With <see cref="DesktopSecretStoreOptions.AllowUserInteraction"/> <see langword="false"/>, a locked login keychain
    /// fails with <see cref="PlatformFailureCode.Locked"/> before any query, and queries forbid authentication UI
    /// (<c>kSecUseAuthenticationUIFail</c>; an <c>LAContext</c> with <c>interactionNotAllowed</c> for the
    /// data-protection keychain), so an access prompt is <see cref="PlatformFailureCode.Locked"/> too.
    /// <see cref="DesktopSecretStoreOptions.RequireEncryption"/> has no effect: the Keychain transfers nothing in the
    /// clear.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static IDesktopSecretStore CreateSecretStore(DesktopSecretStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        return new MacSecretStore(options);
    }
    /// <summary>Creates the Trash, through <c>NSFileManager</c> as Finder moves items.</summary>
    /// <remarks>It needs no window and no AppKit event loop. A volume without a trash, such as a network share, reports
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/> and the item stays where it was.</remarks>
    public static IDesktopTrash CreateTrash()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacTrash(); }
    /// <summary>Creates thumbnails from Quick Look, as Finder shows them.</summary>
    /// <remarks>It needs no window and no AppKit event loop. A file Quick Look cannot thumbnail gets no thumbnail, never
    /// its icon.</remarks>
    public static IDesktopThumbnails CreateThumbnails()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacThumbnails(); }
    /// <summary>Creates the computer's storage volumes, through Disk Arbitration as Finder and Disk Utility see them.</summary>
    /// <remarks>
    /// It needs no window and no AppKit event loop; its Disk Arbitration session starts with the first request and
    /// ends with disposal. An application that keeps a file open on a volume, or refuses through Disk Arbitration,
    /// makes an unmount or eject <see cref="PlatformFailureCode.ResourceBusy"/>. The startup disk's own volumes, and
    /// the images the system mounts under <c>/System</c> or hidden from Finder, are listed with
    /// <see cref="DesktopVolume.IsSystem"/> and are never mounted, unmounted or ejected. A locked APFS volume is listed
    /// locked and unlocks with its passphrase through <c>diskutil</c>; mount options go to Disk Arbitration.
    /// </remarks>
    public static IDesktopVolumes CreateVolumes()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacVolumes(new MacVolumeSource(), MacVolumes.DefaultSettle); }

    /// <summary>Creates the desktop's application database from Launch Services and uniform type identifiers.</summary>
    /// <remarks>
    /// <para>A content type is a uniform type identifier, such as <c>public.plain-text</c>; requests also take a MIME type,
    /// an extension such as <c>.txt</c>, or <c>x-scheme-handler/&lt;scheme&gt;</c> for a URL scheme. An application is
    /// its bundle identifier, such as <c>com.apple.TextEdit</c>.</para>
    /// <para>It needs no window and no AppKit event loop. macOS may ask the user to confirm
    /// <see cref="IDesktopApplications.SetDefaultApplicationAsync"/>, which waits for the answer.</para>
    /// </remarks>
    public static IDesktopApplications CreateApplications()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacDesktopApplications(); }
    // Owned file and URI handoff operations for a window. The caller retains security-scoped access through completion.
    internal static IDesktopFileLauncher CreateFileLauncher(INativeWindowOwner owner)
    { ArgumentNullException.ThrowIfNull(owner); if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacDesktopFileLauncher(owner); }

    // AppKit file dialogs for a window.
    internal static IPickerBackend CreateFileDialogs(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("AppKit requires macOS.");
        return new NativePickerBackend(owner, new MacOsFilePicker(owner));
    }

    /// <summary>Creates the computer's network status from Network.framework's path monitor.</summary>
    /// <remarks>
    /// A satisfied path is available. macOS has no public captive-portal or internet check, so
    /// <see cref="DesktopNetworkState.Connectivity"/> is <see cref="DesktopNetworkConnectivity.Unknown"/> while a network
    /// is available and <see cref="DesktopNetworkConnectivity.None"/> otherwise. An expensive path, such as cellular or a
    /// personal hotspot, and a constrained one (Low Data Mode) are metered. It needs no window and no AppKit event loop.
    /// </remarks>
    public static IDesktopNetworkStatus CreateNetworkStatus()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new NetworkStatusService(new MacNetworkStatus()); }

    // NSAlert sheets on a window.
    internal static IDesktopMessageBoxes CreateMessageBoxes(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("AppKit requires macOS.");
        return new MacMessageBoxes(owner);
    }

    // Badge and progress on the Dock tile, which the application's windows share.
    internal static IWindowTaskbar CreateTaskbar(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("AppKit requires macOS.");
        return new MacDockTile(owner);
    }

    // The general pasteboard for a window: text, HTML, RTF, images, files and the format watch.
    internal static IDesktopClipboard CreateTextClipboard(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("AppKit requires macOS.");
        return new MacOsTextClipboard(owner);
    }
}
