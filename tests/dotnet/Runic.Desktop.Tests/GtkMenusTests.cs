using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// B10 PR 3: the rules the GTK 3 and GTK 4 menu renderers share. The GTK smokes check the widgets.
public sealed class GtkMenusTests
{
    private const uint Shift = DesktopMenuPlanner.GdkShiftMask, Control = DesktopMenuPlanner.GdkControlMask, Alt = DesktopMenuPlanner.GdkAltMask;
    // GdkModifierType's Lock (Caps Lock) and Mod2 (Num Lock), which do not change a shortcut.
    private const uint Lock = 1 << 1, NumLock = 1 << 4;

    private static DesktopMenuItem Item(string title, string id, string? shortcut = null) =>
        new(title, id) { Shortcut = shortcut is null ? null : DesktopShortcut.Parse(shortcut) };

    [Test]
    public void TheBarIsInTheWindowForTheNativePresentationOfADecoratedWindow()
    {
        Assert.Equal(DesktopMenuBarPlacement.Window, GtkMenus.Placement(DesktopMenuBarPresentation.Native, frameless: false, kiosk: false));
        Assert.Equal(DesktopMenuBarPlacement.None, GtkMenus.Placement(DesktopMenuBarPresentation.Native, frameless: true, kiosk: false));
        Assert.Equal(DesktopMenuBarPlacement.None, GtkMenus.Placement(DesktopMenuBarPresentation.Native, frameless: false, kiosk: true));
        Assert.Equal(DesktopMenuBarPlacement.Page, GtkMenus.Placement(DesktopMenuBarPresentation.Page, frameless: false, kiosk: false));
        Assert.Equal(DesktopMenuBarPlacement.Page, GtkMenus.Placement(DesktopMenuBarPresentation.Page, frameless: true, kiosk: false));
        Assert.Equal(DesktopMenuBarPlacement.None, GtkMenus.Placement(DesktopMenuBarPresentation.None, frameless: false, kiosk: false));
        Assert.True(GtkMenus.ShowsInWindowBar(DesktopMenuBarPresentation.Native, frameless: false, kiosk: false));
        Assert.False(GtkMenus.ShowsInWindowBar(DesktopMenuBarPresentation.Page, frameless: false, kiosk: false));
    }

    [Test]
    public void OnlyAGroupWithOneCheckedItemBecomesAGtkRadioGroup()
    {
        DesktopMenuItem Radio(string group, bool isChecked) => new("R", "r") { RadioGroup = group, IsChecked = isChecked };
        var plan = DesktopMenuPlanner.PlanEntries(
        [
            Radio("one", false), Radio("one", true),
            Radio("none", false), Radio("none", false),
            Radio("both", true), Radio("both", true),
            new DesktopMenu("Sub", [Radio("nested", true)]),
            new DesktopMenuItem("Check", "c") { IsChecked = true },
        ], MenuPlatform.Linux);
        // A group lives in one menu level: the nested group is the submenu's.
        Assert.Equal(["one"], GtkMenus.SingleCheckedRadioGroups(plan).Order());
        Assert.Equal(["nested"], GtkMenus.SingleCheckedRadioGroups(plan[6].Children));
    }

    [Test]
    public void PagePointsFollowTheZoomLevel()
    {
        Assert.Equal((40, 60), GtkMenus.PagePoint(40, 60, 1));
        Assert.Equal((60, 90), GtkMenus.PagePoint(40, 60, 1.5));
        Assert.Equal((13, 1), GtkMenus.PagePoint(12.5, 0.5, 1));
        Assert.Equal((40, 60), GtkMenus.PagePoint(40, 60, double.NaN));
        Assert.Equal((40, 60), GtkMenus.PagePoint(40, 60, 0));
    }

    [Test]
    public void ShortcutsMatchGdkKeysAfterThePlannerRemovedReservedOnes()
    {
        IReadOnlyList<DesktopMenu> bar =
        [
            new("Edit", [Item("Copy Special", "edit.copy", "Primary+C"), Item("Find", "edit.find", "Control+F"), Item("Zoom In", "view.in", "Primary+Plus")]),
            new("File", [Item("Save", "file.save", "Primary+S"), Item("Save As", "file.saveAs", "Primary+Shift+S"), Item("Reload", "file.reload", "F5")])
            {
                Role = DesktopMenuRole.File,
            },
            new("Tools", [new DesktopMenu("More", [Item("Run", "tools.run", "Alt+R")]) { IsEnabled = false }]),
        ];
        var shortcuts = GtkMenuShortcuts.Create(DesktopMenuPlanner.PlanBar(bar, MenuPlatform.Linux));
        Assert.Equal(6, shortcuts.Count);
        Assert.Equal("file.save", shortcuts.Match('s', Control)?.Item?.CommandId);
        // GDK reports an upper-case keyval with Shift, and Caps Lock and Num Lock do not count.
        Assert.Equal("file.saveAs", shortcuts.Match('S', Control | Shift)?.Item?.CommandId);
        Assert.Equal("file.save", shortcuts.Match('S', Control | Lock | NumLock)?.Item?.CommandId);
        Assert.Null(shortcuts.Match('s', Shift));
        Assert.Null(shortcuts.Match('s', Control | Alt));
        // Ctrl+C stays the page's on Linux; Control and Primary are both Ctrl.
        Assert.Null(shortcuts.Match('c', Control));
        Assert.Equal("edit.find", shortcuts.Match('f', Control)?.Item?.CommandId);
        Assert.Equal("file.reload", shortcuts.Match(0xFFC2, 0)?.Item?.CommandId);
        // + is Shift+= on a US keyboard; the item declares it without Shift.
        Assert.Equal("view.in", shortcuts.Match('+', Control | Shift)?.Item?.CommandId);
        // A non-Latin layout: the event's keyval is Cyrillic, the same physical key is s in the Latin group.
        Assert.Equal("file.save", shortcuts.Match(0x6FB, Control, [0x6FB, 's', 'S'])?.Item?.CommandId);
        // A disabled menu's item is matched, so the key is consumed by nothing; the caller checks Enabled.
        Assert.False(shortcuts.Match('r', Alt)?.Enabled ?? true);
        Assert.Null(GtkMenuShortcuts.Empty.Match('s', Control));
    }

    [Test]
    public void TheFirstDeclaredItemAnswersADuplicateShortcut()
    {
        IReadOnlyList<DesktopMenu> bar =
        [
            new("Tools", [Item("Save Tools", "tools.save", "Primary+S")]),
            new("File", [Item("Save", "file.save", "Primary+S")]) { Role = DesktopMenuRole.File },
        ];
        // The bar shows File first, but Tools was declared first, as DesktopHost.Validate's duplicate warning says.
        Assert.Equal("tools.save", GtkMenuShortcuts.Create(DesktopMenuPlanner.PlanBar(bar, MenuPlatform.Linux)).Match('s', Control)?.Item?.CommandId);
    }

    [Test]
    public void TheBuiltInLinuxHostShowsTheBarSoValidationReportsNoMissingBar()
    {
        Assert.True(LinuxWebKitGtkHost.KnownCapabilities.HasFlag(DesktopWindowCapabilities.MenuBar | DesktopWindowCapabilities.ContextMenu));
        IReadOnlyList<DesktopMenu> bar = [new("Tools", [Item("Run", "tools.run")])];
        Assert.DoesNotContain(DesktopMenuBar.Check(bar, MenuPlatform.Linux, DesktopMenuBarPresentation.Native, nativeBar: true),
            static item => item.Code == DesktopMenuBar.UnsupportedCode);
    }
}
