using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// The platform a menu is planned for: its menu order, reserved shortcuts and display text.
internal enum MenuPlatform
{
    MacOS,
    Windows,
    Linux,
}

// What a planned entry is. A toggle kind is part of the entry's key, because Plasma's dbusmenu importer treats it as
// immutable per id.
internal enum PlannedMenuKind
{
    Menu,
    Item,
    Check,
    Radio,
    Separator,
}

// One entry of a planned menu. Title is the visible text: the declared title, or the title and the access key in
// parentheses when the title does not contain the key. AccessKeyIndex is the key's index in Title, or -1.
internal sealed record PlannedMenuEntry(
    string Key,
    PlannedMenuKind Kind,
    DesktopMenuEntry Source,
    string Title,
    int AccessKeyIndex,
    bool Enabled,
    PlannedShortcut? Shortcut,
    ImmutableArray<PlannedMenuEntry> Children)
{
    internal DesktopMenuItem? Item => Source as DesktopMenuItem;

    // The label of a Win32 menu: & before the access key, && for a literal &, and the shortcut after a tab.
    internal string Win32Label => DesktopMenuPlanner.Mnemonic(Title, AccessKeyIndex, '&')
        + (Shortcut is { } shortcut ? "\t" + shortcut.DisplayText : "");

    // The label of a GTK mnemonic or a dbusmenu item: _ before the access key and __ for a literal _.
    internal string MnemonicLabel => DesktopMenuPlanner.Mnemonic(Title, AccessKeyIndex, '_');
}

// A shortcut in each native form. A null VirtualKey or GdkKeyval means the key has no layout-independent code; the
// host maps the character at run time.
internal sealed record PlannedShortcut(
    DesktopShortcut Shortcut,
    string DisplayText,
    string AriaKeyShortcuts,
    ushort? VirtualKey,
    byte Win32Modifiers,
    uint? GdkKeyval,
    uint GdkModifiers,
    ImmutableArray<string> DBusMenu);

// Plans a declared menu bar or context menu for a renderer: the platform's menu order, stable keys, access keys and
// escaping, and each shortcut's native forms. Pure, so every renderer (Win32, GTK 3, GTK 4, dbusmenu and the in-page
// state) shares one result and the rules are tested on every platform; self-contained, as Runic.Desktop.Gtk4 links it.
internal static class DesktopMenuPlanner
{
    internal static MenuPlatform CurrentPlatform =>
        OperatingSystem.IsMacOS() ? MenuPlatform.MacOS : OperatingSystem.IsWindows() ? MenuPlatform.Windows : MenuPlatform.Linux;

    // Win32 ACCEL flags.
    internal const byte FVirtKey = 0x01, FShift = 0x04, FControl = 0x08, FAlt = 0x10;
    // GdkModifierType.
    internal const uint GdkShiftMask = 1 << 0, GdkControlMask = 1 << 2, GdkAltMask = 1 << 3;

    // The menus of a bar in the platform's order: on Windows and Linux File first, the rest as declared with the
    // Application menu as a plain menu, then Help. macOS keeps the declaration; its host places menus among the standard ones.
    internal static ImmutableArray<PlannedMenuEntry> PlanBar(IReadOnlyList<DesktopMenu> menus, MenuPlatform platform)
    {
        var planned = new List<(int Order, PlannedMenuEntry Entry)>(menus.Count);
        for (var index = 0; index < menus.Count; index++)
        {
            var menu = menus[index];
            var order = platform == MenuPlatform.MacOS ? 1 : menu.Role switch
            {
                DesktopMenuRole.File => 0,
                DesktopMenuRole.Help => 2,
                _ => 1,
            };
            planned.Add((order, PlanMenu(menu, index.ToString(CultureInfo.InvariantCulture), enabled: true, platform)));
        }
        // OrderBy is stable, so menus of one group keep their declaration order.
        return [.. planned.OrderBy(static menu => menu.Order).Select(static menu => menu.Entry)];
    }

    // The entries of a context menu; Role is ignored and shortcuts are hints only.
    internal static ImmutableArray<PlannedMenuEntry> PlanEntries(ImmutableArray<DesktopMenuEntry> entries, MenuPlatform platform) =>
        PlanChildren(entries, "c", enabled: true, platform);

