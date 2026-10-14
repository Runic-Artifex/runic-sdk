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
