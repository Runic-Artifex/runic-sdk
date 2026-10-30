using System.Text.Json;
using Runic.Desktop.Internal;
using TUnit.Core;

namespace Runic.Desktop.Tests;

public sealed class WindowPlacementClampTests
{
    // A laptop screen with a taskbar, and an external monitor to its right.
    private static readonly ScreenRect Laptop = new(0, 0, 1920, 1040);
    private static readonly ScreenRect External = new(1920, 0, 2560, 1400);

    [Test]
    public void WindowInsideAWorkAreaStaysWhereItIs()
    {
        var window = new ScreenRect(100, 80, 800, 600);
        Assert.Equal(window, WindowPlacementClamp.Clamp(window, [Laptop, External]));
        var onExternal = new ScreenRect(2200, 300, 1200, 900);
        Assert.Equal(onExternal, WindowPlacementClamp.Clamp(onExternal, [Laptop, External]));
    }

    [Test]
    public void WindowSavedOnADisconnectedMonitorMovesToTheNearestRemainingOne()
    {
        // Saved on the external monitor, which is no longer connected.
        var saved = new ScreenRect(2200, 300, 1200, 900);
        var restored = WindowPlacementClamp.Clamp(saved, [Laptop]);
        Assert.Equal(new ScreenRect(720, 140, 1200, 900), restored);
        AssertInside(restored, Laptop);
    }

    [Test]
    public void LargeWindowFromADisconnectedMonitorShrinksToFit()
    {
        var saved = new ScreenRect(1920, 0, 2560, 1400);
        var restored = WindowPlacementClamp.Clamp(saved, [Laptop]);
        Assert.Equal(Laptop, restored);
    }

    [Test]
    public void FarOffScreenPositionMovesOnScreen()
    {
        var restored = WindowPlacementClamp.Clamp(new ScreenRect(-100_000, 5_000, 800, 600), [Laptop, External]);
        AssertInside(restored, Laptop);
        Assert.Equal(800, restored.Width);
        Assert.Equal(600, restored.Height);
        AssertInside(WindowPlacementClamp.Clamp(new ScreenRect(int.MinValue, int.MaxValue - 10, int.MaxValue, int.MaxValue), [Laptop]), Laptop);
    }

    [Test]
    public void SpanningWindowMovesIntoTheWorkAreaItOverlapsMost()
    {
        // Mostly on the external monitor, partly on the laptop.
        var restored = WindowPlacementClamp.Clamp(new ScreenRect(1700, 100, 1000, 700), [Laptop, External]);
        Assert.Equal(new ScreenRect(1920, 100, 1000, 700), restored);
    }

    [Test]
    public void PartlyOffTheBottomMovesUpIntoTheWorkArea()
    {
        // Below the taskbar of the laptop.
        var restored = WindowPlacementClamp.Clamp(new ScreenRect(200, 900, 800, 600), [Laptop]);
        Assert.Equal(new ScreenRect(200, 440, 800, 600), restored);
    }

    [Test]
    public void MonitorsLeftOfAndAboveThePrimaryWork()
    {
        var left = new ScreenRect(-2560, -360, 2560, 1400);
        var restored = WindowPlacementClamp.Clamp(new ScreenRect(-3000, -500, 800, 600), [Laptop, left]);
        Assert.Equal(new ScreenRect(-2560, -360, 800, 600), restored);
    }

    [Test]
    public void WithoutWorkAreasTheWindowIsUnchanged()
    {
        var window = new ScreenRect(-5000, -5000, 800, 600);
        Assert.Equal(window, WindowPlacementClamp.Clamp(window, []));
        Assert.Equal(window, WindowPlacementClamp.Clamp(window, [new ScreenRect(0, 0, 0, 0)]));
    }

    [Test]
    public void SizeOnlyClampFitsTheWorkArea()
    {
        Assert.Equal((1920, 1040), WindowPlacementClamp.ClampSize(4000, 3000, Laptop));
        Assert.Equal((800, 600), WindowPlacementClamp.ClampSize(800, 600, Laptop));
        Assert.Equal((4000, 3000), WindowPlacementClamp.ClampSize(4000, 3000, null));
        Assert.Equal(External, WindowPlacementClamp.Largest([Laptop, External]));
    }

