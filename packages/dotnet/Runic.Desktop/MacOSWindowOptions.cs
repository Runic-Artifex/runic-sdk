namespace Runic.Desktop;

/// <summary>The title bar of a macOS embedded window (<see cref="MacOSWindowOptions.TitleBar"/>).</summary>
/// <remarks>Open: a later minor release may add styles; this release rejects a value it does not define.</remarks>
public enum MacOSTitleBarStyle
{
    /// <summary>The standard title bar, above the page.</summary>
    Default,

    /// <summary>
    /// No visible title bar: the page fills the whole window, under a transparent title bar that keeps the close,
    /// minimize and zoom buttons (the traffic lights) and hides the title, as in Notes or Music.
    /// </summary>
    Hidden,

    /// <summary>As <see cref="Hidden"/>, but the title bar still shows the window's title over the page.</summary>
    Transparent,
}

/// <summary>The title bar of a macOS embedded window: its style and where its traffic lights sit.</summary>
/// <remarks>
/// <para>
/// Only the macOS WKWebView window applies these options; Windows, Linux, installed browsers and custom window hosts
/// ignore them. A <see cref="DesktopWindowOptions.Frameless"/> or <see cref="DesktopWindowOptions.Kiosk"/> window has
/// no title bar, so it ignores them too, and <see cref="DesktopHost.Validate"/> reports the ignored option.
/// </para>
/// <para>
/// A window with a <see cref="MacOSTitleBarStyle.Hidden"/> or <see cref="MacOSTitleBarStyle.Transparent"/> title bar,
/// and a frameless window, moves when the user drags a part of the page marked with the CSS
/// <c>app-region: drag</c> (or <c>-webkit-app-region: drag</c>) and <c>no-drag</c> declarations that Electron uses;
/// see the package README.
/// </para>
/// </remarks>
public sealed record MacOSWindowOptions
{
    /// <summary>Gets the title bar style: <see cref="MacOSTitleBarStyle.Default"/>, the default, for the standard title bar.</summary>
    public MacOSTitleBarStyle TitleBar { get; init; }

    /// <summary>
    /// Gets the position of the close button's top-left corner from the window's top-left corner, in points, or
    /// <see langword="null"/>, the default, for AppKit's position. The minimize and zoom buttons follow it with AppKit's
    /// spacing.
    /// </summary>
    /// <remarks>
    /// Applies with a <see cref="MacOSTitleBarStyle.Hidden"/> or <see cref="MacOSTitleBarStyle.Transparent"/> title bar,
    /// for example to line the buttons up with the page's own header; both coordinates must not be negative. The title
    /// bar becomes as high as a button plus twice the vertical position, so the buttons stay centered in it, as with
    /// Electron's <c>trafficLightPosition</c>.
    /// </remarks>
    public System.Drawing.Point? TrafficLightPosition { get; init; }
}
