using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// sdk#864, P2: the side panel's geometry (SidePanelPlanner) for every platform, and DesktopWindow's side panel API over a
// built-in host of the test's own and over the presentations that refuse it.
public sealed class SidePanelTests
{
    private static readonly double[] Scales = [1, 1.25, 1.5, 1.75, 2];

    // A 1920 × 1080 work area at the origin, scaled; a 860 × 640 window (in device-independent pixels) in its middle.
    private static SidePanelWindow Window(double scale = 1, int x = 500, bool maximized = false, bool fullScreen = false,
        bool snapped = false, bool minimized = false, bool position = true, bool workArea = true)
    {
        var bounds = new DesktopWindowBounds
        {
            X = position ? (int)(x * scale) : null, Y = position ? (int)(200 * scale) : null,
            Width = (int)Math.Round(860 * scale), Height = (int)Math.Round(640 * scale), Scale = scale,
        };
        // Windows 10 and 11: about 7 device-independent pixels of invisible border left, right and below.
        var border = (int)Math.Round(7 * scale);
        var visible = position
            ? bounds with { X = bounds.X + border, Width = bounds.Width - 2 * border, Height = bounds.Height - border }
            : null;
        ScreenRect? work = workArea ? new ScreenRect(0, 0, (int)(1920 * scale), (int)(1040 * scale)) : null;
        return new SidePanelWindow(bounds, visible, work, maximized, fullScreen, snapped, minimized);
    }

    [Test]
    public void OpeningOnTheRightWidensAndOnTheLeftMovesTheLeftEdgeAtEveryScale()
    {
        foreach (var scale in Scales)
        {
            var window = Window(scale);
            var native = (int)Math.Round(372 * scale, MidpointRounding.AwayFromZero);

            var right = SidePanelPlanner.Open(window, left: false, 372);
            Assert.Null(right.Refusal);
            Assert.Equal(native, right.NativeWidth);
            Assert.Equal(window.Bounds with { Width = window.Bounds.Width + native }, right.Target);

            var left = SidePanelPlanner.Open(window, left: true, 372);
            Assert.Null(left.Refusal);
            Assert.Equal(window.Bounds with { X = window.Bounds.X - native, Width = window.Bounds.Width + native }, left.Target);
            // The right edge, and with it the page on the screen, stays.
            Assert.Equal(window.Bounds.X + window.Bounds.Width, left.Target!.X + left.Target.Width);
        }
    }

    [Test]
    public void At150PercentA1001PixelWindowOpensTo1559AndClosesBackTo1001()
    {
        // The issue's case: rounding through device-independent pixels would give 1000 or 1558.
        var window = Window(1.5) with { Bounds = new DesktopWindowBounds { X = 1000, Y = 200, Width = 1001, Height = 900, Scale = 1.5 } };
        window = window with { Visible = null };
        var plan = SidePanelPlanner.Open(window, left: true, 372);
        Assert.Equal(new DesktopWindowBounds { X = 442, Y = 200, Width = 1559, Height = 900, Scale = 1.5 }, plan.Target);
        var closed = SidePanelPlanner.Close(plan.Target!, plan.Target!, window.Bounds, left: true, plan.NativeWidth);
        Assert.Equal(window.Bounds, closed);
    }

    [Test]
    public void ClosingRestoresTheExactBoundsOrMovesTheDockedEdgeBackAfterAUserChange()
    {
        foreach (var scale in Scales)
        {
            foreach (var left in (bool[])[false, true])
            {
                var window = Window(scale);
                var plan = SidePanelPlanner.Open(window, left, 300);
                var opened = plan.Target!;
                Assert.Equal(window.Bounds, SidePanelPlanner.Close(opened, opened, window.Bounds, left, plan.NativeWidth));

                // The user moved and widened the window meanwhile: only the docked edge moves back.
                var moved = opened with { X = opened.X + 40, Y = opened.Y + 10, Width = opened.Width + 25 };
                var closed = SidePanelPlanner.Close(moved, opened, window.Bounds, left, plan.NativeWidth);
                Assert.Equal(moved.Width - plan.NativeWidth, closed.Width);
                Assert.Equal(moved.Y, closed.Y);
                Assert.Equal(left ? moved.X + plan.NativeWidth : moved.X, closed.X);
                Assert.Equal(moved.X + moved.Width, left ? closed.X + closed.Width : moved.X + moved.Width);
            }
        }
    }