    private static void AssertInside(ScreenRect window, ScreenRect area)
    {
        Assert.True(window.X >= area.X && window.Y >= area.Y, $"{window} starts outside {area}");
        Assert.True(window.Right <= area.Right && window.Bottom <= area.Bottom, $"{window} ends outside {area}");
    }
}

public sealed class WindowStateTests
{
    private static readonly DesktopWindowState Opened = new()
    {
        X = 10, Y = 20, Width = 800, Height = 600,
        Placement = new() { X = 10, Y = 20, Width = 800, Height = 600 },
    };

    [Test]
    public async Task StateIsReadAtOpenRaisedOnChangeAndKeptAfterClose()
    {
        var factory = new StateFactory { InitialState = Opened };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        Assert.Equal(Opened, window.State);
        Assert.True(window.Capabilities.HasFlag(DesktopWindowCapabilities.State));

        var changes = new List<(DesktopWindowState State, bool PoolThread)>();
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.StateChanged += (sender, args) =>
        {
            Assert.Same(window, sender);
            lock (changes) changes.Add((args.State, Thread.CurrentThread.IsThreadPoolThread));
            raised.TrySetResult();
        };
        var maximized = Opened with { X = 0, Y = 0, Width = 1920, Height = 1040, IsMaximized = true, Placement = Opened.Placement with { Maximized = true } };
        var native = factory.Hosts[0];
        native.Report(maximized);
        await raised.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(maximized, window.State);
        lock (changes)
        {
            Assert.Equal(maximized, changes[^1].State);
            Assert.True(changes[^1].PoolThread);
        }

        await window.CloseAsync();
        Assert.False(window.IsOpen);
        // The save path: the last state stays readable after the window closed.
        Assert.Equal(maximized, window.State);
        native.Report(Opened);
        await Task.Delay(100);
        Assert.Equal(maximized, window.State);
    }

    [Test]
    public async Task RepeatedChangesAreCombinedAndHandlersNeverOverlap()
    {
        var factory = new StateFactory { InitialState = Opened };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var active = 0;
        var overlapped = false;
        var seen = new List<int>();
        var last = Opened with { Width = 1000, Placement = Opened.Placement with { Width = 1000 } };
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.StateChanged += (_, args) =>
        {
            if (Interlocked.Increment(ref active) != 1) overlapped = true;
            Thread.Sleep(20);
            lock (seen) seen.Add(args.State.Width);
            Interlocked.Decrement(ref active);
            if (args.State == last) done.TrySetResult();
        };
        var native = factory.Hosts[0];
        for (int width = 801; width < 1000; width++)
        {
            native.Report(Opened with { Width = width, Placement = Opened.Placement with { Width = width } });
        }
        native.Report(last);
        await done.Task.WaitScaledAsync(TimeSpan.FromSeconds(10));
        Assert.False(overlapped);
        lock (seen)
        {
            Assert.True(seen.Count < 200, $"{seen.Count} events for 200 changes");
            Assert.Equal(1000, seen[^1]);
            Assert.Equal(seen.OrderBy(static width => width), seen);
        }
    }

    [Test]
    public async Task ThrowingHandlerIsLoggedAndLaterHandlersAndEventsRun()
    {
        var factory = new StateFactory { InitialState = Opened };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var received = new TaskCompletionSource<DesktopWindowState>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.StateChanged += (_, _) => throw new InvalidOperationException("handler failure");
        window.StateChanged += (_, args) => { if (args.State.Width == 900) received.TrySetResult(args.State); };
        factory.Hosts[0].Report(Opened with { Width = 700 });
        factory.Hosts[0].Report(Opened with { Width = 900 });
        Assert.Equal(900, (await received.Task.WaitScaledAsync(TimeSpan.FromSeconds(5))).Width);
    }

    [Test]
    public async Task ReplacementWindowStateDoesNotReachTheClosedWindow()
    {
        var factory = new StateFactory { InitialState = Opened };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        var first = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        // A user close: the native window closes without DesktopWindow.CloseAsync.
        await factory.Hosts[0].CloseAsync();
        var replacementState = Opened with { Width = 1234 };
        factory.InitialState = replacementState;
        await using var second = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        factory.Hosts[1].Report(replacementState with { Height = 777 });
        await Task.Delay(100);
        Assert.Equal(Opened, first.State);
        Assert.Equal(777, second.State!.Height);
        await first.DisposeAsync();
    }

