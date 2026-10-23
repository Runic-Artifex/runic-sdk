using System.Collections.Immutable;
using System.Text.Json;
using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// B12 (I-066, I-073): monitors, full screen, placement on a monitor, layout direction, web language and the macOS
// standard menu titles, through a window host factory and the shared internal parts.
public sealed class MonitorsAndFullScreenTests
{
    private static readonly DesktopMonitor Laptop = new("laptop", "Built-in Display")
    {
        Bounds = new(0, 0, 1920, 1080), WorkArea = new(0, 0, 1920, 1040), Scale = 1.5, IsPrimary = true,
    };
    private static readonly DesktopMonitor External = new("external", "Studio Display")
    {
        Bounds = new(1920, 0, 2560, 1440), WorkArea = new(1920, 0, 2560, 1440),
    };

    [Test]
    public void MonitorRecordsCompareByValueAndDefaultToScaleOne()
    {
        Assert.Equal(Laptop, Laptop with { });
        Assert.NotEqual(Laptop, Laptop with { Scale = 2 });
        Assert.Equal(1, new DesktopMonitor("id", "name").Scale);
        Assert.False(new DesktopMonitor("id", "name").IsPrimary);
        Assert.Equal(new DesktopRect(1, 2, 3, 4), new DesktopRect(1, 2, 3, 4));
    }

