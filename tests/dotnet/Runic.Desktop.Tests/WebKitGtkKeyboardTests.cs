using TUnit.Core;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// The pure parts of the WebKitGTK keyboard behaviour. The native key and focus
// paths are covered by tests/native/Runic.Desktop.WebViewSmoke/keyboard-driver.sh and keyboard-script.test.mjs.
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

    // Physical keys of real XKB layouts: the produced symbol, its unshifted and shifted
    // levels, and the evdev keycode plus 8. AE10 (19), AE11 (20) and AE12 (21) are the
    // US 0, - and = keys.
    [Test]
    [Arguments("fr AZERTY: à/0 key", 0x0e0u, 0x0e0u, 0x030u, 19u, "Reset")]
    [Arguments("fr AZERTY: Shift+à/0 types 0", 0x030u, 0x0e0u, 0x030u, 19u, "Reset")]
    [Arguments("fr AZERTY: )/° key", 0x029u, 0x029u, 0x0b0u, 20u, "None")]
    [Arguments("fr AZERTY: =/+ key", 0x03du, 0x03du, 0x02bu, 21u, "In")]
    [Arguments("fr AZERTY: -/6 key", 0x02du, 0x02du, 0x036u, 15u, "Out")]
    [Arguments("fr AZERTY: é/2 key", 0x0e9u, 0x0e9u, 0x032u, 11u, "None")]
    [Arguments("be AZERTY: à/0 key", 0x0e0u, 0x0e0u, 0x030u, 19u, "Reset")]
    [Arguments("cz QWERTZ: é/0 key", 0x0e9u, 0x0e9u, 0x030u, 19u, "Reset")]
    [Arguments("cz QWERTZ: =/% key", 0x03du, 0x03du, 0x025u, 20u, "In")]
    [Arguments("cz QWERTZ: +/1 key", 0x02bu, 0x02bu, 0x031u, 10u, "In")]
    [Arguments("cz QWERTZ: ´/ˇ key", 0xfe51u, 0xfe51u, 0xfe5au, 21u, "None")]
    [Arguments("sk QWERTZ: é/0 key", 0x0e9u, 0x0e9u, 0x030u, 19u, "Reset")]
    [Arguments("lt: “/” key (0 on level 3)", 0xad2u, 0xad2u, 0xad3u, 19u, "Reset")]
    [Arguments("de QWERTZ: 0/= key", 0x030u, 0x030u, 0x03du, 19u, "Reset")]
    [Arguments("de QWERTZ: ß/? key", 0x0dfu, 0x0dfu, 0x03fu, 20u, "None")]
    [Arguments("de QWERTZ: ´/` key", 0xfe51u, 0xfe51u, 0xfe50u, 21u, "None")]
    [Arguments("de QWERTZ: +/* key", 0x02bu, 0x02bu, 0x02au, 35u, "In")]
    [Arguments("de QWERTZ: -/_ key", 0x02du, 0x02du, 0x05fu, 61u, "Out")]
    [Arguments("us Dvorak: [/{ key", 0x05bu, 0x05bu, 0x07bu, 20u, "None")]
    [Arguments("us Dvorak: ]/} key", 0x05du, 0x05du, 0x07du, 21u, "None")]
    [Arguments("us Dvorak: -/_ key", 0x02du, 0x02du, 0x05fu, 40u, "Out")]
    [Arguments("us Dvorak: =/+ key", 0x03du, 0x03du, 0x02bu, 35u, "In")]
    [Arguments("ru ЙЦУКЕН: 0/) key", 0x030u, 0x030u, 0x029u, 19u, "Reset")]
    [Arguments("ru ЙЦУКЕН: -/_ key", 0x02du, 0x02du, 0x05fu, 20u, "Out")]
    [Arguments("ru ЙЦУКЕН: х/Х key", 0x6c8u, 0x6c8u, 0x6e8u, 34u, "None")]
    public void MapsZoomKeysOfLayouts(string key, uint keyval, uint unshifted, uint shifted, uint keycode, string expected)
    {
        _ = key;
        Assert.Equal(Enum.Parse<PageZoomCommand>(expected),
            WebKitGtkKeyboard.GetZoomCommand(keyval, Control, [unshifted, shifted], keycode));
        // Without Control nothing zooms, whatever the layout.
        Assert.Equal(PageZoomCommand.None, WebKitGtkKeyboard.GetZoomCommand(keyval, 0, [unshifted, shifted], keycode));
    }

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
        Assert.Contains("isTrusted", WebKitGtkKeyboard.Script, StringComparison.Ordinal);
    }
}
