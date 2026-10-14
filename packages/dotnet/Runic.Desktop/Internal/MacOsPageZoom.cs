namespace Runic.Desktop.Internal;

// Page zoom in the macOS WKWebView window. WKWebView has pageZoom (macOS 11) but no zoom keys: Safari zooms from its
// View menu. The host adds the same menu, whose key equivalents AppKit matches only after the page: WKWebView sends
// a Command key to the page first and hands it back to AppKit when the page did not cancel its keydown.
internal static class MacOsPageZoom
{
    // NSEventModifierFlagCommand. The items use Command alone, as in Safari, not Control as on Linux.
    internal const ulong CommandModifier = 1UL << 20;

    /// <summary>An item of the View menu. A hidden item still answers its key equivalent.</summary>
    internal readonly record struct MenuItem(string Title, string KeyEquivalent, PageZoomCommand Command, bool Hidden);

    // Command and + or =, Command and - (or _ with Shift) and Command and 0, as on Linux with Control. AppKit matches
    // the character the key types with the active layout and also the keypad's + and -.
    internal static readonly MenuItem[] MenuItems =
    [
        new("Actual Size", "0", PageZoomCommand.Reset, Hidden: false),
        new("Zoom In", "+", PageZoomCommand.In, Hidden: false),
        new("Zoom In", "=", PageZoomCommand.In, Hidden: true),
        new("Zoom Out", "-", PageZoomCommand.Out, Hidden: false),
        new("Zoom Out", "_", PageZoomCommand.Out, Hidden: true),
    ];

    internal static string Selector(PageZoomCommand command) => command switch
    {
        PageZoomCommand.In => "runicZoomIn:",
        PageZoomCommand.Out => "runicZoomOut:",
        PageZoomCommand.Reset => "runicActualSize:",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    /// <summary>The next page zoom: the browser steps from 30 % to 300 % of Linux, and 100 % for a reset.</summary>
    internal static double Apply(PageZoomCommand command, double current) => WebKitGtkKeyboard.Apply(command, current);
}