    [Test]
    public async Task UnavailableMonitorsReportTheReasonAndWatchWaits()
    {
        var monitors = DesktopMonitors.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        var list = await monitors.ListAsync();
        Assert.Equal(PlatformUnavailableReason.BackendUnavailable, Assert.IsType<PlatformResult<ImmutableArray<DesktopMonitor>>.Unavailable>(list).Reason);
        Assert.IsType<CapabilityStatus.Unavailable>(DesktopServiceStatus.Get(monitors));
        using var cancellation = new CancellationTokenSource();
        await using var watch = monitors.WatchAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await watch.MoveNextAsync());
        Assert.IsType<PlatformResult<ImmutableArray<DesktopMonitor>>.Unavailable>(watch.Current);
        var next = watch.MoveNextAsync().AsTask();
        await Task.Delay(50);
        Assert.False(next.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopMonitors.Unavailable((PlatformUnavailableReason)999));
    }

    [Test]
    public async Task WatchReportsTheListThenEachChangeAndEndsOnFailure()
    {
        var source = new FakeMonitorSource([Laptop]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var watch = source.WatchAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal([Laptop], Success(watch.Current));

        // A signal that leaves the list equal is not reported; the next change is.
        source.Change([Laptop]);
        source.Change([Laptop, External]);
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal([Laptop, External], Success(watch.Current));

        source.Change([Laptop with { WorkArea = new(0, 0, 1920, 1000) }, External]);
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal(1000, Success(watch.Current)[0].WorkArea.Height);

        source.Fail();
        Assert.True(await watch.MoveNextAsync());
        Assert.IsType<PlatformResult<ImmutableArray<DesktopMonitor>>.Failed>(watch.Current);
        Assert.False(await watch.MoveNextAsync());
        Assert.Equal(0, source.Subscribers);
    }

    [Test]
    public async Task HostMonitorsComeFromTheWindowHostFactory()
    {
        var monitors = new FakeMonitorSource([Laptop, External]);
        await using (var host = await DesktopHost.StartAsync(new() { WindowHostFactory = new PlacementFactory { Monitors = monitors } }))
        {
            Assert.Same(monitors, host.Monitors);
            Assert.Equal([Laptop, External], Success(await host.Monitors.ListAsync()));
        }
        // A factory without monitors reports them unavailable.
        await using (var host = await DesktopHost.StartAsync(new() { WindowHostFactory = new MinimalFactory() }))
        {
            var result = await host.Monitors.ListAsync();
            Assert.Equal(PlatformUnavailableReason.BackendUnavailable,
                Assert.IsType<PlatformResult<ImmutableArray<DesktopMonitor>>.Unavailable>(result).Reason);
        }
    }

    [Test]
    public async Task FullScreenAndMonitorPlacementReachTheHostWithTheirCapabilities()
    {
        var factory = new PlacementFactory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new()
        {
            Browser = BrowserKind.Embedded,
            MonitorId = "external",
            LayoutDirection = DesktopLayoutDirection.RightToLeft,
            Language = "ar-EG",
        });
        var native = factory.Hosts[0];
        Assert.Equal("external", native.Options!.MonitorId);
        Assert.Equal(DesktopLayoutDirection.RightToLeft, native.Options.LayoutDirection);
        Assert.Equal("ar-EG", native.Options.Language);
        Assert.True(window.Capabilities.HasFlag(DesktopWindowCapabilities.FullScreen | DesktopWindowCapabilities.MonitorPlacement));

        await window.SetFullScreenAsync(true);
        await window.MoveToMonitorAsync("laptop");
        await window.SetFullScreenAsync(false);
        Assert.Equal(["full:True", "move:laptop", "full:False"], native.Calls);
        await Assert.ThrowsAsync<ArgumentException>(async () => await window.MoveToMonitorAsync(""));
    }

    [Test]
    public async Task OperationsWithoutTheirCapabilityAndKioskExitFail()
    {
        var factory = new PlacementFactory { Capabilities = DesktopWindowCapabilities.NativeHandle };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using (var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded }))
        {
            var error = await Assert.ThrowsAsync<DesktopException>(async () => await window.SetFullScreenAsync(true));
            Assert.Equal("window-capability-unavailable", error.Code);
            error = await Assert.ThrowsAsync<DesktopException>(async () => await window.MoveToMonitorAsync("laptop"));
            Assert.Equal("window-capability-unavailable", error.Code);
        }

        var kioskFactory = new PlacementFactory();
        await using var kioskHost = await DesktopHost.StartAsync(new() { WindowHostFactory = kioskFactory, WaitForConnection = false });
        await using var kioskSurface = await kioskHost.CreateSurfaceAsync();
        await using var kiosk = await kioskSurface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded, Kiosk = true });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await kiosk.SetFullScreenAsync(false));
        await kiosk.SetFullScreenAsync(true);
        Assert.Equal(["full:True"], kioskFactory.Hosts[0].Calls);
    }

    [Test]
    public async Task APlacementPositionDecidesOverTheMonitor()
    {
        var factory = new PlacementFactory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        var placement = new DesktopWindowPlacement { X = 100, Y = 80, Width = 900, Height = 600, FullScreen = true };
        await using (await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded, MonitorId = "external", Placement = placement }))
        {
            Assert.Null(factory.Hosts[0].Options!.MonitorId);
            Assert.True(factory.Hosts[0].Options!.Placement!.FullScreen);
        }
        // Without a saved position the monitor still decides.
        await using (await surface.OpenWindowAsync(new()
        {
            Browser = BrowserKind.Embedded, MonitorId = "external", Placement = placement with { X = null, Y = null },
        }))
        {
            Assert.Equal("external", factory.Hosts[1].Options!.MonitorId);
        }
    }

    [Test]
    public void FullScreenPlacementRoundTripsThroughJsonAndOlderOnesRestoreAsBefore()
    {
        var placement = new DesktopWindowPlacement { X = 10, Y = 20, Width = 800, Height = 600, FullScreen = true };
        Assert.Equal(placement, JsonSerializer.Deserialize<DesktopWindowPlacement>(JsonSerializer.Serialize(placement)));
        // A placement a 0.10 application saved has no FullScreen value.
        var older = JsonSerializer.Deserialize<DesktopWindowPlacement>("""{"X":10,"Y":20,"Width":800,"Height":600,"Maximized":false}""")!;
        Assert.False(older.FullScreen);
    }

    [Test]
    [Arguments("en")]
    [Arguments("ar-EG")]
    [Arguments("zh-Hant-TW")]
    [Arguments("sr-Latn-RS")]
    [Arguments("de-CH-1901")]
    [Arguments("x-private")]
    [DisplayName("LanguageTagsAreAccepted(tag: $tag)")]
    public void LanguageTagsAreAccepted(string tag) => Assert.True(DesktopSurface.IsLanguageTag(tag));

    [Test]
    [Arguments("")]
    [Arguments("e")]
    [Arguments("en_US")]
    [Arguments("en-")]
    [Arguments("-en")]
    [Arguments("en--US")]
    [Arguments("abcd")]
    [Arguments("en-abcdefghi")]
    [Arguments("ar EG")]
    [Arguments("1en")]
    [DisplayName("MalformedLanguageTagsAreRejected(tag: $tag)")]
    public void MalformedLanguageTagsAreRejected(string tag) => Assert.False(DesktopSurface.IsLanguageTag(tag));

    [Test]
    public async Task InvalidWindowOptionsFailBeforeOpening()
    {
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = new PlacementFactory(), WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await Assert.ThrowsAsync<ArgumentException>(async () => await surface.OpenWindowAsync(new() { Language = "en_US" }));
        await Assert.ThrowsAsync<ArgumentException>(async () => await surface.OpenWindowAsync(new() { MonitorId = "" }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await surface.OpenWindowAsync(new() { LayoutDirection = (DesktopLayoutDirection)7 }));
        Assert.Contains(host.Validate(new() { Language = "en_US" }).Diagnostics, static item => item.Code == "window-option-invalid");
    }

    [Test]
    public async Task BrowsersReportTheOptionsTheyIgnore()
    {
        await using var host = await DesktopHost.StartAsync(new());
        var preflight = host.GetPresentationPreflight(new()
        {
            Browser = BrowserKind.Chrome,
            MonitorId = "external",
            LayoutDirection = DesktopLayoutDirection.RightToLeft,
            Language = "he-IL",
            Placement = new() { Width = 900, Height = 700, FullScreen = true },
        });
        string[] ignored = ["DesktopWindowOptions.LayoutDirection", "DesktopWindowOptions.Language", "DesktopWindowOptions.Placement"];
        foreach (var option in ignored)
        {
            var diagnostic = Assert.Single(preflight.OptionDiagnostics, item => item.Option == option);
            Assert.Equal(DesktopDiagnosticSeverity.Warning, diagnostic.Severity);
        }
        Assert.Contains(preflight.OptionDiagnostics, static item => item.Message.Contains("Placement.FullScreen", StringComparison.Ordinal));
        // Without a saved position, the monitor applies and a browser ignores it.
        Assert.Contains(preflight.OptionDiagnostics, static item => item.Option == "DesktopWindowOptions.MonitorId");
    }

    [Test]
    public async Task ACustomHostWithoutMonitorPlacementReportsTheMonitorIgnored()
    {
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = new MinimalFactory() });
        var diagnostics = host.Validate(new() { MonitorId = "external" }).Diagnostics;
        Assert.Contains(diagnostics, static item => item.Option == "DesktopWindowOptions.MonitorId"
            && item.Severity == DesktopDiagnosticSeverity.Warning);
        await using var placing = await DesktopHost.StartAsync(new() { WindowHostFactory = new PlacementFactory() });
        Assert.DoesNotContain(placing.Validate(new() { MonitorId = "external" }).Diagnostics,
            static item => item.Option == "DesktopWindowOptions.MonitorId");
    }

    [Test]
    public void StandardMenuTitlesDefaultToTheEnglishOnes()
    {
        var titles = new DesktopStandardMenuTitles();
        Assert.Empty(titles.Check());
        Assert.Equal(22, titles.Titles().Count());
        Assert.Equal(["About {0}", "", "Hide {0}", "Hide Others", "Show All", "", "Quit {0}"],
            MacOsMainMenu.ApplicationItems(titles).Select(static item => item.Title));
        Assert.Equal(MacOsMainMenu.Edit, MacOsMainMenu.EditItems(titles));
        Assert.Equal(MacOsPageZoom.MenuItems, MacOsPageZoom.Items(titles));
    }

    [Test]
    public void StandardMenuTitlesReplaceEveryStandardItemAndKeepTheActions()
    {
        var titles = new DesktopStandardMenuTitles
        {
            About = "Über {0}", Hide = "{0} ausblenden", Quit = "{0} beenden", Copy = "Kopieren", Close = "Schließen",
            ZoomIn = "Vergrößern", Minimize = "Im Dock ablegen", HideOthers = "Andere ausblenden",
        };
        Assert.Empty(titles.Check());
        var application = MacOsMainMenu.ApplicationItems(titles);
        Assert.Equal(["Über {0}", "", "{0} ausblenden", "Andere ausblenden", "Show All", "", "{0} beenden"],
            application.Select(static item => item.Title));
        Assert.Equal(MacOsMainMenu.Application.Select(static item => item.Action), application.Select(static item => item.Action));
        Assert.Equal("Kopieren", Assert.Single(MacOsMainMenu.EditItems(titles), static item => item.Action == "copy:").Title);
        Assert.Equal("Schließen", Assert.Single(MacOsMainMenu.FileItems(titles)).Title);
        Assert.Equal("Im Dock ablegen", MacOsMainMenu.WindowItems(titles)[0].Title);
        Assert.Equal(["Vergrößern", "Vergrößern"],
            MacOsPageZoom.Items(titles).Where(static item => item.Command == PageZoomCommand.In).Select(static item => item.Title));
        Assert.Equal("Runic beenden", DesktopStandardMenuTitles.Format(titles.Quit, "Runic"));
        // A translation may leave the name out.
        Assert.Equal("Beenden", DesktopStandardMenuTitles.Format("Beenden", "Runic"));
    }

    [Test]
    public async Task InvalidStandardMenuTitlesFailTheHostAndValidationNamesThem()
    {
        DesktopStandardMenuTitles[] invalid =
        [
            new() { Edit = "" },
            new() { Copy = "   " },
            new() { Paste = new string('x', 257) },
            new() { Cut = "Cu\0t" },
            new() { Quit = "{0} {0}" },
            new() { Undo = "Undo {0}" },
            new() { About = "About {1}" },
            new() { Hide = null! },
        ];
        foreach (var titles in invalid)
        {
            var diagnostic = Assert.Single(titles.Check());
            Assert.Equal("menu-title-invalid", diagnostic.Code);
            Assert.Equal(DesktopDiagnosticSeverity.Error, diagnostic.Severity);
            Assert.StartsWith("DesktopHostOptions.StandardMenuTitles.", diagnostic.Option);
            await Assert.ThrowsAsync<ArgumentException>(async () => await DesktopHost.StartAsync(new() { StandardMenuTitles = titles }));
        }
        Assert.Equal("DesktopHostOptions.StandardMenuTitles.Edit", Assert.Single(new DesktopStandardMenuTitles { Edit = "" }.Check()).Option);
        Assert.Empty(new DesktopStandardMenuTitles { Edit = new string('x', 256), Quit = "Beenden" }.Check());
        await using var host = await DesktopHost.StartAsync(new() { StandardMenuTitles = new() { Edit = "Bearbeiten" } });
        Assert.DoesNotContain(host.Validate().Diagnostics, static item => item.Code == "menu-title-invalid");
    }

    private static DesktopMonitor[] Success(PlatformResult<ImmutableArray<DesktopMonitor>> result) =>
        [.. Assert.IsType<PlatformResult<ImmutableArray<DesktopMonitor>>.Success>(result).Value];

    private sealed class FakeMonitorSource(ImmutableArray<DesktopMonitor> initial) : DesktopMonitorSource
    {
        private PlatformResult<ImmutableArray<DesktopMonitor>> _current = new PlatformResult<ImmutableArray<DesktopMonitor>>.Success(initial);
        private readonly List<Action> _subscribers = [];

        internal int Subscribers { get { lock (_subscribers) return _subscribers.Count; } }

        internal void Change(ImmutableArray<DesktopMonitor> monitors) => Signal(new PlatformResult<ImmutableArray<DesktopMonitor>>.Success(monitors));

        internal void Fail() => Signal(new PlatformResult<ImmutableArray<DesktopMonitor>>.Failed(PlatformFailureCode.Other));

        private void Signal(PlatformResult<ImmutableArray<DesktopMonitor>> result)
        {
            Action[] subscribers;
            lock (_subscribers)
            {
                Volatile.Write(ref _current, result);
                subscribers = [.. _subscribers];
            }
            foreach (var subscriber in subscribers) subscriber();
            // Lets the watch read this value before the next one replaces it.
            Thread.Sleep(50);
        }

        protected override ValueTask<PlatformResult<ImmutableArray<DesktopMonitor>>> ReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Volatile.Read(ref _current));

        protected override IDisposable Subscribe(Action changed)
        {
            lock (_subscribers) _subscribers.Add(changed);
            return new Unsubscribe(this, changed);
        }

        private sealed class Unsubscribe(FakeMonitorSource source, Action changed) : IDisposable
        {
            public void Dispose()
            {
                lock (source._subscribers) source._subscribers.Remove(changed);
            }
        }
    }

    private sealed class MinimalFactory : IDesktopWindowHostFactory
    {
        public bool IsSupported => true;
        public IDesktopWindowHost Create() => new PlacementHost(DesktopWindowCapabilities.NativeHandle);
    }

    private sealed class PlacementFactory : IDesktopWindowHostFactory
    {
        internal List<PlacementHost> Hosts { get; } = [];
        internal DesktopWindowCapabilities Capabilities { get; init; } = DesktopWindowCapabilities.NativeHandle |
            DesktopWindowCapabilities.FullScreen | DesktopWindowCapabilities.MonitorPlacement;
        internal IDesktopMonitors? Monitors { get; init; }
        public bool IsSupported => true;
        DesktopWindowCapabilities IDesktopWindowHostFactory.Capabilities => Capabilities;
        IDesktopMonitors IDesktopWindowHostFactory.Monitors =>
            Monitors ?? DesktopMonitors.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        public IDesktopWindowHost Create()
        {
            var host = new PlacementHost(Capabilities);
            Hosts.Add(host);
            return host;
        }
    }

    private sealed class PlacementHost(DesktopWindowCapabilities capabilities) : IDesktopPlacementWindowHost
    {
        public event EventHandler? Closed;
        public DesktopWindowCapabilities Capabilities => capabilities;
        public bool IsOpen { get; private set; }
        public nint NativeHandle => IsOpen ? 1 : 0;
        internal DesktopWindowHostOptions? Options;
        internal List<string> Calls { get; } = [];

        public ValueTask SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken)
        {
            Calls.Add($"full:{fullScreen}");
            return ValueTask.CompletedTask;
        }

        public ValueTask MoveToMonitorAsync(string monitorId, CancellationToken cancellationToken)
        {
            Calls.Add($"move:{monitorId}");
            return ValueTask.CompletedTask;
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
