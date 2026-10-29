using System.Runtime.Versioning;
using Runic.Platform;
using Runic.Platform.Runtime;

[assembly: SupportedOSPlatform("linux")]

namespace Runic.Platform.Linux.Gtk4;

/// <summary>Creates GTK 4 services bound to a verified GTK 4 presentation owner. Internal: Runic.Application.Views.Desktop
/// selects them for GTK 4 windows.</summary>
[SupportedOSPlatform("linux")]
internal static class Gtk4PlatformProvider
{
    /// <summary>Creates a GTK 4 clipboard that dispatches all native work through <paramref name="owner"/>.</summary>
    public static IDesktopClipboard CreateTextClipboard(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new Gtk4TextClipboard(owner);
    }

    /// <summary>Creates GtkAlertDialog message boxes for the GTK 4 window; unavailable before GTK 4.10.</summary>
    public static IDesktopMessageBoxes CreateMessageBoxes(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new Gtk4MessageBoxes(owner);
    }

    // The application's GtkAlertDialogs without a parent window, on the GTK 4 thread that the owner dispatches to.
    internal static IDesktopMessageBoxes CreateUnownedMessageBoxes(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new Gtk4MessageBoxes(owner, unowned: true);
    }

    /// <summary>Creates a portal owner that exports GTK 4 X11 or Wayland parents on demand.</summary>
    public static IPortalWindowOwner CreatePortalWindowOwner(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new Gtk4PortalWindowOwner(owner);
    }
}
