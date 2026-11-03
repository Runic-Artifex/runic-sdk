using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Core;

namespace Runic.Navigation.Tests;

// 0.16 D2: route maps, navigation paths (GetPath / RestoreAsync) and routes opened from deep links.
internal sealed partial class NavigationTests
{
    private static void Routes(NavigationRouteMapBuilder routes) => routes
        .Map<CustomersPage>("customers")
        .Map<CustomerPage, int>("customers/{id:int}")
        .Map<InvoicePage, InvoiceRef>("customers/{id:int}/invoices/{invoice:int}",
            values => new InvoiceRef(values.GetInt32("id"), values.GetInt32("invoice")),
            invoice => new NavigationRouteValues().Set("id", invoice.Customer).Set("invoice", invoice.Invoice))
        .Map<OverviewPage>("overview")
        .Map<NotesPage, string>("notes/{topic}")
        .Map<DocumentPage, int>("documents/{id:int}")
        .Map<DocumentInfoPage>("documents/info");

    // ---- Route map ----------------------------------------------------------------------------------------------------

    [Test]
    public async Task RouteMapCreatesTargetsAndNamesEntriesAsync()
    {
        await using var app = new RoutedApp();
        var map = app.Map;
        var main = app.Main;
        Require(map.CreateTarget(main, "/Customers/42/") is INavigationTarget<IRoutePage> target && await Wait(main.PushAsync(target)) is NavigationResult<IRoutePage>.Committed
                && main.Current is CustomerPage { Id: 42 } && map.GetRoute(main.CurrentEntry!) == "customers/42",
            "customers/42 did not open the customer page or name its entry.");
        // An entry pushed without the map has the route its content and input describe.
        await Wait(main.PushAsync<InvoicePage, InvoiceRef>(new InvoiceRef(42, 7)));
        Require(map.GetRoute(main.CurrentEntry!) == "customers/42/invoices/7", $"The invoice is named {map.GetRoute(main.CurrentEntry!)}.");
        await Wait(main.PushAsync<NotesPage, string>("a b/c"));
        Require(map.GetRoute(main.CurrentEntry!) == "notes/a%20b%2Fc" && map.CreateTarget(main, "notes/a%20b%2Fc") is not null,
            $"A string parameter was not percent-encoded: {map.GetRoute(main.CurrentEntry!)}.");
        Require(map.GetRoute(main.History[0]) is null, "The borrowed home page has a route.");

        Require(map.CreateTarget(main, "customers/x") is null && map.CreateTarget(main, "customers/42/extra") is null
                && map.CreateTarget(main, "") is null && map.CreateTarget(main, "customers//42") is null
                && map.CreateTarget(main, "customers/99999999999") is null,
            "A route that matches no template created a target.");
        Require(map.TryMatch("CUSTOMERS/+42", out var canonical, out var values) && canonical == "customers/42" && values.GetInt32("id") == 42,
            $"TryMatch did not canonicalize: {canonical}.");
        // A route for a content type the region can't present doesn't match there.
        var other = app.Navigator.CreateRegion<OtherPage>(app.Root);
        Require(map.CreateTarget(other, "customers") is null, "A route matched a region of another content type.");

        Require(Throws<ArgumentException>(() => new NavigationRouteMapBuilder().Map<CustomerPage, int>("customers/{id}")),
            "A string parameter was accepted for an int input.");
        Require(Throws<ArgumentException>(() => new NavigationRouteMapBuilder().Map<CustomersPage>("customers/{id:int}")),
            "A parameter was accepted for a route without input.");
        Require(Throws<ArgumentException>(() => new NavigationRouteMapBuilder().Map<CustomerPage, int>("a/{id:int}/{ID:int}", _ => 1, _ => null)),
            "A parameter declared twice was accepted.");
        Require(Throws<ArgumentException>(() => new NavigationRouteMapBuilder().Map<CustomerPage, int>("a/{id:decimal}")),
            "An unknown parameter type was accepted.");
    }