    [Test]
    public void TheWindowStateRefusesInOrder()
    {
        Assert.Equal(DesktopSidePanelRefusal.Minimized, SidePanelPlanner.Open(Window(minimized: true, maximized: true), false, 300).Refusal);
        Assert.Equal(DesktopSidePanelRefusal.FullScreen, SidePanelPlanner.Open(Window(fullScreen: true, maximized: true), false, 300).Refusal);
        Assert.Equal(DesktopSidePanelRefusal.Maximized, SidePanelPlanner.Open(Window(maximized: true, snapped: true), false, 300).Refusal);
        Assert.Equal(DesktopSidePanelRefusal.Snapped, SidePanelPlanner.Open(Window(snapped: true), false, 300).Refusal);
        // The same refusals hold for moving the panel and changing its width.
        Assert.Equal(DesktopSidePanelRefusal.Maximized, SidePanelPlanner.Move(Window(maximized: true), true, 300).Refusal);
        Assert.Equal(DesktopSidePanelRefusal.Snapped, SidePanelPlanner.Resize(Window(snapped: true), false, 300, 400).Refusal);
    }

    [Test]
    public void WithoutPositionsOnlyTheRightEdgeOpens()
    {
        // Wayland (D062.4): no position, so the left edge, which moves the window, is refused; no work area to check.
        var window = Window(position: false, workArea: false);
        Assert.Equal(DesktopSidePanelRefusal.PositionUnavailable, SidePanelPlanner.Open(window, left: true, 300).Refusal);
        var right = SidePanelPlanner.Open(window, left: false, 300);
        Assert.Null(right.Refusal);
        Assert.Null(right.Target!.X);
        Assert.Equal(1160, right.Target.Width);
        Assert.Equal(DesktopSidePanelRefusal.PositionUnavailable, SidePanelPlanner.Move(window, true, 300).Refusal);
        Assert.Equal(DesktopSidePanelRefusal.PositionUnavailable, SidePanelPlanner.Resize(window, true, 300, 320).Refusal);
        Assert.Null(SidePanelPlanner.Resize(window, false, 300, 320).Refusal);
    }

    [Test]
    public void TheGrownVisibleFrameMustStayInTheWorkArea()
    {
        // D062.3: never moved to make room. The visible frame, not the invisible borders, has to fit.
        foreach (var scale in Scales)
        {
            // 500 + 860 + 372 = 1732 fits on the right; the left edge would go to 128.
            Assert.Null(SidePanelPlanner.Open(Window(scale), false, 372).Refusal);
            Assert.Null(SidePanelPlanner.Open(Window(scale), true, 372).Refusal);
            // At x 100 the left edge has no room; at x 800 the right edge has none.
            Assert.Equal(DesktopSidePanelRefusal.NoRoom, SidePanelPlanner.Open(Window(scale, x: 100), true, 372).Refusal);
            Assert.Null(SidePanelPlanner.Open(Window(scale, x: 100), false, 372).Refusal);
            Assert.Equal(DesktopSidePanelRefusal.NoRoom, SidePanelPlanner.Open(Window(scale, x: 800), false, 372).Refusal);
            // More than half the work area is refused wherever the window is.
            Assert.Equal(DesktopSidePanelRefusal.NoRoom, SidePanelPlanner.Open(Window(scale, x: 0), false, 961).Refusal);
        }
        // The invisible border may leave the work area: the visible frame touches its edge exactly.
        var flush = Window(1, x: 1920 - 860 - 372 + 7);
        Assert.Null(SidePanelPlanner.Open(flush, false, 372).Refusal);
        Assert.Equal(DesktopSidePanelRefusal.NoRoom, SidePanelPlanner.Open(flush, false, 373).Refusal);
    }

