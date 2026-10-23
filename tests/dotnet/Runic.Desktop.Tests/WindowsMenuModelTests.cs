using System.Collections.Immutable;
using System.Text.Json;
using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;
using Model = Runic.Desktop.Internal.WindowsMenuModel;

namespace Runic.Desktop.Tests;

// B10 PR 2: the Win32 menu bar and context menus of the WebView2 window, as InsertMenuItemW receives them, and the
// shortcut and access-key matching of the keys the page left unhandled. The Windows leg's smoke reads the same values
// back from the native menu (Runic.Desktop.WebViewSmoke --menus).
public sealed class WindowsMenuModelTests
{
    private static DesktopMenuItem Item(string title, string id, string? shortcut = null) =>
        new(title, id) { Shortcut = shortcut is null ? null : DesktopShortcut.Parse(shortcut) };

    private static readonly IReadOnlyList<DesktopMenu> Bar =
    [
        new("Help", [Item("About", "help.about")]) { Role = DesktopMenuRole.Help, AccessKey = 'H' },
        new("Tools & More",
        [
            Item("Run", "tools.run", "Primary+Shift+R") with { AccessKey = 'R' },
            new DesktopMenuSeparator(),
            Item("Word Wrap", "view.wrap") with { IsChecked = true },
            Item("Ruler", "view.ruler") with { IsChecked = false },
            new DesktopMenuItem("Small", "view.zoom") { RadioGroup = "zoom", IsChecked = false, CommandParameter = "0.8" },
            new DesktopMenuItem("Large", "view.zoom") { RadioGroup = "zoom", IsChecked = true, CommandParameter = "1.5" },
            Item("Locked", "tools.run") with { IsEnabled = false },
            Item("Copy Special", "tools.copy", "Primary+C"),
            Item("Next", "tools.next", "F3"),
            Item("Comment", "tools.comment", "Control+/"),
            new DesktopMenu("Disabled More", [Item("Inside", "tools.inside", "Alt+I")]) { IsEnabled = false },
        ]) { AccessKey = 'T' },
        new("Fichier", [Item("Enregistrer", "file.save", "Primary+S") with { AccessKey = 'S' }, Item("Store", "file.store", "Primary+S")])
        {
            Role = DesktopMenuRole.File,
            AccessKey = 'F',
        },
    ];

    [Test]
    public void TheBarIsInWindowsOrderWithAccessKeysShortcutsAndStates()
    {
        var model = Model.ForBar(Bar, new MenuIdAllocator(1, Model.LastId), rightToLeft: false);
        Assert.Equal(["&Fichier", "&Tools && More", "&Help"], model.Entries.Select(static entry => entry.Label));
        Assert.All(model.Entries, static entry => Assert.True(entry.IsSubmenu));
        var tools = model.Entries[1].Children;
        Assert.Equal("&Run\tCtrl+Shift+R", tools[0].Label);
        Assert.Equal(Model.MftSeparator, tools[1].Type);
        // Check items as declared; radio items with MFT_RADIOCHECK; a declared-disabled item grayed.
        Assert.Equal(Model.MfsChecked, tools[2].State);
        Assert.Equal(Model.MfsEnabled, tools[3].State);
        Assert.Equal(Model.MftRadioCheck, tools[4].Type & Model.MftRadioCheck);
        Assert.Equal(Model.MfsEnabled, tools[4].State);
        Assert.Equal(Model.MftRadioCheck | Model.MftString, tools[5].Type);
        Assert.Equal(Model.MfsChecked, tools[5].State);
        Assert.Equal("1.5", tools[5].Item!.CommandParameter);
        Assert.Equal(Model.MfsDisabled, tools[6].State);
        // Ctrl+C stays the focused control's: the item has no shortcut.
        Assert.Equal("Copy Special", tools[7].Label);
        Assert.Equal("Next\tF3", tools[8].Label);
        Assert.Equal("Comment\tCtrl+/", tools[9].Label);
        // A disabled menu is grayed and so are its entries.
        Assert.Equal(Model.MfsDisabled, tools[10].State);
        Assert.Equal(Model.MfsDisabled, Assert.Single(tools[10].Children).State);
        // Every item has an id; menus and separators have none.
        Assert.Equal(13, model.Items.Count);
        Assert.All(model.Items, static pair => Assert.True(pair.Key is > 0 and <= Model.LastId && pair.Value.Id == pair.Key));
        Assert.Equal(0u, tools[1].Id);
        Assert.Equal(0u, model.Entries[0].Id);
        Assert.Equal("Enregistrer", model.Items[model.Entries[0].Children[0].Id].Item!.Title);
    }

