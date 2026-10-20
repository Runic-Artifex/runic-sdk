using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

/// <summary>Explicitly selects the Windows provider without probing or loading other providers.</summary>
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
    /// <summary>Creates login startup through the user's Run key, under <paramref name="applicationId"/>.</summary>
    /// <remarks>
    /// An entry the user turned off in Task Manager stays off: enabling reports PermissionDenied until the application
    /// disables it. A process installed from an MSIX package gets
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>: Windows redirects its writes to the Run key into the
    /// package, where they have no effect. Such a package declares a <c>desktop:StartupTask</c> in its manifest instead.
    /// </remarks>
    public static IDesktopLoginStartup CreateLoginStartup(string applicationId) => CreateLoginStartup(applicationId, WindowsPackageIdentity.Current);

    internal static IDesktopLoginStartup CreateLoginStartup(string applicationId, string? packageApplicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        if (applicationId.Length > 128 || applicationId.Contains('\0') || applicationId.Contains('\\'))
            throw new ArgumentException("Use the application's ID, at most 128 characters.", nameof(applicationId));
        if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException();
        return packageApplicationId is null
            ? new WindowsLoginStartup(applicationId)
            : DesktopLoginStartup.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    }
    /// <summary>Creates secret storage in the user's Credential Manager, as generic credentials.</summary>
    /// <remarks>Each secret is a credential whose target is <c>service/account</c>, with <c>/</c> and <c>%</c> in the
    /// service percent-encoded, and whose user name is the account. It persists for this user on this computer.</remarks>
    public static IDesktopSecretStore CreateSecretStore()
    { if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException(); return new WindowsSecretStore(); }
    /// <summary>Creates the Recycle Bin, through the shell's own file operation without UI.</summary>
    /// <remarks>An item the shell cannot recycle, such as one on a network share or larger than the Recycle Bin, is not
    /// deleted permanently: the request reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>.</remarks>
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
    /// <see cref="IDesktopVolumes.MountAsync"/> returns a mounted volume as it is and
    /// <see cref="IDesktopVolumes.UnmountAsync"/> succeeds for a volume without a mount path; otherwise both report
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>. <see cref="IDesktopVolumes.EjectAsync"/> safely
    /// removes the drive, as "Safely Remove Hardware" does; a program that keeps a file open on it is
    /// <see cref="PlatformFailureCode.ResourceBusy"/>. The volumes need no window and hold no native resources
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
    // The notification-area icon. Internal: Runic.Application.Views.Desktop selects it through
    // PlatformServices.CreateStatusIcon, as it does the other providers' status icons.
    internal static IDesktopStatusIcon CreateStatusIcon()
    { if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException(); return new WindowsStatusIcon(); }

    // Restores and raises an owned window, or flashes its taskbar button; used by PlatformServices.PresentAsync.
    internal static ValueTask<PlatformResult<PlatformUnit>> PresentAsync(INativeWindowOwner owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException();
        return WindowsWindowPresentation.PresentAsync(owner, cancellationToken);
    }

    /// <summary>Creates owned shell handoff operations: files, and http, https and mailto URIs through the shell's URL association.</summary>
    public static IDesktopFileLauncher CreateFileLauncher(INativeWindowOwner owner)
    { ArgumentNullException.ThrowIfNull(owner); if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException(); return new WindowsFileLauncher(owner); }

    /// <summary>Creates owned native file dialogs. The owner must dispatch on its Windows STA.</summary>
    public static IPickerBackend CreateFileDialogs(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new NativePickerBackend(owner, new WindowsFilePicker(owner));
    }

    /// <summary>Creates native Unicode text clipboard services for a verified presentation owner.</summary>
    public static ITextClipboard CreateTextClipboard(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new WindowsTextClipboard(owner, new Win32Clipboard());
    }
}
