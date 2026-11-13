using Runic.Desktop.Internal;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// sdk#897 W1a: DesktopWindow.MaximizeAsync, RestoreAsync and SetPlacementAsync through a custom window host, the Windows
// placement arithmetic (device-independent sizes, physical positions, workspace coordinates, the clamp) and the side
// panel's share of a placement.
public sealed class WindowPlacementTests
{
    private static readonly uint[] Dpis = [96, 120, 144, 168, 192];

    [Test]
    public async Task MaximizeRestoreAndPlacementReachACustomHost()
    {
        var factory = new ShowStateFactory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var native = factory.Hosts[0];
        Assert.True(window.Capabilities.HasFlag(DesktopWindowCapabilities.WindowPlacement));

        await window.MaximizeAsync();
        await window.MaximizeAsync();
        await window.RestoreAsync();
        var placement = new DesktopWindowPlacement { X = -1200, Y = 40, Width = 900, Height = 700, Maximized = true, Origin = DesktopCoordinateOrigin.TopLeft };
        await window.SetPlacementAsync(placement);
        Assert.Equal(placement, window.State!.Placement);
        // A saved placement is passed as it is; a size out of range is sanitized as at open.
        await window.SetPlacementAsync(new DesktopWindowPlacement { Width = -5, Height = 100_000 });
        Assert.Equal(["maximize", "maximize", "restore", "placement:-1200,40,900,700,max", "placement:,,0,65535,normal"], native.Calls);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await window.SetPlacementAsync(null!));
        var error = await Assert.ThrowsAsync<ArgumentException>(async () => await window.SetPlacementAsync(placement with { Y = null }));
        Assert.Equal("placement", error.ParamName);
        await Assert.ThrowsAsync<ArgumentException>(async () => await window.SetPlacementAsync(placement with { Origin = (DesktopCoordinateOrigin)7 }));
        // A placement saved before Runic 0.12 has no origin, and macOS converts it as at open.
        await window.SetPlacementAsync(placement with { Origin = null });
        Assert.Equal(6, native.Calls.Count);
    }

    [Test]
    public async Task AHostWithoutTheCapabilityRefusesWithItsReason()
    {
        var factory = new ShowStateFactory { Capabilities = DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.State };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });

        foreach (var call in (Func<ValueTask>[])[() => window.MaximizeAsync(), () => window.RestoreAsync(),
            () => window.SetPlacementAsync(new DesktopWindowPlacement { Width = 800, Height = 600 })])
        {
            var error = await Assert.ThrowsAsync<DesktopException>(async () => await call());
            Assert.Equal("window-capability-unavailable", error.Code);
            Assert.Equal(DesktopErrorCategory.Unavailable, error.Category);
            Assert.Contains("DesktopWindowCapabilities.WindowPlacement", error.Message);
            Assert.Contains("custom window host", error.Message);
        }
        Assert.Empty(factory.Hosts[0].Calls);
    }

    [Test]
    public async Task ACustomHostWithTheFlagButWithoutTheInterfaceFailsAsUnsupported()
    {
        var factory = new ShowStateFactory { WithoutInterface = true };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        await Assert.ThrowsAsync<NotSupportedException>(async () => await window.MaximizeAsync());
    }

    [Test]
    public async Task AKioskWindowRefusesToLeaveFullScreen()
    {
        var factory = new ShowStateFactory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded, Kiosk = true });

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await window.MaximizeAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await window.RestoreAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await window.SetPlacementAsync(new DesktopWindowPlacement { Width = 800, Height = 600 }));
        // A full-screen placement keeps it full-screen and reaches the host.
        await window.SetPlacementAsync(new DesktopWindowPlacement { Width = 800, Height = 600, FullScreen = true });
        Assert.Equal(["placement:,,800,600,full"], factory.Hosts[0].Calls);
    }

    [Test]
    public async Task APositionTheHostCannotSetFailsWithItsCodeAndChangesNothing()
    {
        // As on Wayland (D064.11): the whole placement fails.
        var factory = new ShowStateFactory { RefusePosition = true };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var before = window.State;

        var error = await Assert.ThrowsAsync<DesktopException>(async () =>
            await window.SetPlacementAsync(new DesktopWindowPlacement { X = 0, Y = 0, Width = 800, Height = 600, Maximized = true }));
        Assert.Equal(WindowBoundsSupport.PositionUnavailableCode, error.Code);
        Assert.Equal(before, window.State);
        await window.SetPlacementAsync(new DesktopWindowPlacement { Width = 800, Height = 600, Maximized = true });
        Assert.Equal(["placement:,,800,600,max"], factory.Hosts[0].Calls);
    }

    [Test]
    [Arguments(96u)]
    [Arguments(120u)]
    [Arguments(144u)]
    [Arguments(168u)]
    [Arguments(192u)]
    [DisplayName("A Windows placement round-trips at $dpi DPI")]
    public void AWindowsPlacementRoundTrips(uint dpi)
    {
        PlacementMonitor[] monitors = [new(new ScreenRect(0, 0, 3840, 2100), dpi)];
        var current = new ScreenRect(100, 100, 1000, 800);
        foreach (var (width, height) in (ReadOnlySpan<(int, int)>)[(800, 600), (801, 601), (1001, 701), (333, 257)])
        {
            var placement = new DesktopWindowPlacement { X = 240, Y = 130, Width = width, Height = height };
            var (restored, used) = WindowPlacementMath.RestoredRect(placement, current, dpi, monitors, panel: null);
            Assert.Equal(dpi, used);
            // The position is physical and exact; the size is device-independent and rounds once each way.
            Assert.Equal((240, 130), (restored.X, restored.Y));
            Assert.Equal((int)Math.Round(width * dpi / 96d), restored.Width);
            var reported = WindowPlacementMath.Report(restored, dpi, panel: null);
            Assert.Equal((240, 130), (reported.X, reported.Y));
            Assert.InRange(reported.Width, width - 1, width + 1);
            Assert.InRange(reported.Height, height - 1, height + 1);
            Assert.Equal(DesktopCoordinateOrigin.TopLeft, reported.Origin);
            // Set again, the reported placement gives the same rectangle.
            Assert.Equal(restored, WindowPlacementMath.RestoredRect(reported, restored, dpi, monitors, null).Restored);
        }
    }

    [Test]
    public void AWindowsPlacementKeepsWhatItLeavesOutAndFitsTheWorkArea()
    {
        PlacementMonitor[] monitors = [new(new ScreenRect(0, 0, 1920, 1040), 144)];
        var current = new ScreenRect(300, 100, 1200, 900);
        // A null position and a zero size keep the current restored rectangle.
        Assert.Equal(current, WindowPlacementMath.RestoredRect(new DesktopWindowPlacement(), current, 144, monitors, null).Restored);
        Assert.Equal(current with { Width = 1500 },
            WindowPlacementMath.RestoredRect(new DesktopWindowPlacement { Width = 1000 }, current, 144, monitors, null).Restored);
        // Off every monitor: onto the nearest work area, and shrunk to fit it.
        var far = WindowPlacementMath.RestoredRect(new DesktopWindowPlacement { X = 5000, Y = 3000, Width = 2000, Height = 900 },
            current, 144, monitors, null).Restored;
        Assert.Equal(new ScreenRect(0, 0, 1920, 1040), far);
        var partly = WindowPlacementMath.RestoredRect(new DesktopWindowPlacement { X = 1500, Y = -50, Width = 400, Height = 300 },
            current, 144, monitors, null).Restored;
        Assert.Equal(new ScreenRect(1320, 0, 600, 450), partly);
    }

    [Test]
    public void AWindowsPlacementConvertsWithTheScaleOfTheMonitorItLandsOn()
    {
        // 100 % on the left, 200 % on the right; the window is on the left one now.
        PlacementMonitor[] monitors = [new(new ScreenRect(0, 0, 1920, 1040), 96), new(new ScreenRect(1920, 0, 3840, 2120), 192)];
        var current = new ScreenRect(100, 100, 800, 600);
        var (restored, dpi) = WindowPlacementMath.RestoredRect(new DesktopWindowPlacement { X = 2000, Y = 100, Width = 800, Height = 600 },
            current, 96, monitors, null);
        Assert.Equal(192u, dpi);
        Assert.Equal(new ScreenRect(2000, 100, 1600, 1200), restored);
        Assert.Equal(new DesktopWindowPlacement { X = 2000, Y = 100, Width = 800, Height = 600, Origin = DesktopCoordinateOrigin.TopLeft },
            WindowPlacementMath.Report(restored, dpi, null));
        // A placement that keeps its size takes it in device-independent pixels from the current monitor.
        Assert.Equal(new ScreenRect(2000, 100, 1600, 1200), WindowPlacementMath.RestoredRect(
            new DesktopWindowPlacement { X = 2000, Y = 100 }, current, 96, monitors, null).Restored);
    }

    [Test]
    [Arguments(0, 48, "top")]
    [Arguments(48, 0, "left")]
    [Arguments(0, 0, "bottom")]
    [DisplayName("Workspace coordinates with the taskbar at the $side")]
    public void WorkspaceCoordinatesStartAtTheWorkArea(int left, int top, string side)
    {
        var monitor = new ScreenRect(1920, 0, 1920, 1080);
        var work = new ScreenRect(monitor.X + left, monitor.Y + top, monitor.Width - left, monitor.Height - top - (side == "bottom" ? 48 : 0));
        var screen = new ScreenRect(2100, 300, 1000, 700);
        var workspace = WindowPlacementMath.ScreenToWorkspace(screen, monitor, work);
        Assert.Equal(new ScreenRect(2100 - left, 300 - top, 1000, 700), workspace);
        Assert.Equal(screen, WindowPlacementMath.WorkspaceToScreen(workspace, monitor, work));
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    [DisplayName("A placement with a side panel on the left: $left")]
    public void APlacementWithASidePanelIsThePagesAndRoundTrips(bool left)
    {
        PlacementMonitor[] monitors = [new(new ScreenRect(0, 0, 3840, 2100), 144)];
        var panel = new PlacementPanel(left, 450);
        // The window shows the page and a 450 px panel; the page's placement excludes the panel.
        var current = new ScreenRect(600, 200, 1650, 900);
        var placement = new DesktopWindowPlacement { X = 900, Y = 150, Width = 800, Height = 600 };
        var (restored, dpi) = WindowPlacementMath.RestoredRect(placement, current, 144, monitors, panel);
        Assert.Equal(new ScreenRect(left ? 450 : 900, 150, 1200 + 450, 900), restored);
        var reported = WindowPlacementMath.Report(restored, dpi, panel);
        Assert.Equal(placement with { Origin = DesktopCoordinateOrigin.TopLeft }, reported);
        // The current page placement is kept by an empty placement.
        var kept = WindowPlacementMath.RestoredRect(new DesktopWindowPlacement(), current, 144, monitors, panel).Restored;
        Assert.Equal(current, kept);
    }

    [Test]
    public void AWindowWithASidePanelFitsTheWorkAreaWithThePanel()
    {
        PlacementMonitor[] monitors = [new(new ScreenRect(0, 0, 1920, 1040), 96)];
        // The page asks for the left edge of the work area; the panel on the left needs room, so the window moves right.
        var (restored, dpi) = WindowPlacementMath.RestoredRect(new DesktopWindowPlacement { X = 0, Y = 0, Width = 800, Height = 600 },
            new ScreenRect(500, 100, 1100, 600), 96, monitors, new PlacementPanel(true, 300));
        Assert.Equal(new ScreenRect(0, 0, 1100, 600), restored);
        Assert.Equal(300, WindowPlacementMath.Report(restored, dpi, new PlacementPanel(true, 300)).X);
    }

    [Test]
    public void IncludePanelIsTheInverseOfExcludePanel()
    {
        var page = new DesktopWindowBounds { X = 400, Y = 200, Width = 1001, Height = 900, Scale = 1.5 };
        foreach (var left in (ReadOnlySpan<bool>)[true, false])
        {
            var window = SidePanelPlanner.IncludePanel(page, left, 450);
            Assert.Equal(1451, window.Width);
            Assert.Equal(left ? -50 : 400, window.X);
            Assert.Equal(page, SidePanelPlanner.ExcludePanel(window, left, 450));
        }
        // Without a position (Wayland) only the width changes.
        Assert.Equal(page with { X = null, Y = null, Width = 1451 }, SidePanelPlanner.IncludePanel(page with { X = null, Y = null }, true, 450));
    }

    private sealed class ShowStateFactory : IDesktopWindowHostFactory
    {
        internal List<ShowStateHost> Hosts { get; } = [];
        internal DesktopWindowCapabilities Capabilities { get; init; } = DesktopWindowCapabilities.NativeHandle |
            DesktopWindowCapabilities.State | DesktopWindowCapabilities.WindowPlacement | DesktopWindowCapabilities.FullScreen;
        internal bool RefusePosition { get; init; }
        internal bool WithoutInterface { get; init; }
        public bool IsSupported => true;
        DesktopWindowCapabilities IDesktopWindowHostFactory.Capabilities => Capabilities;
        public IDesktopWindowHost Create()
        {
            ShowStateHost host = WithoutInterface ? new PlainHost(Capabilities, RefusePosition) : new FullShowStateHost(Capabilities, RefusePosition);
            Hosts.Add(host);
            return host;
        }
    }

    // A host that reports the capability without implementing IDesktopWindowShowStateHost: a programming error of the
    // host, which surfaces as NotSupportedException.
    private sealed class PlainHost(DesktopWindowCapabilities capabilities, bool refusePosition) : ShowStateHost(capabilities, refusePosition);

    // A custom host as an application would write one: it records each request and reports the placement back.
    private class ShowStateHost(DesktopWindowCapabilities capabilities, bool refusePosition) : IDesktopWindowStateHost
    {
        public event EventHandler? Closed;
        public event EventHandler? StateChanged;
        public DesktopWindowCapabilities Capabilities => capabilities;
        public bool IsOpen { get; private set; }
        public nint NativeHandle => IsOpen ? 1 : 0;
        public DesktopWindowState? State { get; private set; }
        internal List<string> Calls { get; } = [];

        protected void Report(DesktopWindowState state)
        {
            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        protected ValueTask Maximize()
        {
            Calls.Add("maximize");
            Report(State! with { IsMaximized = true, Placement = State.Placement with { Maximized = true } });
            return ValueTask.CompletedTask;
        }

        protected ValueTask Restore()
        {
            Calls.Add("restore");
            Report(State! with { IsMaximized = false, Placement = State.Placement with { Maximized = false } });
            return ValueTask.CompletedTask;
        }

        protected ValueTask Place(DesktopWindowPlacement placement)
        {
            if (refusePosition && placement.X is not null) return ValueTask.FromException(WindowBoundsSupport.PositionUnavailable());
            var mode = placement.FullScreen ? "full" : placement.Maximized ? "max" : "normal";
            Calls.Add($"placement:{placement.X},{placement.Y},{placement.Width},{placement.Height},{mode}");
            Report(State! with { IsMaximized = placement.Maximized, Placement = placement });
            return ValueTask.CompletedTask;
        }

        public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
        {
            IsOpen = true;
            Report(new DesktopWindowState { Width = 800, Height = 600, Placement = new() { Width = 800, Height = 600 } });
            return ValueTask.CompletedTask;
        }

        public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            if (IsOpen) { IsOpen = false; Closed?.Invoke(this, EventArgs.Empty); }
            return ValueTask.CompletedTask;
        }
        public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }

    private sealed class FullShowStateHost(DesktopWindowCapabilities capabilities, bool refusePosition)
        : ShowStateHost(capabilities, refusePosition), IDesktopWindowShowStateHost
    {
        public ValueTask MaximizeAsync(CancellationToken cancellationToken = default) => Maximize();
        public ValueTask RestoreAsync(CancellationToken cancellationToken = default) => Restore();
        public ValueTask SetPlacementAsync(DesktopWindowPlacement placement, CancellationToken cancellationToken = default) => Place(placement);
    }
}
