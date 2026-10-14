using System.Runtime.InteropServices;

namespace Runic.Platform.Windows;

// Shell_NotifyIconW and NOTIFYICONDATAW are handwritten: the Win32 metadata declares NOTIFYICONDATAW per CPU
// architecture (32-bit packing differs), and CsWin32 refuses to generate it for an AnyCPU assembly. This layout is
// the natural one used on x64 and ARM64, the architectures .NET 10 supports on Windows besides x86. See
// docs/interop-inventory.md.
[System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
internal static unsafe partial class WindowsShellInterop
{
    internal const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    internal const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    internal const uint NOTIFYICON_VERSION_4 = 4;
    // Callback events with NOTIFYICON_VERSION_4, in the low word of lParam.
    internal const uint NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401;
    internal const uint TPM_RIGHTBUTTON = 0x2, TPM_LEFTALIGN = 0x0, TPM_RIGHTALIGN = 0x8, TPM_BOTTOMALIGN = 0x20,
        TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NOTIFYICONDATAW
    {
        internal uint cbSize;
        internal nint hWnd;
        internal uint uID;
        internal uint uFlags;
        internal uint uCallbackMessage;
        internal nint hIcon;
        internal fixed char szTip[WindowsStatusIconModel.TooltipCapacity];
        internal uint dwState;
        internal uint dwStateMask;
        internal fixed char szInfo[256];
        // A union with uTimeout.
        internal uint uVersion;
        internal fixed char szInfoTitle[64];
        internal uint dwInfoFlags;
        internal Guid guidItem;
        internal nint hBalloonIcon;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    internal static partial int Shell_NotifyIcon(uint message, NOTIFYICONDATAW* data);
}
