using System.Text.Json;
using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// B-3 (D045.3): the macOS host's one conversion between AppKit's bottom-left screen coordinates and Runic's top-left
// ones. The native side, a window moved and restored on a real screen, is covered by
// tests/native/Runic.Desktop.WebViewSmoke --monitors on the macOS CI runner.
public sealed class MacOsCoordinatesTests
{
    // A 1440x900 primary screen, as AppKit reports it: origin 0,0, a 25-point menu bar on top and a 70-point Dock below.
    private const double PrimaryHeight = 900;
    private static readonly (double X, double Y, double Width, double Height) PrimaryFrame = (0, 0, 1440, 900);
    private static readonly (double X, double Y, double Width, double Height) PrimaryVisible = (0, 70, 1440, 805);

    // A window of 800x600 content under a 28-point title bar.
    private const double FrameHeight = 628;

    [Test]
    public void SingleScreenFlipsToTopLeft()
    {
        Assert.Equal(new DesktopRect(0, 0, 1440, 900), ToTopLeft(PrimaryFrame));
        // The work area starts below the menu bar and ends above the Dock.
        Assert.Equal(new DesktopRect(0, 25, 1440, 805), ToTopLeft(PrimaryVisible));
        // A frame whose top touches the visible frame's top reports the work area's top.
        var bottom = PrimaryVisible.Y + PrimaryVisible.Height - FrameHeight;
        Assert.Equal((100, 25), MacOsCoordinates.FrameTopLeft(100, bottom, FrameHeight, PrimaryHeight));
        Assert.Equal(bottom, MacOsCoordinates.FrameBottom(25, FrameHeight, PrimaryHeight, bottomLeft: false));
        // At (x, 0) the frame's top is the screen's top edge.
        Assert.Equal(PrimaryHeight - FrameHeight, MacOsCoordinates.FrameBottom(0, FrameHeight, PrimaryHeight, bottomLeft: false));
    }

    [Test]
    public void SecondaryScreenAboveThePrimaryHasNegativeTop()
    {
        // A 2560x1440 display arranged above, its bottom on the primary's top.
        Assert.Equal(new DesktopRect(0, -1440, 2560, 1440), ToTopLeft((0, 900, 2560, 1440)));
        // A window near its top: AppKit y 2000 for the bottom of a 628-high frame.
        Assert.Equal((300, -1728), MacOsCoordinates.FrameTopLeft(300, 2000, FrameHeight, PrimaryHeight));
    }

    [Test]
    public void SecondaryScreenBelowThePrimaryStartsAtItsHeight()
    {
        // A 1920x1080 display arranged below: AppKit y is negative there.
        Assert.Equal(new DesktopRect(0, 900, 1920, 1080), ToTopLeft((0, -1080, 1920, 1080)));
        Assert.Equal((10, 1200), MacOsCoordinates.FrameTopLeft(10, -928, FrameHeight, PrimaryHeight));
    }

    [Test]
    public void SecondaryScreenLeftOfThePrimaryWithAnotherHeight()
    {
        // Bottom-aligned 1920x1080 display on the left: taller than the primary, so its top is above the primary's.
        Assert.Equal(new DesktopRect(-1920, -180, 1920, 1080), ToTopLeft((-1920, 0, 1920, 1080)));
        // Top-aligned, as macOS arranges a new display by default: both tops at 0.
        Assert.Equal(new DesktopRect(-1920, 0, 1920, 1080), ToTopLeft((-1920, -180, 1920, 1080)));
        // A smaller 1280x720 display right of the primary, vertically centered.
        Assert.Equal(new DesktopRect(1440, 90, 1280, 720), ToTopLeft((1440, 90, 1280, 720)));
    }

    [Test]
    public void OnlyThePrimaryHeightDefinesTheFlip()
    {
        // The same AppKit rectangle against a 1080-high primary instead of a 900-high one moves down by the difference.
        var onShort = MacOsCoordinates.ToTopLeft(-1920, -180, 1920, 1080, 900);
        var onTall = MacOsCoordinates.ToTopLeft(-1920, -180, 1920, 1080, 1080);
        Assert.Equal(onShort with { Y = onShort.Y + 180 }, onTall);
    }

    [Test]
    public void FlipRoundTrips()
    {
        foreach (var (y, height, primary) in new (double, double, double)[]
            { (0, 900, 900), (70, 805, 900), (2000, 628, 900), (-928, 628, 900), (-100_000, 15_028, 1117), (12.5, 300, 982) })
        {
            Assert.Equal(y, MacOsCoordinates.FlipY(MacOsCoordinates.FlipY(y, height, primary), height, primary));
        }
        // A position set top-left reads back the same.
        foreach (var (x, top) in new[] { (0, 0), (40, 60), (-1900, -1400), (1500, 1200) })
        {
            var bottom = MacOsCoordinates.FrameBottom(top, FrameHeight, PrimaryHeight, bottomLeft: false);
            Assert.Equal((x, top), MacOsCoordinates.FrameTopLeft(x, bottom, FrameHeight, PrimaryHeight));
        }
    }

