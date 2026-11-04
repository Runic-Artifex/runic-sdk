using System.Runtime.InteropServices;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// The modifier names of the shortcut labels Runic writes itself (DesktopMenuPlanner.DisplayText): the Win32 menus and
// the in-page menu bar. One source names all three, so a label never mixes two languages: the application's
// (DesktopPlatformTexts, English for a name it leaves out) once it sets one, else on Windows the keyboard layout's
// (GetKeyNameText: Strg and Umschalt on a German layout), else English. GTK and AppKit draw their own labels.
// GetKeyNameText reads the calling thread's layout, so only a Runic Desktop window thread reads it (ReadKeyboardLayout:
// when the window opens, is activated and its input language changes), and every bar plans with the names it read last.
// Without such a window, as on CS-WebUI, the names are English unless the application sets them.
// Runic.Desktop.Gtk4 and Runic.Application.Views.CsWebUi link this file. Without unsafe code or LibraryImport, which
// Runic.Application.Views.CsWebUi does not allow.
internal static class ShortcutModifierNames
{
    private sealed record Names(string Control, string Shift, string Alt);

    private static Names? s_layout;

    // Raised, on the thread that read them, when the keyboard layout's names change: menu bars plan again.
    internal static event EventHandler? Changed;

    internal static (string Control, string Shift, string Alt) For(MenuPlatform platform)
    {
        var texts = DesktopPlatformTexts.Current;
        if (texts.ControlKeyName is not null || texts.ShiftKeyName is not null || texts.AltKeyName is not null)
            return (texts.ControlKeyName ?? "Ctrl", texts.ShiftKeyName ?? "Shift", texts.AltKeyName ?? "Alt");
        if (platform == MenuPlatform.Windows && Volatile.Read(ref s_layout) is { } layout)
            return (layout.Control, layout.Shift, layout.Alt);
        return ("Ctrl", "Shift", "Alt");
    }

    // On a Windows window thread: the names of the thread's current keyboard layout, or English when it lacks one.
    internal static void ReadKeyboardLayout()
    {
        if (!OperatingSystem.IsWindows()) return;
        UseLayoutNames(KeyName(VirtualKeyControl), KeyName(VirtualKeyShift), KeyName(VirtualKeyMenu));
    }

    // The keyboard layout's names; tests set them on any platform.
    internal static void UseLayoutNames(string? control, string? shift, string? alt)
    {
        var names = control is not null && shift is not null && alt is not null ? new Names(control, shift, alt) : null;
        if (Interlocked.Exchange(ref s_layout, names) != names) Changed?.Invoke(null, EventArgs.Empty);
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
