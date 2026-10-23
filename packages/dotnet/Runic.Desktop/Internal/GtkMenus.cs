using System.Collections.Immutable;
using System.Globalization;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// The rules the GTK 3 and GTK 4 menu renderers share: where the bar goes, which radio items GTK can show as one radio
// group, and the shortcut table of the unhandled-key path. Pure, so the rules are tested on every platform; linked
// into Runic.Desktop.Gtk4 with the planner.
internal static class GtkMenus
{
    // GdkModifierType's Shift, Control and Alt (Mod1) masks, the same values in GTK 3 and GTK 4.
    internal const uint ModifierMask = DesktopMenuPlanner.GdkShiftMask | DesktopMenuPlanner.GdkControlMask | DesktopMenuPlanner.GdkAltMask;

    // The in-window bar: the Native presentation of a decorated window. Frameless and kiosk windows show none.
    internal static bool ShowsInWindowBar(DesktopMenuBarPresentation presentation, bool frameless, bool kiosk) =>
        presentation == DesktopMenuBarPresentation.Native && !frameless && !kiosk;

    internal static DesktopMenuBarPlacement Placement(DesktopMenuBarPresentation presentation, bool frameless, bool kiosk, bool global = false) =>
        global && UsesGlobalMenu(presentation, kiosk, globalMenu: true) ? DesktopMenuBarPlacement.Global
        : ShowsInWindowBar(presentation, frameless, kiosk) ? DesktopMenuBarPlacement.Window
        : presentation == DesktopMenuBarPresentation.Page ? DesktopMenuBarPlacement.Page
        : DesktopMenuBarPlacement.None;

    // The desktop's global menu (LinuxGlobalMenu): the Native presentation of a window that is not a kiosk, frameless
    // too, while LinuxDesktopOptions.GlobalMenu allows it.
    internal static bool UsesGlobalMenu(DesktopMenuBarPresentation presentation, bool kiosk, bool globalMenu) =>
        globalMenu && presentation == DesktopMenuBarPresentation.Native && !kiosk;

    // The radio groups of one menu level that have exactly one checked item. GTK radio items always keep one item of
    // their group checked, so a group with none or several checked is shown as check items drawn as radio items.
    internal static IReadOnlySet<string> SingleCheckedRadioGroups(ImmutableArray<PlannedMenuEntry> level)
    {
        var checkedCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in level)
        {
            if (entry is { Kind: PlannedMenuKind.Radio, Item: { RadioGroup: { } group } item })
                checkedCount[group] = checkedCount.GetValueOrDefault(group) + (item.IsChecked == true ? 1 : 0);
        }
        return checkedCount.Where(static pair => pair.Value == 1).Select(static pair => pair.Key).ToHashSet(StringComparer.Ordinal);
    }

    // A point of the page's viewport in CSS pixels as widget coordinates of the web view at its zoom level.
    internal static (int X, int Y) PagePoint(double x, double y, double zoom)
    {
        if (!double.IsFinite(zoom) || zoom <= 0) zoom = 1;
        return ((int)Math.Round(x * zoom, MidpointRounding.AwayFromZero), (int)Math.Round(y * zoom, MidpointRounding.AwayFromZero));
    }
}

// The bar's shortcuts for the host's unhandled-key path. The first item in declaration order answers a shortcut, as
// DesktopHost.Validate's menu-shortcut-duplicate says; reserved shortcuts were removed by the planner.
internal sealed class GtkMenuShortcuts
{
    private readonly Dictionary<(uint Keyval, uint Modifiers), PlannedMenuEntry> _entries;

    private GtkMenuShortcuts(Dictionary<(uint Keyval, uint Modifiers), PlannedMenuEntry> entries) => _entries = entries;

    internal static readonly GtkMenuShortcuts Empty = new([]);

    internal int Count => _entries.Count;

    // plan: DesktopMenuPlanner.PlanBar's menus, whose keys start with the menu's declaration index.
    internal static GtkMenuShortcuts Create(ImmutableArray<PlannedMenuEntry> plan)
    {
        var entries = new Dictionary<(uint, uint), PlannedMenuEntry>();
        foreach (var menu in plan.OrderBy(static menu => DeclarationIndex(menu.Key)))
        {
            Add(menu, entries);
        }
        return new(entries);
    }

    private static void Add(PlannedMenuEntry entry, Dictionary<(uint, uint), PlannedMenuEntry> entries)
    {
        if (entry.Kind == PlannedMenuKind.Menu)
        {
            foreach (var child in entry.Children) Add(child, entries);
        }
        else if (entry is { Shortcut: { GdkKeyval: { } keyval } shortcut })
        {
            entries.TryAdd((keyval, shortcut.GdkModifiers), entry);
        }
    }

    private static int DeclarationIndex(string key) =>
        int.TryParse(key.AsSpan(0, key.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : int.MaxValue;

    // The entry a key press answers, or null. keyval and state are the event's; alternates are the keyvals of the same
    // physical key on its other levels and groups, so Ctrl+S also works with a non-Latin layout. The caller checks the
    // entry's Enabled and runs its item through the command path.
    internal PlannedMenuEntry? Match(uint keyval, uint state, ReadOnlySpan<uint> alternates = default)
    {
        if (_entries.Count == 0) return null;
        var modifiers = state & GtkMenus.ModifierMask;
        if (Find(keyval, modifiers) is { } entry) return entry;
        foreach (var alternate in alternates)
        {
            if (alternate != keyval && Find(alternate, modifiers) is { } other) return other;
        }
        return null;
    }

    private PlannedMenuEntry? Find(uint keyval, uint modifiers)
    {
        var key = ToLower(keyval);
        if (_entries.TryGetValue((key, modifiers), out var entry)) return entry;
        // A symbol typed with Shift, such as + on a US keyboard, is declared without it.
        if ((modifiers & DesktopMenuPlanner.GdkShiftMask) != 0 && key == keyval && !IsLetter(key)
            && _entries.TryGetValue((key, modifiers & ~DesktopMenuPlanner.GdkShiftMask), out entry)
            && entry.Shortcut is { Shortcut.Modifiers: var declared } && (declared & DesktopShortcutModifiers.Shift) == 0)
            return entry;
        return null;
    }

    // GDK keyvals of ASCII letters are their characters; the table has the lower-case ones.
    private static uint ToLower(uint keyval) => keyval is >= 'A' and <= 'Z' ? keyval + 0x20 : keyval;

    private static bool IsLetter(uint keyval) => keyval is >= 'a' and <= 'z';
}