    private static PlannedMenuEntry PlanMenu(DesktopMenu menu, string key, bool enabled, MenuPlatform platform)
    {
        key += "m";
        var (title, index) = PlaceAccessKey(menu.Title, menu.AccessKey);
        var isEnabled = enabled && menu.IsEnabled;
        return new PlannedMenuEntry(key, PlannedMenuKind.Menu, menu, title, index, isEnabled, null,
            PlanChildren(menu.Items, key, isEnabled, platform));
    }

    private static ImmutableArray<PlannedMenuEntry> PlanChildren(ImmutableArray<DesktopMenuEntry> entries, string parent, bool enabled,
        MenuPlatform platform)
    {
        var children = ImmutableArray.CreateBuilder<PlannedMenuEntry>(entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            var key = parent + "/" + index.ToString(CultureInfo.InvariantCulture);
            switch (entries[index])
            {
                case DesktopMenu submenu:
                    children.Add(PlanMenu(submenu, key, enabled, platform));
                    break;
                case DesktopMenuItem item:
                    var kind = item.RadioGroup is not null ? PlannedMenuKind.Radio
                        : item.IsChecked is not null ? PlannedMenuKind.Check
                        : PlannedMenuKind.Item;
                    key += kind switch { PlannedMenuKind.Radio => "r", PlannedMenuKind.Check => "c", _ => "i" };
                    var (title, accessKey) = PlaceAccessKey(item.Title, item.AccessKey);
                    var shortcut = item.Shortcut is { Key.Length: > 0 } declared && !IsReserved(declared, platform)
                        ? MapShortcut(declared, platform)
                        : null;
                    children.Add(new PlannedMenuEntry(key, kind, item, title, accessKey, enabled && item.IsEnabled, shortcut, []));
                    break;
                case DesktopMenuSeparator separator:
                    children.Add(new PlannedMenuEntry(key + "s", PlannedMenuKind.Separator, separator, "", -1, true, null, []));
                    break;
                default:
                    // An entry kind of a later release, which renderers of this one do not show.
                    break;
            }
        }
        return children.ToImmutable();
    }

    // The visible title and the access key's index in it: the first letter equal to the key in any letter case, or
    // else the key in parentheses after the title, as Windows shows translated titles without the letter.
    internal static (string Title, int Index) PlaceAccessKey(string title, char? accessKey)
    {
        if (accessKey is not { } key) return (title, -1);
        var upper = char.ToUpperInvariant(key);
        for (var index = 0; index < title.Length; index++)
        {
            if (char.ToUpperInvariant(title[index]) == upper) return (title, index);
        }
        return (title + " (" + upper + ")", title.Length + 2);
    }

    // The title with a marker before the access key and every literal marker doubled.
    internal static string Mnemonic(string title, int accessKeyIndex, char marker)
    {
        var text = new StringBuilder(title.Length + 4);
        for (var index = 0; index < title.Length; index++)
        {
            if (index == accessKeyIndex) text.Append(marker);
            if (title[index] == marker) text.Append(marker);
            text.Append(title[index]);
        }
        return text.ToString();
    }

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

    // A shortcut as the platform tells it apart: Control and Primary are both Ctrl on Windows and Linux.
    internal static DesktopShortcut Normalize(DesktopShortcut shortcut, MenuPlatform platform) =>
        platform != MenuPlatform.MacOS && (shortcut.Modifiers & DesktopShortcutModifiers.Control) != 0
            ? new DesktopShortcut(shortcut.Key, (shortcut.Modifiers & ~DesktopShortcutModifiers.Control) | DesktopShortcutModifiers.Primary)
            : shortcut;

    // The standard item that keeps a shortcut on the platform, or null.
    internal static string? ReservedBy(DesktopShortcut shortcut, MenuPlatform platform)
    {
        if (shortcut.Key.Length == 0) return null;
        if (platform == MenuPlatform.MacOS)
            return ReservedMacOS.FirstOrDefault(reserved => reserved.Shortcut == shortcut).Item;
        var normalized = Normalize(shortcut, platform);
        return ReservedOther.FirstOrDefault(reserved => reserved.Shortcut == normalized).Item;
    }

