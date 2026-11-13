extern alias cswebui;
using System.Reflection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;
using CsWebUiWindowPresentation = cswebui::Runic.Application.Views.CsWebUi.CsWebUiWindowPresentation;

// sdk#864, P5 (side-panel/DESIGN.md §3.2, §3.5; D062.8): a Runic Window's IRunicWindowPresentation opens the side panel of
// its DesktopWindow, and every other presentation refuses it with Unsupported.
internal sealed class SidePanelPresentationChecks
{
    private static readonly RunicSidePanelOptions Options = new() { Width = 300, Path = "side-panel" };

    [Test, NotInParallel(Order = 81)]
    public async Task ThePresentationMapsOntoTheDesktopWindowsPanel()
    {
        var factory = new PanelHostFactory();
        await using var host = await DesktopHost.StartAsync(new() { BuiltInHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<p>page</p>") });
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var native = factory.Hosts.Single();
        var presentation = new DesktopWindowPresentation(window, () => null);
        Require(presentation.Features.HasFlag(RunicWindowPresentationFeatures.SidePanel), $"Features: {presentation.Features}");

        var raised = new List<string?>();
        presentation.PropertyChanged += (_, args) => { lock (raised) raised.Add(args.PropertyName); };
        var panel = Opened(await presentation.OpenSidePanelAsync(Options with { Path = "/side-panel?tab=log", LoadTimeout = TimeSpan.FromSeconds(3) }));
        Require(ReferenceEquals(panel, presentation.SidePanel) && window.SidePanel is { IsOpen: true }, "The presentation's panel is not the window's.");
        Require(panel.Edge == RunicSidePanelEdge.Right && panel.Width == 300 && panel.IsOpen, $"Opened on {panel.Edge}, {panel.Width} wide.");
        Require(native.Request is { Left: false, Width: 300 } request && request.Url == new Uri(surface.Url, "side-panel?tab=log")
            && request.LoadTimeout == TimeSpan.FromSeconds(3), "The options did not reach the host.");
        lock (raised) Require(raised.SequenceEqual([nameof(IRunicWindowPresentation.SidePanel)]), $"Raised: {string.Join(", ", raised)}");
        Require(Refusal(await presentation.OpenSidePanelAsync(Options)) == RunicSidePanelRefusal.AlreadyOpen, "A second panel was not refused.");

        Require(await panel.MoveToEdgeAsync(RunicSidePanelEdge.Start) is null && panel.Edge == RunicSidePanelEdge.Left, "The panel did not move to Start.");
        Require(await panel.SetWidthAsync(320) is null && panel.Width == 320, "The width did not change.");
        await panel.FocusAsync();
        var closed = new TaskCompletionSource<RunicSidePanelCloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        panel.Closed += (_, args) => closed.TrySetResult(args.Reason);
        await panel.CloseAsync();
        Require(await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)) == RunicSidePanelCloseReason.Closed, "Closed reported another reason.");
        Require(!panel.IsOpen && presentation.SidePanel is null && window.SidePanel is null, "The panel stayed.");
        await WaitUntilAsync(() => { lock (raised) return raised.Count == 2; });
        Require(native.Calls.SequenceEqual(["open:right", "move:left", "width:320", "close"]), $"Host calls: {string.Join(", ", native.Calls)}");
        // A closed panel answers instead of throwing; its edge and width stay readable.
        Require(await panel.MoveToEdgeAsync(RunicSidePanelEdge.Right) == RunicSidePanelRefusal.Closed
            && await panel.SetWidthAsync(200) == RunicSidePanelRefusal.Closed && panel.Edge == RunicSidePanelEdge.Left && panel.Width == 320,
            "A closed panel did not report Closed.");
        await panel.FocusAsync();

        // The window's refusals arrive as the presentation's.
        native.Report(native.State! with { IsMaximized = true });
        Require(Refusal(await presentation.OpenSidePanelAsync(Options)) == RunicSidePanelRefusal.Maximized, "Maximized was not mapped.");
        native.Report(native.State! with { IsMaximized = false });

        // A panel opened on the DesktopWindow itself is the presentation's too; the window's close closes it.
        var direct = Assert(await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Width = 300, Path = "panel" }));
        var adopted = presentation.SidePanel ?? throw new InvalidOperationException("The presentation did not adopt the window's panel.");
        var windowClosed = new TaskCompletionSource<RunicSidePanelCloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        adopted.Closed += (_, args) => windowClosed.TrySetResult(args.Reason);
        await window.CloseAsync();
        Require(await windowClosed.Task.WaitAsync(TimeSpan.FromSeconds(10)) == RunicSidePanelCloseReason.WindowClosed && !direct.IsOpen,
            "The window's close did not close the panel.");
        Require(Refusal(await presentation.OpenSidePanelAsync(Options)) == RunicSidePanelRefusal.Closed, "A closed window did not refuse with Closed.");
        Require(!presentation.Features.HasFlag(RunicWindowPresentationFeatures.SidePanel), "A closed window reports a side panel.");
    }

    [Test, NotInParallel(Order = 81)]
    public async Task TheHostsRefusalsAreMapped()
    {
        var factory = new PanelHostFactory { Refusal = DesktopSidePanelRefusal.LoadFailed };
        await using var host = await DesktopHost.StartAsync(new() { BuiltInHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<p>page</p>") });
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var presentation = new DesktopWindowPresentation(window, () => null);
        Require(Refusal(await presentation.OpenSidePanelAsync(Options)) == RunicSidePanelRefusal.LoadFailed, "LoadFailed was not mapped.");
        Require(presentation.SidePanel is null, "A refused panel is the presentation's.");
        // The window checks the path; the presentation checks what it can itself.
        await Catch<ArgumentException>(() => presentation.OpenSidePanelAsync(Options with { Path = "../escape" }).AsTask());
        await Catch<ArgumentOutOfRangeException>(() => presentation.OpenSidePanelAsync(Options with { Width = 100 }).AsTask());

        // Every refusal maps by name, and a value of a later Runic.Desktop is Unsupported.
        foreach (var refusal in Enum.GetValues<DesktopSidePanelRefusal>())
            Require(DesktopWindowPresentation.Convert(refusal).ToString() == refusal.ToString(), $"{refusal} was mapped to {DesktopWindowPresentation.Convert(refusal)}.");
        Require(DesktopWindowPresentation.Convert((DesktopSidePanelRefusal)99) == RunicSidePanelRefusal.Unsupported, "An unknown refusal was not Unsupported.");
        foreach (var reason in Enum.GetValues<DesktopSidePanelCloseReason>())
            Require(DesktopWindowPresentation.Convert(reason).ToString() == reason.ToString(), $"{reason} was mapped to {DesktopWindowPresentation.Convert(reason)}.");
        foreach (var edge in Enum.GetValues<RunicSidePanelEdge>())
            Require(DesktopWindowPresentation.Convert(edge).ToString() == edge.ToString(), $"{edge} was mapped to {DesktopWindowPresentation.Convert(edge)}.");
    }

    [Test, NotInParallel(Order = 81)]
    public async Task OtherPresentationsRefuseThePanelAsUnsupported()
    {
        // A custom window host (as WPF's RunicWebView) and a built-in host without a panel (macOS and Linux until P3, P4).
        await using (var host = await DesktopHost.StartAsync(new() { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false }))
        {
            await using var surface = await host.CreateSurfaceAsync();
            await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
            await RequireUnsupportedAsync(new DesktopWindowPresentation(window, () => null), "A custom window host");
        }
        await using (var host = await DesktopHost.StartAsync(new() { BuiltInHostFactory = new PanelHostFactory { SupportsSidePanel = false }, WaitForConnection = false }))
        {
            await using var surface = await host.CreateSurfaceAsync();
            await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
            await RequireUnsupportedAsync(new DesktopWindowPresentation(window, () => null), "A built-in host without a panel");
        }

        // A hosted application's Window (runic-host/1 guest), also in a region: the host protocol has no side panel.
        var hosted = new HostedWindowPresentation(RunicWindowPresentationFeatures.Present, RunicWindowActivity.Assumed,
            DesktopColorScheme.NoPreference, static (_, _) => Task.FromResult<System.Text.Json.Nodes.JsonObject?>(null));
        await RequireUnsupportedAsync(hosted, "A hosted Window");

        // CS-WebUI, the hosted guest and WPF keep the interface's default members.
        foreach (var type in new[] { typeof(CsWebUiWindowPresentation), typeof(HostedWindowPresentation) })
        {
            Require(type.GetMethod(nameof(IRunicWindowPresentation.OpenSidePanelAsync), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is null,
                $"{type.Name} implements OpenSidePanelAsync.");
        }
    }

    private static async Task RequireUnsupportedAsync(IRunicWindowPresentation presentation, string what)
    {
        Require(!presentation.Features.HasFlag(RunicWindowPresentationFeatures.SidePanel), $"{what} reports a side panel.");
        Require(Refusal(await presentation.OpenSidePanelAsync(Options)) == RunicSidePanelRefusal.Unsupported, $"{what} did not refuse with Unsupported.");
        Require(presentation.SidePanel is null, $"{what} has a panel.");
    }

    private static IRunicSidePanel Opened(RunicSidePanelResult result) =>
        result is RunicSidePanelResult.Opened opened ? opened.Panel : throw new InvalidOperationException($"The panel did not open: {result}");

    private static DesktopSidePanel Assert(DesktopSidePanelResult result) =>
        result is DesktopSidePanelResult.Opened opened ? opened.Panel : throw new InvalidOperationException($"The panel did not open: {result}");

    private static RunicSidePanelRefusal Refusal(RunicSidePanelResult result) =>
        result is RunicSidePanelResult.Refused refused ? refused.Reason : throw new InvalidOperationException($"The panel opened: {result}");

    private static async Task Catch<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            if (waited > 5000) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
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

    // A built-in host as the WebView2 one behaves (Runic.Desktop.Tests' SidePanelTests has the same): it plans with
    // SidePanelPlanner and reports every close.
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
}