    [Test]
    public async Task RouteMapResolvesFromTheNavigatorServicesAsync()
    {
        await using var fixture = new Fixture();
        var main = fixture.Navigator.CreateRegion<IRoutePage>(fixture.Root);
        Require(Throws<InvalidOperationException>(() => fixture.Navigator.GetPath()), "GetPath without a route map did not throw.");
        await using var app = new RoutedApp();
        Require(ReferenceEquals(app.Services.GetRequiredService<INavigationRouteMap>(), app.Services.GetRequiredService<NavigationRouteMap>())
                && app.Navigator.GetPath().Regions.Count == 1,
            "AddRunicNavigationRoutes did not register the map for the navigator.");
    }

    [Test]
    public void RouteLinksReadDeepLinks()
    {
        string[] schemes = ["myapp"];
        Require(NavigationRouteLinks.TryGetRoute(new Uri("myapp://invoice/7?from=mail#top"), schemes, out var route) && route == "invoice/7",
            $"myapp://invoice/7 gave {route}.");
        Require(NavigationRouteLinks.TryGetRoute(new Uri("MYAPP:customers/42"), schemes, out route) && route == "customers/42",
            $"myapp:customers/42 gave {route}.");
        Require(NavigationRouteLinks.TryGetRoute(new Uri("https://app.example.org/customers/42"), schemes, out route, ["APP.example.org"])
                && route == "customers/42",
            $"A web link gave {route}.");
        Require(!NavigationRouteLinks.TryGetRoute(new Uri("https://evil.example.org/customers/42"), schemes, out _, ["app.example.org"])
                && !NavigationRouteLinks.TryGetRoute(new Uri("other://invoice/7"), schemes, out _)
                && !NavigationRouteLinks.TryGetRoute(new Uri("myapp://user@invoice/7"), schemes, out _)
                && !NavigationRouteLinks.TryGetRoute(new Uri("myapp://"), schemes, out _),
            "A link of another scheme or host, with user information or without a route gave a route.");
    }

    // ---- Paths --------------------------------------------------------------------------------------------------------

    // Customers → Customer 42 (with its second tab selected and a page pushed in it) → Invoice 7, plus two document tabs,
    // saved as JSON, survive a restart: a new navigator restores the same path through guarded, initialized navigation.
    [Test]
    public async Task PathSurvivesARestartAsync()
    {
        string json;
        await using (var before = new RoutedApp())
        {
            await Wait(before.Main.OpenRouteAsync("customers/42/invoices/7", NavigationRouteOpen.Stack));
            var saved = (CustomerPage)before.Main.History[2].Content;
            await Wait(saved.Tabs.SelectAsync(saved.Notes));
            await Wait(saved.Notes.PushAsync<NotesPage, string>("billing"));
            await Wait(before.Documents.OpenAsync(1));
            await Wait(before.Documents.OpenAsync(2));
            await Wait(((NavigationRegion<IRoutePage>)before.Documents.Tabs.Regions[0]).PushAsync<DocumentInfoPage>());
            await Wait(before.Documents.Tabs.SelectAsync(before.Documents.Tabs.Regions[1]));
            json = before.Navigator.GetPath().ToJson();
        }
        Require(json.Contains("\"route\":\"customers/42/invoices/7\"", StringComparison.Ordinal)
                && json.Contains("\"route\":\"notes/billing\"", StringComparison.Ordinal)
                && json.Contains("\"route\":\"documents/info\"", StringComparison.Ordinal),
            $"The saved path misses entries: {json}");

        await using var after = new RoutedApp();
        var restored = await Wait(after.Navigator.RestoreAsync(NavigationPath.FromJson(json)));
        Require(restored.IsComplete, $"The restore reported {string.Join("; ", restored.Issues)}.");
        Require(after.Navigator.GetPath().ToJson() == json, $"The restored path differs:\n{after.Navigator.GetPath().ToJson()}\n{json}");
        Require(after.Main.Current is InvoicePage { Input: { Customer: 42, Invoice: 7 }, Arrived: 1 }
                && after.Main.History is [{ Content: RouteHomePage }, { Content: CustomersPage }, { Content: CustomerPage { Id: 42 } customer }]
                && customer.Tabs.Selected == customer.Notes && customer.Notes.Current is NotesPage { Topic: "billing" },
            "The main region or the customer's tabs were not restored.");
        Require(after.Documents.Tabs.Regions.Count == 2 && after.Documents.Tabs.SelectedIndex == 1
                && after.Documents.Tabs.Regions[0].Current is DocumentInfoPage
                && after.Documents.Tabs.Regions[1].Current is DocumentPage { Id: 2 },
            "The document tabs were not restored.");
    }

