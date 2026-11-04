using System.Runtime.InteropServices;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// The modifier names of the shortcut labels Runic writes itself (DesktopMenuPlanner.DisplayText): the Win32 menus and
// the in-page menu bar. The application's (DesktopPlatformTexts), else on Windows the keyboard layout's (GetKeyNameText:
// Strg and Umschalt on a German layout), else English. GTK and AppKit draw their own labels. Runic.Desktop.Gtk4 links it.
// Without unsafe code or LibraryImport, which Runic.Application.Views.CsWebUi, which links it too, does not allow.
internal static class ShortcutModifierNames
{
    internal static (string Control, string Shift, string Alt) For(MenuPlatform platform)
    {
        var texts = DesktopPlatformTexts.Current;
        var native = platform == MenuPlatform.Windows && OperatingSystem.IsWindows();
        return (
            texts.ControlKeyName ?? (native ? KeyName(VirtualKeyControl) : null) ?? "Ctrl",
            texts.ShiftKeyName ?? (native ? KeyName(VirtualKeyShift) : null) ?? "Shift",
            texts.AltKeyName ?? (native ? KeyName(VirtualKeyMenu) : null) ?? "Alt");
    }

    private const uint VirtualKeyShift = 0x10, VirtualKeyControl = 0x11, VirtualKeyMenu = 0x12;

    // The key's name on the current keyboard layout, or null when Windows has none.
    private static string? KeyName(uint virtualKey)
    {
        try
        {
            var scanCode = MapVirtualKey(virtualKey, 0); // MAPVK_VK_TO_VSC
            if (scanCode == 0) return null;
            var buffer = new char[64];
            var length = GetKeyNameText((int)(scanCode << 16), buffer, buffer.Length);
            return length > 0 ? new string(buffer, 0, Math.Min(length, buffer.Length)) : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW", ExactSpelling = true)]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll", EntryPoint = "GetKeyNameTextW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameText(int lParam, [Out] char[] buffer, int size);
}