    [Test]
    public async Task HostsWithoutStateAndBrowsersReportNone()
    {
        var factory = new StateFactory { WithoutState = true };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        Assert.Null(window.State);
        Assert.False(window.Capabilities.HasFlag(DesktopWindowCapabilities.State));
    }

    [Test]
    public async Task PlacementReplacesSizeAndPositionAndReachesTheHost()
    {
        var factory = new StateFactory { InitialState = Opened };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        var placement = new DesktopWindowPlacement { X = -3000, Y = 40, Width = 1024, Height = 700, Maximized = true };
        await using (await surface.OpenWindowAsync(new()
        {
            Browser = BrowserKind.Embedded, Width = 300, Height = 200, X = 5, Y = 6, Centered = true, Placement = placement,
        }))
        {
            var options = factory.Hosts[0].Options!;
            Assert.Equal(placement, options.Placement);
            Assert.Equal(1024, options.Width);
            Assert.Equal(700, options.Height);
            Assert.Equal(-3000, options.X);
            Assert.Equal(40, options.Y);
            Assert.False(options.Centered);
        }

        // A damaged settings file still opens: no positive size keeps the options, one coordinate is no position.
        await using (await surface.OpenWindowAsync(new()
        {
            Browser = BrowserKind.Embedded, Width = 300, Height = 200, Centered = true,
            Placement = new() { X = 12, Width = -5, Height = int.MaxValue },
        }))
        {
            var options = factory.Hosts[1].Options!;
            Assert.Equal(300, options.Width);
            Assert.Equal(WindowPlacementClamp.MaximumDimension, options.Height);
            Assert.Null(options.X);
            Assert.Null(options.Y);
            Assert.False(options.Centered);
        }
    }

    [Test]
    public async Task ValidationReportsMaximizedPlacementIgnoredByBrowsers()
    {
        await using var host = await DesktopHost.StartAsync(new());
        var preflight = host.GetPresentationPreflight(new()
        {
            Browser = BrowserKind.Chrome,
            Placement = new() { Width = 900, Height = 700, Maximized = true },
        });
        var diagnostic = Assert.Single(preflight.OptionDiagnostics, static item => item.Option == "DesktopWindowOptions.Placement");
        Assert.Equal(DesktopDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Placement.Maximized", diagnostic.Message);

        var restored = host.GetPresentationPreflight(new()
        {
            Browser = BrowserKind.Chrome,
            Placement = new() { Width = 900, Height = 700 },
        });
        Assert.DoesNotContain(restored.OptionDiagnostics, static item => item.Option == "DesktopWindowOptions.Placement");
    }

    [Test]
    public void PlacementRoundTripsThroughJson()
    {
        var placement = new DesktopWindowPlacement { X = -1280, Y = 24, Width = 1200, Height = 800, Maximized = true };
        var json = JsonSerializer.Serialize(placement);
        Assert.Equal(placement, JsonSerializer.Deserialize<DesktopWindowPlacement>(json));
        Assert.Equal(new DesktopWindowPlacement(), JsonSerializer.Deserialize<DesktopWindowPlacement>("{}"));
    }

    private sealed class StateFactory : IDesktopWindowHostFactory
    {
        internal List<StateHost> Hosts { get; } = [];
        internal DesktopWindowState? InitialState { get; set; }
        internal bool WithoutState { get; init; }
        public bool IsSupported => true;
        public IDesktopWindowHost Create()
        {
            StateHost host = WithoutState ? new StatelessHost() : new StateHost { State = InitialState };
            Hosts.Add(host);
            return host;
        }
    }

    private sealed class StatelessHost : StateHost
    {
        public override DesktopWindowCapabilities Capabilities => DesktopWindowCapabilities.NativeHandle;
    }

    private class StateHost : IDesktopWindowStateHost
    {
        public event EventHandler? Closed;
        public event EventHandler? StateChanged;
        public DesktopWindowState? State { get; set; }
        public virtual DesktopWindowCapabilities Capabilities => DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.State;
        public bool IsOpen { get; private set; }
        public nint NativeHandle => IsOpen ? 1 : 0;
        internal DesktopWindowHostOptions? Options;

        internal void Report(DesktopWindowState state)
        {
            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
        { Options = options; IsOpen = true; return ValueTask.CompletedTask; }
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
