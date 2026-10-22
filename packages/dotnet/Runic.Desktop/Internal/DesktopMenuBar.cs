using Runic.Platform;

namespace Runic.Desktop.Internal;

// Checks a declared menu bar and maps it to AppKit. Pure, so the rules are tested on every platform.
internal static class DesktopMenuBar
{
    internal const string ReservedCode = "menu-shortcut-reserved";
    internal const string DuplicateCode = "menu-shortcut-duplicate";
    internal const string UnsupportedCode = "menu-bar-unsupported";

    // The key equivalents of the standard menus Runic Desktop provides on macOS, with their items.
    internal static readonly (DesktopShortcut Shortcut, string Item)[] Reserved =
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

    internal const string RadioCheckedCode = "menu-radio-checked";
    internal const string PageNativeCode = "menu-bar-page-native";

    // Throws for a menu bar that cannot be built; reports what the bar ignores. The overload of the pure tests: a
    // Windows window without a native bar, or a macOS one.
    internal static List<DesktopDiagnostic> Check(IReadOnlyList<DesktopMenu>? menus, bool macOS) =>
        Check(menus, macOS ? MenuPlatform.MacOS : MenuPlatform.Windows, DesktopMenuBarPresentation.Native, nativeBar: macOS);

    // nativeBar: whether the window's presentation can show the bar natively (DesktopWindowCapabilities.MenuBar).
    internal static List<DesktopDiagnostic> Check(IReadOnlyList<DesktopMenu>? menus, MenuPlatform platform,
        DesktopMenuBarPresentation presentation, bool nativeBar)
    {
        List<DesktopDiagnostic> diagnostics = [];
        if (menus is null || menus.Count == 0) return diagnostics;
        foreach (var role in new[] { DesktopMenuRole.Application, DesktopMenuRole.File, DesktopMenuRole.Help })
        {
            if (menus.Count(menu => menu?.Role == role) > 1)
                throw new ArgumentException($"The menu bar has more than one {role} menu.", nameof(menus));
        }
        var unsupported = platform != MenuPlatform.MacOS && presentation == DesktopMenuBarPresentation.Native && !nativeBar;
        var seen = new Dictionary<DesktopShortcut, string>();
        foreach (var menu in menus)
        {
            if (menu is null) throw new ArgumentException("The menu bar contains a null menu.", nameof(menus));
            CheckMenu(menu, "", seen, diagnostics, platform, reportReserved: !unsupported);
        }
        if (unsupported)
        {
            diagnostics.Add(new DesktopDiagnostic(
                DesktopErrorCategory.Unavailable,
                UnsupportedCode,
                "This window shows no native menu bar: its presentation, an installed browser or a window host without a menu bar, has none.",
                Retryable: false,
                Remediation: "Set DesktopWindowOptions.MenuBarPresentation to Page and render DesktopHost.MenuBar in the page, bound to the same commands, or to None.")
            {
                Severity = DesktopDiagnosticSeverity.Warning,
                Option = "DesktopHostOptions.MenuBar",
            });
        }
        if (platform == MenuPlatform.MacOS && presentation != DesktopMenuBarPresentation.Native)
        {
            diagnostics.Add(new DesktopDiagnostic(
                DesktopErrorCategory.Unavailable,
                PageNativeCode,
                $"The window's menu bar presentation is {presentation}, but on macOS the declared menus stay in the application menu bar, which every window shares; the window's placement is Global.",
                Retryable: false,
                Remediation: "Render no menu bar in the page while DesktopWindow.MenuBarPlacement is Global.")
            {
                Severity = DesktopDiagnosticSeverity.Warning,
                Option = "DesktopWindowOptions.MenuBarPresentation",
            });
        }
        return diagnostics;
    }

