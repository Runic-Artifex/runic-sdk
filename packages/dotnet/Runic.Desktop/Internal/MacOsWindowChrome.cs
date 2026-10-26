using System.Collections.Immutable;
using System.Text.Json;

namespace Runic.Desktop.Internal;

// The macOS window chrome of DesktopWindowOptions.MacOS and the page's drag regions, without AppKit: the style mask,
// the reported regions and their hit test, the double-click action and the traffic lights' layout. The native side is
// MacOsWkWebViewHost.WindowChrome.cs.
//
// The drag regions are tested in the application's process: MacOsWindowChrome.js reports them through a WebKit script
// message whenever they change, and the web view's mouseDown: tests the event against the last report and calls
// performWindowDragWithEvent: with it at once. A Bridge round trip would answer after the event is gone, and
// performWindowDragWithEvent: needs the mouse-down it is given to be the current one.
internal static class MacOsWindowChrome
{
    internal const string MessageHandler = "runicWindowChrome";
    internal const string ContentWorld = "runic-desktop-window-chrome";
    internal const string ScriptResource = "Runic.Desktop.MacOsWindowChrome.js";
    // The most regions a report may carry; the script stops at the same number.
    internal const int MaximumRegions = 512;

    // NSWindowStyleMask.
    internal const ulong StyleBorderless = 0;
    internal const ulong StyleTitled = 1;
    internal const ulong StyleClosable = 2;
    internal const ulong StyleMiniaturizable = 4;
    internal const ulong StyleResizable = 8;
    internal const ulong StyleFullSizeContentView = 1UL << 15;

    // NSWindowTitleVisibility.
    internal const long TitleVisible = 0;
    internal const long TitleHidden = 1;

    private static readonly Lazy<string> LazyScript = new(static () =>
    {
        using var stream = typeof(MacOsWindowChrome).Assembly.GetManifestResourceStream(ScriptResource)
            ?? throw new InvalidOperationException("The macOS window chrome script is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    internal static string Script => LazyScript.Value;

    // The window has a title bar that the options change: neither frameless nor kiosk.
    internal static bool HasCustomTitleBar(WebUiEmbeddedHostOptions options) =>
        !options.Frameless && !options.Kiosk && options.MacOS.TitleBar != MacOSTitleBarStyle.Default;

    // A window without the standard title bar moves by the page's drag regions, as in Electron. A kiosk window is
    // full-screen and does not move.
    internal static bool UsesDragRegions(WebUiEmbeddedHostOptions options) =>
        !options.Kiosk && (options.Frameless || options.MacOS.TitleBar != MacOSTitleBarStyle.Default);

    internal static ulong StyleMask(WebUiEmbeddedHostOptions options)
    {
        var resizable = options.Resizable ? StyleResizable : 0;
        if (options.Frameless || options.Kiosk) return StyleBorderless | resizable;
        var style = StyleTitled | StyleClosable | StyleMiniaturizable | resizable;
        return HasCustomTitleBar(options) ? style | StyleFullSizeContentView : style;
    }

    internal static long TitleVisibility(MacOSTitleBarStyle titleBar) =>
        titleBar == MacOSTitleBarStyle.Hidden ? TitleHidden : TitleVisible;

    internal readonly record struct DragRegion(bool Drag, double X, double Y, double Width, double Height)
    {
        internal bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
    }

    // A report of the page: its viewport width in CSS pixels, which scales points to CSS pixels, and its regions.
    internal sealed record DragRegions(double ViewportWidth, ImmutableArray<DragRegion> Regions)
    {
        internal static DragRegions Empty { get; } = new(0, []);
    }

    // The report of MacOsWindowChrome.js, or null when it is not one: {"w": width, "r": [[kind, x, y, w, h], ...]}.
    internal static DragRegions? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 64 * 1024) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("w", out var widthElement) || !widthElement.TryGetDouble(out var width)
                || !double.IsFinite(width) || width < 0
                || !root.TryGetProperty("r", out var list) || list.ValueKind != JsonValueKind.Array
                || list.GetArrayLength() > MaximumRegions)
            {
                return null;
            }
            var regions = ImmutableArray.CreateBuilder<DragRegion>(list.GetArrayLength());
            Span<double> values = stackalloc double[5];
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 5) return null;
                var index = 0;
                foreach (var value in item.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out values[index]) || !double.IsFinite(values[index]))
                        return null;
                    index++;
                }
                if (values[0] is not (0 or 1) || values[3] < 0 || values[4] < 0) return null;
                regions.Add(new DragRegion(values[0] == 1, values[1], values[2], values[3], values[4]));
            }
            return new DragRegions(width, regions.MoveToImmutable());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Whether a point of the viewport, in CSS pixels, drags the window: the last region that contains it decides, so a
    // no-drag region over a drag region, such as a button in a header, keeps its clicks.
    internal static bool IsDraggable(DragRegions regions, double x, double y)
    {
        for (var index = regions.Regions.Length - 1; index >= 0; index--)
        {
            if (regions.Regions[index].Contains(x, y)) return regions.Regions[index].Drag;
        }
        return false;
    }

    // A point of the web view, in points from its top-left corner, in CSS pixels of the reported viewport: the page
    // zoom makes the viewport narrower than the view.
    internal static (double X, double Y) ToViewport(DragRegions regions, double viewWidth, double x, double y)
    {
        var scale = regions.ViewportWidth > 0 && viewWidth > 0 ? viewWidth / regions.ViewportWidth : 1;
        return (x / scale, y / scale);
    }

    internal enum DoubleClickAction
    {
        Zoom,
        Minimize,
        None,
    }

    // What a double click on the title bar does, from System Settings › Desktop & Dock › "Double-click a window's title
    // bar to": AppleActionOnDoubleClick is Maximize, Fill or Zoom (zoom), Minimize, or None ("Do Nothing"); before it
    // existed, AppleMiniaturizeOnDoubleClick chose minimizing. Unset, the default zooms.
    internal static DoubleClickAction ForDoubleClick(string? actionOnDoubleClick, bool miniaturizeOnDoubleClick) =>
        actionOnDoubleClick switch
        {
            "Minimize" => DoubleClickAction.Minimize,
            "None" or "No Action" => DoubleClickAction.None,
            null or "" => miniaturizeOnDoubleClick ? DoubleClickAction.Minimize : DoubleClickAction.Zoom,
            _ => DoubleClickAction.Zoom,
        };

    // Where the traffic lights go for MacOSWindowOptions.TrafficLightPosition: the title bar container becomes as high
    // as a button plus twice the vertical position, and the buttons keep AppKit's spacing from the given left edge,
    // centered in it. As Electron's trafficLightPosition. Origins are in AppKit's bottom-left coordinates of each
    // view's superview: the container's in the window's frame view, the buttons' in the title bar.
    internal readonly record struct TrafficLightLayout(double ContainerY, double ContainerHeight, double ButtonY, double FirstButtonX, double Step);

    internal static TrafficLightLayout LayoutTrafficLights(System.Drawing.Point position, double windowHeight, double buttonHeight, double closeX, double minimizeX)
    {
        var containerHeight = buttonHeight + 2 * position.Y;
        var step = minimizeX - closeX;
        return new TrafficLightLayout(windowHeight - containerHeight, containerHeight, (containerHeight - buttonHeight) / 2, position.X, step);
    }
}
