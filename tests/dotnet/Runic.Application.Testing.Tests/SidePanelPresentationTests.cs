using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// sdk#864, P5 (side-panel/DESIGN.md §3.2, §4): IRunicWindowPresentation.OpenSidePanelAsync in a Window's scope, the
// recording fake's decisions, and the default interface members of a presentation written for 0.18.
internal sealed class SidePanelPresentationTests
{
    private static readonly RunicSidePanelOptions Panel = new() { Width = 300, Path = "side-panel" };

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddRunicReactiveModelContext();
        services.AddScoped<WindowPresentationViewModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, WindowPresentationViewModel, IDisposable>>(
            _ => (transport, content, vm) => new WindowPresentationBridge(transport, vm, content: content));
        services.AddRunicWindowService();
        return services;
    }

    [Test]
    public async Task TheFakeGrowsAndShrinksAsADesktopWindowAsync()
    {
        var fake = new RunicTestWindowPresentation();
        Require(fake.Features.HasFlag(RunicWindowPresentationFeatures.SidePanel), "The fake has no side panel.");
        Require((fake.WindowX, fake.WindowWidth, fake.WorkAreaWidth) == (530, 860, 1920), "The fake is not an 860-wide window centred on 1920.");

        var raised = new List<string?>();
        fake.PropertyChanged += (_, args) => raised.Add(args.PropertyName);
        var panel = Opened(await fake.OpenSidePanelAsync(Panel));
        Require(ReferenceEquals(panel, fake.SidePanel) && panel.IsOpen, "The open panel is not the fake's SidePanel.");
        Require(panel.Edge == RunicSidePanelEdge.Right && panel.Width == 300, $"Opened on {panel.Edge}, {panel.Width} wide.");
        Require((fake.WindowX, fake.WindowWidth) == (530, 1160), $"The window is {fake.WindowX}, {fake.WindowWidth}.");
        Require(raised.SequenceEqual([nameof(IRunicWindowPresentation.SidePanel)]), $"Raised: {string.Join(", ", raised)}");
        Require(Refusal(await fake.OpenSidePanelAsync(Panel)) == RunicSidePanelRefusal.AlreadyOpen, "A second panel was not refused.");

        // Moving to the other edge moves the window by the panel's width; the page stays where it is.
        Require(await panel.MoveToEdgeAsync(RunicSidePanelEdge.End) is null, "Moving to its own edge was refused.");
        Require(await panel.MoveToEdgeAsync(RunicSidePanelEdge.Start) is null && panel.Edge == RunicSidePanelEdge.Left, "The panel did not move left.");
        Require((fake.WindowX, fake.WindowWidth) == (230, 1160), $"After the move the window is {fake.WindowX}, {fake.WindowWidth}.");
        Require(await panel.SetWidthAsync(400) is null && panel.Width == 400, "The width did not change.");
        Require((fake.WindowX, fake.WindowWidth) == (130, 1260), $"After the width change the window is {fake.WindowX}, {fake.WindowWidth}.");
        Require(await panel.SetWidthAsync(600) == RunicSidePanelRefusal.NoRoom && panel.Width == 400, "Leaving the work area was not refused.");
        await Catch<ArgumentOutOfRangeException>(() => panel.SetWidthAsync(119).AsTask());
        await panel.FocusAsync();

        var closed = new List<RunicSidePanelCloseReason>();
        panel.Closed += (_, args) => closed.Add(args.Reason);
        await panel.DisposeAsync();
        Require(!panel.IsOpen && fake.SidePanel is null && closed.SequenceEqual([RunicSidePanelCloseReason.Closed]), "Disposing did not close the panel once.");
        Require((fake.WindowX, fake.WindowWidth) == (530, 860), $"Closing left the window at {fake.WindowX}, {fake.WindowWidth}.");
        await panel.CloseAsync();
        Require(await panel.MoveToEdgeAsync(RunicSidePanelEdge.Right) == RunicSidePanelRefusal.Closed, "A closed panel moved.");
        Require(closed.Count == 1, "Closed was raised twice.");
        Require(fake.Requests.Select(static request => request.Kind).SequenceEqual([
            RunicWindowPresentationRequestKind.OpenSidePanel, RunicWindowPresentationRequestKind.OpenSidePanel,
            RunicWindowPresentationRequestKind.MoveSidePanel, RunicWindowPresentationRequestKind.MoveSidePanel,
            RunicWindowPresentationRequestKind.SetSidePanelWidth, RunicWindowPresentationRequestKind.SetSidePanelWidth,
            RunicWindowPresentationRequestKind.FocusSidePanel, RunicWindowPresentationRequestKind.CloseSidePanel,
            RunicWindowPresentationRequestKind.MoveSidePanel]), $"Recorded: {string.Join(", ", fake.Requests.Select(static r => r.Kind))}");
        Require(fake.Requests[0] is { SidePanel: { Path: "side-panel" }, Edge: RunicSidePanelEdge.End, Width: 300 }, "The open request lost its options.");

        // The page or the desktop closes the panel.
        var crashed = Opened(await fake.OpenSidePanelAsync(Panel with { Edge = RunicSidePanelEdge.Left }));
        Require((fake.WindowX, fake.WindowWidth) == (230, 1160), $"The left panel grew the window to {fake.WindowX}, {fake.WindowWidth}.");
        RunicSidePanelCloseReason? reason = null;
        crashed.Closed += (_, args) => reason = args.Reason;
        fake.CloseSidePanel(RunicSidePanelCloseReason.Crashed);
        Require(reason == RunicSidePanelCloseReason.Crashed && fake.SidePanel is null && (fake.WindowX, fake.WindowWidth) == (530, 860),
            "A crashed panel did not close and shrink the window.");

        // A right-to-left window opens End on its left edge.
        fake.IsRightToLeft = true;
        var rtl = Opened(await fake.OpenSidePanelAsync(Panel));
        Require(rtl.Edge == RunicSidePanelEdge.Left, $"End of a right-to-left window was {rtl.Edge}.");
        await rtl.CloseAsync();
    }

    [Test]
    public async Task TheFakeRefusesInTheDesktopsOrderAsync()
    {
        var fake = new RunicTestWindowPresentation();
        foreach (var (options, message) in new (RunicSidePanelOptions?, string)[]
        {
            (null, "null"), (Panel with { Width = 119 }, "narrow"), (Panel with { Edge = (RunicSidePanelEdge)9 }, "edge"),
            (Panel with { LoadTimeout = TimeSpan.Zero }, "timeout"), (Panel with { Path = " " }, "empty"),
            (Panel with { Path = "https://example.com/" }, "absolute"), (Panel with { Path = "//example.com/x" }, "network path"),
            (Panel with { Path = "a/../../b" }, "escape"), (Panel with { Path = "%2e%2e/x" }, "escaped escape"),
            (Panel with { Path = "javascript:alert(1)" }, "scheme"),
        })
        {
            var threw = false;
            try { await fake.OpenSidePanelAsync(options!); }
            catch (ArgumentException) { threw = true; }
            Require(threw, $"The {message} options were accepted.");
        }
        Require(fake.Requests.Count == 0, "Invalid options were recorded.");

        fake.Features = RunicTestWindowPresentation.AllFeatures & ~RunicWindowPresentationFeatures.SidePanel;
        fake.SetSidePanelRefusal(RunicSidePanelRefusal.LoadFailed);
        Require(Refusal(await fake.OpenSidePanelAsync(Panel)) == RunicSidePanelRefusal.Unsupported, "Without the feature it was not Unsupported.");
        fake.Features = RunicTestWindowPresentation.AllFeatures;
        Require(Refusal(await fake.OpenSidePanelAsync(Panel)) == RunicSidePanelRefusal.LoadFailed, "The set refusal was not used.");
        fake.SetSidePanelRefusal(null);

        await fake.SetFullScreenAsync(true);
        fake.IsMaximized = true;
        fake.IsSnapped = true;
        await fake.MinimizeAsync();
        Require(Refusal(await fake.OpenSidePanelAsync(Panel)) == RunicSidePanelRefusal.Minimized, "Minimized first.");
        await fake.PresentAsync();
        Require(Refusal(await fake.OpenSidePanelAsync(Panel)) == RunicSidePanelRefusal.FullScreen, "Then full screen.");
        await fake.SetFullScreenAsync(false);
        Require(Refusal(await fake.OpenSidePanelAsync(Panel)) == RunicSidePanelRefusal.Maximized, "Then maximized.");
        fake.IsMaximized = false;
        Require(Refusal(await fake.OpenSidePanelAsync(Panel)) == RunicSidePanelRefusal.Snapped, "Then snapped.");
        fake.IsSnapped = false;
        Require(Refusal(await fake.OpenSidePanelAsync(Panel with { Width = 961 })) == RunicSidePanelRefusal.NoRoom, "Over half the work area.");
        Require(Refusal(await fake.OpenSidePanelAsync(Panel with { Width = 531 })) == RunicSidePanelRefusal.NoRoom, "Past the right edge.");
        Require(Refusal(await fake.OpenSidePanelAsync(Panel with { Width = 531, Edge = RunicSidePanelEdge.Left })) == RunicSidePanelRefusal.NoRoom,
            "Past the left edge.");
        await (Opened(await fake.OpenSidePanelAsync(Panel with { Width = 530 }))).CloseAsync();
        fake.SetSidePanelRefusal(RunicSidePanelRefusal.PositionUnavailable);
        Require(Refusal(await fake.OpenSidePanelAsync(Panel with { Edge = RunicSidePanelEdge.Left })) == RunicSidePanelRefusal.PositionUnavailable,
            "The Wayland refusal was not used.");
        Require((fake.WindowX, fake.WindowWidth) == (530, 860), "A refusal changed the window.");
    }

    [Test]
    public async Task TheScopesPanelRaisesItsChangesInTurnsAsync()
    {
        var services = Services();
        services.AddRunicTestWindowPresentation();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        using var host = RunicWindowTestHost.Create<WindowPresentationViewModel>(scope.ServiceProvider);
        var fake = host.Presentation!;
        var window = host.ViewModel.Window;
        Require(window.Features.HasFlag(RunicWindowPresentationFeatures.SidePanel), "The Window does not report the fake's side panel.");

        var raised = new List<(string? Property, bool InTurn)>();
        window.PropertyChanged += (_, args) => { lock (raised) raised.Add((args.PropertyName, RunicModelContext.Current is not null)); };
        var panel = Opened(await window.OpenSidePanelAsync(Panel));
        // The Window's own panel, not the fake's, and set when the request returns.
        Require(!ReferenceEquals(panel, fake.SidePanel) && ReferenceEquals(panel, window.SidePanel), "The scope did not wrap the fake's panel.");
        Require(panel.Edge == RunicSidePanelEdge.Right && panel.Width == 300 && panel.IsOpen, "The wrapped panel does not read the fake's.");
        await host.FlushWindowPresentationAsync();
        lock (raised) Require(raised is [(nameof(IRunicWindowPresentation.SidePanel), true)], $"Raised: {string.Join(", ", raised)}");

        Require(await panel.MoveToEdgeAsync(RunicSidePanelEdge.Left) is null && fake.SidePanel!.Edge == RunicSidePanelEdge.Left, "The move did not reach the fake.");
        var closed = new List<(RunicSidePanelCloseReason Reason, bool InTurn, bool Cleared)>();
        panel.Closed += (_, args) => closed.Add((args.Reason, RunicModelContext.Current is not null, window.SidePanel is null));
        panel.Closed += (_, _) => throw new InvalidOperationException("A failing handler does not stop the others.");
        panel.Closed += (_, _) => closed.Add((RunicSidePanelCloseReason.Closed, true, true));
        fake.CloseSidePanel(RunicSidePanelCloseReason.Crashed);
        await host.FlushWindowPresentationAsync();
        Require(closed is [(RunicSidePanelCloseReason.Crashed, true, true), _], $"Closed: {string.Join(", ", closed)}");
        Require(!panel.IsOpen && window.SidePanel is null, "The Window still has the panel.");
        lock (raised) Require(raised.Count == 2 && raised[1] == (nameof(IRunicWindowPresentation.SidePanel), true), $"Raised: {string.Join(", ", raised)}");
        Require(await panel.SetWidthAsync(200) == RunicSidePanelRefusal.Closed, "A closed panel changed its width.");

        // A ViewModel falls back to its overlay when the window is snapped.
        fake.IsSnapped = true;
        Require(Refusal(await window.OpenSidePanelAsync(Panel)) == RunicSidePanelRefusal.Snapped, "A snapped window opened the panel.");

        // The panel closes with the Window; then requests report it closed.
        fake.IsSnapped = false;
        var last = Opened(await window.OpenSidePanelAsync(Panel));
        await scope.DisposeAsync();
        Require(Refusal(await window.OpenSidePanelAsync(Panel)) == RunicSidePanelRefusal.Closed, "A released Window did not refuse with Closed.");
        await last.CloseAsync();
    }

    [Test]
    public async Task PresentationsWithoutAPanelRefuseItAsUnsupportedAsync()
    {
        // A presentation written for 0.18 keeps compiling: the default members refuse.
        IRunicWindowPresentation legacy = new LegacyPresentation();
        Require(Refusal(await legacy.OpenSidePanelAsync(Panel)) == RunicSidePanelRefusal.Unsupported && legacy.SidePanel is null,
            "The default interface members did not refuse.");
        await Catch<ArgumentNullException>(() => legacy.OpenSidePanelAsync(null!).AsTask());

        // A Window whose host attached no presentation, as a serve-only one: Unsupported, then Closed once released.
        await using var provider = Services().BuildServiceProvider();
        var window = await new TestWindowHost(provider).OpenWindowAsync<WindowPresentationWindow>();
        var presentation = window.Presentation;
        Require(Refusal(await presentation.OpenSidePanelAsync(Panel)) == RunicSidePanelRefusal.Unsupported, "A Window without a host opened a panel.");
        await Catch<ArgumentOutOfRangeException>(() => presentation.OpenSidePanelAsync(Panel with { Width = 10 }).AsTask());
        await window.DisposeAsync();
        Require(Refusal(await presentation.OpenSidePanelAsync(Panel)) == RunicSidePanelRefusal.Closed, "A released Window did not report Closed.");
        Require(presentation.SidePanel is null, "A Window without a host has a panel.");
    }

    private static IRunicSidePanel Opened(RunicSidePanelResult result) =>
        result is RunicSidePanelResult.Opened opened ? opened.Panel : throw new InvalidOperationException($"The panel did not open: {result}");

    private static RunicSidePanelRefusal Refusal(RunicSidePanelResult result) =>
        result is RunicSidePanelResult.Refused refused ? refused.Reason : throw new InvalidOperationException($"The panel opened: {result}");

    private static async Task<T?> Catch<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // IRunicWindowPresentation as a 0.18 host implemented it: no side panel members.
    private sealed class LegacyPresentation : IRunicWindowPresentation
    {
        private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event EventHandler<RunicWindowActivityChangedEventArgs>? ActivityChanged { add { } remove { } }
        public RunicWindowPresentationFeatures Features => RunicWindowPresentationFeatures.Present;
        public RunicWindowActivity Activity => RunicWindowActivity.Assumed;
        public bool IsActive => true;
        public bool IsMinimized => false;
        public bool IsVisible => true;
        public DesktopColorScheme ColorScheme => DesktopColorScheme.NoPreference;
        public ValueTask<PlatformResult<PlatformUnit>> PresentAsync(DesktopActivationContext? activation = null, CancellationToken cancellationToken = default) => ValueTask.FromResult(Done);
        public ValueTask<PlatformResult<PlatformUnit>> MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Done);
        public ValueTask<PlatformResult<PlatformUnit>> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.FromResult(Done);
        public ValueTask<PlatformResult<PlatformUnit>> SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default) => ValueTask.FromResult(Done);
        public ValueTask<PlatformResult<PlatformUnit>> SetDocumentEditedAsync(bool edited, CancellationToken cancellationToken = default) => ValueTask.FromResult(Done);
        public ValueTask<PlatformResult<PlatformUnit>> SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default) => ValueTask.FromResult(Done);
    }
}
