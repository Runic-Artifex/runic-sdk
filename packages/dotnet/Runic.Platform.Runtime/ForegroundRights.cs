using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Runic.Platform.Runtime;

// Windows lets only the process the user just started take the foreground. A launch forwarded to the running
// instance passes that right on with AllowSetForegroundWindow, so the instance can bring its window forward.
internal static partial class ForegroundRights
{
    // SIO_AF_UNIX_GETPEERPID: the process at the other end of a Unix domain socket.
    private const int GetPeerProcessId = 0x58000100;

    internal static void GrantPeer(Socket socket)
    {
        if (!OperatingSystem.IsWindows() || PeerProcessId(socket) is not { } processId) return;
        try { _ = AllowSetForegroundWindow((uint)processId); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { /* Without user32 no window comes forward. */ }
    }

    // Null where Windows cannot tell, such as before the Windows 10 versions that report it.
    internal static int? PeerProcessId(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var processId = new byte[sizeof(int)];
        try
        {
            // The byte count is not reported reliably; a process ID is never zero.
            _ = socket.IOControl(GetPeerProcessId, null, processId);
            return BitConverter.ToInt32(processId) is > 0 and var id ? id : null;
        }
        catch (Exception error) when (error is SocketException or NotSupportedException or PlatformNotSupportedException) { return null; }
    }

    [LibraryImport("user32.dll")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static partial int AllowSetForegroundWindow(uint processId);
}
