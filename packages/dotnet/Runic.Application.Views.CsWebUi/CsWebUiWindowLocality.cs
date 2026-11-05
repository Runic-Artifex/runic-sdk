using System.Net;
using System.Net.NetworkInformation;

namespace Runic.Application.Views.CsWebUi;

// Whether a CS-WebUI window's page can only be reached from this computer, so a dialog it asks for is shown to the user
// at this computer. One per window scope, with unowned dialogs (AddRunicPlatformServices(unownedFileDialogs)): the host
// attaches the window when it shows it, and the window services ask before each dialog. Until then, and for a window that
// RunicCsWebUiHost does not show, the window is not local.
internal sealed class CsWebUiWindowLocality
{
    private Func<bool>? _local;

    // Whether the window is local now; false before the host attaches it.
    internal bool IsLocal
    {
        get
        {
            try { return Volatile.Read(ref _local)?.Invoke() == true; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    internal void Attach(Func<bool> local)
    {
        ArgumentNullException.ThrowIfNull(local);
        Volatile.Write(ref _local, local);
    }

    // A window the host showed itself (no ShowWindow, which may only start the server for another device) and outside
    // serve-only mode, whose server listens on loopback only: WebUiWindow.SetPublic(true) makes it listen on every
    // interface. WebUI reports no peer address for a bridge connection, so the listener stands for its clients.
    internal static bool For(bool serveOnly, bool customShow, Func<int> port) =>
        !serveOnly && !customShow && ListensOnLoopbackOnly(port());

    // Whether something listens on the TCP port, and only on loopback addresses.
    internal static bool ListensOnLoopbackOnly(int port)
    {
        if (port is <= 0 or > IPEndPoint.MaxPort) return false;
        IPEndPoint[] listeners;
        try { listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(); }
        catch (Exception error) when (error is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException) { return false; }
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

// Registered by AddRunicPlatformServices(unownedFileDialogs): RunicCsWebUiHost.Run serves the macOS main thread for the
// dialogs only when an application registers them.
internal sealed class CsWebUiUnownedDialogs;
