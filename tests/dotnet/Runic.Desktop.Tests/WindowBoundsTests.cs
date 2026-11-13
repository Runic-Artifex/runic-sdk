using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// #864 B1: exact window bounds, their unit maths, the snapped heuristics, and DesktopWindow.SetBoundsAsync and
// SetMinimumSizeAsync through a custom window host.
public sealed class WindowBoundsTests
{
    private static readonly double[] Scales = [1, 1.25, 1.5, 1.75, 2, 2.25, 3];

    [Test]
    public void At150PercentA1001PixelWindowGrowsBy372AndShrinksBackExactly()
    {
        // The issue's case: the device-independent width rounds 1001 px to 667 and back to 1000 or 1558.
        var bounds = new DesktopWindowBounds { X = 400, Y = 200, Width = 1001, Height = 900, Scale = 1.5 };

        var right = bounds.Expand(DesktopWindowEdge.Right, 372);
        Assert.Equal(new DesktopWindowBounds { X = 400, Y = 200, Width = 1559, Height = 900, Scale = 1.5 }, right);
        Assert.Equal(bounds, right.Expand(DesktopWindowEdge.Right, -372));

        // The left edge moves; the right edge stays where it was.
        var left = bounds.Expand(DesktopWindowEdge.Left, 372);
        Assert.Equal(new DesktopWindowBounds { X = -158, Y = 200, Width = 1559, Height = 900, Scale = 1.5 }, left);
        Assert.Equal(bounds.X + bounds.Width, left.X + left.Width);
        Assert.Equal(bounds, left.Expand(DesktopWindowEdge.Left, -372));
    }

    [Test]
    public void At125PercentTheLengthRoundsOnceAndTheRoundTripIsExact()
    {
        var bounds = new DesktopWindowBounds { X = 10, Y = 20, Width = 1001, Height = 701, Scale = 1.25 };
        Assert.Equal(1466, bounds.Expand(DesktopWindowEdge.Right, 372).Width);
        // 2 × 1.25 = 2.5 rounds away from zero, both ways, so it does not drift by a pixel.
        Assert.Equal(1004, bounds.Expand(DesktopWindowEdge.Right, 2).Width);
        Assert.Equal(998, bounds.Expand(DesktopWindowEdge.Right, -2).Width);
        Assert.Equal(bounds, bounds.Expand(DesktopWindowEdge.Right, 2).Expand(DesktopWindowEdge.Right, -2));
        Assert.Equal(bounds, bounds.Expand(DesktopWindowEdge.Left, 371.5).Expand(DesktopWindowEdge.Left, -371.5));
    }

    [Test]
    public void GrowingAndShrinkingByTheSameLengthRestoresTheBoundsAtEveryScale()
    {
        foreach (var scale in Scales)
        {
            foreach (var width in (int[])[1, 667, 860, 1001, 1919])
            {
                for (var length = 0.0; length <= 400; length += 0.5)
                {
                    var bounds = new DesktopWindowBounds { X = -37, Y = 5, Width = width, Height = 640, Scale = scale };
                    foreach (var edge in (DesktopWindowEdge[])[DesktopWindowEdge.Left, DesktopWindowEdge.Right])
                    {
                        var grown = bounds.Expand(edge, length);
                        Assert.Equal((int)Math.Round(length * scale, MidpointRounding.AwayFromZero), grown.Width - width);
                        Assert.Equal(bounds, grown.Expand(edge, -length));
                    }
                }
            }
        }
    }