    internal static bool IsReserved(DesktopShortcut shortcut, MenuPlatform platform) => ReservedBy(shortcut, platform) is not null;

    internal static PlannedShortcut MapShortcut(DesktopShortcut shortcut, MenuPlatform platform)
    {
        var modifiers = shortcut.Modifiers;
        var ctrl = (modifiers & (DesktopShortcutModifiers.Primary | DesktopShortcutModifiers.Control)) != 0;
        var shift = (modifiers & DesktopShortcutModifiers.Shift) != 0;
        var alt = (modifiers & DesktopShortcutModifiers.Alt) != 0;
        byte win32 = (byte)(FVirtKey | (ctrl ? FControl : 0) | (shift ? FShift : 0) | (alt ? FAlt : 0));
        uint gdk = (ctrl ? GdkControlMask : 0) | (shift ? GdkShiftMask : 0) | (alt ? GdkAltMask : 0);
        var dbus = ImmutableArray.CreateBuilder<string>(4);
        if (ctrl) dbus.Add("Control");
        if (alt) dbus.Add("Alt");
        if (shift) dbus.Add("Shift");
        dbus.Add(QtKeyName(shortcut.Key));
        return new PlannedShortcut(shortcut, DisplayText(shortcut, platform), AriaKeyShortcuts(shortcut, platform),
            VirtualKey(shortcut.Key), win32, GdkKeyval(shortcut.Key), gdk, dbus.ToImmutable());
    }

    // ⇧⌘S on macOS, Ctrl+Shift+S on Windows, and GTK's order, Shift+Ctrl+S, on Linux.
    internal static string DisplayText(DesktopShortcut shortcut, MenuPlatform platform)
    {
        if (platform != MenuPlatform.Linux) return shortcut.ToDisplayString(platform == MenuPlatform.MacOS);
        if (shortcut.Key.Length == 0) return "";
        var text = new StringBuilder();
        if ((shortcut.Modifiers & DesktopShortcutModifiers.Shift) != 0) text.Append("Shift+");
        if ((shortcut.Modifiers & (DesktopShortcutModifiers.Primary | DesktopShortcutModifiers.Control)) != 0) text.Append("Ctrl+");
        if ((shortcut.Modifiers & DesktopShortcutModifiers.Alt) != 0) text.Append("Alt+");
        // The key as Windows shows it; GTK shows the same names for these keys.
        var other = new DesktopShortcut(shortcut.Key, DesktopShortcutModifiers.None).ToDisplayString(macOS: false);
        return text.Append(other).ToString();
    }

    // The aria-keyshortcuts value: Control+Shift+S, or Meta+Shift+S for Command on macOS.
    internal static string AriaKeyShortcuts(DesktopShortcut shortcut, MenuPlatform platform)
    {
        if (shortcut.Key.Length == 0) return "";
        var modifiers = shortcut.Modifiers;
        var text = new StringBuilder();
        if (platform == MenuPlatform.MacOS)
        {
            if ((modifiers & DesktopShortcutModifiers.Control) != 0) text.Append("Control+");
            if ((modifiers & DesktopShortcutModifiers.Primary) != 0) text.Append("Meta+");
        }
        else if ((modifiers & (DesktopShortcutModifiers.Primary | DesktopShortcutModifiers.Control)) != 0)
        {
            text.Append("Control+");
        }
        if ((modifiers & DesktopShortcutModifiers.Alt) != 0) text.Append("Alt+");
        if ((modifiers & DesktopShortcutModifiers.Shift) != 0) text.Append("Shift+");
        return text.Append(shortcut.Key).ToString();
    }

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
}

// Native item ids for planned keys. A key keeps its id across rebuilds and an id is never given to another key, so a
// click that arrives after the menu changed never lands on a different entry.
internal sealed class MenuIdAllocator(int first = 1, int last = int.MaxValue)
{
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private int _next = first;

    internal int this[string key]
    {
        get
        {
            if (_ids.TryGetValue(key, out var id)) return id;
            if (_next > last) throw new InvalidOperationException("The menu has used every native item id.");
            _ids.Add(key, _next);
            return _next++;
        }
    }

    internal int Count => _ids.Count;
}
