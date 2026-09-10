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
    /// <summary>Creates owned file handoff operations. Caller retains security-scoped access through completion.</summary>
    public static IDesktopFileLauncher CreateFileLauncher(INativePickerOwner owner)
    { ArgumentNullException.ThrowIfNull(owner); if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); return new MacDesktopFileLauncher(owner); }

    public static IPickerBackend CreateFileDialogs(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("AppKit requires macOS.");
        return new NativePickerBackend(owner, new MacOsFilePicker(owner));
    }

    public static ITextClipboard CreateTextClipboard(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("AppKit requires macOS.");
        return new MacOsTextClipboard(owner);
    }
}
