using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// 0.16 D2, navigation-path/1: the window's Back and its route path, through the window's Bridge.
internal sealed class NavigationPathBridgeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Test]
    public async Task BackAndRoutePathsGoThroughTheGuardsAsync()
    {
        await using var window = new PathWindow();
        var host = window.Host;
        using var sync = RunicPresentation.SyncNavigationPath(window.Shell.Main, window.Routes);
        host.Root.Snapshot();

        var refused = await host.NavigateBackAsync().WaitScaledAsync(Timeout);
        Require(refused is { Succeeded: true, Outcome.Rejection: NavigationRejection.NoHistory, HasPath: true, Path: "home" },
            $"Back with no history: {refused.Json}");

        // An address bar or Forward opens a route: pushed, and the page is told to push its history.
        var opened = await host.OpenNavigationPathAsync("editors/a").WaitScaledAsync(Timeout);
        Require(opened is { Outcome.Kind: NavigationOutcomeKind.Committed, Path: "editors/a" } && window.Shell.Main.Current is NavEditorViewModel { Title: "a" },
            $"editors/a did not open: {opened.Json}");
        await Until(() => host.NavigationPathPushes is [("editors/a", "push")], $"The push was {string.Join(", ", host.NavigationPathPushes)}.");
        await host.OpenNavigationPathAsync("editors/b").WaitScaledAsync(Timeout);

        // A browser Back opens the previous route: the region goes back to it.
        var back = await host.OpenNavigationPathAsync("editors/a").WaitScaledAsync(Timeout);
        Require(back is { Outcome.Kind: NavigationOutcomeKind.Committed, Path: "editors/a" } && window.Shell.Main.History.Count == 1
                && host.NavigationPathPushes[^1] == new RunicNavigationPathPush("editors/a", "back"),
            $"Opening an earlier route did not go back: {back.Json}, {host.NavigationPathPushes[^1]}.");

        // A guard that refuses keeps the region; the reply names the path the page reverts its URL to.
        var keep = false;
        using (window.Shell.Main.AddDepartureGuard(new Guard(() => keep)))
        {
            var vetoed = await host.OpenNavigationPathAsync("home").WaitScaledAsync(Timeout);
            Require(vetoed is { Outcome.Rejection: NavigationRejection.Guard, Path: "editors/a" } && window.Shell.Main.Current is NavEditorViewModel,
                $"A refused route changed the region: {vetoed.Json}");
            var vetoedBack = await host.NavigateBackAsync().WaitScaledAsync(Timeout);
            Require(vetoedBack is { Outcome.Rejection: NavigationRejection.Guard, Path: "editors/a" }, $"A refused Back: {vetoedBack.Json}");
        }

        var unknown = await host.OpenNavigationPathAsync("nowhere/1").WaitScaledAsync(Timeout);
        Require(unknown is { Succeeded: true, Outcome: null, Path: "editors/a" }, $"An unmatched route: {unknown.Json}");

        // The mouse's Back button and Alt+Left.
        var mouseBack = await host.NavigateBackAsync().WaitScaledAsync(Timeout);
        Require(mouseBack is { Outcome.Kind: NavigationOutcomeKind.Committed, Path: "home" } && window.Shell.Main.Current is NavHomeViewModel
                && host.NavigationPathPushes[^1] == new RunicNavigationPathPush("home", "back"),
            $"Back did not go back: {mouseBack.Json}");
        Require(Throws<InvalidOperationException>(() => RunicPresentation.SyncNavigationPath(window.Other, window.Routes)),
            "A second path region was accepted.");
    }

    [Test]
    public async Task BackWorksWithoutAPathRegionAsync()
    {
        await using var window = new PathWindow();
        var host = window.Host;
        host.Root.Snapshot();
        await window.Shell.Main.PushAsync(NavigationTarget.Own<INavPageViewModel>(new NavEditorViewModel("a"))).AsTask().WaitScaledAsync(Timeout);
        var back = await host.NavigateBackAsync().WaitScaledAsync(Timeout);
        Require(back is { Outcome.Kind: NavigationOutcomeKind.Committed, HasPath: false } && window.Shell.Main.Current is NavHomeViewModel,
            $"Back without a path region: {back.Json}");
        var open = await host.OpenNavigationPathAsync("home").WaitScaledAsync(Timeout);
        Require(open is { Succeeded: false, ErrorReason: "unavailable" }, $"Opening a path without a path region: {open.Json}");
        Require(host.NavigationPathPushes.Count == 0, "A window without a path region pushed a path.");
    }

    private sealed class Guard(Func<bool> answer) : INavigationRegionGuard
    {
        public ValueTask<bool> CanDepartAsync(NavigationRegionDeparture departure, CancellationToken cancellationToken) =>
            ValueTask.FromResult(answer());
    }

    private sealed class PathWindow : IAsyncDisposable
    {
        public PathWindow()
        {
            Navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = Context });
            Shell = new NavShellViewModel(Navigator, Home);
            Other = Navigator.CreateRegion<INavPageViewModel>(this);
            Routes = new NavigationRouteMapBuilder()
                .Map<NavHomeViewModel>("home", (_, _) => NavigationTarget.Borrow(Home), _ => new NavigationRouteValues())
                .Map<NavEditorViewModel>("editors/{title}", (_, values) => NavigationTarget.Create(_ => new NavEditorViewModel(values.GetString("title"))),
                    editor => new NavigationRouteValues().Set("title", editor.Title))
                .Build();
            Host = new RunicWindowTestHost<NavShellViewModel>(Shell,
                (transport, content, vm) => new NavShellBridge(transport, vm, content: content),
                new RunicWindowTestHostOptions { ModelContext = Context, ViewLocator = new NavViewLocator() });
        }

        public RunicModelContext Context { get; } = new();
        public RunicNavigator Navigator { get; }
        public NavHomeViewModel Home { get; } = new();
        public NavShellViewModel Shell { get; }
        public NavigationRegion<INavPageViewModel> Other { get; }
        public NavigationRouteMap Routes { get; }
        public RunicWindowTestHost<NavShellViewModel> Host { get; }

        public async ValueTask DisposeAsync()
        {
            Host.Dispose();
            await Navigator.DisposeAsync();
            await Context.DisposeAsync();
        }
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message);
            await Task.Delay(10);
        }
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
