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

    // Two pages of one window, such as two CS-WebUI browsers: both follow the path region, and the last change wins.
    [Test]
    public async Task TwoConnectionsShareThePathRegionAsync()
    {
        await using var window = new PathWindow();
        var host = window.Host;
        using var sync = RunicPresentation.SyncNavigationPath(window.Shell.Main, window.Routes);
        host.Root.Snapshot();
        const string second = "connection-2";
        var hello = await host.Transport.RequestAsync("""{"kind":"hello","protocol":4,"session":"second-session","client":{"name":"second","version":"1"},"features":["navigation-path/1"]}""", second)
            .AsTask().WaitScaledAsync(Timeout);
        Require(hello.Contains("\"navigationPath\":\"home\"", StringComparison.Ordinal), $"The second hello: {hello}");

        await host.OpenNavigationPathAsync("editors/a").WaitScaledAsync(Timeout);
        var reply = await host.Transport.RequestAsync("""{"kind":"navigation","action":"open","path":"editors/b"}""", second).AsTask().WaitScaledAsync(Timeout);
        Require(reply.Contains("\"path\":\"editors/b\"", StringComparison.Ordinal), $"The second page's open: {reply}");
        await Until(() => Pushes(host, second).SequenceEqual(["editors/a push", "editors/b push"])
                && host.NavigationPathPushes.Select(push => $"{push.Path} {push.Operation}").SequenceEqual(["editors/a push", "editors/b push"]),
            $"The connections did not both follow: {string.Join(", ", Pushes(host, second))} / {string.Join(", ", host.NavigationPathPushes)}.");
        Require(window.Shell.Main.Current is NavEditorViewModel { Title: "b" }, "The last change did not win.");
    }

    // The window's Back skips a region that no Bridge presents, however recently it was created.
    [Test]
    public async Task BackSkipsRegionsNoBridgePresentsAsync()
    {
        await using var window = new PathWindow();
        var host = window.Host;
        host.Root.Snapshot();
        await window.Other.PushAsync(NavigationTarget.Own<INavPageViewModel>(new NavEditorViewModel("hidden 1"))).AsTask().WaitScaledAsync(Timeout);
        await window.Other.PushAsync(NavigationTarget.Own<INavPageViewModel>(new NavEditorViewModel("hidden 2"))).AsTask().WaitScaledAsync(Timeout);
        var none = await host.NavigateBackAsync().WaitScaledAsync(Timeout);
        Require(none.Outcome?.Rejection == NavigationRejection.NoHistory && window.Other.History.Count == 1,
            $"Back went back in a region no Bridge presents: {none.Json}");
        await window.Shell.Main.PushAsync(NavigationTarget.Own<INavPageViewModel>(new NavEditorViewModel("a"))).AsTask().WaitScaledAsync(Timeout);
        var back = await host.NavigateBackAsync().WaitScaledAsync(Timeout);
        Require(back.Outcome?.Kind == NavigationOutcomeKind.Committed && window.Shell.Main.Current is NavHomeViewModel && window.Other.History.Count == 1,
            $"Back did not go back in the presented region: {back.Json}");
    }

    // A page that said hello before the window had a path region learns it when the region appears.
    [Test]
    public async Task ALatePathRegionIsAnnouncedAsync()
    {
        await using var window = new PathWindow();
        var host = window.Host;
        host.Root.Snapshot();
        var back = await host.NavigateBackAsync().WaitScaledAsync(Timeout);
        Require(!back.HasPath && host.NavigationPathPushes.Count == 0, "A window without a path region pushed a path.");
        using var sync = RunicPresentation.SyncNavigationPath(window.Shell.Main, window.Routes);
        await Until(() => host.NavigationPathPushes is [("home", "replace")], $"The late path region was not announced: {string.Join(", ", host.NavigationPathPushes)}.");
    }

    private static string[] Pushes(RunicWindowTestHost<NavShellViewModel> host, string connection) =>
    [
        .. host.Transport.Messages.Where(message => message.ConnectionKey == connection && message.Kind == "navigation-path")
            .Select(static message =>
            {
                using var document = System.Text.Json.JsonDocument.Parse(message.Json);
                return $"{document.RootElement.GetProperty("path").GetString()} {document.RootElement.GetProperty("operation").GetString()}";
            }),
    ];

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
