using TUnit.Core;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// The macOS View menu that gives WKWebView its zoom keys. The native path, a Command key reaching the page and
// then the menu, is covered by tests/native/Runic.Desktop.WebViewSmoke --zoom on the macOS CI runner.
public sealed class MacOsPageZoomTests
{
    [Test]
    public void ZoomKeysUseCommandAsSafariDoes()
    {
        Assert.Equal(1UL << 20, MacOsPageZoom.CommandModifier); // NSEventModifierFlagCommand
        Assert.Equal(["0", "+", "=", "-", "_"], MacOsPageZoom.MenuItems.Select(item => item.KeyEquivalent));
        Assert.Equal(
            [PageZoomCommand.Reset, PageZoomCommand.In, PageZoomCommand.In, PageZoomCommand.Out, PageZoomCommand.Out],
            MacOsPageZoom.MenuItems.Select(item => item.Command));
        // One visible item per command, titled as in Safari; the alternatives answer their keys while hidden.
        Assert.Equal(["Actual Size", "Zoom In", "Zoom Out"], MacOsPageZoom.MenuItems.Where(item => !item.Hidden).Select(item => item.Title));
        Assert.Equal(["=", "_"], MacOsPageZoom.MenuItems.Where(item => item.Hidden).Select(item => item.KeyEquivalent));
    }

    [Test]
    public void EachCommandHasItsOwnAction()
    {
        var selectors = new[] { PageZoomCommand.In, PageZoomCommand.Out, PageZoomCommand.Reset }.Select(MacOsPageZoom.Selector).ToArray();
        Assert.Equal(["runicZoomIn:", "runicZoomOut:", "runicActualSize:"], selectors);
        Assert.Throws<ArgumentOutOfRangeException>(() => MacOsPageZoom.Selector(PageZoomCommand.None));
    }

    [Test]
    public void ZoomStepsMatchLinux()
    {
        foreach (var command in new[] { PageZoomCommand.In, PageZoomCommand.Out, PageZoomCommand.Reset })
            foreach (var level in WebKitGtkKeyboard.ZoomLevels.Append(0.25).Append(3.5).Append(1.05))
                Assert.Equal(WebKitGtkKeyboard.Apply(command, level), MacOsPageZoom.Apply(command, level));
        Assert.Equal(1.1, MacOsPageZoom.Apply(PageZoomCommand.In, 1.0));
        Assert.Equal(3.0, MacOsPageZoom.Apply(PageZoomCommand.In, 3.0));
        Assert.Equal(0.3, MacOsPageZoom.Apply(PageZoomCommand.Out, 0.3));
        Assert.Equal(1.0, MacOsPageZoom.Apply(PageZoomCommand.Reset, 2.4));
    }
}

// The standard menus of the macOS host (#272).
public sealed class MacOsMainMenuTests
{
    private static string Key(MacOsMainMenu.Item item) =>
        ((item.Modifiers & MacOsMainMenu.Shift) != 0 ? "Shift+" : "") + ((item.Modifiers & MacOsMainMenu.Option) != 0 ? "Option+" : "")
        + ((item.Modifiers & MacOsMainMenu.Command) != 0 ? "Command+" : "") + item.KeyEquivalent;

    [Test]
    public void ApplicationMenuHasTheStandardItems()
    {
        Assert.Equal(["About {0}", "", "Hide {0}", "Hide Others", "Show All", "", "Quit {0}"], MacOsMainMenu.Application.Select(item => item.Title));
        var quit = MacOsMainMenu.Application[^1];
        Assert.Equal(("runicQuit:", "Command+q", true), (quit.Action, Key(quit), quit.OwnTarget));
        Assert.Equal("Option+Command+h", Key(MacOsMainMenu.Application[3]));
        Assert.Equal(["orderFrontStandardAboutPanel:", "hide:", "hideOtherApplications:", "unhideAllApplications:"],
            MacOsMainMenu.Application.Where(item => item.Action is not null && !item.OwnTarget).Select(item => item.Action!));
    }

    [Test]
    public void EditAndWindowSendStandardActionsAlongTheResponderChain()
    {
        Assert.Equal(["undo:/Command+z", "redo:/Shift+Command+z", "cut:/Command+x", "copy:/Command+c", "paste:/Command+v", "selectAll:/Command+a"],
            MacOsMainMenu.Edit.Where(item => item.Action is not null).Select(item => $"{item.Action}/{Key(item)}"));
        Assert.Equal(["performMiniaturize:/Command+m", "performZoom:/", "arrangeInFront:/"],
            MacOsMainMenu.Window.Where(item => item.Action is not null).Select(item => $"{item.Action}/{Key(item)}"));
        Assert.DoesNotContain(MacOsMainMenu.Edit.Concat(MacOsMainMenu.Window), item => item.OwnTarget);
        Assert.Contains("copy:", MacOsMainMenu.EditActions);
        Assert.Contains("toggleFullScreen:", MacOsMainMenu.ViewActions);
        Assert.Contains("performMiniaturize:", MacOsMainMenu.WindowActions);
    }

    [Test]
    public void ZoomKeysCountOnlyWithCommandAlone()
    {
        Assert.True(MacOsMainMenu.IsCommandKey("=", MacOsMainMenu.Command, "="));
        Assert.True(MacOsMainMenu.IsCommandKey("=", MacOsMainMenu.Command | (1UL << 21), "=")); // the keypad flag
        Assert.False(MacOsMainMenu.IsCommandKey("=", MacOsMainMenu.Command | MacOsMainMenu.Option, "="));
        Assert.False(MacOsMainMenu.IsCommandKey("=", 1UL << 18, "=")); // Control
        Assert.False(MacOsMainMenu.IsCommandKey("+", MacOsMainMenu.Command, "="));
    }

    [Test]
    public void CompletesOnlyAnEmptyOrMissingMainMenu()
    {
        Assert.True(MacOsMainMenu.Completes(hasMainMenu: false, hasKeyEquivalent: false));
        Assert.True(MacOsMainMenu.Completes(hasMainMenu: true, hasKeyEquivalent: false)); // AppKit's default menu
        Assert.False(MacOsMainMenu.Completes(hasMainMenu: true, hasKeyEquivalent: true)); // the application's own
    }
}
