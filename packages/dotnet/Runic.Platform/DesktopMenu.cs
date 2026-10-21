using System.Collections.Immutable;
using System.Text;

namespace Runic.Platform;

/// <summary>An entry of a menu: a <see cref="DesktopMenuItem"/>, a <see cref="DesktopMenuSeparator"/> or a submenu.</summary>
/// <remarks>
/// <para>
/// One model serves the Runic Desktop menu bar (<c>DesktopHostOptions.MenuBar</c>) and status icon menus
/// (<see cref="DesktopStatusIconState.Menu"/>).
/// </para>
/// <para>
/// This set is open: a minor release may add entry kinds, so give a <see langword="switch"/> over entries a default arm.
/// Code that walks a menu skips the entries it does not know.
/// </para>
/// </remarks>
public abstract record DesktopMenuEntry
{
    private protected DesktopMenuEntry()
    {
    }
}

/// <summary>A menu of the menu bar, or a submenu of any menu.</summary>
/// <param name="Title">
/// The visible title, literal text in the user's language: not blank, at most 128 characters. Mnemonic markers such as
/// <c>&amp;</c> or <c>_</c> are shown as written.
/// </param>
/// <param name="Items">The items, separators and submenus, in order.</param>
public sealed record DesktopMenu(string Title, ImmutableArray<DesktopMenuEntry> Items) : DesktopMenuEntry
{
    /// <summary>Gets where the menu goes in the menu bar; ignored for a submenu and in a status icon menu.</summary>
    public DesktopMenuRole Role { get; init; }

    /// <summary>Gets whether the menu can be opened; a disabled menu also disables its entries.</summary>
    public bool IsEnabled { get; init; } = true;
}

/// <summary>A menu item that runs a command.</summary>
/// <param name="Title">
/// The visible title, literal text in the user's language: not blank, at most 128 characters. Mnemonic markers such as
/// <c>&amp;</c> or <c>_</c> are shown as written.
/// </param>
/// <param name="CommandId">
/// The command the item runs: an application-owned routing key, never a command line. It has 1 to 64 ASCII letters,
/// digits, dots, underscores or hyphens; ids starting with <c>runic.</c> are reserved for Runic's standard commands.
/// </param>
/// <remarks>
/// In the menu bar the key window's command target, or the application's, runs the item, and the same command id may
/// appear on more than one item. In a status icon menu choosing the item raises
/// <see cref="IDesktopStatusIcon.Activated"/> with its <see cref="DesktopStatusIconActivation.CommandId"/>, and command
/// ids are unique.
/// </remarks>
public sealed record DesktopMenuItem(string Title, string CommandId) : DesktopMenuEntry
{
    /// <summary>Gets the item's keyboard shortcut, or <see langword="null"/> for none; status icon menus ignore it.</summary>
    /// <remarks>
    /// On the macOS menu bar it is the item's key equivalent. A shortcut of the standard Edit, Window or application
    /// menus, such as ⌘C or ⌘Q, stays theirs: the item is shown without it and <c>DesktopHost.Validate</c> reports
    /// <c>menu-shortcut-reserved</c>. The focused page sees a key first and can keep it with <c>preventDefault()</c>.
    /// </remarks>
    public DesktopShortcut? Shortcut { get; init; }

    /// <summary>Gets whether the item can be chosen.</summary>
    /// <remarks>In the menu bar the item is enabled while this is <see langword="true"/> and its command can run.</remarks>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Gets <see langword="null"/> for a plain item; <see langword="true"/> or <see langword="false"/> for a check item and its state.</summary>
    public bool? IsChecked { get; init; }
}

/// <summary>A line between groups of menu entries.</summary>
public sealed record DesktopMenuSeparator : DesktopMenuEntry;

/// <summary>Where a <see cref="DesktopMenu"/> goes in the menu bar.</summary>
/// <remarks>This set is open: a minor release may add values.</remarks>
public enum DesktopMenuRole
{
    /// <summary>After the standard View menu and before the Window menu, in declaration order.</summary>
    None,

    /// <summary>Its items go into the application menu after About, such as Settings… with Primary+Comma.</summary>
    Application,

    /// <summary>The File menu, right after the application menu. The standard Close item is added at its end.</summary>
    File,

