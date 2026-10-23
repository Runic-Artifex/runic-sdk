using System.Collections.Immutable;
using System.Text;

namespace Runic.Platform;

// The rules of a DesktopMessageBox and how each platform orders and labels its buttons. One source: Runic.Platform
// checks boxes with this copy, and Runic.Platform.Runtime compiles the file too for the providers, as it does
// DesktopMenuRules.
internal static class DesktopMessageBoxRules
{
    internal const int MaximumButtons = 6;

    // Throws for a box that cannot be shown.
    internal static void Check(DesktopMessageBox messageBox)
    {
        ArgumentNullException.ThrowIfNull(messageBox);
        if (string.IsNullOrWhiteSpace(messageBox.Message)) throw new ArgumentException("A message box needs a message.", nameof(messageBox));
        Text(messageBox.Message, "message");
        Text(messageBox.Detail, "detail");
        Text(messageBox.Title, "title");
        if (!Enum.IsDefined(messageBox.Kind)) throw new ArgumentException($"The message box kind {(int)messageBox.Kind} is not defined.", nameof(messageBox));
        if (!Enum.IsDefined(messageBox.OwnerPolicy)) throw new ArgumentException("The message box owner policy is not defined.", nameof(messageBox));
        var buttons = messageBox.Buttons;
        if (buttons.IsDefaultOrEmpty) return;
        if (buttons.Length > MaximumButtons) throw new ArgumentException($"A message box has at most {MaximumButtons} buttons.", nameof(messageBox));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        bool hasDefault = false, hasCancel = false;
        foreach (var button in buttons)
        {
            if (button is null) throw new ArgumentException("The message box buttons contain null.", nameof(messageBox));
            if (string.IsNullOrEmpty(button.Id) || button.Id.Contains('\0'))
                throw new ArgumentException("A message box button needs an id without NUL.", nameof(messageBox));
            if (!ids.Add(button.Id)) throw new ArgumentException($"The message box has two buttons with the id '{button.Id}'.", nameof(messageBox));
            if (string.IsNullOrWhiteSpace(button.Label) || button.Label.Contains('\0'))
                throw new ArgumentException($"The message box button '{button.Id}' needs a label without NUL.", nameof(messageBox));
            if (!Enum.IsDefined(button.Role)) throw new ArgumentException($"The role of the message box button '{button.Id}' is not defined.", nameof(messageBox));
            if (button.AccessKey is { } key && (!char.IsLetterOrDigit(key) || char.IsSurrogate(key)))
                throw new ArgumentException($"The access key '{key}' of the message box button '{button.Id}' is not valid: use one letter or digit.", nameof(messageBox));
            if (button.Role == DesktopMessageBoxButtonRole.Default)
            {
                if (hasDefault) throw new ArgumentException("A message box has at most one default button.", nameof(messageBox));
                hasDefault = true;
            }
            if (button.Role == DesktopMessageBoxButtonRole.Cancel)
            {
                if (hasCancel) throw new ArgumentException("A message box has at most one cancel button.", nameof(messageBox));
                hasCancel = true;
            }
        }

        static void Text(string? value, string what)
        {
            if (value is not null && value.Contains('\0')) throw new ArgumentException($"The message box {what} contains NUL.", nameof(messageBox));
        }
    }

    // The buttons in the order the platform shows them, left to right on Windows and GTK; macOS adds them to NSAlert
    // in this order, which shows the first rightmost.
    internal static ImmutableArray<DesktopMessageBoxButton> Ordered(ImmutableArray<DesktopMessageBoxButton> buttons, MessageBoxPlatform platform)
    {
        if (buttons.IsDefaultOrEmpty) return [];
        var defaults = buttons.Where(static button => button.Role == DesktopMessageBoxButtonRole.Default);
        var cancels = buttons.Where(static button => button.Role == DesktopMessageBoxButtonRole.Cancel);
        var others = buttons.Where(static button => button.Role is not DesktopMessageBoxButtonRole.Default and not DesktopMessageBoxButtonRole.Cancel);
        return platform switch
        {
            // Save, Don't Save, Cancel.
            MessageBoxPlatform.Windows => [.. defaults, .. others, .. cancels],
            // Save rightmost, Cancel left of it, Don't Save at the far left.
            MessageBoxPlatform.MacOS => [.. defaults, .. cancels, .. others],
            // Don't Save, Cancel, Save.
            _ => [.. others, .. cancels, .. defaults],
        };
    }

    // The label with the platform's mnemonic marker before the access key and every literal marker doubled: the first
    // letter equal to the key in any case, else the key in parentheses after the label, as menu items place it.
    internal static string Label(DesktopMessageBoxButton button, char marker)
    {
        string title = button.Label;
        int accessKeyIndex = -1;
        if (button.AccessKey is { } key)
        {
            var upper = char.ToUpperInvariant(key);
            accessKeyIndex = title.AsSpan().IndexOfAny(upper, char.ToLowerInvariant(key));
            if (accessKeyIndex < 0)
            {
                accessKeyIndex = title.Length + 2;
                title = title + " (" + upper + ")";
            }
        }
        var text = new StringBuilder(title.Length + 4);
        for (var index = 0; index < title.Length; index++)
        {
            if (index == accessKeyIndex) text.Append(marker);
            if (title[index] == marker) text.Append(marker);
            text.Append(title[index]);
        }
        return text.ToString();
    }
}

// The platform a message box is laid out for.
internal enum MessageBoxPlatform
{
    Windows,
    MacOS,
    Gtk,
}
