using System.Globalization;
using System.Text;

namespace Runic.Platform;

// A DesktopShortcut's key and modifiers in each native form, and the shortcuts each platform keeps for itself. One
// source for the menus of Runic Desktop and the global shortcuts of the platform providers: Runic.Desktop uses this copy
// through InternalsVisibleTo, and Runic.Platform.Runtime and Runic.Desktop.Gtk4 compile the file too, as they do
// DesktopMenuRules and the menu planner. Self-contained for that reason: it uses only DesktopShortcut's public members.
internal static class DesktopShortcutKeys
{
    // Win32 ACCEL flags.
    internal const byte FVirtKey = 0x01, FShift = 0x04, FControl = 0x08, FAlt = 0x10;
    // RegisterHotKey modifiers (HOT_KEY_MODIFIERS).
    internal const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModNoRepeat = 0x4000;
    // GdkModifierType.
    internal const uint GdkShiftMask = 1 << 0, GdkControlMask = 1 << 2, GdkAltMask = 1 << 3;
    // Carbon modifier masks (Events.h) for RegisterEventHotKey.
    internal const uint CarbonCommand = 0x0100, CarbonShift = 0x0200, CarbonOption = 0x0800, CarbonControl = 0x1000;

    // The key equivalents of the standard menus Runic Desktop provides on macOS, with their items.
    internal static readonly (DesktopShortcut Shortcut, string Item)[] ReservedMacOS =
    [
        (new("Z"), "Edit ▸ Undo"),
        (new("Z", DesktopShortcutModifiers.Primary | DesktopShortcutModifiers.Shift), "Edit ▸ Redo"),
        (new("X"), "Edit ▸ Cut"),
        (new("C"), "Edit ▸ Copy"),
        (new("V"), "Edit ▸ Paste"),
        (new("A"), "Edit ▸ Select All"),
        (new("Q"), "Quit"),
        (new("H"), "Hide"),
        (new("H", DesktopShortcutModifiers.Primary | DesktopShortcutModifiers.Alt), "Hide Others"),
        (new("W"), "File ▸ Close"),
        (new("M"), "Window ▸ Minimize"),
    ];

    // The editing and window shortcuts that Windows and Linux keep for the focused control and the window manager.
    internal static readonly (DesktopShortcut Shortcut, string Item)[] ReservedOther =
    [
        (new("Z"), "Undo"),
        (new("Y"), "Redo"),
        (new("Z", DesktopShortcutModifiers.Primary | DesktopShortcutModifiers.Shift), "Redo"),
        (new("X"), "Cut"),
        (new("C"), "Copy"),
        (new("V"), "Paste"),
        (new("A"), "Select All"),
        (new("F4", DesktopShortcutModifiers.Alt), "Close Window"),
        (new("F10", DesktopShortcutModifiers.None), "the menu bar"),
    ];

    // A shortcut as Windows and Linux tell it apart: Control and Primary are both Ctrl there.
    internal static DesktopShortcut WithoutControl(DesktopShortcut shortcut) =>
        (shortcut.Modifiers & DesktopShortcutModifiers.Control) != 0
            ? new DesktopShortcut(shortcut.Key, (shortcut.Modifiers & ~DesktopShortcutModifiers.Control) | DesktopShortcutModifiers.Primary)
            : shortcut;

    // The standard item that keeps a shortcut on macOS or on Windows and Linux, or null.
    internal static string? ReservedBy(DesktopShortcut shortcut, bool macOS)
    {
        if (shortcut.Key.Length == 0) return null;
        if (macOS) return ReservedMacOS.FirstOrDefault(reserved => reserved.Shortcut == shortcut).Item;
        var normalized = WithoutControl(shortcut);
        return ReservedOther.FirstOrDefault(reserved => reserved.Shortcut == normalized).Item;
    }

    // Ctrl on Windows and Linux is Primary or Control.
    internal static bool HasCtrl(DesktopShortcutModifiers modifiers) =>
        (modifiers & (DesktopShortcutModifiers.Primary | DesktopShortcutModifiers.Control)) != 0;