    // Restoring keeps the entries that already match from the bottom, goes back to the last of them through the guards,
    // and pushes the rest.
    [Test]
    public async Task RestoreReconcilesWithTheCurrentStackAsync()
    {
        await using var app = new RoutedApp();
        await Wait(app.Main.OpenRouteAsync("customers/1/invoices/3", NavigationRouteOpen.Stack));
        var customers = app.Main.History[1];
        var invoice = (InvoicePage)app.Main.Current!;
        var target = new NavigationPath(
            [new NavigationRegionPath([new(null), new("customers"), new("customers/42")])], []);
        var result = await Wait(app.Navigator.RestoreAsync(target));
        Require(result.IsComplete && Names(app.Main) == "home,customers,customer 42" && ReferenceEquals(app.Main.History[1], customers)
                && invoice.Departures is [{ Kind: NavigationDepartureKind.Retire, Operation: NavigationOperation.BackTo }],
            $"The restore did not back to the kept entries and push the rest: {Names(app.Main)}, {result}.");

        // A guard that refuses stops the region; the result says where.
        ((CustomerPage)app.Main.Current!).Refuse = true;
        result = await Wait(app.Navigator.RestoreAsync(new NavigationPath([new NavigationRegionPath([new(null), new("overview")])], [])));
        Require(result.Issues is [{ Kind: NavigationRestoreIssueKind.NotCommitted, Location: "regions[0]/entries[1]", Outcome.Rejection: NavigationRejection.Guard }]
                && Names(app.Main) == "home,customers,customer 42",
            $"A refusing guard did not keep the region: {string.Join("; ", result.Issues)}.");
    }

    // What can't be restored is left out and reported, and the rest is restored.
    [Test]
    public async Task RestoreFailsSoftlyAsync()
    {
        await using var app = new RoutedApp();
        var path = new NavigationPath(
            [
                new NavigationRegionPath([new(null), new("customers"), new("gone/7"), new("customers/5")]),
                new NavigationRegionPath([new("customers")]),
            ],
            [
                new NavigationSelectionPath([new NavigationRegionPath([new("documents/9")])], 0),
                new NavigationSelectionPath([], -1),
            ]);
        var result = await Wait(app.Navigator.RestoreAsync(path));
        Require(result.Issues.Select(issue => (issue.Kind, issue.Location, issue.Route)).SequenceEqual([
                (NavigationRestoreIssueKind.UnmappedRoute, "regions[0]/entries[2]", (string?)"gone/7"),
                (NavigationRestoreIssueKind.MissingRegion, "regions[1]", null),
                (NavigationRestoreIssueKind.MissingSelection, "selections[1]", null),
            ]),
            $"The issues were {string.Join("; ", result.Issues)}.");
        Require(Names(app.Main) == "home,customers,customer 5" && app.Documents.Tabs.Regions is [{ Current: DocumentPage { Id: 9 } }],
            "The parts that could be restored were not.");
        Require(app.Logs.Count(1220) == 3 && app.Logs.Has(1220, LogLevel.Information), "The left-out parts were not logged as event 1220.");

        // Without RestoreRegion, tabs beyond the selection's regions are left out.
        var bare = app.Navigator.CreateSelection(app.Root, []);
        result = await Wait(bare.RestoreAsync(new NavigationSelectionPath([new NavigationRegionPath([new("documents/1")])], 0)));
        Require(result.Issues is [{ Kind: NavigationRestoreIssueKind.MissingSelectionRegion, Location: "regions[0]" }] && bare.Regions.Count == 0,
            $"A tab was restored without RestoreRegion: {result}.");
    }

