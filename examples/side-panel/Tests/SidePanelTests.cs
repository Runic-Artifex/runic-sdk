using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace SidePanelSample.Tests;

// Drives MainViewModel's debug panel as the page would, with RunicTestWindowPresentation as the Window: an 860-wide
// window at x 530 on a 1920-wide work area that grows by the panel's width, or refuses as a desktop window does.
public sealed class SidePanelTests : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly AsyncServiceScope _scope;
    private readonly RunicWindowTestHost<MainViewModel> _host;

    public SidePanelTests()
    {
        _services = new ServiceCollection().AddSidePanelSample().AddRunicViews().AddRunicTestWindowPresentation()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _services.CreateAsyncScope();
        _host = RunicWindowTestHost.Create<MainViewModel>(_scope.ServiceProvider);
    }

    private RunicTestWindowPresentation Window => _host.Presentation!;

    private MainViewModel ViewModel => _host.ViewModel;

    [Test]
    public async Task The_panel_opens_beside_the_window_and_closing_it_shrinks_the_window_back()
    {
        await ExecuteAsync(vm => vm.ToggleDebugCommand);

        await Assert.That(ViewModel.IsDebugPanelOpen).IsTrue();
        await Assert.That(ViewModel.ShowDebugOverlay).IsFalse();
        await Assert.That((Window.WindowX, Window.WindowWidth)).IsEqualTo((530, 860 + MainViewModel.PanelWidth));
        var open = Window.Requests.Single(request => request.Kind == RunicWindowPresentationRequestKind.OpenSidePanel);
        await Assert.That(open.SidePanel!.Path).IsEqualTo(MainViewModel.PanelPath);
        await Assert.That(open.SidePanel.Edge).IsEqualTo(RunicSidePanelEdge.Right);

        await ExecuteAsync(vm => vm.ToggleDebugCommand);

        await Assert.That(ViewModel.IsDebugPanelOpen).IsFalse();
        await Assert.That(Window.SidePanel).IsNull();
        await Assert.That((Window.WindowX, Window.WindowWidth)).IsEqualTo((530, 860));
        await Assert.That(ViewModel.Log[^1]).EndsWith("Debug panel closed (Closed)");
    }

    [Test]
    public async Task Docking_on_the_other_edge_moves_the_window_by_the_panel()
    {
        await ExecuteAsync(vm => vm.ToggleDebugCommand);
        await ExecuteAsync(vm => vm.MoveDebugCommand);

        await Assert.That(ViewModel.IsDebugPanelOnLeft).IsTrue();
        await Assert.That(Window.SidePanel!.Edge).IsEqualTo(RunicSidePanelEdge.Left);
        await Assert.That((Window.WindowX, Window.WindowWidth)).IsEqualTo((530 - MainViewModel.PanelWidth, 860 + MainViewModel.PanelWidth));

        // The next panel opens on the edge the user chose last.
        await ExecuteAsync(vm => vm.CloseDebugCommand);
        await ExecuteAsync(vm => vm.ToggleDebugCommand);
        await Assert.That(Window.SidePanel!.Edge).IsEqualTo(RunicSidePanelEdge.Left);
    }

    [Test]
    [Arguments(RunicSidePanelRefusal.Maximized)]
    [Arguments(RunicSidePanelRefusal.FullScreen)]
    [Arguments(RunicSidePanelRefusal.Snapped)]
    [Arguments(RunicSidePanelRefusal.NoRoom)]
    [Arguments(RunicSidePanelRefusal.Unsupported)]
    [Arguments(RunicSidePanelRefusal.PositionUnavailable)]
    [Arguments(RunicSidePanelRefusal.LoadFailed)]
    [DisplayName("A window that cannot grow ($reason) shows the panel as an overlay")]
    public async Task A_window_that_cannot_grow_shows_the_panel_as_an_overlay(RunicSidePanelRefusal reason)
    {
        await RefuseAsync(reason);

        await ExecuteAsync(vm => vm.ToggleDebugCommand);

        await Assert.That(ViewModel.IsDebugPanelOpen).IsFalse();
        await Assert.That(ViewModel.ShowDebugOverlay).IsTrue();
        await Assert.That(ViewModel.Refusal).IsEqualTo(reason.ToString());
        await Assert.That(Window.SidePanel).IsNull();
        await Assert.That((Window.WindowX, Window.WindowWidth)).IsEqualTo((530, 860));

        // The toggle hides the overlay again.
        await ExecuteAsync(vm => vm.ToggleDebugCommand);
        await Assert.That(ViewModel.ShowDebugOverlay).IsFalse();
        await Assert.That(ViewModel.Refusal).IsEqualTo("");
    }

    [Test]
    public async Task Wayland_refuses_the_left_edge_and_the_panel_stays_on_the_right()
    {
        await ExecuteAsync(vm => vm.ToggleDebugCommand);
        Window.SetSidePanelRefusal(RunicSidePanelRefusal.PositionUnavailable);

        await ExecuteAsync(vm => vm.MoveDebugCommand);

        await Assert.That(ViewModel.IsDebugPanelOpen).IsTrue();
        await Assert.That(ViewModel.IsDebugPanelOnLeft).IsFalse();
        await Assert.That(ViewModel.Refusal).IsEqualTo(nameof(RunicSidePanelRefusal.PositionUnavailable));
        await Assert.That(Window.SidePanel!.Edge).IsEqualTo(RunicSidePanelEdge.Right);
    }

    [Test]
    [Arguments(RunicSidePanelCloseReason.Closed)]
    [Arguments(RunicSidePanelCloseReason.Crashed)]
    [Arguments(RunicSidePanelCloseReason.WindowClosed)]
    [DisplayName("A panel closed by its page, a crash or the window ($reason) leaves the page without it")]
    public async Task A_panel_closed_elsewhere_leaves_the_page_without_it(RunicSidePanelCloseReason reason)
    {
        await ExecuteAsync(vm => vm.ToggleDebugCommand);

        // As the panel's page calling window.close(), a second renderer crash, or the Window's close.
        Window.CloseSidePanel(reason);
        await _host.FlushWindowPresentationAsync();

        await Assert.That(ViewModel.IsDebugPanelOpen).IsFalse();
        await Assert.That(ViewModel.ShowDebugOverlay).IsFalse();
        await Assert.That(ViewModel.Log[^1]).EndsWith($"Debug panel closed ({reason})");
        await Assert.That((Window.WindowX, Window.WindowWidth)).IsEqualTo((530, 860));
    }

    [Test]
    public async Task The_page_reads_the_panel_state_and_the_log()
    {
        await ExecuteAsync(vm => vm.AddCommand);
        await ExecuteAsync(vm => vm.ToggleDebugCommand);

        var state = await _host.Root.SnapshotAsync();
        await Assert.That(state.Read(vm => vm.Quantity)).IsEqualTo(2);
        await Assert.That(state.Read(vm => vm.IsDebugPanelOpen)).IsTrue();
        await Assert.That(state.Get(vm => vm.Log).GetArrayLength()).IsEqualTo(2);
    }

    private async Task RefuseAsync(RunicSidePanelRefusal reason)
    {
        switch (reason)
        {
            case RunicSidePanelRefusal.Maximized: Window.IsMaximized = true; break;
            case RunicSidePanelRefusal.FullScreen: await Window.SetFullScreenAsync(true); break;
            case RunicSidePanelRefusal.Snapped: Window.IsSnapped = true; break;
            case RunicSidePanelRefusal.NoRoom: Window.WorkAreaWidth = 1200; break;
            case RunicSidePanelRefusal.Unsupported: Window.Features &= ~RunicWindowPresentationFeatures.SidePanel; break;
            default: Window.SetSidePanelRefusal(reason); break;
        }
    }

    private async Task ExecuteAsync(System.Linq.Expressions.Expression<Func<MainViewModel, object?>> command)
    {
        (await _host.Root.ExecuteAsync(command)).ThrowIfFailed();
        await _host.FlushWindowPresentationAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _host.Dispose();
        await _scope.DisposeAsync();
        await _services.DisposeAsync();
    }
}
