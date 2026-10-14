using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Runic.Platform.Runtime;

// Windows lets only the process the user just started take the foreground. A launch forwarded to the running
// instance passes that right on with AllowSetForegroundWindow, so the instance can bring its window forward; on
// macOS the forwarding process yields activation to it.
internal static partial class ForegroundRights
{
    // SIO_AF_UNIX_GETPEERPID: the process at the other end of a Unix domain socket.
    private const int GetPeerProcessId = 0x58000100;

    // Observes each grant (the process ID, and whether Windows allowed it), for the tests.
    internal static Action<int, bool>? Granted;

    internal static void GrantPeer(Socket socket)
    {
        if (OperatingSystem.IsMacOS()) { YieldToPeer(socket); return; }
        if (!OperatingSystem.IsWindows() || PeerProcessId(socket) is not { } processId) return;
        bool allowed;
        try { allowed = AllowSetForegroundWindow((uint)processId) != 0; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return; /* Without user32 no window comes forward. */ }
        Granted?.Invoke(processId, allowed);
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

    // macOS 14 activates cooperatively: the active application yields activation to the running instance, which
    // then activates when it presents its window. The forwarding process can yield only while it runs AppKit on its
    // main thread, as a launch from Finder or the Dock does before it awaits; elsewhere it has nothing to yield.
    private static void YieldToPeer(Socket socket)
    {
        if (MacPeerProcessId(socket) is not { } processId || PthreadMainNp() == 0) return;
        bool yielded = false;
        try
        {
            if (!NativeLibrary.TryLoad("/System/Library/Frameworks/AppKit.framework/AppKit", out _)) return;
            var application = Send(ObjCClass("NSApplication"), Sel("sharedApplication"));
            if (application == 0 || SendBool(application, Sel("respondsToSelector:"), Sel("yieldActivationToApplication:")) == 0) return;
            var peer = SendNint(ObjCClass("NSRunningApplication"), Sel("runningApplicationWithProcessIdentifier:"), processId);
            if (peer == 0) return;
            SendNint(application, Sel("yieldActivationToApplication:"), peer);
            yielded = true;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return; }
        finally { Granted?.Invoke(processId, yielded); }
    }

    // LOCAL_PEERPID of SOL_LOCAL: the process at the other end of a Unix domain socket on macOS.
    internal static int? MacPeerProcessId(Socket socket)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var processId = new byte[sizeof(int)];
        try
        {
            return socket.GetRawSocketOption(0, 2, processId) == sizeof(int) && BitConverter.ToInt32(processId) is > 0 and var id ? id : null;
        }
        catch (Exception error) when (error is SocketException or NotSupportedException or PlatformNotSupportedException) { return null; }
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_main_np")] private static partial int PthreadMainNp();
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint ObjCClass(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial nint SendNint(nint target, nint selector, nint value);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial byte SendBool(nint target, nint selector, nint value);

    [LibraryImport("user32.dll")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static partial int AllowSetForegroundWindow(uint processId);
}
