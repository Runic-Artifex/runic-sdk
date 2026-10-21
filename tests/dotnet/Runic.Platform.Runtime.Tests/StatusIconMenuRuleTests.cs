using System.Text.Json;
using Runic.Platform.Runtime;
using TUnit.Core;

namespace Runic.Platform.Runtime.Tests;

// The menu entry rules of status icon menus, with the same vectors as the Runic Desktop menu bar
// (Runic.Desktop.Tests, DesktopMenuBarTests): one model for both (D031.2).
internal sealed class StatusIconMenuRuleTests
{
    private static readonly DesktopIcon Icon = new() { IconName = "folder" };

    private static JsonElement Rules() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "menu-entries.json"))).RootElement;

    private static IEnumerable<string> Values(JsonElement rules, string group, string kind) =>
        rules.GetProperty(group).GetProperty(kind).EnumerateArray().Select(static value => value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Concat(Enumerable.Repeat(value.GetProperty("repeat").GetString(), value.GetProperty("count").GetInt32())));

    private static void Validate(params DesktopMenuEntry[] menu) =>
        DesktopServiceValidation.StatusIcon(new DesktopStatusIconState(Icon, "Status") { Menu = [.. menu] });

    private static ArgumentException Rejects(Action action, string what)
    {
        try { action(); }
        catch (ArgumentException error) { return error; }
        throw new InvalidOperationException($"accepted {what}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [Test]
    public void SharedTitleAndCommandIdRulesApplyToStatusIconMenus()
    {
        var rules = Rules();
        foreach (var title in Values(rules, "titles", "valid"))
        {
            Validate(new DesktopMenuItem(title, "id"));
            Validate(new DesktopMenu(title, []));
        }
        foreach (var title in Values(rules, "titles", "invalid"))
        {
            Rejects(() => Validate(new DesktopMenuItem(title, "id")), $"the item title '{title}'");
            Rejects(() => Validate(new DesktopMenu(title, [])), $"the submenu title '{title}'");
        }
        foreach (var id in Values(rules, "commandIds", "valid")) Validate(new DesktopMenuItem("Run", id));
        foreach (var id in Values(rules, "commandIds", "invalid"))
        {
            var error = Rejects(() => Validate(new DesktopMenuItem("Run", id)), $"the command id '{id}'");
            Check(id.Length == 0 || error.Message.Contains($"'{id}'", StringComparison.Ordinal), $"the message names '{id}': {error.Message}");
            Check(error.Message.Contains("ASCII letters, digits, dots, underscores or hyphens", StringComparison.Ordinal), $"the message names the rule: {error.Message}");
        }
        foreach (var id in Values(rules, "commandIds", "reserved"))
        {
            var error = Rejects(() => Validate(new DesktopMenuItem("Run", id)), $"the reserved command id '{id}'");
            Check(error.Message.Contains($"'{id}'", StringComparison.Ordinal) && error.Message.Contains("reserved", StringComparison.Ordinal),
                $"the message names '{id}' and the reserved prefix: {error.Message}");
        }
    }

    [Test]
    public void StatusIconMenusKeepTheirOwnLimits()
    {
        var duplicate = Rejects(() => Validate(new DesktopMenuItem("One", "a"), new DesktopMenu("More", [new DesktopMenuItem("Two", "a")])), "a repeated command id");
        Check(duplicate.Message.Contains("'a'", StringComparison.Ordinal), "the duplicate message names the id");
        Rejects(() => Validate(new DesktopMenu("More", default)), "a default item list");
        Rejects(() => Validate(new DesktopMenuItem("One", "a"), null!), "a null entry");
        // Shortcut and Role are ignored by status icons; IsEnabled and IsChecked are their state.
        Validate(new DesktopMenuItem("Save", "save") { Shortcut = DesktopShortcut.Parse("Primary+S"), IsChecked = true, IsEnabled = false },
            new DesktopMenuSeparator(), new DesktopMenu("Help", [new DesktopMenuItem("About", "about")]) { Role = DesktopMenuRole.Help, IsEnabled = false });
    }
}
