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
// state) shares one result and the rules are tested on every platform. Runic.Desktop.Gtk4 links it with DesktopShortcutKeys.
internal static class DesktopMenuPlanner
{
    internal static MenuPlatform CurrentPlatform =>
        OperatingSystem.IsMacOS() ? MenuPlatform.MacOS : OperatingSystem.IsWindows() ? MenuPlatform.Windows : MenuPlatform.Linux;

    // The key tables are DesktopShortcutKeys of Runic.Platform, which the platform providers' global shortcuts share.
    internal const byte FVirtKey = DesktopShortcutKeys.FVirtKey, FShift = DesktopShortcutKeys.FShift,
        FControl = DesktopShortcutKeys.FControl, FAlt = DesktopShortcutKeys.FAlt;
    internal const uint GdkShiftMask = DesktopShortcutKeys.GdkShiftMask, GdkControlMask = DesktopShortcutKeys.GdkControlMask,
        GdkAltMask = DesktopShortcutKeys.GdkAltMask;

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

    internal static (DesktopShortcut Shortcut, string Item)[] ReservedMacOS => DesktopShortcutKeys.ReservedMacOS;

    internal static (DesktopShortcut Shortcut, string Item)[] ReservedOther => DesktopShortcutKeys.ReservedOther;

    // A shortcut as the platform tells it apart: Control and Primary are both Ctrl on Windows and Linux.
    internal static DesktopShortcut Normalize(DesktopShortcut shortcut, MenuPlatform platform) =>
        platform != MenuPlatform.MacOS ? DesktopShortcutKeys.WithoutControl(shortcut) : shortcut;

    // The standard item that keeps a shortcut on the platform, or null.
    internal static string? ReservedBy(DesktopShortcut shortcut, MenuPlatform platform) =>
        DesktopShortcutKeys.ReservedBy(shortcut, platform == MenuPlatform.MacOS);

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

    internal static ushort? VirtualKey(string key) => DesktopShortcutKeys.VirtualKey(key);

    internal static uint? GdkKeyval(string key) => DesktopShortcutKeys.GdkKeyval(key);

    internal static string QtKeyName(string key) => DesktopShortcutKeys.QtKeyName(key);
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
