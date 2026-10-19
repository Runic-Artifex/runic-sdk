using Runic.Platform.Runtime;

namespace Runic.Platform.MacOS;

/// <summary>Explicit AppKit provider selection; no native initialization occurs until used.</summary>
public static class MacOSPlatformProvider
{
    /// <summary>Creates operation-scoped idle power inhibition.</summary>
    public static IDesktopInhibition CreateInhibition()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacDesktopInhibition(); }

    /// <summary>Creates appearance preferences. The AppKit main loop must be running before use.</summary>
    public static IDesktopSettings CreateSettings()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacDesktopSettings(); }
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
    /// Call it on the main thread before the first window or status icon, because the system sends the open event of
    /// the launch itself as the application finishes launching; one instance per process. The events arrive while
    /// the Desktop event loop runs.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Not on the main thread, after the application finished launching,
    /// or while another instance is alive.</exception>
    public static IDesktopOpenRequests CreateOpenRequests(DesktopOpenOptions options, IApplicationInstance? instance = null, ApplicationLaunch? launch = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        return new MacOpenRequests(options, instance, launch);
    }

    /// <summary>Creates secret storage in the user's default keychain, as generic passwords.</summary>
    /// <remarks>Each secret is an item with the secret's service and account, labelled with the service. The Keychain
    /// may ask the user to unlock it or to allow this application; a request waits for the answer, which cancellation
    /// does not interrupt.</remarks>
    public static IDesktopSecretStore CreateSecretStore()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacSecretStore(); }
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
    /// makes an unmount or eject <see cref="PlatformFailureCode.ResourceBusy"/>. The startup disk's own volumes are
    /// listed with <see cref="DesktopVolume.IsSystem"/> and are never mounted, unmounted or ejected.
    /// </remarks>
    public static IDesktopVolumes CreateVolumes()
    { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacVolumes(new MacVolumeSource(), MacVolumes.DefaultSettle); }
    /// <summary>Creates owned file handoff operations. Caller retains security-scoped access through completion.</summary>
    public static IDesktopFileLauncher CreateFileLauncher(INativePickerOwner owner)
    { ArgumentNullException.ThrowIfNull(owner); if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacDesktopFileLauncher(owner); }

    /// <summary>Creates AppKit file dialogs bound to a verified presentation owner.</summary>
    public static IPickerBackend CreateFileDialogs(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("AppKit requires macOS.");
        return new NativePickerBackend(owner, new MacOsFilePicker(owner));
    }

    /// <summary>Creates AppKit text clipboard services for a verified presentation owner.</summary>
    public static ITextClipboard CreateTextClipboard(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("AppKit requires macOS.");
        return new MacOsTextClipboard(owner);
    }
}