    [Test]
    public void ItemsKeepTheirIdsAcrossRebuildsAndChangedTogglesGetNewOnes()
    {
        var ids = new MenuIdAllocator(1, Model.LastId);
        var first = Model.ForBar(Bar, ids, rightToLeft: false);
        var again = Model.ForBar(Bar, ids, rightToLeft: false);
        Assert.Equal(first.Items.Keys.Order(), again.Items.Keys.Order());
        // Ruler becomes a check item that is on: same key kind, same id; a plain item that becomes a check item, a new id.
        IReadOnlyList<DesktopMenu> changed = [.. Bar.Take(1), Bar[1] with { Items = Bar[1].Items.SetItem(0, Item("Run", "tools.run") with { IsChecked = false }) }, Bar[2]];
        var third = Model.ForBar(changed, ids, rightToLeft: false);
        var run = first.Entries[1].Children[0].Id;
        Assert.NotEqual(run, third.Entries[1].Children[0].Id);
        Assert.False(third.Items.ContainsKey(run));
        Assert.Equal(first.Entries[1].Children[3].Id, third.Entries[1].Children[3].Id);
    }

    [Test]
    public void ContextMenusHaveTheirOwnIdsAndNoShortcuts()
    {
        ImmutableArray<DesktopMenuEntry> items =
        [
            new DesktopMenuItem("Rename", "row.rename") { CommandParameter = "row-7", Shortcut = DesktopShortcut.Parse("F2") },
            new DesktopMenuSeparator(),
            new DesktopMenu("More", [new DesktopMenuItem("Delete", "row.delete") { IsEnabled = false }]) { Role = DesktopMenuRole.Help },
            new DesktopMenuItem("Rename", "row.rename") { CommandParameter = "row-8" },
        ];
        var menu = Model.ForContextMenu(items, rightToLeft: false);
        Assert.Equal([1u, 2u, 3u], menu.Items.Keys.Order());
        Assert.Equal("Rename\tF2", menu.Entries[0].Label);
        Assert.Same(items[0], menu.Items[menu.Entries[0].Id].Item);
        Assert.Same(items[3], menu.Items[menu.Entries[3].Id].Item);
        Assert.Equal(Model.MfsDisabled, menu.Entries[2].Children[0].State);
        // The shortcut is a hint.
        Assert.Empty(menu.Shortcuts);
        Assert.Null(menu.Match(0x71, "F2", false, false, false, false));
    }

    [Test]
    public void RightToLeftMenusCascadeAndAlignRightWhileTheBarFollowsItsWindow()
    {
        const uint rightToLeft = Model.MftRightOrder | Model.MftRightJustify;
        var bar = Model.ForBar(Bar, new MenuIdAllocator(1, Model.LastId), rightToLeft: true);
        Assert.All(bar.Entries, static entry => Assert.Equal(0u, entry.Type & rightToLeft));
        Assert.All(bar.Entries.SelectMany(static entry => entry.Children), static entry => Assert.Equal(rightToLeft, entry.Type & rightToLeft));
        Assert.Equal(rightToLeft, bar.Entries[1].Children[10].Children[0].Type & rightToLeft);
        var context = Model.ForContextMenu([new DesktopMenuItem("Cut", "cut"), new DesktopMenuSeparator()], rightToLeft: true);
        Assert.All(context.Entries, static entry => Assert.Equal(rightToLeft, entry.Type & rightToLeft));
        Assert.All(Model.ForBar(Bar, new MenuIdAllocator(1, Model.LastId), rightToLeft: false).Entries.SelectMany(static entry => entry.Children),
            static entry => Assert.Equal(0u, entry.Type & rightToLeft));
    }

    [Test]
    public void ShortcutsMatchTheirKeyAndModifiersAndTheFirstItemAnswers()
    {
        var model = Model.ForBar(Bar, new MenuIdAllocator(1, Model.LastId), rightToLeft: false);
        Assert.Equal("Enregistrer", model.Match('S', "s", control: true, shift: false, alt: false, meta: false)?.Item?.Title);
        Assert.Null(model.Match('S', "S", control: true, shift: true, alt: false, meta: false));
        Assert.Null(model.Match('S', "s", control: false, shift: false, alt: false, meta: false));
        Assert.Null(model.Match('S', "s", control: true, shift: false, alt: false, meta: true));
        Assert.Equal("tools.run", model.Match('R', "R", control: true, shift: true, alt: false, meta: false)?.Item?.CommandId);
        Assert.Equal("tools.next", model.Match(0x72, "F3", control: false, shift: false, alt: false, meta: false)?.Item?.CommandId);
        // Control is Ctrl on Windows, as Primary; "/" is VK_OEM_2 on the US layout.
        Assert.Equal("tools.comment", model.Match(0xBF, "/", control: true, shift: false, alt: false, meta: false)?.Item?.CommandId);
        // Reserved shortcuts stay the focused control's; an item of a disabled menu matches, and runs nothing.
        Assert.Null(model.Match('C', "c", control: true, shift: false, alt: false, meta: false));
        var inside = model.Match('I', "i", control: false, shift: false, alt: true, meta: false);
        Assert.Equal("tools.inside", inside?.Item?.CommandId);
        Assert.False(inside!.Planned.Enabled);
        Assert.True(model.IsShortcut('S', control: true, shift: false, alt: false));
        Assert.False(model.IsShortcut('C', control: true, shift: false, alt: false));
        Assert.False(Model.Empty.IsShortcut('S', control: true, shift: false, alt: false));
    }