    private static void CheckMenu(DesktopMenu menu, string path, Dictionary<DesktopShortcut, string> seen, List<DesktopDiagnostic> diagnostics,
        MenuPlatform platform, bool reportReserved)
    {
        DesktopMenuRules.Title(menu.Title, path.Length == 0 ? "A menu" : $"A submenu of '{path}'");
        path = path.Length == 0 ? menu.Title : path + " ▸ " + menu.Title;
        DesktopMenuRules.AccessKey(menu.AccessKey, $"the menu '{path}'");
        if (menu.Items.IsDefault) throw new ArgumentException($"The menu '{path}' has no item list.");
        foreach (var entry in menu.Items)
        {
            switch (entry)
            {
                case null:
                    throw new ArgumentException($"The menu '{path}' contains a null entry.");
                case DesktopMenu submenu:
                    CheckMenu(submenu, path, seen, diagnostics, platform, reportReserved);
                    break;
                case DesktopMenuItem item:
                    DesktopMenuRules.Title(item.Title, $"An item of '{path}'");
                    var itemPath = path + " ▸ " + item.Title;
                    DesktopMenuRules.Item(item, $"'{itemPath}'");
                    if (item.Shortcut is { Key.Length: > 0 } declared)
                    {
                        var shortcut = DesktopMenuPlanner.Normalize(declared, platform);
                        if (DesktopMenuPlanner.ReservedBy(declared, platform) is { } standard)
                        {
                            if (reportReserved)
                            {
                                diagnostics.Add(Warning(ReservedCode, platform == MenuPlatform.MacOS
                                        ? $"{itemPath} uses {declared}, the shortcut of {standard} on macOS; the item is shown without it."
                                        : $"{itemPath} uses {declared}, which {(platform == MenuPlatform.Windows ? "Windows" : "Linux")} keeps for {standard}; the item is shown without it.",
                                    "Choose another shortcut."));
                            }
                        }
                        else if (seen.TryGetValue(shortcut, out var other))
                        {
                            diagnostics.Add(Warning(DuplicateCode, $"{itemPath} and {other} both use {declared}; only the first answers it.",
                                "Give each item its own shortcut."));
                        }
                        else
                        {
                            seen[shortcut] = itemPath;
                        }
                    }
                    break;
                default:
                    // A separator, or an entry kind of a later release that the bar does not show.
                    break;
            }
        }
        foreach (var group in DesktopMenuRules.RadioGroupsWithSeveralChecked(menu.Items))
        {
            diagnostics.Add(Warning(RadioCheckedCode, $"More than one item of the radio group '{group}' in '{path}' is checked.",
                "Check one item of the group."));
        }
    }

    internal static bool IsReserved(DesktopShortcut shortcut) => Reserved.Any(reserved => reserved.Shortcut == shortcut);

    private static DesktopDiagnostic Warning(string code, string message, string remediation) => new(
        DesktopErrorCategory.InvalidArgument, code, message, Retryable: false, Remediation: remediation)
    {
        Severity = DesktopDiagnosticSeverity.Warning,
        Option = "DesktopHostOptions.MenuBar",
    };

    internal const ulong Command = 1UL << 20, Option = 1UL << 19, Control = 1UL << 18, Shift = 1UL << 17;

    // The NSMenuItem key equivalent and modifier mask of a shortcut. Letters are lower-case with an explicit Shift.
    internal static (string Key, ulong Modifiers) ToMacKeyEquivalent(DesktopShortcut shortcut)
    {
        ulong modifiers = 0;
        if ((shortcut.Modifiers & DesktopShortcutModifiers.Primary) != 0) modifiers |= Command;
        if ((shortcut.Modifiers & DesktopShortcutModifiers.Shift) != 0) modifiers |= Shift;
        if ((shortcut.Modifiers & DesktopShortcutModifiers.Alt) != 0) modifiers |= Option;
        if ((shortcut.Modifiers & DesktopShortcutModifiers.Control) != 0) modifiers |= Control;
        var key = shortcut.Key switch
        {
            "Enter" => "\r",
            "Escape" => "\u001b",
            "Tab" => "\t",
            "Space" => " ",
            "Backspace" => "\u0008",
            "Delete" => "",
            "ArrowUp" => "",
            "ArrowDown" => "",
            "ArrowLeft" => "",
            "ArrowRight" => "",
            "Home" => "",
            "End" => "",
            "PageUp" => "",
            "PageDown" => "",
            "Plus" => "+",
            ['F', .. var number] when number.Length > 0 && char.IsAsciiDigit(number[0]) =>
                ((char)(0xF704 + int.Parse(number, System.Globalization.CultureInfo.InvariantCulture) - 1)).ToString(),
            [var character] when char.IsAsciiLetter(character) => char.ToLowerInvariant(character).ToString(),
            var other => other,
        };
        return (key, modifiers);
    }

    // Where the declared menus go: File right after the application menu, others before the Window menu, Help last.
    internal static int InsertionIndex(DesktopMenuRole role, int count, int windowMenuIndex) => role switch
    {
        DesktopMenuRole.File => Math.Min(1, count),
        DesktopMenuRole.Help => count,
        _ => windowMenuIndex >= 0 ? windowMenuIndex : count,
    };
}