    [Test]
    public void StartAndEndFollowTheLayoutDirection()
    {
        var bounds = new DesktopWindowBounds { X = 100, Y = 0, Width = 800, Height = 600 };
        Assert.Equal(-272, bounds.Expand(DesktopWindowEdge.Start, 372).X);
        Assert.Equal(100, bounds.Expand(DesktopWindowEdge.End, 372).X);
        Assert.Equal(-272, bounds.Expand(DesktopWindowEdge.Start, 372, DesktopLayoutDirection.LeftToRight).X);
        Assert.Equal(100, bounds.Expand(DesktopWindowEdge.Start, 372, DesktopLayoutDirection.RightToLeft).X);
        Assert.Equal(-272, bounds.Expand(DesktopWindowEdge.End, 372, DesktopLayoutDirection.RightToLeft).X);
        Assert.Equal(1172, bounds.Expand(DesktopWindowEdge.End, 372, DesktopLayoutDirection.RightToLeft).Width);
    }

    [Test]
    public void ExpandRefusesWhatItCannotDo()
    {
        // Wayland: no position, so the left edge cannot move; the right edge can.
        var wayland = new DesktopWindowBounds { Width = 800, Height = 600 };
        Assert.Throws<InvalidOperationException>(() => wayland.Expand(DesktopWindowEdge.Left, 10));
        Assert.Equal(new DesktopWindowBounds { Width = 810, Height = 600 }, wayland.Expand(DesktopWindowEdge.Right, 10));

        var bounds = new DesktopWindowBounds { X = 0, Y = 0, Width = 100, Height = 100, Scale = 2 };
        Assert.Throws<ArgumentOutOfRangeException>(() => bounds.Expand(DesktopWindowEdge.Right, -50));
        Assert.Equal(2, bounds.Expand(DesktopWindowEdge.Right, -49).Width);
        Assert.Throws<ArgumentOutOfRangeException>(() => bounds.Expand(DesktopWindowEdge.Right, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => bounds.Expand(DesktopWindowEdge.Right, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => bounds.Expand((DesktopWindowEdge)42, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => bounds.Expand(DesktopWindowEdge.Right, 1, (DesktopLayoutDirection)42));
        Assert.Throws<InvalidOperationException>(() => (bounds with { Scale = 0 }).Expand(DesktopWindowEdge.Right, 1));
        Assert.Throws<InvalidOperationException>(() => (bounds with { Scale = double.NaN }).Expand(DesktopWindowEdge.Right, 1));
    }

    [Test]
    public void HalvesAndQuartersOfTheWorkAreaLookTiled()
    {
        // A 1920 × 1040 work area below a 40 pixel top bar, as on GNOME or macOS.
        var work = new ScreenRect(0, 40, 1920, 1040);
        Assert.True(WindowBoundsSupport.LooksTiled(new ScreenRect(0, 40, 960, 1040), work, 0, 0));
        Assert.True(WindowBoundsSupport.LooksTiled(new ScreenRect(960, 40, 960, 1040), work, 0, 0));
        Assert.True(WindowBoundsSupport.LooksTiled(new ScreenRect(0, 40, 1920, 520), work, 0, 0));
        Assert.True(WindowBoundsSupport.LooksTiled(new ScreenRect(960, 560, 960, 520), work, 0, 0));
        Assert.True(WindowBoundsSupport.LooksTiled(new ScreenRect(0, 560, 960, 520), work, 0, 0));
        // An odd work area width: the right half starts at the rounded-down middle.
        Assert.True(WindowBoundsSupport.LooksTiled(new ScreenRect(683, 0, 683, 768), new ScreenRect(0, 0, 1366, 768), 0, 0));

        // Not tiled: a window of its own size, a maximized-sized one, and one a few pixels off without tolerance.
        Assert.False(WindowBoundsSupport.LooksTiled(new ScreenRect(200, 140, 860, 640), work, 0, 0));
        Assert.False(WindowBoundsSupport.LooksTiled(work, work, 0, 0));
        Assert.False(WindowBoundsSupport.LooksTiled(new ScreenRect(2, 40, 960, 1040), work, 0, 0));
        Assert.True(WindowBoundsSupport.LooksTiled(new ScreenRect(1, 40, 960, 1040), work, 1, 0));
        Assert.False(WindowBoundsSupport.LooksTiled(new ScreenRect(0, 40, 0, 1040), work, 0, 0));
    }

    [Test]
    public void TiledWindowsWithMacOsMarginsLookTiled()
    {
        // macOS 15 with "Tiled windows have margins": 8 points around the tile and between the tiles.
        var visible = new ScreenRect(0, 0, 1512, 944);
        Assert.True(WindowBoundsSupport.LooksTiled(new ScreenRect(8, 8, 748, 928), visible, 1, 16));
        Assert.True(WindowBoundsSupport.LooksTiled(new ScreenRect(760, 8, 744, 928), visible, 1, 16));
        Assert.False(WindowBoundsSupport.LooksTiled(new ScreenRect(8, 8, 748, 928), visible, 0, 0));
        // Far inside the half is a window of its own.
        Assert.False(WindowBoundsSupport.LooksTiled(new ScreenRect(60, 60, 600, 800), visible, 1, 16));
    }

    [Test]
    public void WindowsFallbackComparesTheRestoredRectangleInScreenCoordinates()
    {
        // A taskbar on top: workspace coordinates start 48 pixels lower than screen coordinates.
        var window = new ScreenRect(100, 148, 800, 600);
        Assert.False(WindowBoundsSupport.DiffersFromRestored(window, new ScreenRect(100, 100, 800, 600), 0, 48));
        // Snap arranged the window but kept its restored bounds.
        Assert.True(WindowBoundsSupport.DiffersFromRestored(new ScreenRect(0, 48, 960, 1032), new ScreenRect(100, 100, 800, 600), 0, 48));
    }

    [Test]
    public void TheWorkAreaThatHoldsMostOfTheFrameIsChosen()
    {
        ScreenRect[] areas = [new(0, 0, 1920, 1040), new(1920, 0, 2560, 1400)];
        Assert.Equal(areas[1], WindowBoundsSupport.WorkAreaOf(new ScreenRect(1800, 0, 1000, 800), areas));
        Assert.Equal(areas[0], WindowBoundsSupport.WorkAreaOf(new ScreenRect(1000, 0, 1000, 800), areas));
        Assert.Null(WindowBoundsSupport.WorkAreaOf(new ScreenRect(-5000, 0, 100, 100), areas));
    }

    [Test]
    public async Task SetBoundsAndMinimumSizeReachACustomHost()
    {
        var factory = new BoundsFactory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var native = factory.Hosts[0];
        Assert.True(window.Capabilities.HasFlag(DesktopWindowCapabilities.Bounds | DesktopWindowCapabilities.MinimumSize));

        var bounds = new DesktopWindowBounds { X = -158, Y = 200, Width = 1559, Height = 900, Scale = 1.5 };
        await window.SetBoundsAsync(bounds);
        await window.SetBoundsAsync(bounds with { X = null, Y = null });
        await window.SetMinimumSizeAsync(1232, 640);
        await window.SetMinimumSizeAsync(0, 0);
        Assert.Equal(["bounds:-158,200,1559,900", "bounds:,,1559,900", "minimum:1232x640", "minimum:0x0"], native.Calls);
        Assert.Equal(bounds, window.State!.Bounds);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await window.SetBoundsAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(async () => await window.SetBoundsAsync(bounds with { Width = 0 }));
        await Assert.ThrowsAsync<ArgumentException>(async () => await window.SetBoundsAsync(bounds with { Height = -1 }));
        await Assert.ThrowsAsync<ArgumentException>(async () => await window.SetBoundsAsync(bounds with { Y = null }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await window.SetMinimumSizeAsync(-1, 10));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await window.SetMinimumSizeAsync(10, -1));
        Assert.Equal(4, native.Calls.Count);

        // The frame of a maximized or full-screen window is the desktop's.
        native.Report(native.State! with { IsMaximized = true });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await window.SetBoundsAsync(bounds));
        native.Report(native.State! with { IsMaximized = false, IsFullScreen = true });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await window.SetBoundsAsync(bounds));
        Assert.Equal(4, native.Calls.Count);
    }

    [Test]
    public async Task AHostWithoutTheCapabilityRefusesWithItsReason()
    {
        var factory = new BoundsFactory { Capabilities = DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.State };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });

