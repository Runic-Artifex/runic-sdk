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
    internal static void CommandId(string? commandId, string what)
    {
        if (string.IsNullOrEmpty(commandId)) throw new ArgumentException($"{what} has no command id: {CommandIdRule}");
        if (commandId.Length > MaximumCommandIdLength || commandId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new ArgumentException($"The command id '{commandId}' of {what} is not valid: {CommandIdRule}");
        if (commandId.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The command id '{commandId}' of {what} starts with '{ReservedPrefix}', which is reserved for Runic's standard commands.");
    }
}
