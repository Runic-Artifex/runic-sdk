using Windows.Win32;
using Windows.Win32.Foundation;

namespace Runic.Platform.Windows;

// The package identity of a process installed from an MSIX package. Windows starts such a process with its
// package's AppUserModelID (PackageFamilyName!ApplicationId), which the application cannot know before the package
// is signed: the family name contains a hash of the publisher. Notifications use it instead of the ID the
// application passes, as macOS uses the bundle identifier; HKCU writes are redirected into the package, so login
// startup through the Run key is unavailable.
internal static class WindowsPackageIdentity
{
    private static readonly Lazy<string?> s_current = new(Read);

    // The process's package AppUserModelID, or null for an unpackaged process.
    internal static string? Current => WindowsSupport.IsAvailable ? s_current.Value : null;

    // The AppUserModelID that notifications use: the package's when there is one.
    internal static string NotificationsApplicationId(string applicationId, string? packageApplicationId) =>
        packageApplicationId ?? applicationId;

    private static string? Read()
    {
        uint length = 0;
        // APPMODEL_ERROR_NO_APPLICATION: the process has no package identity.
        if (PInvoke.GetCurrentApplicationUserModelId(ref length, Span<char>.Empty) != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER || length is 0 or > 1024)
            return null;
        // APPLICATION_USER_MODEL_ID_MAX_LENGTH is 130 characters including the terminator.
        Span<char> buffer = stackalloc char[(int)length];
        if (PInvoke.GetCurrentApplicationUserModelId(ref length, buffer) != WIN32_ERROR.NO_ERROR || length == 0 || length > buffer.Length) return null;
        return new string(buffer[..((int)length - 1)]);
    }
}