        var error = await Assert.ThrowsAsync<DesktopException>(async () =>
            await window.SetBoundsAsync(new DesktopWindowBounds { X = 0, Y = 0, Width = 800, Height = 600 }));
        Assert.Equal("window-capability-unavailable", error.Code);
        Assert.Equal(DesktopErrorCategory.Unavailable, error.Category);
        Assert.Contains("DesktopWindowCapabilities.Bounds", error.Message);
        Assert.Contains("custom window host", error.Message);
        error = await Assert.ThrowsAsync<DesktopException>(async () => await window.SetMinimumSizeAsync(400, 300));
        Assert.Equal("window-capability-unavailable", error.Code);
        Assert.Contains("DesktopWindowCapabilities.MinimumSize", error.Message);
        Assert.Empty(factory.Hosts[0].Calls);
    }

    [Test]
    public async Task APositionTheHostCannotSetFailsWithItsCode()
    {
        var factory = new BoundsFactory { RefusePosition = true };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });

        var error = await Assert.ThrowsAsync<DesktopException>(async () =>
            await window.SetBoundsAsync(new DesktopWindowBounds { X = 0, Y = 0, Width = 800, Height = 600 }));
        Assert.Equal("window-position-unavailable", error.Code);
        await window.SetBoundsAsync(new DesktopWindowBounds { Width = 800, Height = 600 });
        Assert.Equal(["bounds:,,800,600"], factory.Hosts[0].Calls);
    }

    private sealed class BoundsFactory : IDesktopWindowHostFactory
    {
        internal List<BoundsHost> Hosts { get; } = [];
        internal DesktopWindowCapabilities Capabilities { get; init; } = DesktopWindowCapabilities.NativeHandle |
            DesktopWindowCapabilities.State | DesktopWindowCapabilities.Bounds | DesktopWindowCapabilities.MinimumSize;
        internal bool RefusePosition { get; init; }
        public bool IsSupported => true;
        DesktopWindowCapabilities IDesktopWindowHostFactory.Capabilities => Capabilities;
        public IDesktopWindowHost Create()
        {
            var host = new BoundsHost(Capabilities, RefusePosition);
            Hosts.Add(host);
            return host;
        }
    }

    // A custom host as an application would write one: native units of its own, one call per bounds change.
    private sealed class BoundsHost(DesktopWindowCapabilities capabilities, bool refusePosition) : IDesktopWindowBoundsHost, IDesktopWindowStateHost
    {
        public event EventHandler? Closed;
        public event EventHandler? StateChanged;
        public DesktopWindowCapabilities Capabilities => capabilities;
        public bool IsOpen { get; private set; }
        public nint NativeHandle => IsOpen ? 1 : 0;
        public DesktopWindowState? State { get; private set; }
        internal List<string> Calls { get; } = [];

        internal void Report(DesktopWindowState state)
        {
            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask SetBoundsAsync(DesktopWindowBounds bounds, CancellationToken cancellationToken = default)
        {
            if (refusePosition && bounds.X is not null) return ValueTask.FromException(WindowBoundsSupport.PositionUnavailable());
            Calls.Add($"bounds:{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}");
            Report(State! with { Bounds = bounds with { X = bounds.X ?? State.Bounds?.X, Y = bounds.Y ?? State.Bounds?.Y } });
            return ValueTask.CompletedTask;
        }

        public ValueTask SetMinimumSizeAsync(int width, int height, CancellationToken cancellationToken = default)
        {
            Calls.Add($"minimum:{width}x{height}");
            return ValueTask.CompletedTask;
        }

        public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
        {
            IsOpen = true;
            Report(new DesktopWindowState
            {
                Width = 800, Height = 600, Bounds = new DesktopWindowBounds { X = 0, Y = 0, Width = 1200, Height = 900, Scale = 1.5 },
            });
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
}
