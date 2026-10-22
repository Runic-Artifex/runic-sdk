using System.Text.Json;
using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Desktop.Tests;

public sealed class DesktopShortcutTests
{
    private static JsonElement Cases() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "shortcuts.json"))).RootElement;

    // The same cases as matchesShortcut and formatShortcut in @runic-artifex/views.
    [Test]
    public void SharedCasesParseAndDisplayAsInThePage()
    {
        foreach (var item in Cases().GetProperty("parse").EnumerateArray())
        {
            var text = item.GetProperty("text").GetString()!;
            var shortcut = DesktopShortcut.Parse(text);
            Assert.Equal(item.GetProperty("canonical").GetString(), shortcut.ToString());
            Assert.Equal(item.GetProperty("apple").GetString(), shortcut.ToDisplayString(macOS: true));
            Assert.Equal(item.GetProperty("other").GetString(), shortcut.ToDisplayString(macOS: false));
            Assert.Equal(shortcut, DesktopShortcut.Parse(shortcut.ToString()));
        }
        foreach (var item in Cases().GetProperty("invalid").EnumerateArray())
        {
            Assert.False(DesktopShortcut.TryParse(item.GetString(), out _), item.GetString());
        }
    }

    [Test]
    public void KeysAreNormalizedAndCheckedOnConstruction()
    {
        Assert.Equal("S", new DesktopShortcut("s").Key);
        Assert.Equal("ArrowUp", new DesktopShortcut("arrowup").Key);
        Assert.Equal("Plus", new DesktopShortcut("+").Key);
        Assert.Equal(DesktopShortcutModifiers.Primary, new DesktopShortcut("S").Modifiers);
        Assert.Throws<ArgumentException>(() => new DesktopShortcut("Hyper"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopShortcut("S", (DesktopShortcutModifiers)64));
        // The default value is no shortcut: an empty key that formats as empty text.
        Assert.Equal(string.Empty, default(DesktopShortcut).Key);
        Assert.Equal(string.Empty, default(DesktopShortcut).ToDisplayString(macOS: false));
        Assert.Equal("", default(DesktopShortcut).ToString());
    }

    [Test]
    public void MacKeyEquivalentsUseAppKitKeysAndModifierFlags()
    {
        Assert.Equal(("s", DesktopMenuBar.Command), DesktopMenuBar.ToMacKeyEquivalent(DesktopShortcut.Parse("Primary+S")));
        Assert.Equal(("s", DesktopMenuBar.Command | DesktopMenuBar.Shift), DesktopMenuBar.ToMacKeyEquivalent(DesktopShortcut.Parse("Primary+Shift+S")));
        Assert.Equal(("\t", DesktopMenuBar.Control), DesktopMenuBar.ToMacKeyEquivalent(DesktopShortcut.Parse("Control+Tab")));
        Assert.Equal(("i", DesktopMenuBar.Command | DesktopMenuBar.Option), DesktopMenuBar.ToMacKeyEquivalent(DesktopShortcut.Parse("Primary+Alt+I")));
        Assert.Equal(("", DesktopMenuBar.Command), DesktopMenuBar.ToMacKeyEquivalent(DesktopShortcut.Parse("Primary+ArrowUp")));
        Assert.Equal(("", 0UL), DesktopMenuBar.ToMacKeyEquivalent(DesktopShortcut.Parse("F5")));
        Assert.Equal(("", 0UL), DesktopMenuBar.ToMacKeyEquivalent(DesktopShortcut.Parse("F24")));
        Assert.Equal((",", DesktopMenuBar.Command), DesktopMenuBar.ToMacKeyEquivalent(DesktopShortcut.Parse("Primary+,")));
        Assert.Equal(("+", DesktopMenuBar.Command), DesktopMenuBar.ToMacKeyEquivalent(DesktopShortcut.Parse("Primary+Plus")));
        Assert.Equal(("\r", DesktopMenuBar.Command), DesktopMenuBar.ToMacKeyEquivalent(DesktopShortcut.Parse("Primary+Enter")));
    }
}

public sealed class DesktopMenuBarTests
{
    private static DesktopMenuItem Item(string title, string id, string? shortcut = null) =>
        new(title, id) { Shortcut = shortcut is null ? null : DesktopShortcut.Parse(shortcut) };