    /// <summary>The last menu, after the Window menu.</summary>
    Help,
}

/// <summary>Modifier keys of a <see cref="DesktopShortcut"/>.</summary>
[Flags]
public enum DesktopShortcutModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>The platform's command modifier: Command (⌘) on macOS, Ctrl elsewhere.</summary>
    Primary = 1 << 0,

    /// <summary>Shift (⇧).</summary>
    Shift = 1 << 1,

    /// <summary>Alt, Option (⌥) on macOS.</summary>
    Alt = 1 << 2,

    /// <summary>The Control key (⌃) on macOS; on Windows and Linux the same Ctrl as <see cref="Primary"/>.</summary>
    Control = 1 << 3,
}

/// <summary>A keyboard shortcut: a key and its modifiers, written as <c>Primary+Shift+S</c>.</summary>
/// <remarks>
/// <c>matchesShortcut</c> and <c>formatShortcut</c> in <c>@runic-artifex/views</c> read the same text with the same
/// rule for the page, so a shortcut declared once works on the menu item and in the frontend. The default value is no
/// shortcut: its <see cref="Key"/> is empty and it formats as empty text; a menu item without a shortcut uses
/// <see langword="null"/>.
/// </remarks>
public readonly record struct DesktopShortcut
{
    private readonly string? _key;

    private static readonly string[] NamedKeys =
    [
        "Enter", "Escape", "Tab", "Space", "Backspace", "Delete", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
        "Home", "End", "PageUp", "PageDown", "Plus",
    ];

    /// <summary>Creates a shortcut, checking and normalizing the key.</summary>
    /// <param name="key">
    /// The key: a letter or digit, one of <c>F1</c> to <c>F24</c>, <c>Enter</c>, <c>Escape</c>, <c>Tab</c>, <c>Space</c>,
    /// <c>Backspace</c>, <c>Delete</c>, <c>ArrowUp</c>, <c>ArrowDown</c>, <c>ArrowLeft</c>, <c>ArrowRight</c>, <c>Home</c>,
    /// <c>End</c>, <c>PageUp</c>, <c>PageDown</c>, <c>Plus</c>, or one punctuation character such as <c>,</c> or <c>/</c>.
    /// Letters are stored upper-case.
    /// </param>
    /// <param name="modifiers">The modifiers; <see cref="DesktopShortcutModifiers.Primary"/> by default.</param>
    /// <exception cref="ArgumentException">The key is not one of the supported keys.</exception>
    public DesktopShortcut(string key, DesktopShortcutModifiers modifiers = DesktopShortcutModifiers.Primary)
    {
        _key = NormalizeKey(key) ?? throw new ArgumentException($"'{key}' is not a supported shortcut key.", nameof(key));
        if ((modifiers & ~(DesktopShortcutModifiers.Primary | DesktopShortcutModifiers.Shift | DesktopShortcutModifiers.Alt | DesktopShortcutModifiers.Control)) != 0)
            throw new ArgumentOutOfRangeException(nameof(modifiers));
        Modifiers = modifiers;
    }

    /// <summary>Gets the normalized key; empty for the default value.</summary>
    public string Key => _key ?? string.Empty;

    /// <summary>Gets the modifiers.</summary>
    public DesktopShortcutModifiers Modifiers { get; }

    /// <summary>Reads a shortcut such as <c>Primary+Shift+S</c>, <c>Alt+F4</c> or <c>Primary+,</c>.</summary>
    /// <exception cref="FormatException">The text is not a shortcut.</exception>
    public static DesktopShortcut Parse(string text) =>
        TryParse(text, out var shortcut) ? shortcut : throw new FormatException($"'{text}' is not a keyboard shortcut.");

    /// <summary>Reads a shortcut, returning whether the text is one.</summary>
    public static bool TryParse(string? text, out DesktopShortcut shortcut)
    {
        shortcut = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('+');
        // "Primary++" ends with an empty part for the plus key.
        if (parts.Length >= 2 && parts[^1].Length == 0 && parts[^2].Length == 0) parts = [.. parts[..^2], "+"];
        var modifiers = DesktopShortcutModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = part.Trim().ToUpperInvariant() switch
            {
                "PRIMARY" => DesktopShortcutModifiers.Primary,
                "SHIFT" => DesktopShortcutModifiers.Shift,
                "ALT" => DesktopShortcutModifiers.Alt,
                "CONTROL" => DesktopShortcutModifiers.Control,
                _ => DesktopShortcutModifiers.None,
            };
            if (modifier == DesktopShortcutModifiers.None || (modifiers & modifier) != 0) return false;
            modifiers |= modifier;
        }
        if (NormalizeKey(parts[^1].Trim()) is not { } key) return false;
        shortcut = new DesktopShortcut(key, modifiers);
        return true;
    }

    /// <summary>Gets the shortcut as text that <see cref="Parse"/> reads, such as <c>Primary+Shift+S</c>.</summary>
    public override string ToString()
    {
        if (_key is null) return "";
        var text = new StringBuilder();
        if ((Modifiers & DesktopShortcutModifiers.Primary) != 0) text.Append("Primary+");
        if ((Modifiers & DesktopShortcutModifiers.Control) != 0) text.Append("Control+");
        if ((Modifiers & DesktopShortcutModifiers.Alt) != 0) text.Append("Alt+");
        if ((Modifiers & DesktopShortcutModifiers.Shift) != 0) text.Append("Shift+");
        return text.Append(Key == "Plus" ? "Plus" : Key).ToString();
    }

    /// <summary>Gets the shortcut as the platform shows it: <c>⇧⌘S</c> on macOS, <c>Ctrl+Shift+S</c> elsewhere.</summary>
    public string ToDisplayString() => ToDisplayString(OperatingSystem.IsMacOS());

    /// <summary>Gets the shortcut as macOS (<c>⇧⌘S</c>) or Windows and Linux (<c>Ctrl+Shift+S</c>) show it.</summary>
    /// <param name="macOS">Whether to use the macOS symbols and order.</param>
    public string ToDisplayString(bool macOS)
    {
        if (_key is null) return "";
        var text = new StringBuilder();
        if (macOS)
        {
            if ((Modifiers & DesktopShortcutModifiers.Control) != 0) text.Append('⌃');
            if ((Modifiers & DesktopShortcutModifiers.Alt) != 0) text.Append('⌥');
            if ((Modifiers & DesktopShortcutModifiers.Shift) != 0) text.Append('⇧');
            if ((Modifiers & DesktopShortcutModifiers.Primary) != 0) text.Append('⌘');
            return text.Append(MacKeyName(Key)).ToString();
        }
        if ((Modifiers & (DesktopShortcutModifiers.Primary | DesktopShortcutModifiers.Control)) != 0) text.Append("Ctrl+");
        if ((Modifiers & DesktopShortcutModifiers.Alt) != 0) text.Append("Alt+");
        if ((Modifiers & DesktopShortcutModifiers.Shift) != 0) text.Append("Shift+");
        return text.Append(KeyName(Key)).ToString();
    }

    private static string MacKeyName(string key) => key switch
    {
        "Enter" => "↩", "Escape" => "⎋", "Tab" => "⇥", "Space" => "Space", "Backspace" => "⌫", "Delete" => "⌦",
        "ArrowUp" => "↑", "ArrowDown" => "↓", "ArrowLeft" => "←", "ArrowRight" => "→", "Home" => "↖", "End" => "↘",
        "PageUp" => "⇞", "PageDown" => "⇟", "Plus" => "+",
        _ => key,
    };

    private static string KeyName(string key) => key switch
    {
        "Escape" => "Esc", "Delete" => "Del", "ArrowUp" => "Up", "ArrowDown" => "Down", "ArrowLeft" => "Left",
        "ArrowRight" => "Right", "PageUp" => "Page Up", "PageDown" => "Page Down", "Plus" => "+",
        _ => key,
    };

    internal static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (key == "+") return "Plus";
        if (key.Length == 1)
        {
            var character = key[0];
            if (char.IsAsciiLetter(character)) return char.ToUpperInvariant(character).ToString();
            if (char.IsAsciiDigit(character)) return key;
            return character is > ' ' and < (char)0x7F ? key : null;
        }
        foreach (var name in NamedKeys)
        {
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) return name;
        }
        if ((key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 24 && key[1] != '0')
            return "F" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }
}