    // The Win32 virtual-key code; punctuation uses the US layout's OEM keys, and other characters have none.
    internal static ushort? VirtualKey(string key) => key switch
    {
        [var letter] when char.IsAsciiLetterUpper(letter) => letter,
        [var digit] when char.IsAsciiDigit(digit) => digit,
        ['F', .. var number] when number.Length > 0 && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var f) =>
            (ushort)(0x70 + f - 1),
        "Enter" => 0x0D,
        "Escape" => 0x1B,
        "Tab" => 0x09,
        "Space" => 0x20,
        "Backspace" => 0x08,
        "Delete" => 0x2E,
        "ArrowLeft" => 0x25,
        "ArrowUp" => 0x26,
        "ArrowRight" => 0x27,
        "ArrowDown" => 0x28,
        "Home" => 0x24,
        "End" => 0x23,
        "PageUp" => 0x21,
        "PageDown" => 0x22,
        "Plus" or "=" => 0xBB,
        "," => 0xBC,
        "-" => 0xBD,
        "." => 0xBE,
        "/" => 0xBF,
        ";" => 0xBA,
        "`" => 0xC0,
        "[" => 0xDB,
        "\\" => 0xDC,
        "]" => 0xDD,
        "'" => 0xDE,
        _ => null,
    };

    // The character a punctuation key types, which the current keyboard layout maps to a key at run time; null for
    // letters, digits and named keys, whose codes do not depend on the layout's punctuation.
    internal static char? Character(string key) => key switch
    {
        "Plus" => '+',
        [var character] when !char.IsAsciiLetterOrDigit(character) => character,
        _ => null,
    };

    // The GDK keyval: letters lower-case with the Shift mask carrying case, as GTK accelerators use them.
    internal static uint? GdkKeyval(string key) => key switch
    {
        [var letter] when char.IsAsciiLetterUpper(letter) => char.ToLowerInvariant(letter),
        [var character] when character is > ' ' and < (char)0x7F => character,
        ['F', .. var number] when number.Length > 0 && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var f) =>
            (uint)(0xFFBE + f - 1),
        "Enter" => 0xFF0D,
        "Escape" => 0xFF1B,
        "Tab" => 0xFF09,
        "Space" => 0x20,
        "Backspace" => 0xFF08,
        "Delete" => 0xFFFF,
        "Home" => 0xFF50,
        "ArrowLeft" => 0xFF51,
        "ArrowUp" => 0xFF52,
        "ArrowRight" => 0xFF53,
        "ArrowDown" => 0xFF54,
        "PageUp" => 0xFF55,
        "PageDown" => 0xFF56,
        "End" => 0xFF57,
        "Plus" => 0x2B,
        _ => null,
    };

    // The key of a dbusmenu shortcut, in Qt's portable key names as Qt's own exporter writes them.
    internal static string QtKeyName(string key) => key switch
    {
        "Enter" => "Return",
        "Escape" => "Esc",
        "Delete" => "Del",
        "ArrowUp" => "Up",
        "ArrowDown" => "Down",
        "ArrowLeft" => "Left",
        "ArrowRight" => "Right",
        "PageUp" => "PgUp",
        "PageDown" => "PgDown",
        "Plus" => "+",
        _ => key,
    };

    // The macOS virtual key code (kVK_… in HIToolbox's Events.h). Keys without a character use fixed codes; letters,
    // digits and punctuation use the ANSI (US) positions, which the provider replaces with the key that types the
    // character on the current layout. F21 to F24 have no key on a Mac.
    internal static ushort? MacKeyCode(string key) => key switch
    {
        "A" => 0x00, "S" => 0x01, "D" => 0x02, "F" => 0x03, "H" => 0x04, "G" => 0x05, "Z" => 0x06, "X" => 0x07,
        "C" => 0x08, "V" => 0x09, "B" => 0x0B, "Q" => 0x0C, "W" => 0x0D, "E" => 0x0E, "R" => 0x0F, "Y" => 0x10,
        "T" => 0x11, "1" => 0x12, "2" => 0x13, "3" => 0x14, "4" => 0x15, "6" => 0x16, "5" => 0x17, "=" => 0x18,
        "9" => 0x19, "7" => 0x1A, "-" => 0x1B, "8" => 0x1C, "0" => 0x1D, "]" => 0x1E, "O" => 0x1F, "U" => 0x20,
        "[" => 0x21, "I" => 0x22, "P" => 0x23, "L" => 0x25, "J" => 0x26, "'" => 0x27, "K" => 0x28, ";" => 0x29,
        "\\" => 0x2A, "," => 0x2B, "/" => 0x2C, "N" => 0x2D, "M" => 0x2E, "." => 0x2F, "`" => 0x32,
        "Enter" => 0x24,
        "Tab" => 0x30,
        "Space" => 0x31,
        "Backspace" => 0x33,
        "Escape" => 0x35,
        "Delete" => 0x75,
        "Home" => 0x73,
        "End" => 0x77,
        "PageUp" => 0x74,
        "PageDown" => 0x79,
        "ArrowLeft" => 0x7B,
        "ArrowRight" => 0x7C,
        "ArrowDown" => 0x7D,
        "ArrowUp" => 0x7E,
        "F1" => 0x7A, "F2" => 0x78, "F3" => 0x63, "F4" => 0x76, "F5" => 0x60, "F6" => 0x61, "F7" => 0x62, "F8" => 0x64,
        "F9" => 0x65, "F10" => 0x6D, "F11" => 0x67, "F12" => 0x6F, "F13" => 0x69, "F14" => 0x6B, "F15" => 0x71,
        "F16" => 0x6A, "F17" => 0x40, "F18" => 0x4F, "F19" => 0x50, "F20" => 0x5A,
        _ => null,
    };

    // Whether the key's macOS code is fixed: named keys, and digits, which keep the top-row keys as Windows' virtual keys
    // do. Letters and punctuation are the keys that type them on the current layout.
    internal static bool IsMacLayoutIndependent(string key) => key.Length > 1 ? key != "Plus" : char.IsAsciiDigit(key[0]);

    // Carbon modifiers: Primary is Command and Control the Control key.
    internal static uint CarbonModifiers(DesktopShortcutModifiers modifiers) =>
        ((modifiers & DesktopShortcutModifiers.Primary) != 0 ? CarbonCommand : 0)
        | ((modifiers & DesktopShortcutModifiers.Control) != 0 ? CarbonControl : 0)
        | ((modifiers & DesktopShortcutModifiers.Alt) != 0 ? CarbonOption : 0)
        | ((modifiers & DesktopShortcutModifiers.Shift) != 0 ? CarbonShift : 0);

    // RegisterHotKey modifiers without MOD_NOREPEAT: Primary and Control are both Ctrl.
    internal static uint HotKeyModifiers(DesktopShortcutModifiers modifiers) =>
        (HasCtrl(modifiers) ? ModControl : 0)
        | ((modifiers & DesktopShortcutModifiers.Alt) != 0 ? ModAlt : 0)
        | ((modifiers & DesktopShortcutModifiers.Shift) != 0 ? ModShift : 0);

    // The XKB keysym name of a key, as the XDG shortcuts specification writes triggers; letters lower-case.
    internal static string? KeysymName(string key) => key switch
    {
        [var letter] when char.IsAsciiLetterUpper(letter) => char.ToLowerInvariant(letter).ToString(),
        [var digit] when char.IsAsciiDigit(digit) => key,
        ['F', .. var number] when number.Length > 0 => key,
        "Enter" => "Return",
        "Escape" => "Escape",
        "Tab" => "Tab",
        "Space" => "space",
        "Backspace" => "BackSpace",
        "Delete" => "Delete",
        "Home" => "Home",
        "End" => "End",
        "PageUp" => "Page_Up",
        "PageDown" => "Page_Down",
        "ArrowLeft" => "Left",
        "ArrowUp" => "Up",
        "ArrowRight" => "Right",
        "ArrowDown" => "Down",
        "Plus" or "+" => "plus",
        [var character] => character switch
        {
            '!' => "exclam", '"' => "quotedbl", '#' => "numbersign", '$' => "dollar", '%' => "percent", '&' => "ampersand",
            '\'' => "apostrophe", '(' => "parenleft", ')' => "parenright", '*' => "asterisk", ',' => "comma", '-' => "minus",
            '.' => "period", '/' => "slash", ':' => "colon", ';' => "semicolon", '<' => "less", '=' => "equal",
            '>' => "greater", '?' => "question", '@' => "at", '[' => "bracketleft", '\\' => "backslash",
            ']' => "bracketright", '^' => "asciicircum", '_' => "underscore", '`' => "grave", '{' => "braceleft",
            '|' => "bar", '}' => "braceright", '~' => "asciitilde",
            _ => null,
        },
        _ => null,
    };

    // The trigger of the XDG shortcuts specification, such as CTRL+SHIFT+s, for a portal's preferred_trigger.
    internal static string? XdgTrigger(DesktopShortcut shortcut)
    {
        if (KeysymName(shortcut.Key) is not { } key) return null;
        var text = new StringBuilder();
        if (HasCtrl(shortcut.Modifiers)) text.Append("CTRL+");
        if ((shortcut.Modifiers & DesktopShortcutModifiers.Alt) != 0) text.Append("ALT+");
        if ((shortcut.Modifiers & DesktopShortcutModifiers.Shift) != 0) text.Append("SHIFT+");
        return text.Append(key).ToString();
    }
}