    [Test]
    public void MovingToTheOtherEdgeShiftsTheWindowByThePanelAndKeepsItsSize()
    {
        var window = Window(1.5);
        var open = SidePanelPlanner.Open(window, false, 300);
        var opened = window with { Bounds = open.Target!, Visible = null };
        var moved = SidePanelPlanner.Move(opened, toLeft: true, open.NativeWidth);
        Assert.Null(moved.Refusal);
        Assert.Equal(opened.Bounds with { X = opened.Bounds.X - 450 }, moved.Target);
        var back = SidePanelPlanner.Move(opened with { Bounds = moved.Target! }, toLeft: false, open.NativeWidth);
        Assert.Equal(opened.Bounds, back.Target);
        // Closing after the move gives the bounds before the open: the page never moved.
        Assert.Equal(window.Bounds, SidePanelPlanner.Close(moved.Target!, moved.Target!, window.Bounds, true, open.NativeWidth));
        Assert.Equal(DesktopSidePanelRefusal.NoRoom,
            SidePanelPlanner.Move(opened with { Bounds = opened.Bounds with { X = 100 } }, toLeft: true, open.NativeWidth).Refusal);
    }

    [Test]
    public void ChangingTheWidthMovesTheDockedEdgeByTheDifference()
    {
        var window = Window(1.25);
        foreach (var left in (bool[])[false, true])
        {
            var open = SidePanelPlanner.Open(window, left, 300);
            var opened = window with { Bounds = open.Target!, Visible = null };
            var wider = SidePanelPlanner.Resize(opened, left, open.NativeWidth, 340);
            Assert.Null(wider.Refusal);
            Assert.Equal(425, wider.NativeWidth);
            Assert.Equal(opened.Bounds.Width + 50, wider.Target!.Width);
            Assert.Equal(left ? opened.Bounds.X - 50 : opened.Bounds.X, wider.Target.X);
            // The page's edge stays: closing from the new width gives the bounds before the open.
            Assert.Equal(window.Bounds, SidePanelPlanner.Close(wider.Target, wider.Target, window.Bounds, left, wider.NativeWidth));
            var narrower = SidePanelPlanner.Resize(opened, left, open.NativeWidth, 150);
            Assert.Equal(opened.Bounds.Width - 375 + 188, narrower.Target!.Width);
        }
        Assert.Equal(DesktopSidePanelRefusal.NoRoom, SidePanelPlanner.Resize(Window(x: 1000), false, 300, 400).Refusal);
        // A narrower panel always fits.
        Assert.Null(SidePanelPlanner.Resize(Window(x: 1100), false, 300, 200).Refusal);
    }

    [Test]
    public void ThePlacementExcludesThePanelAndAMaximizedWindowRestoresWithoutIt()
    {
        var bounds = new DesktopWindowBounds { X = 100, Y = 50, Width = 1559, Height = 900, Scale = 1.5 };
        Assert.Equal(bounds with { Width = 1001 }, SidePanelPlanner.ExcludePanel(bounds, left: false, 558));
        Assert.Equal(bounds with { X = 658, Width = 1001 }, SidePanelPlanner.ExcludePanel(bounds, left: true, 558));
        Assert.Equal(new ScreenRect(100, 50, 1001, 900), SidePanelPlanner.ShrinkRestored(new ScreenRect(100, 50, 1559, 900), false, 558));
        Assert.Equal(new ScreenRect(658, 50, 1001, 900), SidePanelPlanner.ShrinkRestored(new ScreenRect(100, 50, 1559, 900), true, 558));
    }

    [Test]
    public void ThePanelKeepsItsWidthAndThePageTakesTheRest()
    {
        // Maximized while open (D062.5): the panel keeps its width, the page takes the rest.
        Assert.Equal((0, 2000, 2000, 558), SidePanelPlanner.Layout(2558, left: false, 558));
        Assert.Equal((558, 2000, 0, 558), SidePanelPlanner.Layout(2558, left: true, 558));
        // A client area narrower than the panel gives the page nothing, never a negative width.
        Assert.Equal((0, 0, 0, 300), SidePanelPlanner.Layout(300, left: false, 558));
        Assert.Equal((0, 0, 0, 0), SidePanelPlanner.Layout(-5, left: true, 558));
    }

    [Test]
    public void StartAndEndFollowTheLayoutDirection()
    {
        Assert.False(DesktopWindowBounds.ResolvesToLeft(DesktopWindowEdge.End, DesktopLayoutDirection.Default));
        Assert.False(DesktopWindowBounds.ResolvesToLeft(DesktopWindowEdge.End, DesktopLayoutDirection.LeftToRight));
        Assert.True(DesktopWindowBounds.ResolvesToLeft(DesktopWindowEdge.End, DesktopLayoutDirection.RightToLeft));
        Assert.True(DesktopWindowBounds.ResolvesToLeft(DesktopWindowEdge.Start, DesktopLayoutDirection.Default));
        Assert.False(DesktopWindowBounds.ResolvesToLeft(DesktopWindowEdge.Start, DesktopLayoutDirection.RightToLeft));
        Assert.Equal(DesktopWindowEdge.End, new DesktopSidePanelOptions { Width = 300, Path = "panel" }.Edge);
    }

