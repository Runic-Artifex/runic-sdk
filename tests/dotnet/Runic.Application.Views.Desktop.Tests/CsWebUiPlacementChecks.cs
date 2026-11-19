extern alias cswebui;
using CsWebUi;
using Runic.Application.Views;
using TUnit.Core;
using CsWebUiPlacement = cswebui::Runic.Application.Views.CsWebUi.CsWebUiPlacement;
using WorkArea = cswebui::Runic.Application.Views.CsWebUi.WorkArea;

// sdk#1149: a CS-WebUI window's placement. The presentation's polling, maximize and restore run against a real window in
// the probes' --probe-webview-placement; these check the arithmetic: the conversion, and that a saved placement stays on
// a work area that exists now, by Runic Desktop's rule.
internal sealed class CsWebUiPlacementChecks
{
    private static readonly WorkArea Primary = new(0, 0, 1920, 1040), Left = new(-1280, 0, 1280, 984);

    [Test]
    public void APlacementConvertsWithAndWithoutAPosition()
    {
        Require(CsWebUiPlacement.ToRunic(new WebUiWindowPlacement(-1200, 40, 800, 600, IsMaximized: true))
                == new RunicWindowPlacement { X = -1200, Y = 40, Width = 800, Height = 600, Maximized = true },
            "A placement with a position did not convert.");
        Require(CsWebUiPlacement.ToRunic(new WebUiWindowPlacement(0, 0, 800, 600, HasPosition: false))
                == new RunicWindowPlacement { Width = 800, Height = 600 },
            "A Wayland placement kept a position.");
    }

    [Test]
    public void ASavedPlacementStaysOnAMonitorThatExistsNow()
    {
        var current = new WebUiWindowPlacement(100, 100, 640, 480);
        // On a monitor left of the primary one: kept, with its negative position.
        Require(CsWebUiPlacement.Fit(new RunicWindowPlacement { X = -1100, Y = 50, Width = 900, Height = 700 }, current, [Primary, Left])
                == new WebUiWindowPlacement(-1100, 50, 900, 700),
            "A placement on the left monitor moved.");
        // Saved on a monitor that is gone: moved onto the nearest one, inside its work area.
        Require(CsWebUiPlacement.Fit(new RunicWindowPlacement { X = 2500, Y = 900, Width = 900, Height = 700, Maximized = true }, current, [Primary])
                == new WebUiWindowPlacement(1020, 340, 900, 700, IsMaximized: true),
            "A placement off every monitor was not moved onto the nearest.");
        // Larger than the work area: shrunk to fit.
        Require(CsWebUiPlacement.Fit(new RunicWindowPlacement { X = 0, Y = 0, Width = 4000, Height = 3000 }, current, [Primary])
                == new WebUiWindowPlacement(0, 0, 1920, 1040),
            "A placement larger than the work area was not shrunk.");
        // No position and no size keep the window's own.
        Require(CsWebUiPlacement.Fit(new RunicWindowPlacement { Maximized = true }, current, [Primary])
                == current with { IsMaximized = true },
            "A placement without bounds changed them.");
        // Without monitors (none known) the bounds are taken as they are.
        Require(CsWebUiPlacement.Fit(new RunicWindowPlacement { X = 5000, Y = 5000, Width = 300, Height = 200 }, current, [])
                == new WebUiWindowPlacement(5000, 5000, 300, 200),
            "A placement changed without work areas to fit.");
    }

    [Test]
    public void WithoutAPositionOnlyTheSizeFits()
    {
        var wayland = new WebUiWindowPlacement(0, 0, 640, 480, HasPosition: false);
        Require(CsWebUiPlacement.Fit(new RunicWindowPlacement { Width = 2500, Height = 900 }, wayland, [Primary, Left])
                == new WebUiWindowPlacement(0, 0, 1920, 900, HasPosition: false),
            "A Wayland placement did not fit its size to the largest work area.");
    }

    [Test]
    public void BeforeShowingWebUisLimitsApply()
    {
        var (size, position) = CsWebUiPlacement.BeforeShow(new RunicWindowPlacement { X = 200, Y = 100, Width = 900, Height = 700 }, 800, 600, [Primary]);
        Require(size == (900u, 700u) && position == (200u, 100u), $"Size {size}, position {position}.");
        // A position left of the primary monitor waits for TrySetPlacement; the window's options give the size.
        (size, position) = CsWebUiPlacement.BeforeShow(new RunicWindowPlacement { X = -1100, Y = 50 }, 800, 600, [Primary, Left]);
        Require(size == (800u, 600u) && position is null, $"Size {size}, position {position}.");
        // Neither the placement nor the options size the window.
        (size, position) = CsWebUiPlacement.BeforeShow(new RunicWindowPlacement { Maximized = true }, null, null, [Primary]);
        Require(size is null && position is null, $"Size {size}, position {position}.");
        // WebUI's smallest size.
        (size, _) = CsWebUiPlacement.BeforeShow(new RunicWindowPlacement { Width = 40, Height = 30 }, null, null, []);
        Require(size == (100u, 100u), $"Size {size}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
