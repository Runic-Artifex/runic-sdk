using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.UI.WindowsAndMessaging;
using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

// Opens a web or mail URI with the shell's URL association: the user's default browser or mail client.
internal sealed class WindowsUriLauncher(INativeWindowOwner owner)
{
    private const int ErrorAccessDenied = 5, ErrorNoAssociation = 1155, ErrorCancelled = 1223;

    public ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(Uri uri, CancellationToken cancellationToken) =>
        DispatchAsync(DesktopUriPolicy.Require(uri), cancellationToken);

    // Hands an absolute URI the policy has accepted to the shell, on the owner's thread. Tests dispatch a test-only
    // scheme through it.
    internal async ValueTask<PlatformResult<PlatformUnit>> DispatchAsync(string uri, CancellationToken cancellationToken)
    {
        PlatformResult<PlatformUnit> result = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        try
        {
            await owner.InvokeAsync(window =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException();
                result = Launch(window, uri);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OwnerClosedException) { }
        return result;
    }

    // ShellExecuteEx with the URI as the file and no class: a string that starts with a scheme of two or more letters
    // is a URL to the shell, which opens it with the scheme's association (UserChoice included), never as a path. No
    // error UI; for a scheme without a handler, Windows 11 still offers to find an application and reports success.
    // Zone checks stay on.
    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static unsafe PlatformResult<PlatformUnit> Launch(nint window, string uri)
    {
        // Shell handlers may use COM; the owner's thread is an STA.
        if (PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED).Value < 0)
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        try
        {
            fixed (char* verb = "open")
            fixed (char* file = uri)
            {
                var info = new WindowsShellInterop.SHELLEXECUTEINFOW
                {
                    cbSize = (uint)sizeof(WindowsShellInterop.SHELLEXECUTEINFOW),
                    fMask = WindowsShellInterop.SEE_MASK_NOASYNC | WindowsShellInterop.SEE_MASK_FLAG_NO_UI,
                    hwnd = window,
                    lpVerb = verb,
                    lpFile = file,
                    nShow = (int)SHOW_WINDOW_CMD.SW_SHOWNORMAL,
                };
                if (WindowsShellInterop.ShellExecuteEx(&info) != 0) return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
                int error = Marshal.GetLastPInvokeError();
                return error switch
                {
                    ErrorNoAssociation => new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
                    ErrorAccessDenied => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied, PlatformDiagnostic.FromWin32Error(error)),
                    ErrorCancelled => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.UserDismissed, PlatformDiagnostic.FromWin32Error(error)),
                    _ => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other, PlatformDiagnostic.FromWin32Error(error)),
                };
            }
        }
        finally { PInvoke.CoUninitialize(); }
    }
}
