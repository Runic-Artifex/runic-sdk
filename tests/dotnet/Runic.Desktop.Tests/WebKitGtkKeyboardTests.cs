using TUnit.Core;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// The pure parts of the WebKitGTK keyboard behaviour. The native key and focus
// paths are covered by tests/native/Runic.Desktop.WebViewSmoke/keyboard-wayland.sh.
public sealed class WebKitGtkKeyboardTests
{
    private const uint Shift = 1 << 0;
    private const uint CapsLock = 1 << 1;
    private const uint Control = 1 << 2;
    private const uint Alt = 1 << 3;
    private const uint NumLock = 1 << 4;
    private const uint Super = 1 << 26;
    private const uint Meta = 1 << 28;

    [Test]
    [Arguments(0x03du, Control, "In")] // equal
    [Arguments(0x02bu, Control | Shift, "In")] // plus, Shift on US layouts
    [Arguments(0x02bu, Control, "In")] // plus key of German layouts
    [Arguments(0xffabu, Control | NumLock, "In")] // keypad +
    [Arguments(0x02du, Control, "Out")] // minus
    [Arguments(0x05fu, Control | Shift, "Out")] // underscore
    [Arguments(0xffadu, Control, "Out")] // keypad -
    [Arguments(0x030u, Control | CapsLock, "Reset")] // 0
    [Arguments(0xffb0u, Control | NumLock, "Reset")] // keypad 0
    [Arguments(0x03du, 0u, "None")]
    [Arguments(0x03du, Shift, "None")]
    [Arguments(0x03du, Control | Alt, "None")]
    [Arguments(0x03du, Control | Super, "None")]
    [Arguments(0x030u, Control | Meta, "None")]
    [Arguments(0x061u, Control, "None")] // Control+A selects all
    [Arguments(0xff09u, Control, "None")] // Control+Tab
    public void MapsBrowserZoomKeys(uint keyval, uint state, string expected) =>
        Assert.Equal(Enum.Parse<PageZoomCommand>(expected), WebKitGtkKeyboard.GetZoomCommand(keyval, state));

    [Test]
    public void ZoomStepsMatchBrowsers()
    {
        var level = WebKitGtkKeyboard.DefaultZoom;
        List<double> zoomIn = [];
        for (var step = 0; step < 10; step++)
        {
            level = WebKitGtkKeyboard.Apply(PageZoomCommand.In, level);
            zoomIn.Add(level);
        }
        Assert.Equal([1.1, 1.2, 1.33, 1.5, 1.7, 2.0, 2.4, 3.0, 3.0, 3.0], zoomIn);

        level = WebKitGtkKeyboard.DefaultZoom;
        List<double> zoomOut = [];
        for (var step = 0; step < 7; step++)
        {
            level = WebKitGtkKeyboard.Apply(PageZoomCommand.Out, level);
            zoomOut.Add(level);
        }
        Assert.Equal([0.9, 0.8, 0.67, 0.5, 0.3, 0.3, 0.3], zoomOut);
        Assert.Equal(0.3, WebKitGtkKeyboard.MinimumZoom);
        Assert.Equal(3.0, WebKitGtkKeyboard.MaximumZoom);
    }

    [Test]
    [Arguments(1.25, 1.33, 1.2)] // a level set elsewhere moves to the neighbouring steps
    [Arguments(1.1001, 1.2, 1.0)] // within rounding of a step counts as that step
    [Arguments(0.1, 0.3, 0.3)]
    [Arguments(5.0, 3.0, 3.0)]
    public void ZoomFromAnyLevel(double current, double zoomIn, double zoomOut)
    {
        Assert.Equal(zoomIn, WebKitGtkKeyboard.ZoomIn(current));
        Assert.Equal(zoomOut, WebKitGtkKeyboard.ZoomOut(current));
    }

    [Test]
    public void ResetAndNoneKeepBrowserSemantics()
    {
        Assert.Equal(1.0, WebKitGtkKeyboard.Apply(PageZoomCommand.Reset, 2.4));
        Assert.Equal(2.4, WebKitGtkKeyboard.Apply(PageZoomCommand.None, 2.4));
    }

    [Test]
    [Arguments(1, "In")]
    [Arguments(-1, "Out")]
    [Arguments(0, "None")]
    [Arguments(2, "None")]
    public void MapsWheelMessages(int message, string expected) =>
        Assert.Equal(Enum.Parse<PageZoomCommand>(expected), WebKitGtkKeyboard.GetWheelZoomCommand(message));

    [Test]
    public void ScriptPostsToTheRegisteredHandler()
    {
        Assert.Contains("messageHandlers?." + WebKitGtkKeyboard.ZoomMessageHandler + "?.postMessage", WebKitGtkKeyboard.Script, StringComparison.Ordinal);
        // The wrap acts only on a Tab the page did not cancel, and the wheel only on a wheel the page did not cancel.
        Assert.Contains("pending.defaultPrevented", WebKitGtkKeyboard.Script, StringComparison.Ordinal);
        Assert.Contains("event.defaultPrevented", WebKitGtkKeyboard.Script, StringComparison.Ordinal);
    }
}
