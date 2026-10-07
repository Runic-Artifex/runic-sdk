using System.Runtime.Versioning;

namespace Runic.Platform.Windows;

// Generated bindings carry each API's minimum Windows SDK version; the newest used here is
// Windows 8 (WinRT activation). .NET 10 runs only on Windows 10 or later, so on every
// supported host this guard is equivalent to OperatingSystem.IsWindows().
internal static class WindowsSupport
{
    [SupportedOSPlatformGuard("windows8.0")]
    internal static bool IsAvailable => OperatingSystem.IsWindowsVersionAtLeast(8);
}
