using System.Collections.Immutable;

namespace Runic.Platform;

// The rules of a global shortcut set, for DesktopGlobalShortcuts.Unavailable and the providers. One source: Runic.Platform
// compiles it, and Runic.Platform.Runtime compiles the file too, as it does DesktopMenuRules.
internal static class DesktopGlobalShortcutRules
{
    internal const int MaximumCount = 32;
    internal const int MaximumIdLength = 64;
    internal const int MaximumDescriptionLength = 256;
    private const string IdRule = "use 1 to 64 ASCII letters, digits, dots, underscores or hyphens.";

    // Throws for a set that is not a valid argument of BindAsync.
    internal static void Validate(ImmutableArray<DesktopGlobalShortcut> shortcuts)
    {
        if (shortcuts.IsDefault) throw new ArgumentException("The global shortcut set is a default array.", nameof(shortcuts));
        if (shortcuts.Length > MaximumCount)
            throw new ArgumentException($"A global shortcut set has at most {MaximumCount} actions.", nameof(shortcuts));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shortcut in shortcuts)
        {
            ArgumentNullException.ThrowIfNull(shortcut, nameof(shortcuts));
            var id = shortcut.Id;
            if (string.IsNullOrEmpty(id) || id.Length > MaximumIdLength
                || id.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
                throw new ArgumentException($"The global shortcut id '{id}' is not valid: {IdRule}", nameof(shortcuts));
            if (!ids.Add(id)) throw new ArgumentException($"The global shortcut id '{id}' is used twice.", nameof(shortcuts));
            if (string.IsNullOrWhiteSpace(shortcut.Description) || shortcut.Description.Length > MaximumDescriptionLength
                || shortcut.Description.Contains('\0'))
                throw new ArgumentException(
                    $"The global shortcut '{id}' needs a description of at most {MaximumDescriptionLength} characters without NUL.",
                    nameof(shortcuts));
        }
    }

    // The requested key, or null for none: the default DesktopShortcut is no key.
    internal static DesktopShortcut? Requested(DesktopGlobalShortcut shortcut) =>
        shortcut.PreferredShortcut is { Key.Length: > 0 } key ? key : null;

    // A binding that Windows or macOS reports for a requested key.
    internal static DesktopGlobalShortcutBinding Binding(string id, DesktopShortcut? shortcut, DesktopGlobalShortcutStatus status, bool macOS) =>
        new(id)
        {
            Shortcut = shortcut,
            TriggerDescription = shortcut?.ToDisplayString(macOS),
            Status = shortcut is null ? DesktopGlobalShortcutStatus.Unbound : status,
        };
}
