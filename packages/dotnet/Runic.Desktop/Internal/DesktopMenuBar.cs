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

    // Throws for a menu bar that cannot be built; reports what the bar ignores.
    internal static List<DesktopDiagnostic> Check(IReadOnlyList<DesktopMenu>? menus, bool macOS)
    {
        List<DesktopDiagnostic> diagnostics = [];
        if (menus is null || menus.Count == 0) return diagnostics;
        foreach (var role in new[] { DesktopMenuRole.Application, DesktopMenuRole.File, DesktopMenuRole.Help })
        {
            if (menus.Count(menu => menu?.Role == role) > 1)
                throw new ArgumentException($"The menu bar has more than one {role} menu.", nameof(menus));
        }
        var seen = new Dictionary<DesktopShortcut, string>();
        foreach (var menu in menus)
        {
            if (menu is null) throw new ArgumentException("The menu bar contains a null menu.", nameof(menus));
            CheckMenu(menu, menu.Title, seen, diagnostics);
        }
        if (!macOS)
        {
            diagnostics.Add(new DesktopDiagnostic(
                DesktopErrorCategory.Unavailable,
                UnsupportedCode,
                "The menu bar is shown only on macOS; Windows and Linux windows have no native menu bar in this release.",
                Retryable: false,
                Remediation: "Render the menu bar in the page, bound to the same ViewModel commands.")
            {
                Severity = DesktopDiagnosticSeverity.Warning,
                Option = "DesktopHostOptions.MenuBar",
            });
        }
        return diagnostics;
    }

    private static void CheckMenu(DesktopMenu menu, string path, Dictionary<DesktopShortcut, string> seen, List<DesktopDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(menu.Title)) throw new ArgumentException("A menu has no title.");
        if (menu.Items is null) throw new ArgumentException($"The menu '{path}' has no item list.");
        foreach (var entry in menu.Items)
        {
            switch (entry)
            {
                case DesktopMenu submenu:
                    CheckMenu(submenu, path + " ▸ " + submenu.Title, seen, diagnostics);
                    break;
                case DesktopMenuBarItem item:
                    if (string.IsNullOrWhiteSpace(item.Title)) throw new ArgumentException($"An item of '{path}' has no title.");
                    if (string.IsNullOrWhiteSpace(item.CommandId)) throw new ArgumentException($"The item '{path} ▸ {item.Title}' has no command.");
                    if (item.Shortcut is { Key: not null } shortcut)
                    {
                        var itemPath = path + " ▸ " + item.Title;
                        if (Reserved.FirstOrDefault(reserved => reserved.Shortcut == shortcut) is { Item: { } standard })
                        {
                            diagnostics.Add(Warning(ReservedCode,
                                $"{itemPath} uses {shortcut}, the shortcut of {standard} on macOS; the item is shown without it.",
                                "Choose another shortcut."));
                        }
                        else if (seen.TryGetValue(shortcut, out var other))
                        {
                            diagnostics.Add(Warning(DuplicateCode, $"{itemPath} and {other} both use {shortcut}; only the first answers it.",
                                "Give each item its own shortcut."));
                        }
                        else
                        {
                            seen[shortcut] = itemPath;
                        }
                    }
                    break;
                case DesktopMenuBarSeparator:
                    break;
                default:
                    throw new ArgumentException($"The menu '{path}' contains a null entry.");
            }
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