    [Test]
    public void ShortcutsOfTheStandardMenusAreReservedAndDuplicatesReported()
    {
        IReadOnlyList<DesktopMenu> bar =
        [
            new("File", [Item("Save", "file.save", "Primary+S"), Item("Close Tab", "tab.close", "Primary+W")]) { Role = DesktopMenuRole.File },
            new("Edit Extras", [Item("Copy Special", "copy.special", "Primary+C"), Item("Paste Plain", "paste.plain", "Primary+Shift+Alt+V")]),
            new("Tools", [Item("Store", "tools.store", "Primary+S"), new DesktopMenu("More", [Item("Redo Twice", "redo", "Primary+Shift+Z")])]),
        ];
        var diagnostics = DesktopMenuBar.Check(bar, macOS: true);
        Assert.Equal(["File ▸ Close Tab", "Edit Extras ▸ Copy Special", "Tools ▸ More ▸ Redo Twice"],
            diagnostics.Where(static item => item.Code == DesktopMenuBar.ReservedCode).Select(static item => item.Message.Split(" uses ")[0]));
        var duplicate = Assert.Single(diagnostics, static item => item.Code == DesktopMenuBar.DuplicateCode);
        Assert.Contains("Tools ▸ Store and File ▸ Save", duplicate.Message);
        Assert.All(diagnostics, static item => Assert.Equal(DesktopDiagnosticSeverity.Warning, item.Severity));
        Assert.DoesNotContain(diagnostics, static item => item.Code == DesktopMenuBar.UnsupportedCode);
        Assert.Contains(DesktopMenuBar.Check(bar, macOS: false), static item => item.Code == DesktopMenuBar.UnsupportedCode);
        Assert.Empty(DesktopMenuBar.Check([], macOS: false));
        Assert.True(DesktopMenuBar.IsReserved(DesktopShortcut.Parse("Primary+Q")));
        Assert.False(DesktopMenuBar.IsReserved(DesktopShortcut.Parse("Primary+Shift+Q")));
    }

    [Test]
    public void IncompleteMenusAreRejected()
    {
        Assert.Throws<ArgumentException>(() => DesktopMenuBar.Check([new("", [])], macOS: true));
        Assert.Throws<ArgumentException>(() => DesktopMenuBar.Check([new("Tools", [Item("", "x")])], macOS: true));
        Assert.Throws<ArgumentException>(() => DesktopMenuBar.Check([new("Tools", [Item("Run", " ")])], macOS: true));
        Assert.Throws<ArgumentException>(() => DesktopMenuBar.Check([new("Tools", [null!])], macOS: true));
        Assert.Throws<ArgumentException>(() => DesktopMenuBar.Check(
            [new("Help", []) { Role = DesktopMenuRole.Help }, new("More Help", []) { Role = DesktopMenuRole.Help }], macOS: true));
    }

