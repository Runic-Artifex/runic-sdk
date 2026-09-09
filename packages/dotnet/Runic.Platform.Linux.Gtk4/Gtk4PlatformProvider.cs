using System.Runtime.Versioning;
using Runic.Platform;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Gtk4;

/// <summary>Creates GTK 4 services bound to a verified GTK 4 presentation owner.</summary>
[SupportedOSPlatform("linux")]
public static class Gtk4PlatformProvider
{
    /// <summary>Creates a GTK 4 clipboard that dispatches all native work through <paramref name="owner"/>.</summary>
    public static ITextClipboard CreateTextClipboard(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new Gtk4TextClipboard(owner);
    }

    /// <summary>Creates a portal owner that exports GTK 4 X11 or Wayland parents on demand.</summary>
    public static IPortalWindowOwner CreatePortalWindowOwner(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new Gtk4PortalWindowOwner(owner);
    }
}
