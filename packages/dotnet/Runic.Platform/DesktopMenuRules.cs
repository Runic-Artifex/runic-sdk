using System.Collections.Immutable;

namespace Runic.Platform;

// The rules every menu shares, for the Runic Desktop menu bar and status icon menus. One source: Runic.Desktop uses
// this copy through InternalsVisibleTo, and Runic.Platform.Runtime compiles the file too, as it does DesktopUriPolicy.
internal static class DesktopMenuRules
{
    internal const int MaximumTitleLength = 128;
    internal const int MaximumCommandIdLength = 64;
    internal const string ReservedPrefix = "runic.";
    private const string CommandIdRule = "use 1 to 64 ASCII letters, digits, dots, underscores or hyphens.";

    // Throws for a blank, overlong or NUL-containing title.
    internal static void Title(string? title, string what)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException($"{what} has no title.");
        if (title.Length > MaximumTitleLength || title.Contains('\0'))
            throw new ArgumentException($"{what} has a title longer than {MaximumTitleLength} characters or with a NUL character.");
    }

    // Throws for a command id outside the identifier rule or with the reserved prefix, naming the id and the rule.
    internal static void CommandId(string? commandId, string what) => Identifier(commandId, "command id", what);

    // The radio group name follows the command id rule.
    private static void Identifier(string? value, string kind, string what)
    {
        if (string.IsNullOrEmpty(value)) throw new ArgumentException($"{what} has no {kind}: {CommandIdRule}");
        if (value.Length > MaximumCommandIdLength || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new ArgumentException($"The {kind} '{value}' of {what} is not valid: {CommandIdRule}");
        if (value.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The {kind} '{value}' of {what} starts with '{ReservedPrefix}', which is reserved for Runic's standard commands.");
    }

    internal const int MaximumCommandParameterLength = 1024;

    // Throws for an access key that is not one letter or digit.
    internal static void AccessKey(char? accessKey, string what)
    {
        if (accessKey is { } key && (!char.IsLetterOrDigit(key) || char.IsSurrogate(key)))
            throw new ArgumentException($"The access key '{key}' of {what} is not valid: use one letter or digit.");
    }

    // Throws for an item whose command id, parameter, access key or radio group breaks the rules; the title is checked separately.
    internal static void Item(DesktopMenuItem item, string what)
    {
        CommandId(item.CommandId, what);
        if (item.CommandParameter is { } parameter && (parameter.Length > MaximumCommandParameterLength || parameter.Contains('\0')))
            throw new ArgumentException($"The command parameter of {what} is longer than {MaximumCommandParameterLength} characters or has a NUL character.");
        AccessKey(item.AccessKey, what);
        if (item.RadioGroup is { } group)
        {
            Identifier(group, "radio group", what);
            if (item.IsChecked is null)
                throw new ArgumentException($"{what} is in the radio group '{group}' but has no IsChecked state: set it to true or false.");
        }
    }

    internal const int MaximumContextMenuDepth = 4;
    internal const int MaximumContextMenuEntries = 256;

    // Throws for a context menu that cannot be shown: the status icon's limits, the shared entry rules, at least one
    // entry. Command ids may repeat, as in the menu bar.
    internal static void ContextMenu(ImmutableArray<DesktopMenuEntry> items)
    {
        if (items.IsDefaultOrEmpty) throw new ArgumentException("A context menu needs at least one entry.", nameof(items));
        var count = 0;
        ContextEntries(items, 0, ref count);
    }

    private static void ContextEntries(ImmutableArray<DesktopMenuEntry> entries, int depth, ref int count)
    {
        if (entries.IsDefault) throw new ArgumentException("A context submenu has no item list.");
        if (depth > MaximumContextMenuDepth)
            throw new ArgumentException($"Context menus nest at most {MaximumContextMenuDepth} submenus deep.");
        foreach (var entry in entries)
        {
            if (++count > MaximumContextMenuEntries)
                throw new ArgumentException($"A context menu has at most {MaximumContextMenuEntries} entries.");
            switch (entry)
            {
                case null:
                    throw new ArgumentException("A context menu contains a null entry.");
                case DesktopMenuItem item:
                    Title(item.Title, "A context menu item");
                    Item(item, $"the context menu item '{item.Title}'");
                    break;
                case DesktopMenu submenu:
                    Title(submenu.Title, "A context submenu");
                    AccessKey(submenu.AccessKey, $"the context submenu '{submenu.Title}'");
                    ContextEntries(submenu.Items, depth + 1, ref count);
                    break;
                default:
                    // A separator, or an entry kind of a later release that hosts skip.
                    break;
            }
        }
    }

    // The radio groups of one menu level that have more than one checked item.
    internal static IEnumerable<string> RadioGroupsWithSeveralChecked(IEnumerable<DesktopMenuEntry> entries) =>
        entries.OfType<DesktopMenuItem>()
            .Where(static item => item is { RadioGroup: not null, IsChecked: true })
            .GroupBy(static item => item.RadioGroup!, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key);
}
