using Runic.Platform.Runtime;
using Runic.Platform.Linux.Portal;

namespace Runic.Platform.Linux;

/// <summary>Explicitly selects GTK services without loading another OS provider.</summary>
public static class LinuxPlatformProvider
{
    /// <summary>Creates toolkit-independent portal appearance preferences.</summary>
    public static IDesktopSettings CreateSettings() => Portal.PortalPlatformProvider.CreateSettings();
    /// <summary>Creates application-scoped desktop portal notifications.</summary>
    public static IDesktopNotifications CreateNotifications(string? applicationId = null, Action<PortalDiagnostic>? diagnosticSink = null) => Portal.PortalPlatformProvider.CreateNotifications(applicationId, diagnosticSink);
    /// <summary>Creates owned file handoffs for a GTK3 presentation.</summary>
    public static IDesktopFileLauncher CreateFileLauncher(INativePickerOwner owner)
    { ArgumentNullException.ThrowIfNull(owner); return Portal.PortalPlatformProvider.CreateFileLauncher(new Gtk3PortalWindowOwner(owner)); }

    /// <summary>Creates dialogs bound to the verified GTK presentation owner.</summary>
    public static IPickerBackend CreateFileDialogs(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return PortalPlatformProvider.CreateFileDialogs(new Gtk3PortalWindowOwner(owner));
    }

    /// <summary>Explicitly selects GTK-native dialogs for an unsandboxed compatibility application.</summary>
    public static IPickerBackend CreateGtkNativeFileDialogs(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (File.Exists("/.flatpak-info") || Environment.GetEnvironmentVariable("SNAP") is not null)
            throw new NotSupportedException("Sandboxed applications must use portal file dialogs.");
        return new NativePickerBackend(owner, new LinuxFilePicker(owner));
    }

    /// <summary>Creates the GTK clipboard; dispose it before stopping the owner dispatcher.</summary>
    public static LinuxTextClipboard CreateTextClipboard(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new LinuxTextClipboard(owner);
    }
}
