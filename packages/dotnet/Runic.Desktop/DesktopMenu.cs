using System.Text;

namespace Runic.Desktop;

/// <summary>An entry of a <see cref="DesktopMenu"/>: an item, a separator or a submenu.</summary>
public abstract record DesktopMenuEntry
{
    private protected DesktopMenuEntry()
    {
    }
}

/// <summary>A menu of the application's menu bar, or a submenu of one.</summary>
/// <param name="Title">The visible title, in the user's language.</param>
/// <param name="Items">The items, separators and submenus, in order.</param>
/// <remarks>
/// <para>
/// Declare the menu bar in <see cref="DesktopHostOptions.MenuBar"/>, or replace it with
/// <see cref="DesktopHost.SetMenuBar"/>. On macOS it becomes part of the application's menu bar, beside the standard
/// application, File, Edit, View and Window menus Runic Desktop provides; on Windows and Linux the embedded window
/// has no native menu bar in this release, so render one in the page, bound to the same ViewModel commands.
/// </para>
/// <para>Titles are user-visible text: take them from the application's translations.</para>
/// </remarks>
public sealed record DesktopMenu(string Title, IReadOnlyList<DesktopMenuEntry> Items) : DesktopMenuEntry
{
    /// <summary>Gets where the menu goes in the menu bar; ignored for a submenu.</summary>
    public DesktopMenuRole Role { get; init; }
}

/// <summary>A menu item that runs a command of the key window, or of the application.</summary>
/// <param name="Title">The visible title, in the user's language.</param>
/// <param name="CommandId">
/// The command the item runs: the key window's <see cref="DesktopWindow.MenuCommands"/> handles it, or else
/// <see cref="DesktopHost.MenuCommands"/>. The item is disabled while neither can run it.
/// </param>
public sealed record DesktopMenuBarItem(string Title, string CommandId) : DesktopMenuEntry
{
    /// <summary>Gets the item's keyboard shortcut, or <see langword="null"/> for none.</summary>
    /// <remarks>
    /// On macOS it is the item's key equivalent. A shortcut of the standard Edit, Window or application menus, such as
    /// ⌘C or ⌘Q, stays theirs: the item is shown without it and <see cref="DesktopHost.Validate"/> reports
    /// <c>menu-shortcut-reserved</c>. The focused page sees a key first and can keep it with <c>preventDefault()</c>.
    /// </remarks>
    public DesktopShortcut? Shortcut { get; init; }
}

/// <summary>A separator line between menu items.</summary>
public sealed record DesktopMenuBarSeparator : DesktopMenuEntry;

/// <summary>Where a <see cref="DesktopMenu"/> goes in the menu bar.</summary>
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

/// <summary>Runs the commands of menu items for a window or for the application.</summary>
/// <remarks>
/// Runic Desktop calls both members on the platform's UI thread, while it shows a menu or matches a shortcut: keep
/// <see cref="CanExecute"/> cheap, for example by answering from state the command updates, and let
/// <see cref="Execute"/> hand the work to the application's own thread. Runic Windows get one for their root ViewModel
/// through <c>IRunicMenuCommandSource</c>.
/// </remarks>
public interface IDesktopMenuCommandTarget
{
    /// <summary>Gets whether the command can run now; <see langword="false"/> also for a command this target does not handle.</summary>
    /// <param name="commandId">The <see cref="DesktopMenuBarItem.CommandId"/>.</param>
    bool CanExecute(string commandId);

    /// <summary>Runs the command, after <see cref="CanExecute"/> returned <see langword="true"/>.</summary>
    /// <param name="commandId">The <see cref="DesktopMenuBarItem.CommandId"/>.</param>
    void Execute(string commandId);
}
