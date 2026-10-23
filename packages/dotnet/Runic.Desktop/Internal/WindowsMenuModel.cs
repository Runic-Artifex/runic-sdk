using System.Collections.Immutable;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// One entry of a Win32 menu: what InsertMenuItemW receives (MENUITEMINFOW fType, fState, wID and dwTypeData), and the
// planned entry it shows. A submenu has its entries in Children; a separator and a submenu have Id 0.
internal sealed record Win32MenuEntry(uint Id, uint Type, uint State, string Label, PlannedMenuEntry Planned, ImmutableArray<Win32MenuEntry> Children)
{
    internal bool IsSubmenu => Planned.Kind == PlannedMenuKind.Menu;
    internal DesktopMenuItem? Item => Planned.Item;
}

// A planned bar or context menu as Win32 menu entries, with the item of each command id. Pure, so the flags, labels
// and ids are tested on every platform; WindowsWebView2Host turns it into HMENUs.
internal sealed class WindowsMenuModel
{
    // MENUITEMINFOW fType and fState values.
    internal const uint MftString = 0x0, MftRadioCheck = 0x200, MftSeparator = 0x800, MftRightOrder = 0x2000, MftRightJustify = 0x4000;
    internal const uint MfsEnabled = 0x0, MfsDisabled = 0x3, MfsChecked = 0x8;
    // WM_COMMAND carries the id in LOWORD(wParam); TrackPopupMenuEx returns 0 for no choice.
    internal const int LastId = 0xFFFF;

    private WindowsMenuModel(ImmutableArray<Win32MenuEntry> entries, ImmutableDictionary<uint, Win32MenuEntry> items,
        ImmutableArray<(PlannedShortcut Shortcut, Win32MenuEntry Entry)> shortcuts)
    {
        Entries = entries;
        Items = items;
        Shortcuts = shortcuts;
    }

    internal static WindowsMenuModel Empty { get; } = new([], ImmutableDictionary<uint, Win32MenuEntry>.Empty, []);

    internal ImmutableArray<Win32MenuEntry> Entries { get; }

    // The items by native id.
    internal ImmutableDictionary<uint, Win32MenuEntry> Items { get; }

    // The planned shortcuts of the items in menu order; the first item of a shortcut answers it, as Validate reports.
    internal ImmutableArray<(PlannedShortcut Shortcut, Win32MenuEntry Entry)> Shortcuts { get; }

    // A menu bar: the platform's menu order, each top-level menu a popup of the bar. Ids come from the window's
    // allocator, so an item keeps its id across SetMenuBar calls and a late WM_COMMAND never reaches another item.
    // The bar itself follows its mirrored window (WS_EX_LAYOUTRTL); its menus cascade and align right to left.
    internal static WindowsMenuModel ForBar(IReadOnlyList<DesktopMenu> menus, MenuIdAllocator ids, bool rightToLeft) =>
        Build(DesktopMenuPlanner.PlanBar(menus, MenuPlatform.Windows), ids, rightToLeft, registerShortcuts: true, bar: true);

    // A context menu: Role is ignored and shortcuts are hints only.
    internal static WindowsMenuModel ForContextMenu(ImmutableArray<DesktopMenuEntry> items, bool rightToLeft) =>
        Build(DesktopMenuPlanner.PlanEntries(items, MenuPlatform.Windows), new MenuIdAllocator(1, LastId), rightToLeft, registerShortcuts: false, bar: false);

    private static WindowsMenuModel Build(ImmutableArray<PlannedMenuEntry> planned, MenuIdAllocator ids, bool rightToLeft, bool registerShortcuts,
        bool bar)
    {
        var items = ImmutableDictionary.CreateBuilder<uint, Win32MenuEntry>();
        var shortcuts = ImmutableArray.CreateBuilder<(PlannedShortcut, Win32MenuEntry)>();
        var entries = Convert(planned, ids, rightToLeft, items, registerShortcuts ? shortcuts : null, topLevelOfBar: bar);
        return new WindowsMenuModel(entries, items.ToImmutable(), shortcuts.ToImmutable());
    }

    private static ImmutableArray<Win32MenuEntry> Convert(ImmutableArray<PlannedMenuEntry> planned, MenuIdAllocator ids, bool rightToLeft,
        ImmutableDictionary<uint, Win32MenuEntry>.Builder items, ImmutableArray<(PlannedShortcut, Win32MenuEntry)>.Builder? shortcuts,
        bool topLevelOfBar = false)
    {
        // As the status icon's menu (WindowsShellInterop.MarkRightToLeft): submenus open to the left, text on the right.
        var order = rightToLeft && !topLevelOfBar ? MftRightOrder | MftRightJustify : 0;
        var entries = ImmutableArray.CreateBuilder<Win32MenuEntry>(planned.Length);
        foreach (var entry in planned)
        {
            var state = entry.Enabled ? MfsEnabled : MfsDisabled;
            switch (entry.Kind)
            {
                case PlannedMenuKind.Separator:
                    entries.Add(new Win32MenuEntry(0, MftSeparator | order, MfsEnabled, "", entry, []));
                    break;
                case PlannedMenuKind.Menu:
                    entries.Add(new Win32MenuEntry(0, MftString | order, state, entry.Win32Label, entry,
                        Convert(entry.Children, ids, rightToLeft, items, shortcuts)));
                    break;
                default:
                    var id = (uint)ids[entry.Key];
                    var type = MftString | order | (entry.Kind == PlannedMenuKind.Radio ? MftRadioCheck : 0);
                    if (entry.Item?.IsChecked == true) state |= MfsChecked;
                    var item = new Win32MenuEntry(id, type, state, entry.Win32Label, entry, []);
                    items[id] = item;
                    if (shortcuts is not null && entry.Shortcut is { } shortcut) shortcuts.Add((shortcut, item));
                    entries.Add(item);
                    break;
            }
        }
        return entries.ToImmutable();
    }