    [Test]
    public async Task OpenRouteModesAsync()
    {
        await using var app = new RoutedApp();
        var main = app.Main;
        Require(await Wait(main.OpenRouteAsync("nowhere")) is null && Names(main) == "home" && app.Logs.Count(1221) == 1,
            "An unmatched route navigated or was not logged.");
        Require(await Wait(main.OpenRouteAsync("customers")) is NavigationResult<object>.Committed && Names(main) == "home,customers",
            "Push did not push.");
        var same = main.CurrentEntry!.Id;
        Require(await Wait(main.OpenRouteAsync("customers")) is NavigationResult<object>.Committed { Current.Id: var id } && id == same
                && Names(main) == "home,customers",
            "Pushing the current route again pushed a second page.");
        await Wait(main.OpenRouteAsync("customers/1"));
        await Wait(main.OpenRouteAsync("overview"));
        Require(await Wait(main.OpenRouteAsync("customers", NavigationRouteOpen.BackToOrPush)) is NavigationResult<object>.Committed
                && Names(main) == "home,customers",
            $"BackToOrPush did not go back: {Names(main)}.");
        await Wait(main.OpenRouteAsync("overview", NavigationRouteOpen.BackToOrPush));
        Require(Names(main) == "home,customers,overview", $"BackToOrPush did not push: {Names(main)}.");
        await Wait(main.OpenRouteAsync("customers/42/invoices/7", NavigationRouteOpen.Stack));
        Require(Names(main) == "home,customers,customer 42,invoice 42/7", $"Stack built {Names(main)}.");
        await Wait(main.OpenRouteAsync("customers/42", NavigationRouteOpen.Replace));
        Require(Names(main) == "home,customers,customer 42,customer 42", $"Replace gave {Names(main)}.");
        await Wait(main.OpenRouteAsync("overview", NavigationRouteOpen.Reset));
        Require(Names(main) == "overview", $"Reset gave {Names(main)}.");
    }

    [Test]
    public void PathJsonIsBoundedAndVersioned()
    {
        var path = new NavigationPath(
            [new NavigationRegionPath([new("a") { Selections = [new NavigationSelectionPath([new NavigationRegionPath([new(null)])], 0)] }])],
            []);
        var json = path.ToJson();
        Require(json == """{"version":1,"regions":[{"entries":[{"route":"a","regions":[],"selections":[{"selected":0,"regions":[{"entries":[{"route":null,"regions":[],"selections":[]}]}]}]}]}],"selections":[]}""",
            $"The JSON format changed: {json}");
        Require(NavigationPath.FromJson(json).ToJson() == json, "The JSON did not round-trip.");
        Require(NavigationPath.FromJson("""{"regions":[{"entries":[{"route":"a","extra":1}]}]}""").Regions[0].Entries[0].Route == "a",
            "Unknown properties or a missing version were refused.");
        foreach (var bad in new[] { "[]", "{\"version\":2}", "{\"regions\":{}}", "{\"regions\":[{\"entries\":[{\"route\":3}]}]}", "not json" })
            Require(Throws<FormatException>(() => NavigationPath.FromJson(bad)), $"{bad} was accepted.");
        var deep = new System.Text.StringBuilder("{\"regions\":");
        for (var level = 0; level < 40; level++) deep.Append("[{\"entries\":[{\"route\":\"a\",\"regions\":");
        deep.Append("[]");
        for (var level = 0; level < 40; level++) deep.Append("}]}]");
        deep.Append('}');
        Require(Throws<FormatException>(() => NavigationPath.FromJson(deep.ToString())), "A path nested 40 levels deep was accepted.");
    }

