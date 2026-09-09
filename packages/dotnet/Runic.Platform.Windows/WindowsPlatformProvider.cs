using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

/// <summary>Explicitly selects the Windows provider without probing or loading other providers.</summary>
public static class WindowsPlatformProvider
{
    /// <summary>Creates observable Windows appearance preferences.</summary>
    public static IDesktopSettings CreateSettings()
    { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); return new WindowsDesktopSettings(); }
    /// <summary>Creates native toasts for an installed AppUserModelID. The application owns shell registration.</summary>
    public static IDesktopNotifications CreateNotifications(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        if (applicationId.Length > 128 || applicationId.Contains('\0')) throw new ArgumentException("Invalid AppUserModelID.", nameof(applicationId));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new WindowsDesktopNotifications(applicationId);
    }
    /// <summary>Creates owned shell handoff operations.</summary>
    public static IDesktopFileLauncher CreateFileLauncher(INativePickerOwner owner)
    { ArgumentNullException.ThrowIfNull(owner); if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); return new WindowsFileLauncher(owner); }

    /// <summary>Creates owned native file dialogs. The owner must dispatch on its Windows STA.</summary>
    public static IPickerBackend CreateFileDialogs(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new NativePickerBackend(owner, new WindowsFilePicker(owner));
    }

    /// <summary>Creates native Unicode text clipboard services for a verified presentation owner.</summary>
    public static ITextClipboard CreateTextClipboard(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new WindowsTextClipboard(owner, new Win32Clipboard());
    }
}
