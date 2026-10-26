using Runic.Platform;

namespace Runic.Desktop.Internal;

// The one conversion between AppKit's screen coordinates and Runic's on macOS (B-3, D045.3). AppKit measures points
// right and up from the bottom-left corner of the primary screen (NSScreen.screens[0], the one with the menu bar, whose
// frame origin is always 0,0); Runic, like Windows, GTK, Electron and Tauri, measures right and down from its top-left
// corner. X stays; a rectangle's top is primaryHeight - (bottom + height). Screens above the primary get negative
// tops, screens left of it negative lefts, and screens of another height need nothing else: only the primary screen's
// height defines the flip. The flip is its own inverse. The macOS host converts at its boundary only: positions it
// takes (MoveAsync, X and Y, a placement) and rectangles it reports (window state, monitors); everything between, such
// as fitting a placement to the visible frames, stays in AppKit's coordinates. Portable, so Linux CI tests it.
internal static class MacOsCoordinates
{
    // A rectangle's AppKit bottom edge to its Runic top edge, and back.
    internal static double FlipY(double y, double height, double primaryHeight) => primaryHeight - (y + height);

    // An AppKit rectangle (a screen's frame or visible frame) in Runic's coordinates, rounded to whole points.
    internal static DesktopRect ToTopLeft(double x, double y, double width, double height, double primaryHeight) =>
        new(Round(x), Round(FlipY(y, height, primaryHeight)), Round(width), Round(height));

    // Whether X and Y of a placement are AppKit's bottom-left corner of the frame: saved by Runic 0.11 or earlier
    // (no origin) or marked so. Unknown values count as top-left, as the enum's documentation says.
    internal static bool IsBottomLeft(DesktopWindowPlacement? placement) =>
        placement is { Origin: null or DesktopCoordinateOrigin.BottomLeft };

    // The AppKit y of the frame's bottom edge (setFrameOrigin:) for a requested y: the top-left corner's y of a frame
    // frameHeight high (title bar included), or, for a legacy placement, already the bottom edge.
    internal static double FrameBottom(int y, double frameHeight, double primaryHeight, bool bottomLeft) =>
        bottomLeft ? y : FlipY(y, frameHeight, primaryHeight);

    // The position a window host reports for an AppKit frame: its top-left corner, rounded to whole points.
    internal static (int X, int Y) FrameTopLeft(double x, double y, double frameHeight, double primaryHeight) =>
        (Round(x), Round(FlipY(y, frameHeight, primaryHeight)));

    private static int Round(double value) =>
        (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue);
}