    [Test]
    public async Task ADesktopWindowOpensThePanelThroughItsHostWithThePanelCredential()
    {
        var factory = new PanelHostFactory();
        await using var host = await DesktopHost.StartAsync(new() { BuiltInHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<p>page</p>") });
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var native = factory.Hosts.Single();

        Assert.Null(window.CheckSidePanel(DesktopWindowEdge.End, 300));
        var result = await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Width = 300, Path = "/side-panel?tab=log" });
        var panel = Assert.IsType<DesktopSidePanelResult.Opened>(result).Panel;
        Assert.Same(panel, window.SidePanel);
        Assert.True(panel.IsOpen);
        Assert.Equal(DesktopWindowEdge.Right, panel.Edge);
        Assert.Equal(300, panel.Width);

        var request = native.Request!;
        Assert.False(request.Left);
        Assert.Equal(300, request.Width);
        Assert.Equal(new Uri(surface.Url, "side-panel?tab=log"), request.Url);
        // The panel's view gets the panel credential, not the page's session credential.
        var credential = surface.SidePanelCredential;
        Assert.NotNull(credential);
        Assert.Contains(credential!, request.DocumentStartScript);
        Assert.Equal(DesktopSidePanelRefusal.AlreadyOpen, window.CheckSidePanel(DesktopWindowEdge.Left, 300));
        Assert.Equal(DesktopSidePanelRefusal.AlreadyOpen,
            Assert.IsType<DesktopSidePanelResult.Refused>(await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Width = 300, Path = "x" })).Reason);

        // Start and End follow the window's direction; moving to the edge it is on does nothing.
        Assert.Null(await panel.MoveToEdgeAsync(DesktopWindowEdge.End));
        Assert.Null(await panel.MoveToEdgeAsync(DesktopWindowEdge.Start));
        Assert.Equal(DesktopWindowEdge.Left, panel.Edge);
        Assert.Null(await panel.SetWidthAsync(320));
        Assert.Equal(320, panel.Width);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await panel.SetWidthAsync(119));

        var closed = new TaskCompletionSource<DesktopSidePanelCloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        panel.Closed += (_, args) => closed.TrySetResult(args.Reason);
        await panel.CloseAsync();
        Assert.False(panel.IsOpen);
        Assert.Null(window.SidePanel);
        Assert.Null(surface.SidePanelCredential);
        Assert.Equal(DesktopSidePanelCloseReason.Closed, await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(["open:right", "move:left", "width:320", "close"], native.Calls);
        await panel.CloseAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await panel.FocusAsync());
    }

    [Test]
    public async Task TheHostsRefusalLeavesNoPanelAndRevokesTheSlot()
    {
        var factory = new PanelHostFactory { Refusal = DesktopSidePanelRefusal.LoadFailed };
        await using var host = await DesktopHost.StartAsync(new() { BuiltInHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<p>page</p>") });
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });

        var result = await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Width = 300, Path = "panel" });
        Assert.Equal(DesktopSidePanelRefusal.LoadFailed, Assert.IsType<DesktopSidePanelResult.Refused>(result).Reason);
        Assert.Null(window.SidePanel);
        Assert.Null(surface.SidePanelCredential);

        // The window's state refuses before the host is asked.
        factory.Hosts.Single().Report(factory.Hosts.Single().State! with { IsMaximized = true });
        Assert.Equal(DesktopSidePanelRefusal.Maximized, window.CheckSidePanel(DesktopWindowEdge.End, 300));
        Assert.Equal(DesktopSidePanelRefusal.Maximized,
            Assert.IsType<DesktopSidePanelResult.Refused>(await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Width = 300, Path = "panel" })).Reason);
        Assert.Single(factory.Hosts.Single().Calls);
    }

    [Test]
    public async Task ClosingTheWindowClosesThePanel()
    {
        var factory = new PanelHostFactory();
        await using var host = await DesktopHost.StartAsync(new() { BuiltInHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<p>page</p>") });
        var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var panel = Assert.IsType<DesktopSidePanelResult.Opened>(
            await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Width = 300, Path = "panel" })).Panel;
        var closed = new TaskCompletionSource<DesktopSidePanelCloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        panel.Closed += (_, args) => closed.TrySetResult(args.Reason);

        await window.CloseAsync();
        Assert.Equal(DesktopSidePanelCloseReason.WindowClosed, await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(panel.IsOpen);
        Assert.Null(window.SidePanel);
        Assert.Null(surface.SidePanelCredential);
    }

    [Test]
    public async Task ArgumentsAreCheckedAndPathsStayInsideTheSurface()
    {
        var factory = new PanelHostFactory();
        await using var host = await DesktopHost.StartAsync(new() { BuiltInHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<p>page</p>") });
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await window.OpenSidePanelAsync(null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await window.OpenSidePanelAsync(new() { Width = 119, Path = "p" }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await window.OpenSidePanelAsync(new() { Width = 300, Path = "p", Edge = (DesktopWindowEdge)9 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await window.OpenSidePanelAsync(new() { Width = 300, Path = "p", LoadTimeout = TimeSpan.Zero }));
        foreach (var path in (string[])[" ", "https://example.com/", "//example.com/x", "../escape", "a/../../b", "%2e%2e/x", "javascript:alert(1)", "a\\b"])
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await window.OpenSidePanelAsync(new() { Width = 300, Path = path }));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => window.CheckSidePanel(DesktopWindowEdge.Left, 100));
        Assert.Empty(factory.Hosts.Single().Calls);

        // A hash route keeps the page; an empty path is the page itself.
        await AssertUrlAsync("#/side-panel", new Uri(surface.Url, "#/side-panel"));
        await AssertUrlAsync("/", surface.Url);

        async Task AssertUrlAsync(string path, Uri expected)
        {
            var panel = Assert.IsType<DesktopSidePanelResult.Opened>(await window.OpenSidePanelAsync(new() { Width = 300, Path = path })).Panel;
            Assert.Equal(expected, factory.Hosts.Single().Request!.Url);
            await panel.CloseAsync();
        }
    }

    [Test]
    public async Task PresentationsWithoutASidePanelRefuseItAsUnsupported()
    {
        // A custom window host (D062.8), as WPF's RunicWebView is one.
        await using (var host = await DesktopHost.StartAsync(new() { WindowHostFactory = new PlainFactory(), WaitForConnection = false }))
        {
            await using var surface = await host.CreateSurfaceAsync();
            await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
            Assert.Equal(DesktopSidePanelRefusal.Unsupported, window.CheckSidePanel(DesktopWindowEdge.End, 300));
            Assert.Equal(DesktopSidePanelRefusal.Unsupported,
                Assert.IsType<DesktopSidePanelResult.Refused>(await window.OpenSidePanelAsync(new() { Width = 300, Path = "panel" })).Reason);
            Assert.Null(surface.SidePanelCredential);
        }

        // A built-in host without a side panel (macOS and Linux until P3 and P4).
        var factory = new PanelHostFactory { SupportsSidePanel = false };
        await using (var host = await DesktopHost.StartAsync(new() { BuiltInHostFactory = factory, WaitForConnection = false }))
        {
            await using var surface = await host.CreateSurfaceAsync();
            await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
            Assert.Equal(DesktopSidePanelRefusal.Unsupported, window.CheckSidePanel(DesktopWindowEdge.End, 300));
            Assert.Equal(DesktopSidePanelRefusal.Unsupported,
                Assert.IsType<DesktopSidePanelResult.Refused>(await window.OpenSidePanelAsync(new() { Width = 300, Path = "panel" })).Reason);
        }

        // A surface that admits several clients has no panel slot.
        factory = new PanelHostFactory();
        await using (var host = await DesktopHost.StartAsync(new() { BuiltInHostFactory = factory, WaitForConnection = false }))
        {
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Security = DesktopSecurityPolicy.Default with { ClientAdmission = DesktopClientAdmission.Multiple },
            });
            await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
            Assert.Equal(DesktopSidePanelRefusal.Unsupported, window.CheckSidePanel(DesktopWindowEdge.End, 300));
        }
    }

    private sealed class PanelHostFactory : IWebUiEmbeddedHostFactory
    {
        internal List<PanelHost> Hosts { get; } = [];
        internal DesktopSidePanelRefusal? Refusal { get; init; }
        internal bool SupportsSidePanel { get; init; } = true;
        public bool IsSupported => true;

        public IWebUiEmbeddedHost Create()
        {
            var host = new PanelHost(Refusal, SupportsSidePanel);
            Hosts.Add(host);
            return host;
        }
    }

    // A built-in host as the WebView2 one behaves: it plans with SidePanelPlanner and reports every close.
    private sealed class PanelHost(DesktopSidePanelRefusal? refusal, bool supportsSidePanel) : IWebUiEmbeddedHost
    {
        private Panel? _panel;
        public event EventHandler? Closed;
        public event EventHandler? StateChanged;
        public bool IsOpen { get; private set; }
        public nint NativeHandle => IsOpen ? 7 : 0;
        public DesktopWindowCapabilities Capabilities => DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.State;
        public DesktopWindowState? State { get; private set; }
        public bool SupportsDocumentStartScript => true;
        public bool SupportsSidePanel => supportsSidePanel;
        internal List<string> Calls { get; } = [];
        internal WebUiSidePanelRequest? Request { get; private set; }

        internal void Report(DesktopWindowState state)
        {
            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private SidePanelWindow Window() => new(State!.Bounds!, null, new ScreenRect(0, 0, 1920, 1040),
            State.IsMaximized, State.IsFullScreen, State.IsSnapped, IsMinimized: false);

        public DesktopSidePanelRefusal? CheckSidePanel(bool left, int width) =>
            _panel is not null ? DesktopSidePanelRefusal.AlreadyOpen : SidePanelPlanner.Open(Window(), left, width).Refusal;

        public ValueTask<WebUiSidePanelOpening> OpenSidePanelAsync(WebUiSidePanelRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            Calls.Add("open:" + (request.Left ? "left" : "right"));
            if (refusal is { } refused) return ValueTask.FromResult(WebUiSidePanelOpening.Refused(refused));
            _panel = new Panel(this, request);
            return ValueTask.FromResult(new WebUiSidePanelOpening(_panel, null));
        }

        public ValueTask ShowAsync(Uri url, WebUiEmbeddedHostOptions options, CancellationToken cancellationToken = default)
        {
            IsOpen = true;
            Report(new DesktopWindowState
            {
                Width = 860, Height = 640, Bounds = new DesktopWindowBounds { X = 500, Y = 200, Width = 860, Height = 640 },
            });
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            if (!IsOpen) return ValueTask.CompletedTask;
            IsOpen = false;
            if (_panel is { } panel)
            {
                _panel = null;
                panel.Request.Closed(DesktopSidePanelCloseReason.WindowClosed);
            }
            Closed?.Invoke(this, EventArgs.Empty);
            return ValueTask.CompletedTask;
        }

        public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MaximizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetSizeAsync(uint width, uint height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetPositionAsync(int x, int y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => CloseAsync();

        private sealed class Panel(PanelHost host, WebUiSidePanelRequest request) : IWebUiSidePanel
        {
            internal WebUiSidePanelRequest Request => request;
            public bool IsLeft { get; private set; } = request.Left;
            public int Width { get; private set; } = request.Width;

            public ValueTask<DesktopSidePanelRefusal?> MoveAsync(bool left, CancellationToken cancellationToken)
            {
                host.Calls.Add("move:" + (left ? "left" : "right"));
                IsLeft = left;
                return ValueTask.FromResult<DesktopSidePanelRefusal?>(null);
            }

            public ValueTask<DesktopSidePanelRefusal?> SetWidthAsync(int width, CancellationToken cancellationToken)
            {
                host.Calls.Add("width:" + width);
                Width = width;
                return ValueTask.FromResult<DesktopSidePanelRefusal?>(null);
            }

            public ValueTask FocusAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

            public ValueTask CloseAsync(CancellationToken cancellationToken)
            {
                host.Calls.Add("close");
                host._panel = null;
                request.Closed(DesktopSidePanelCloseReason.Closed);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class PlainFactory : IDesktopWindowHostFactory
    {
        public bool IsSupported => true;
        public IDesktopWindowHost Create() => new PlainHost();
    }

    private sealed class PlainHost : IDesktopWindowHost
    {
        public event EventHandler? Closed;
        public bool IsOpen { get; private set; }
        public nint NativeHandle => IsOpen ? 1 : 0;
        public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
        {
            IsOpen = true;
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
