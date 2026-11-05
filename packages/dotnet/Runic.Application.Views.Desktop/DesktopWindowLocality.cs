using System.Net;
using System.Net.NetworkInformation;
using Runic.Desktop;

namespace Runic.Application.Views.Desktop;

// Whether a Runic Desktop window's pages can only be reached from this computer, so the host may push them what it read
// from this computer's desktop (appearance/1): accessibility settings and a custom palette describe the user at this
// computer, and are fingerprinting bits for anyone else. The rule CS-WebUI's window locality applies to unowned dialogs
// (sdk#834, CsWebUiWindowLocality), for a DesktopHost:
// - not serve-only, whose pages a browser on another machine may show;
// - the host listens on loopback (DesktopHostOptions.NetworkExposure is Loopback, not AllInterfaces);
// - nothing but loopback listens on the window's port, so a server that ConfigureServices bound further is not local.
// Where the platform cannot list its TCP listeners, the host's own exposure decides.
internal static class DesktopWindowLocality
{
    internal static bool IsLocal(DesktopHost desktop, DesktopSurface surface, bool serveOnly) =>
        IsLocal(serveOnly, desktop.Options.NetworkExposure, () => surface.Url, ListensOnLoopbackOnly);

    internal static bool IsLocal(bool serveOnly, DesktopNetworkExposure exposure, Func<Uri> url, Func<int, bool?> listensOnLoopbackOnly) =>
        !serveOnly && exposure == DesktopNetworkExposure.Loopback && url() is { IsLoopback: true } local
        && listensOnLoopbackOnly(local.Port) != false;

    // Whether something listens on the TCP port, and only on loopback addresses; null when the listeners cannot be read.
    internal static bool? ListensOnLoopbackOnly(int port)
    {
        if (port is <= 0 or > IPEndPoint.MaxPort) return false;
        IPEndPoint[] listeners;
        try { listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(); }
        catch (Exception error) when (error is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException) { return null; }
        bool listening = false;
        foreach (var listener in listeners)
        {
            if (listener.Port != port) continue;
            if (!IPAddress.IsLoopback(listener.Address)) return false;
            listening = true;
        }
        return listening;
    }
}
