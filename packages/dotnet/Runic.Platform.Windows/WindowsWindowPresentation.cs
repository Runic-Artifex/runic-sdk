using Runic.Platform.Runtime;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Runic.Platform.Windows;

// Brings an open window forward on its own thread. Windows lets a process take the foreground only when the user
// just interacted with it, or when the process that has it allows it (AllowSetForegroundWindow, which a forwarded
// launch calls for the running instance); otherwise the taskbar button flashes.
[System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
internal static class WindowsWindowPresentation
{
    internal static readonly PlatformDiagnostic Flashed =
        new("SetForegroundWindow", 0, "Windows kept another window in the foreground; the taskbar button flashes instead.");

    internal static async ValueTask<PlatformResult<PlatformUnit>> PresentAsync(INativePickerOwner owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        PlatformResult<PlatformUnit> result = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        try { await owner.InvokeAsync(handle => result = Present(handle), cancellationToken).ConfigureAwait(false); }
        catch (OwnerClosedException) { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        return result;
    }

    // Success when the window took the foreground; PermissionDenied with Flashed when Windows refused and the
    // taskbar button flashes instead.
    internal static unsafe PlatformResult<PlatformUnit> Present(nint handle)
    {
        var window = PInvoke.GetAncestor((HWND)handle, GET_ANCESTOR_FLAGS.GA_ROOT);
        if (window == default) window = (HWND)handle;
        if (PInvoke.IsIconic(window)) PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_RESTORE);
        if (PInvoke.SetForegroundWindow(window) && PInvoke.GetForegroundWindow() == window)
            return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        var flash = new FLASHWINFO
        {
            cbSize = (uint)sizeof(FLASHWINFO),
            hwnd = window,
            // Until the window comes to the foreground.
            dwFlags = FLASHWINFO_FLAGS.FLASHW_TRAY | FLASHWINFO_FLAGS.FLASHW_TIMERNOFG,
        };
        PInvoke.FlashWindowEx(&flash);
        return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied, Flashed);
    }
}
