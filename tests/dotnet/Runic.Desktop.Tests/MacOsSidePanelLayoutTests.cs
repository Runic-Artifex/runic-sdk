using Runic.Desktop.Internal;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// sdk#864, P3: the AppKit side of the macOS side panel (MacOsSidePanelLayout), from the plans of SidePanelPlanner.
public sealed class MacOsSidePanelLayoutTests
{
    // A 860 × 640 pt frame whose top-left corner is at 500, 200 in Runic's coordinates on a 1080 pt high primary screen:
    // AppKit's bottom edge is 1080 - (200 + 640) = 240.
    private static readonly DesktopWindowBounds Bounds = new() { X = 500, Y = 200, Width = 860, Height = 640, Scale = 1 };
    private static readonly (double X, double Y, double Width, double Height) Frame = (500, 240, 860, 640);
    private static readonly ScreenRect WorkArea = new(0, 25, 1920, 1000);

    private static SidePanelWindow Window(DesktopWindowBounds bounds) => new(bounds, bounds, WorkArea, false, false, false, false);

    [Test]
    public void OpeningOnTheLeftMovesTheFrameLeftAndKeepsItsTopAndRightEdge()
    {
        var plan = SidePanelPlanner.Open(Window(Bounds), left: true, 240);
        var frame = MacOsSidePanelLayout.Frame(Frame, Bounds, plan.Target!);
        Assert.Equal((260d, 240d, 1100d, 640d), frame);
        Assert.Equal(Frame.X + Frame.Width, frame.X + frame.Width);
    }

    [Test]
    public void OpeningOnTheRightWidensTheFrameInPlace()
    {
        var plan = SidePanelPlanner.Open(Window(Bounds), left: false, 240);
        Assert.Equal((500d, 240d, 1100d, 640d), MacOsSidePanelLayout.Frame(Frame, Bounds, plan.Target!));
    }

    [Test]
    public void AFractionalFrameKeepsItsFractionAndClosesBackExactly()
    {
        // AppKit may center a window at half points; Bounds report it rounded.
        var fractional = (X: 500.5, Y: 239.5, Width: 860d, Height: 640d);
        var plan = SidePanelPlanner.Open(Window(Bounds), left: true, 240);
        var opened = MacOsSidePanelLayout.Frame(fractional, Bounds, plan.Target!);
        Assert.Equal((260.5, 239.5, 1100d, 640d), opened);
        var closed = SidePanelPlanner.Close(plan.Target!, plan.Target!, Bounds, left: true, plan.NativeWidth);
        Assert.Equal(fractional, MacOsSidePanelLayout.Frame(opened, plan.Target!, closed));
    }

    [Test]
    public void AHeightChangeKeepsTheTopEdge()
    {
        var taller = Bounds with { Height = 700 };
        var frame = MacOsSidePanelLayout.Frame(Frame, Bounds, taller);
        // The top edge in AppKit's coordinates (bottom + height) stays at 880.
        Assert.Equal(880d, frame.Y + frame.Height);
        Assert.Equal(700d, frame.Height);
    }

    [Test]
    public void TheLayoutGivesThePanelItsWidthOnItsEdgeAndThePageTheRest()
    {
        Assert.Equal((240d, 860d, 0d, 240d), MacOsSidePanelLayout.Layout(0, 1100, left: true, 240));
        Assert.Equal((0d, 860d, 860d, 240d), MacOsSidePanelLayout.Layout(0, 1100, left: false, 240));
        // A zoomed window: the panel keeps its width, the page takes the rest.
        Assert.Equal((0d, 1680d, 1680d, 240d), MacOsSidePanelLayout.Layout(0, 1920, left: false, 240));
        // A content view narrower than the panel gives it all.
        Assert.Equal((100d, 0d, 0d, 100d), MacOsSidePanelLayout.Layout(0, 100, left: true, 240));
    }
}