    // ---- Window Back ----------------------------------------------------------------------------------------------------

    // The window's Back goes back in the innermost presented region that can: the selected tab before its page, a
    // dialog before everything, and the main region last.
    [Test]
    public async Task WindowBackGoesBackInTheInnermostRegionAsync()
    {
        await using var app = new RoutedApp();
        Require(await Wait(app.Navigator.BackAsync()) is NavigationResult<object>.Rejected { Reason: NavigationRejection.NoHistory },
            "Back with no history did not report NoHistory.");
        await Wait(app.Main.OpenRouteAsync("customers/42"));
        var customer = (CustomerPage)app.Main.Current!;
        await Wait(customer.Tabs.SelectAsync(customer.Notes));
        await Wait(customer.Notes.PushAsync<NotesPage, string>("billing"));
        // The overview tab has history too, but isn't selected.
        await Wait(customer.Overview.PushAsync<NotesPage, string>("hidden"));

        Require(await Wait(app.Navigator.BackAsync()) is NavigationResult<object>.Committed && customer.Notes.Current is OverviewPage
                && customer.Overview.Current is NotesPage && app.Main.Current == customer,
            "Back did not go back in the selected tab first.");
        Require(await Wait(app.Navigator.BackAsync()) is NavigationResult<object>.Committed && app.Main.Current is RouteHomePage,
            "Back did not go back in the main region once the tab had no history.");

        // A dialog region created later is asked first; its result entry is dismissed.
        var dialogs = app.Navigator.CreateRegion<IRoutePage>(app.Root);
        var request = dialogs.PushForResult<OverviewPage, bool>();
        Require((await Wait(request.Transition)).IsCommitted, "The dialog was not pushed.");
        await Wait(app.Main.OpenRouteAsync("customers"));
        Require(await Wait(app.Navigator.BackAsync()) is NavigationResult<object>.Committed && dialogs.Current is null
                && await Wait(request.Completion) is NavigationCompletion<bool>.Dismissed && app.Main.Current is CustomersPage,
            "Back did not dismiss the dialog first.");
    }

    // ---- The application under test -----------------------------------------------------------------------------------

    private interface IRoutePage;

    private sealed record InvoiceRef(int Customer, int Invoice);

    private sealed class RoutedApp : IAsyncDisposable
    {
        public RoutedApp()
        {
            Logs = new LogCapture();
            var holder = new RouteNavigatorHolder();
            Services = new ServiceCollection()
                .AddRunicNavigationRoutes(NavigationTests.Routes)
                .AddSingleton(holder)
                .AddSingleton(_ => holder.Navigator!)
                .BuildServiceProvider();
            Context = new RunicModelContext();
            Navigator = holder.Navigator = new RunicNavigator(new RunicNavigatorOptions
            {
                ModelContext = Context,
                LoggerFactory = Logs,
                Services = Services,
            });
            // The window's regions: a main region with a borrowed home page, and document tabs.
            Main = Navigator.CreateRegion<IRoutePage>(Root, NavigationTarget.Borrow<IRoutePage>(new RouteHomePage()));
            Documents = new DocumentsHost(Navigator);
        }

        public object Root { get; } = new();
        public ServiceProvider Services { get; }
        public RunicModelContext Context { get; }
        public RunicNavigator Navigator { get; }
        public LogCapture Logs { get; }
        public NavigationRouteMap Map => Services.GetRequiredService<NavigationRouteMap>();
        public NavigationRegion<IRoutePage> Main { get; }
        public DocumentsHost Documents { get; }