    [Test]
    public void WithoutScreensTheFlipStaysItsOwnInverse()
    {
        var bottom = MacOsCoordinates.FrameBottom(60, FrameHeight, 0, bottomLeft: false);
        Assert.Equal((40, 60), MacOsCoordinates.FrameTopLeft(40, bottom, FrameHeight, 0));
    }

    [Test]
    public void PlacementsWithoutOriginAreLegacyBottomLeft()
    {
        Assert.False(MacOsCoordinates.IsBottomLeft(null));
        Assert.True(MacOsCoordinates.IsBottomLeft(new DesktopWindowPlacement { X = 1, Y = 2 }));
        Assert.True(MacOsCoordinates.IsBottomLeft(new DesktopWindowPlacement { Origin = DesktopCoordinateOrigin.BottomLeft }));
        Assert.False(MacOsCoordinates.IsBottomLeft(new DesktopWindowPlacement { Origin = DesktopCoordinateOrigin.TopLeft }));
        // The set is open: a value from a later release is not taken for AppKit's coordinates.
        Assert.False(MacOsCoordinates.IsBottomLeft(new DesktopWindowPlacement { Origin = (DesktopCoordinateOrigin)7 }));
    }

    [Test]
    public void LegacyPlacementRestoresToTheSamePlaceAndReportsTopLeft()
    {
        // As Runic 0.11 saved it on macOS: the frame's bottom-left corner in AppKit points, the content size, no Origin.
        var legacy = JsonSerializer.Deserialize<DesktopWindowPlacement>(
            """{"X":100,"Y":200,"Width":800,"Height":600,"Maximized":false,"FullScreen":false}""")!;
        Assert.Null(legacy.Origin);
        Assert.True(MacOsCoordinates.IsBottomLeft(legacy));

        // Opening applies the legacy position as setFrameOrigin: took it in 0.11 ...
        var bottom = MacOsCoordinates.FrameBottom(legacy.Y!.Value, FrameHeight, PrimaryHeight, MacOsCoordinates.IsBottomLeft(legacy));
        Assert.Equal(200, bottom);
        // ... and the window then reports the same frame top-left: 900 - (200 + 628) = 72.
        var (x, y) = MacOsCoordinates.FrameTopLeft(legacy.X!.Value, bottom, FrameHeight, PrimaryHeight);
        var migrated = legacy with { X = x, Y = y, Origin = DesktopCoordinateOrigin.TopLeft };
        Assert.Equal(new DesktopWindowPlacement { X = 100, Y = 72, Width = 800, Height = 600, Origin = DesktopCoordinateOrigin.TopLeft }, migrated);

        // Restoring the migrated placement puts the frame at the same AppKit position: converted once, not again.
        Assert.False(MacOsCoordinates.IsBottomLeft(migrated));
        Assert.Equal(bottom, MacOsCoordinates.FrameBottom(migrated.Y!.Value, FrameHeight, PrimaryHeight, MacOsCoordinates.IsBottomLeft(migrated)));

        // A legacy placement on a display above the primary: AppKit y 1500 becomes a negative top.
        var above = new DesktopWindowPlacement { X = 200, Y = 1500, Width = 800, Height = 600 };
        var aboveBottom = MacOsCoordinates.FrameBottom(above.Y!.Value, FrameHeight, PrimaryHeight, MacOsCoordinates.IsBottomLeft(above));
        Assert.Equal((200, -1228), MacOsCoordinates.FrameTopLeft(200, aboveBottom, FrameHeight, PrimaryHeight));
    }

    [Test]
    public void OriginSurvivesJsonAndWindowOptions()
    {
        var placement = new DesktopWindowPlacement { X = 100, Y = 72, Width = 800, Height = 600, Origin = DesktopCoordinateOrigin.TopLeft };
        var json = JsonSerializer.Serialize(placement);
        Assert.Contains("\"Origin\":0", json);
        Assert.Equal(placement, JsonSerializer.Deserialize<DesktopWindowPlacement>(json));
        // The host sees the placement's origin next to the X and Y it opens with.
        var applied = new DesktopWindowOptions { Placement = placement with { Origin = null } }.WithPlacementApplied();
        Assert.Equal((100, 72), (applied.X!.Value, applied.Y!.Value));
        Assert.True(MacOsCoordinates.IsBottomLeft(applied.Placement));
    }

    private static DesktopRect ToTopLeft((double X, double Y, double Width, double Height) rect) =>
        MacOsCoordinates.ToTopLeft(rect.X, rect.Y, rect.Width, rect.Height, PrimaryHeight);
}
