using System.Runtime.Versioning;
using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

/// <summary>The Windows provider of the application-scoped Runic.Platform services.</summary>
/// <remarks>
/// Every factory throws <see cref="PlatformNotSupportedException"/> on another operating system or before Windows 8.
/// The application ID that notifications and login startup require is the AppUserModelID and the Run value name: Windows
/// has no other identity for a process outside an MSIX package. Window services (file dialogs, the clipboard and file
/// handoff) belong to a window: Runic's hosts create them for each window, such as <c>DesktopPlatformServices.ForWindow</c> of
/// Runic.Application.Views.Desktop.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsPlatformProvider
{
    /// <summary>Creates operation-scoped idle power inhibition.</summary>
    public static IDesktopInhibition CreateInhibition()
    { if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException(); return new WindowsDesktopInhibition(); }

    /// <summary>Creates observable Windows appearance preferences.</summary>
    public static IDesktopSettings CreateSettings()
    { if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException(); return new WindowsDesktopSettings(); }
    /// <summary>Creates native toasts for an installed AppUserModelID. The application owns shell registration.</summary>
    /// <remarks>
    /// A process installed from an MSIX package uses its package's AppUserModelID (<c>PackageFamilyName!App</c>), which
    /// Windows registers with the package, and ignores <paramref name="applicationId"/>.
    /// </remarks>
    public static IDesktopNotifications CreateNotifications(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        if (applicationId.Length > 128 || applicationId.Contains('\0')) throw new ArgumentException("Invalid AppUserModelID.", nameof(applicationId));
        if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException();
        return new WindowsDesktopNotifications(WindowsPackageIdentity.NotificationsApplicationId(applicationId, WindowsPackageIdentity.Current));
    }
    /// <summary>Creates login startup through the user's Run key, under <paramref name="applicationId"/>, or the package's
    /// startup task.</summary>
    /// <remarks>
    /// <para>An entry the user turned off in Task Manager stays off: enabling reports PermissionDenied until the application
    /// disables it.</para>
    /// <para>A process installed from an MSIX package uses the <c>desktop:StartupTask</c> with <c>TaskId="LoginStartup"</c>
    /// that its manifest declares, through <c>Windows.ApplicationModel.StartupTask</c>: Windows redirects its writes to the
    /// Run key into the package, where they have no effect. Windows starts the application with the manifest's
    /// arguments, not the request's. A task the user or a policy turned off reports
    /// <see cref="PlatformFailureCode.PermissionDenied"/> and stays off, and a package without the task reports
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>.</para>
    /// </remarks>
    public static IDesktopLoginStartup CreateLoginStartup(string applicationId) => CreateLoginStartup(applicationId, WindowsPackageIdentity.Current);

    internal static IDesktopLoginStartup CreateLoginStartup(string applicationId, string? packageApplicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        if (applicationId.Length > 128 || applicationId.Contains('\0') || applicationId.Contains('\\'))
            throw new ArgumentException("Use the application's ID, at most 128 characters.", nameof(applicationId));
        if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException();
        if (packageApplicationId is null) return new WindowsLoginStartup(applicationId);
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134)
            ? new WindowsStartupTask(WindowsStartupTask.DefaultTaskId)
            : DesktopLoginStartup.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    }

    /// <summary>Creates the per-user registration of URL schemes and file types under <c>HKEY_CURRENT_USER\Software\Classes</c>.</summary>
    /// <param name="applicationId">The application's ID: the prefix of its file types' ProgIDs (<c>&lt;applicationId&gt;.notes</c>)
    /// and the name of its <c>Capabilities</c>, which <c>RegisteredApplications</c> lists so that Settings, Default apps,
    /// shows the application.</param>
    /// <remarks>
    /// <para>A scheme gets a URL protocol key and a file type a ProgID, each with the command <c>"&lt;executable&gt;" "%1"</c>,
    /// and the extension's <c>OpenWithProgids</c> lists the ProgID; the extension's default is never set, so the user
    /// keeps choosing defaults. A key in the current user's or the machine's classes that names another command belongs
    /// to another application: <see cref="IDesktopOpenRegistration.RegisterAsync"/> reports
    /// <see cref="PlatformFailureCode.Conflict"/> before writing anything. Registering and unregistering notify the shell
    /// (<c>SHChangeNotify</c>).</para>
    /// <para>A process installed from an MSIX package reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>:
    /// Windows redirects its writes into the package, whose manifest declares <c>uap:Protocol</c> and
    /// <c>uap:FileTypeAssociation</c> extensions instead.</para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="applicationId"/> is empty, longer than 128 characters, or contains a backslash or NUL.</exception>
    public static IDesktopOpenRegistration CreateOpenRegistration(string applicationId) => CreateOpenRegistration(applicationId, WindowsPackageIdentity.Current);

    internal static IDesktopOpenRegistration CreateOpenRegistration(string applicationId, string? packageApplicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        if (applicationId.Length > 128 || applicationId.Contains('\0') || applicationId.Contains('\\') || applicationId.Contains('"'))
            throw new ArgumentException("Use the application's ID, at most 128 characters.", nameof(applicationId));
        if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException();
        return packageApplicationId is null
            ? new WindowsOpenRegistration(applicationId)
            : DesktopOpenRegistrations.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    }

    /// <summary>This process's launch, with the items of an MSIX package's activation as arguments.</summary>
    /// <param name="arguments">The process's arguments, as <c>Main</c> receives them.</param>
    /// <remarks>
    /// Outside a package, the same as <see cref="ApplicationLaunch.FromProcess"/>. In a package it reads the activation
    /// (<c>AppInstance.GetActivatedEventArgs</c>): a protocol activation adds its URI and a file activation every file,
    /// several at once for a file type with <c>MultiSelectModel="Player"</c>, in one process; items the arguments
    /// already carry are not repeated. A launch or startup task activation carries only the arguments.
    /// </remarks>
    public static ApplicationLaunch GetLaunch(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var list = arguments.ToList();
        if (WindowsPackageIdentity.Current is null || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134)) return ApplicationLaunch.FromProcess(list);
        return WindowsActivation.Merge(list, WindowsActivation.Read());
    }
    /// <summary>Creates secret storage in the user's Credential Manager, as generic credentials.</summary>
    /// <remarks>Each secret is a credential whose target is <c>service/account</c>, with <c>/</c> and <c>%</c> in the
    /// service percent-encoded, and whose user name is the account. It persists for this user on this computer. A binary
    /// secret above 2560 bytes, the blob limit, is kept in a file under <c>%LOCALAPPDATA%\Runic\Secrets</c> protected
    /// with DPAPI for this user (<c>CryptProtectData</c>), which the credential refers to; deleting the secret removes
    /// both.</remarks>
    public static IDesktopSecretStore CreateSecretStore()
    { if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException(); return new WindowsSecretStore(); }
    /// <summary>Creates secret storage in the user's Credential Manager with <paramref name="options"/>.</summary>
    /// <remarks>The same store as <see cref="CreateSecretStore()"/>: Credential Manager never prompts and transfers nothing
    /// in the clear, so <see cref="DesktopSecretStoreOptions.AllowUserInteraction"/> and
    /// <see cref="DesktopSecretStoreOptions.RequireEncryption"/> have no effect, nor has the macOS-only
    /// <see cref="DesktopSecretStoreOptions.UseDataProtectionKeychain"/>.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static IDesktopSecretStore CreateSecretStore(DesktopSecretStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CreateSecretStore();
    }
    /// <summary>Creates the Recycle Bin, through the shell's own file operation without UI.</summary>
    /// <remarks>An item the shell cannot recycle, such as one on a network share or larger than the Recycle Bin, is not
    /// deleted permanently: the request reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>. Listing,
    /// restoring and emptying go through the Recycle Bin's shell folder, without UI.</remarks>
    public static IDesktopTrash CreateTrash()
    { if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException(); return new WindowsTrash(); }
    /// <summary>Creates thumbnails from the shell's thumbnail providers and cache, as Explorer shows them.</summary>
    /// <remarks>It needs no window. A file without a thumbnail provider gets no thumbnail, never its icon. Each request
    /// extracts on a single-threaded apartment thread of its own, a few at a time.</remarks>
    public static IDesktopThumbnails CreateThumbnails()
    { if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException(); return new WindowsThumbnails(); }
    /// <summary>Creates the computer's storage volumes, through the volume management API and the Configuration Manager.</summary>
    /// <remarks>
    /// Windows mounts every volume it recognizes itself, at its drive letters and mounted folders:
    /// <see cref="IDesktopVolumes.MountAsync(string, CancellationToken)"/> returns a mounted volume as it is and
    /// <see cref="IDesktopVolumes.UnmountAsync"/> succeeds for a volume without a mount path; otherwise both report
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>. <see cref="IDesktopVolumes.EjectAsync"/> safely
    /// removes the drive, as "Safely Remove Hardware" does; a program that keeps a file open on it is
    /// <see cref="PlatformFailureCode.ResourceBusy"/>; an optical drive opens its tray. A locked BitLocker volume is
    /// listed locked and unlocks through Explorer's prompt. The volumes need no window and hold no native resources
    /// except during a watch.
    /// </remarks>
    public static IDesktopVolumes CreateVolumes()
    {
        if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException();
        return new WindowsVolumes(new WindowsVolumeSource(), WindowsVolumes.DefaultPoll, WindowsVolumes.DefaultSettle);
    }

    /// <summary>Creates the desktop's application database from the shell's file and protocol associations.</summary>
    /// <remarks>
    /// <para>A content type is its MIME type when the registry's MIME database maps that type back to the same
    /// extension, else the extension, such as <c>.txt</c>; <c>x-scheme-handler/&lt;scheme&gt;</c> names a URL protocol
    /// and <c>inode/directory</c> folders. An application is the path of its executable or, for a packaged application,
    /// its AppUserModelID.</para>
    /// <para>Windows lets only the user choose default applications: <see cref="IDesktopApplications.SetDefaultApplicationAsync"/>
    /// reports <see cref="PlatformFailureCode.PermissionDenied"/> with a diagnostic that points to Settings
    /// (<c>ms-settings:defaultapps</c>). It needs no window.</para>
    /// </remarks>
    public static IDesktopApplications CreateApplications()
    { if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException(); return new WindowsDesktopApplications(new RegistryClasses()); }
    /// <summary>Creates the notification-area icon with a menu.</summary>
    /// <remarks>It needs no window: a tray-only application can create it. The icon and its menu run on a thread of their
    /// own.</remarks>
    public static IDesktopStatusIcon CreateStatusIcon()
    { if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException(); return new WindowsStatusIcon(); }
    /// <summary>Creates the application's global shortcuts, registered with <c>RegisterHotKey</c>.</summary>
    /// <remarks>
    /// It needs no window: the keys belong to a message-only window on a thread of its own, created by the first
    /// <see cref="IDesktopGlobalShortcuts.BindAsync"/>. The application chooses the keys, so
    /// <see cref="IDesktopGlobalShortcuts.ConfigureAsync"/> reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>.
    /// A key another application registered reports <see cref="DesktopGlobalShortcutStatus.Conflict"/>; F12, which Windows
    /// keeps for debuggers, the editing and window shortcuts such as Ctrl+C and Alt+F4, and a character the keyboard layout
    /// types only with AltGr report <see cref="DesktopGlobalShortcutStatus.Refused"/>.
    /// </remarks>
    public static IDesktopGlobalShortcuts CreateGlobalShortcuts()
    { if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException(); return new WindowsGlobalShortcuts(); }

    // Restores and raises an owned window, or flashes its taskbar button; used by DesktopPlatformServices.PresentAsync.
    internal static ValueTask<PlatformResult<PlatformUnit>> PresentAsync(INativeWindowOwner owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException();
        return WindowsWindowPresentation.PresentAsync(owner, cancellationToken);
    }

    // Owned shell handoff operations for a window: files, and http, https, mailto and ms-settings URIs through the shell's URL association.
    internal static IDesktopFileLauncher CreateFileLauncher(INativeWindowOwner owner)
    { ArgumentNullException.ThrowIfNull(owner); if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException(); return new WindowsFileLauncher(owner); }

    // Owned native file dialogs for a window. The owner must dispatch on its Windows STA.
    internal static IPickerBackend CreateFileDialogs(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new NativePickerBackend(owner, new WindowsFilePicker(owner));
    }

    // The application's shell handoffs without a window, on the application's UI thread.
    [SupportedOSPlatform("windows8.0")]
    internal static IDesktopFileLauncher CreateApplicationFileLauncher() => new WindowsFileLauncher(WindowsApplicationThread.Shared);

    // File dialogs without a parent window, on the application's UI thread, for a presentation without a native owner.
    [SupportedOSPlatform("windows8.0")]
    internal static IPickerBackend CreateUnownedFileDialogs()
    {
        var owner = WindowsApplicationThread.Shared;
        return new NativePickerBackend(owner, new WindowsFilePicker(owner, unowned: true));
    }

    /// <summary>Creates the computer's network status from the Network List Manager's connectivity hint.</summary>
    /// <remarks>
    /// <para>
    /// Windows reports the connectivity of its own internet check and the cost of the preferred connection
    /// (<c>GetNetworkConnectivityHint</c>, watched with <c>NotifyNetworkConnectivityHintChange</c>). A captive portal, and
    /// a network whose check failed in another way, is constrained internet access, which Runic reports as
    /// <see cref="DesktopNetworkConnectivity.CaptivePortal"/>. A fixed or variable cost, a connection over its data limit
    /// and roaming are metered.
    /// </para>
    /// <para>The API needs Windows 10 version 2004 (build 19041); an older Windows reports
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>. It needs no window.</para>
    /// </remarks>
    public static IDesktopNetworkStatus CreateNetworkStatus()
    {
        if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException();
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && WindowsNetworkStatus.IsSupported
            ? new NetworkStatusService(new WindowsNetworkStatus())
            : DesktopNetworkStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    }

    // Task dialogs owned by a window, on its thread. Without Common Controls version 6 every box is unavailable.
    internal static IDesktopMessageBoxes CreateMessageBoxes(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException();
        return CreateMessageBoxes(owner, WindowsCommonControls.Shared);
    }

    [SupportedOSPlatform("windows8.0")]
    internal static IDesktopMessageBoxes CreateMessageBoxes(INativeWindowOwner owner, WindowsCommonControls? controls) => controls is not null
        ? new WindowsMessageBoxes(owner, controls)
        : DesktopMessageBoxes.Unavailable(PlatformUnavailableReason.BackendUnavailable);

    // Badge and progress on a window's taskbar button.
    internal static IWindowTaskbar CreateTaskbar(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException();
        return new WindowsTaskbar(owner);
    }

    // The host's window procedure calls this, on the window's thread, for the registered TaskbarButtonCreated message.
    [SupportedOSPlatform("windows8.0")]
    internal static void TaskbarButtonCreated(nint window) => WindowsTaskbar.ButtonCreated(window);

    // The registered TaskbarButtonCreated message.
    [SupportedOSPlatform("windows8.0")]
    internal static uint TaskbarButtonCreatedMessage => WindowsTaskbar.ButtonCreatedMessage;

    // The clipboard of a window: text, HTML, RTF, images, files and the format watch.
    internal static IDesktopClipboard CreateTextClipboard(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var native = new Win32Clipboard();
        return new WindowsTextClipboard(owner, native, native);
    }
}