        public async ValueTask DisposeAsync()
        {
            await Navigator.DisposeAsync();
            await Context.DisposeAsync();
            await Services.DisposeAsync();
        }
    }

    private sealed class RouteNavigatorHolder
    {
        public RunicNavigator? Navigator { get; set; }
    }

    private sealed class DocumentsHost
    {
        private readonly RunicNavigator _navigator;

        public DocumentsHost(RunicNavigator navigator)
        {
            _navigator = navigator;
            Tabs = navigator.CreateSelection(this, [], new NavigationSelectionOptions { RestoreRegion = _ => navigator.CreateRegion<IRoutePage>(this) });
        }

        public NavigationSelection Tabs { get; }

        public async Task OpenAsync(int id)
        {
            var tab = _navigator.CreateRegion<IRoutePage>(this);
            await tab.PushAsync<DocumentPage, int>(id);
            await _navigator.ModelContext.InvokeAsync(() => Tabs.Add(tab));
        }
    }

    private abstract class RoutePage(string name) : IRoutePage, INavigationDepartureGuard
    {
        public List<NavigationDeparture> Departures { get; } = [];
        public bool Refuse { get; set; }

        public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken)
        {
            lock (Departures) Departures.Add(departure);
            return ValueTask.FromResult(!Refuse);
        }

        public override string ToString() => name;
    }

    private sealed class RouteHomePage() : RoutePage("home");

    private sealed class OtherPage;

    private sealed class CustomersPage() : RoutePage("customers");

    private sealed class OverviewPage() : RoutePage("overview");

    private sealed class DocumentInfoPage() : RoutePage("info");

    // A customer with two tabs, each with its own stack.
    private sealed class CustomerPage : RoutePage, INavigationInitialize<int>
    {
        public CustomerPage(RunicNavigator navigator) : base("customer")
        {
            Overview = navigator.CreateRegion<IRoutePage>(this, NavigationTarget.Create<OverviewPage>());
            Notes = navigator.CreateRegion<IRoutePage>(this, NavigationTarget.Create<OverviewPage>());
            Tabs = navigator.CreateSelection(this, [Overview, Notes]);
        }

        public int Id { get; private set; }
        public NavigationRegion<IRoutePage> Overview { get; }
        public NavigationRegion<IRoutePage> Notes { get; }
        public NavigationSelection Tabs { get; }

        public ValueTask InitializeAsync(NavigationEntryContext entry, int input, CancellationToken cancellationToken)
        {
            Id = input;
            return ValueTask.CompletedTask;
        }

        public override string ToString() => $"customer {Id}";
    }

    private sealed class InvoicePage() : RoutePage("invoice"), INavigationArrived<InvoiceRef>
    {
        public InvoiceRef? Input { get; private set; }
        public int Arrived;

        public ValueTask OnArrivedAsync(NavigationEntryContext entry, NavigationArrival arrival, InvoiceRef input, CancellationToken cancellationToken)
        {
            Input = input;
            Interlocked.Increment(ref Arrived);
            return ValueTask.CompletedTask;
        }

        public override string ToString() => $"invoice {Input?.Customer}/{Input?.Invoice}";
    }

    private sealed class NotesPage() : RoutePage("notes"), INavigationInitialize<string>
    {
        public string? Topic { get; private set; }

        public ValueTask InitializeAsync(NavigationEntryContext entry, string input, CancellationToken cancellationToken)
        {
            Topic = input;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DocumentPage() : RoutePage("document"), INavigationInitialize<int>
    {
        public int Id { get; private set; }

        public ValueTask InitializeAsync(NavigationEntryContext entry, int input, CancellationToken cancellationToken)
        {
            Id = input;
            return ValueTask.CompletedTask;
        }
    }

    private static string Names(NavigationRegion<IRoutePage> region) =>
        string.Join(",", region.History.Select(entry => entry.Content.ToString()).Append(region.Current?.ToString()));
}