    [Test]
    public void AltAndAnAccessKeyOpenOnlyADeclaredTopLevelMenu()
    {
        var model = Model.ForBar(Bar, new MenuIdAllocator(1, Model.LastId), rightToLeft: false);
        Assert.True(model.HasMenuAccessKey("f"));
        Assert.True(model.HasMenuAccessKey("T"));
        Assert.False(model.HasMenuAccessKey("r"));
        Assert.False(model.HasMenuAccessKey("x"));
        Assert.False(model.HasMenuAccessKey("F3"));
        Assert.False(model.HasMenuAccessKey(null));
        Assert.False(Model.ForBar([new("Tools", [])], new MenuIdAllocator(1, Model.LastId), rightToLeft: false).HasMenuAccessKey("t"));
    }

    // The shared vectors (specs/desktop/fixtures/menu-entries.json) through the Win32 labels and items.
    [Test]
    public void SharedVectorsReachTheWin32Menu()
    {
        var rules = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "menu-entries.json"))).RootElement;
        IEnumerable<string> Values(string group, string kind) =>
            rules.GetProperty(group).GetProperty(kind).EnumerateArray().Select(static value => value.ValueKind == JsonValueKind.String
                ? value.GetString()!
                : string.Concat(Enumerable.Repeat(value.GetProperty("repeat").GetString(), value.GetProperty("count").GetInt32())));
        foreach (var title in Values("titles", "valid"))
        {
            DesktopMenuBar.Check([new(title, [Item(title, "id")])], MenuPlatform.Windows, DesktopMenuBarPresentation.Native, nativeBar: true);
            var model = Model.ForBar([new(title, [Item(title, "id")])], new MenuIdAllocator(1, Model.LastId), rightToLeft: false);
            // Literal text: every & doubled, no access key without AccessKey.
            Assert.Equal(title.Replace("&", "&&", StringComparison.Ordinal), model.Entries[0].Label);
            Assert.Equal(title.Replace("&", "&&", StringComparison.Ordinal), model.Entries[0].Children[0].Label);
        }
        foreach (var parameter in Values("commandParameters", "valid"))
        {
            var menu = Model.ForContextMenu([new DesktopMenuItem("Run", "id") { CommandParameter = parameter }], rightToLeft: false);
            Assert.Equal(parameter, Assert.Single(menu.Items).Value.Item!.CommandParameter);
        }
        foreach (var key in Values("accessKeys", "valid"))
        {
            var label = Model.ForContextMenu([new DesktopMenuItem("Save", "id") { AccessKey = key[0] }], rightToLeft: false).Entries[0].Label;
            // The first letter of the title in any case, or the key after the title.
            var index = "Save".IndexOf(char.ToUpperInvariant(key[0]).ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(index >= 0 ? "Save".Insert(index, "&") : $"Save (&{char.ToUpperInvariant(key[0])})", label);
        }
        foreach (var group in Values("radioGroups", "valid"))
        {
            var menu = Model.ForContextMenu([new DesktopMenuItem("Small", "id") { RadioGroup = group, IsChecked = true }], rightToLeft: false);
            Assert.Equal(Model.MftRadioCheck, menu.Entries[0].Type & Model.MftRadioCheck);
            Assert.Equal(Model.MfsChecked, menu.Entries[0].State);
        }
    }

    [Test]
    public void ThePagesKeyReportsCarryTheTokenAndTheKey()
    {
        var script = WindowsMenuKeys.Script("t0k3n");
        Assert.Contains("runicMenuKey: \"t0k3n\"", script);
        Assert.Contains("globalThis.top !== globalThis", script);
        Assert.Contains("event.defaultPrevented", script);
        Assert.Equal(new MenuKeyReport(83, "s", true, false, false, false),
            WindowsMenuKeys.Parse("""{"runicMenuKey":"t0k3n","keyCode":83,"key":"s","ctrl":true,"shift":false,"alt":false,"meta":false}""", "t0k3n"));
        Assert.Equal(new MenuKeyReport(114, "F3", false, false, false, false),
            WindowsMenuKeys.Parse("""{"runicMenuKey":"t0k3n","keyCode":114,"key":"F3"}""", "t0k3n"));
        Assert.Null(WindowsMenuKeys.Parse("""{"runicMenuKey":"other","keyCode":83,"key":"s","ctrl":true}""", "t0k3n"));
        Assert.Null(WindowsMenuKeys.Parse("""{"runicFileDrop":"t0k3n"}""", "t0k3n"));
        Assert.Null(WindowsMenuKeys.Parse("""{"runicMenuKey":"t0k3n","keyCode":"83","key":"s"}""", "t0k3n"));
        Assert.Null(WindowsMenuKeys.Parse("not json", "t0k3n"));
        Assert.Null(WindowsMenuKeys.Parse(null, "t0k3n"));
        Assert.Null(WindowsMenuKeys.Parse(new string(' ', 2000), "t0k3n"));
    }
}
