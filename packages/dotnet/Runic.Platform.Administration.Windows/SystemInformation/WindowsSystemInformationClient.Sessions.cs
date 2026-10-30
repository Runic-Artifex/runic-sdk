using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.RemoteDesktop;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.SystemInformation;

/// <summary>The user signed in at the physical console.</summary>
/// <param name="SessionId">The console's Remote Desktop Services session ID.</param>
/// <param name="UserName">The user name, without domain.</param>
/// <param name="DomainName">The user's domain, or the computer name for a local account.</param>
public sealed record ConsoleUser(int SessionId, string UserName, string DomainName);

public sealed partial class WindowsSystemInformationClient
{
    /// <summary>Reads the user signed in at the physical console (<c>WTSGetActiveConsoleSessionId</c>,
    /// <c>WTSQuerySessionInformationW</c>); null when no session is attached to the console or nobody is signed in.</summary>
    /// <remarks>Works from a service or a task running as SYSTEM, which have no interactive user of their own. A user
    /// signed in only through Remote Desktop is not the console user.</remarks>
    public ConsoleUser? GetActiveConsoleUser()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        NativeError.Windows();
        var session = PInvoke.WTSGetActiveConsoleSessionId();
        if (session == uint.MaxValue) return null;
        var user = SessionText(session, WTS_INFO_CLASS.WTSUserName);
        if (user is null || user.Length == 0) return null;
        return new(unchecked((int)session), user, SessionText(session, WTS_INFO_CLASS.WTSDomainName) ?? "");
    }

    // Null when the session ended between the two calls (ERROR_FILE_NOT_FOUND or ERROR_INVALID_PARAMETER).
    private static unsafe string? SessionText(uint session, WTS_INFO_CLASS information)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        PWSTR buffer = default;
        uint bytes = 0;
        if (!PInvoke.WTSQuerySessionInformation(HANDLE.Null, session, information, &buffer, &bytes))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 2 or 87 or 7022) return null;
            throw NativeError.Win32("Read console session", error);
        }
        try { return buffer.ToString(); }
        finally { PInvoke.WTSFreeMemory(buffer.Value); }
    }
}