    // The same vectors as the status icon menu (Runic.Platform.Runtime.Tests, StatusIconMenuRuleTests).
    private static JsonElement Rules() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "menu-entries.json"))).RootElement;

    private static IEnumerable<string> Values(JsonElement rules, string group, string kind) =>
        rules.GetProperty(group).GetProperty(kind).EnumerateArray().Select(static value => value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Concat(Enumerable.Repeat(value.GetProperty("repeat").GetString(), value.GetProperty("count").GetInt32())));

    [Test]
    public void SharedTitleAndCommandIdRulesApplyToTheMenuBar()
    {
        var rules = Rules();
        foreach (var title in Values(rules, "titles", "valid"))
        {
            DesktopMenuBar.Check([new(title, [Item(title, "id")])], macOS: true);
            DesktopMenuBar.Check([new("Tools", [new DesktopMenu(title, [])])], macOS: true);
        }
        foreach (var title in Values(rules, "titles", "invalid"))
        {
            Assert.Throws<ArgumentException>(() => DesktopMenuBar.Check([new(title, [])], macOS: true));
            Assert.Throws<ArgumentException>(() => DesktopMenuBar.Check([new("Tools", [Item(title, "id")])], macOS: true));
            Assert.Throws<ArgumentException>(() => DesktopMenuBar.Check([new("Tools", [new DesktopMenu(title, [])])], macOS: true));
        }
        foreach (var id in Values(rules, "commandIds", "valid")) DesktopMenuBar.Check([new("Tools", [Item("Run", id)])], macOS: true);
        foreach (var id in Values(rules, "commandIds", "invalid"))
        {
            var error = Assert.Throws<ArgumentException>(() => DesktopMenuBar.Check([new("Tools", [Item("Run", id)])], macOS: true));
            // The message names the id and the rule.
            if (id.Length > 0) Assert.Contains($"'{id}'", error.Message);
            Assert.Contains("ASCII letters, digits, dots, underscores or hyphens", error.Message);
        }
        foreach (var id in Values(rules, "commandIds", "reserved"))
        {
            var error = Assert.Throws<ArgumentException>(() => DesktopMenuBar.Check([new("Tools", [Item("Run", id)])], macOS: true));
            Assert.Contains($"'{id}'", error.Message);
            Assert.Contains("reserved", error.Message);
        }
    }

    [Test]
    public void TheMenuBarAllowsRepeatedCommandIdsButNoDefaultItemList()
    {
        DesktopMenuBar.Check([new("File", [Item("Save", "file.save")]), new("Tools", [Item("Save Again", "file.save")])], macOS: true);
        Assert.Throws<ArgumentException>(() => DesktopMenuBar.Check([new("Tools", default)], macOS: true));
        Assert.Throws<ArgumentException>(() => DesktopMenuBar.Check([new("Tools", [new DesktopMenu("More", default)])], macOS: true));
    }

    [Test]
    public void DeclaredMenusGoAroundTheStandardOnes()
    {
        // App, File, Edit, View, Window.
        Assert.Equal(1, DesktopMenuBar.InsertionIndex(DesktopMenuRole.File, 5, 4));
        Assert.Equal(4, DesktopMenuBar.InsertionIndex(DesktopMenuRole.None, 5, 4));
        Assert.Equal(5, DesktopMenuBar.InsertionIndex(DesktopMenuRole.Help, 5, 4));
        Assert.Equal(5, DesktopMenuBar.InsertionIndex(DesktopMenuRole.None, 5, -1));
    }

    [Test]
    public async Task TheHostValidatesKeepsAndReplacesItsMenuBar()
    {
        IReadOnlyList<DesktopMenu> bar = [new("Tools", [Item("Run", "tools.run", "Primary+C")])];
        await using var host = await DesktopHost.StartAsync(new() { MenuBar = bar });
        Assert.Equal(bar, host.MenuBar);
        // A window without a native bar has no reserved shortcuts to report; one whose page renders the bar does.
        DesktopWindowOptions page = new() { MenuBarPresentation = DesktopMenuBarPresentation.Page };
        Assert.Contains(host.Validate(page).Diagnostics, static item => item.Code == DesktopMenuBar.ReservedCode);
        var changed = 0;
        host.MenuBarChanged += (_, _) => changed++;
        IReadOnlyList<DesktopMenu> translated = [new("Werkzeuge", [Item("Ausführen", "tools.run", "Primary+R")])];
        host.SetMenuBar(translated);
        Assert.Equal(1, changed);
        Assert.Equal("Werkzeuge", host.MenuBar[0].Title);
        Assert.DoesNotContain(host.Validate(page).Diagnostics, static item => item.Code == DesktopMenuBar.ReservedCode);
        Assert.Throws<ArgumentException>(() => host.SetMenuBar([new("", [])]));
        Assert.Equal(1, changed);
        await Assert.ThrowsAsync<ArgumentException>(async () => await DesktopHost.StartAsync(new() { MenuBar = [new("Tools", [Item("Run", "")])] }));
    }

    [Test]
    public async Task WindowsAndTheHostHoldTheirMenuCommandTargets()
    {
        await using var host = await DesktopHost.StartAsync(new());
        var target = new Target();
        host.MenuCommands = target;
        Assert.Same(target, host.MenuCommands);
        host.MenuCommands = null;
        Assert.Null(host.MenuCommands);
    }

    private sealed class Target : IDesktopMenuCommandTarget
    {
        public bool CanExecute(DesktopMenuItem item) => true;
        public void Execute(DesktopMenuItem item) { }
    }
}