    // The item whose shortcut a key press is: the virtual key and Ctrl, Shift and Alt, or for a key without a
    // layout-independent virtual key its text. Windows has no Command key, so a press with the Windows key matches none.
    internal Win32MenuEntry? Match(int virtualKey, string? key, bool control, bool shift, bool alt, bool meta)
    {
        if (meta) return null;
        foreach (var (shortcut, entry) in Shortcuts)
        {
            var modifiers = shortcut.Shortcut.Modifiers;
            if (control != ((modifiers & (DesktopShortcutModifiers.Primary | DesktopShortcutModifiers.Control)) != 0)
                || shift != ((modifiers & DesktopShortcutModifiers.Shift) != 0)
                || alt != ((modifiers & DesktopShortcutModifiers.Alt) != 0))
                continue;
            var matches = shortcut.VirtualKey is { } code
                ? code == virtualKey
                : key is { Length: > 0 } && string.Equals(DesktopShortcut.NormalizeKey(key), shortcut.Shortcut.Key, StringComparison.Ordinal);
            if (matches) return entry;
        }
        return null;
    }

    // Whether a top-level menu of the bar has this access key, so Alt and the key open it.
    internal bool HasMenuAccessKey(string? key) =>
        key is [var character] && char.IsLetterOrDigit(character) && Entries.Any(entry => entry is { IsSubmenu: true, Planned.AccessKeyIndex: >= 0 }
            && char.ToUpperInvariant(entry.Planned.Title[entry.Planned.AccessKeyIndex]) == char.ToUpperInvariant(character));

    // Whether a virtual key with these modifiers is the shortcut of an item, so the browser keeps its own accelerator off.
    internal bool IsShortcut(int virtualKey, bool control, bool shift, bool alt) =>
        Match(virtualKey, null, control, shift, alt, meta: false) is not null;
}

// A key the page left unhandled, as the WebView2 document-start script reports it (WindowsMenuKeys.Script).
internal sealed record MenuKeyReport(int KeyCode, string Key, bool Control, bool Shift, bool Alt, bool Meta);

// WebView2 has no unhandled-key event, so the page reports its keys: after the page's own listeners ran, a trusted
// keydown that no listener cancelled and that can be a shortcut (with Ctrl, Alt or the Windows key, or a key without
// text, such as F2 or Delete) reaches the host. The host runs only a declared, enabled item whose command can run, so
// a page that sends such messages itself gains nothing it could not do by pressing the key.
internal static class WindowsMenuKeys
{
    internal static string Script(string token) => $$"""
        (() => {
          "use strict";
          const view = globalThis.chrome?.webview;
          if (globalThis.top !== globalThis || typeof view?.postMessage !== "function") return;
          const post = view.postMessage.bind(view);
          const stringify = JSON.stringify;
          const later = globalThis.setTimeout.bind(globalThis);
          const modifiers = ["Control", "Shift", "Alt", "Meta", "AltGraph", "CapsLock", "NumLock", "ScrollLock", "OS", "Dead", "Process", "Unidentified"];
          globalThis.addEventListener("keydown", event => {
            if (!event.isTrusted || event.isComposing || typeof event.key !== "string" || modifiers.includes(event.key)) return;
            if (event.getModifierState("AltGraph")) return;
            if (!event.ctrlKey && !event.altKey && !event.metaKey && event.key.length === 1) return;
            // After dispatch: a listener of the page may still cancel the key.
            later(() => {
              if (event.defaultPrevented) return;
              post(stringify({ runicMenuKey: "{{token}}", keyCode: event.keyCode, key: event.key,
                ctrl: event.ctrlKey, shift: event.shiftKey, alt: event.altKey, meta: event.metaKey }));
            });
          }, true);
        })();
        """;

    internal static MenuKeyReport? Parse(string? message, string token)
    {
        if (string.IsNullOrEmpty(message) || message.Length > 1024) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("runicMenuKey", out var marker)
                || marker.ValueKind != System.Text.Json.JsonValueKind.String
                || !string.Equals(marker.GetString(), token, StringComparison.Ordinal)
                || !root.TryGetProperty("keyCode", out var keyCode) || keyCode.ValueKind != System.Text.Json.JsonValueKind.Number
                || !keyCode.TryGetInt32(out var code)
                || !root.TryGetProperty("key", out var key) || key.ValueKind != System.Text.Json.JsonValueKind.String)
                return null;
            return new MenuKeyReport(code, key.GetString()!, Flag(root, "ctrl"), Flag(root, "shift"), Flag(root, "alt"), Flag(root, "meta"));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        static bool Flag(System.Text.Json.JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;
    }
}
