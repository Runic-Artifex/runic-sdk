using System.Runtime.InteropServices;

namespace Runic.Platform.Windows;

// Shell_NotifyIconW and NOTIFYICONDATAW, and ShellExecuteExW and SHELLEXECUTEINFOW, are handwritten: the Win32 metadata declares NOTIFYICONDATAW per CPU
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
        TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100, TPM_LAYOUTRTL = 0x8000;
    // A right-to-left menu item: text read right to left (MFT_RIGHTORDER) and aligned to the right (MFT_RIGHTJUSTIFY).
    internal const uint MIIM_FTYPE = 0x100, MFT_RIGHTORDER = 0x2000, MFT_RIGHTJUSTIFY = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MENUITEMINFOW
    {
        internal uint cbSize;
        internal uint fMask;
        internal uint fType;
        internal uint fState;
        internal uint wID;
        internal nint hSubMenu;
        internal nint hbmpChecked;
        internal nint hbmpUnchecked;
        internal nuint dwItemData;
        internal char* dwTypeData;
        internal uint cch;
        internal nint hbmpItem;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetMenuItemInfoW")]
    internal static partial int GetMenuItemInfo(nint menu, uint item, int byPosition, MENUITEMINFOW* info);

    [LibraryImport("user32.dll", EntryPoint = "SetMenuItemInfoW")]
    internal static partial int SetMenuItemInfo(nint menu, uint item, int byPosition, MENUITEMINFOW* info);

    [LibraryImport("user32.dll")]
    internal static partial int GetMenuItemCount(nint menu);

    // Marks every item of a menu, not its submenus, right to left.
    internal static void MarkRightToLeft(nint menu)
    {
        int count = GetMenuItemCount(menu);
        for (uint position = 0; position < count; position++)
        {
            var info = new MENUITEMINFOW { cbSize = (uint)sizeof(MENUITEMINFOW), fMask = MIIM_FTYPE };
            if (GetMenuItemInfo(menu, position, 1, &info) == 0) continue;
            info.fType |= MFT_RIGHTORDER | MFT_RIGHTJUSTIFY;
            _ = SetMenuItemInfo(menu, position, 1, &info);
        }
    }

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

    // ShellExecuteExW and SHELLEXECUTEINFOW, handwritten for the same reason: the metadata packs the structure to one
    // byte on x86 only, so CsWin32 refuses it for AnyCPU (PInvoke005). The natural x64/ARM64 layout is 112 bytes.
    internal const uint SEE_MASK_CLASSNAME = 0x1, SEE_MASK_NOASYNC = 0x100, SEE_MASK_FLAG_NO_UI = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SHELLEXECUTEINFOW
    {
        internal uint cbSize;
        internal uint fMask;
        internal nint hwnd;
        internal char* lpVerb;
        internal char* lpFile;
        internal char* lpParameters;
        internal char* lpDirectory;
        internal int nShow;
        internal nint hInstApp;
        internal void* lpIDList;
        internal char* lpClass;
        internal nint hkeyClass;
        internal uint dwHotKey;
        // A union with hMonitor.
        internal nint hIcon;
        internal nint hProcess;
    }

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true)]
    internal static partial int ShellExecuteEx(SHELLEXECUTEINFOW* info);
}
