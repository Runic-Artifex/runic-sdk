using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Views;
using Runic.Application.Views.Generated;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests.Conformance;

// The tabs View (specs/application/conformance/README.md, "Test Views"): region collections (region-collections/1).
// documents is a [RunicRegionCollection] NavigationSelection of three regions that each present a page View, created
// first on the window's own navigator, so their keys are 1, 2 and 3; pinned is an empty one the page may not close. Each
// page's departure guard refuses while the model's veto is set; after holdGuard, the next ask waits for settleGuard or
// for its cancellation, and asks counts every ask. The hand-written Bridge writes the collections as the generator does.
internal sealed class TabsViewModel : ConformanceModel, IDisposable
{
    private static readonly string[] Titles = ["one", "two", "three"];
    private readonly RunicNavigator _navigator;
    // holdGuard: the answer the next guard ask waits for, until an ask claims it; settleGuard completes it.
    private TaskCompletionSource<bool>? _held;
    private TaskCompletionSource<bool>? _unclaimed;
    private int _asks;
    // navigation-path/1: with arrange.model.path, the first document region is the window's path region.
    private IDisposable? _path;

    private TabsViewModel(RunicNavigator navigator)
    {
        _navigator = navigator;
        Documents = navigator.CreateSelection(this, [.. Titles.Select(title =>
            (NavigationRegion)navigator.CreateRegion(this, NavigationTarget.Own(new TabsPageViewModel(this, title))))]);
        Pinned = navigator.CreateSelection(this, []);
    }

    [RunicRegionCollection(typeof(TabsPageViewModel))]
    public NavigationSelection Documents { get; }

    [RunicRegionCollection(typeof(TabsPageViewModel), PageCanClose = false)]
    public NavigationSelection Pinned { get; }

    // Not a state member: while it is set, every page refuses to be left or closed.
    internal bool Veto { get; private set; }

    // Created in a model turn of the window's context, on a navigator of its own.
    internal static TabsViewModel Create(JsonObject model, IRunicModelContext context)
    {
        var vm = new TabsViewModel(new RunicNavigator(new RunicNavigatorOptions { ModelContext = context }));
        if (model["veto"] is { } veto) vm.Veto = veto.GetValue<bool>();
        if (model["path"]?.GetValue<bool>() == true) vm.SyncPath();
        return vm;
    }

    // navigation-path/1: the first document region becomes the window's path region, with the route pages/{title}.
    private void SyncPath()
    {
        var vm = this;
        _path = RunicPresentation.SyncNavigationPath(Documents.Regions[0], new NavigationRouteMapBuilder()
            .Map<TabsPageViewModel>("pages/{title}",
                (_, values) => NavigationTarget.Create(_ => new TabsPageViewModel(vm, values.GetString("title"))),
                page => new NavigationRouteValues().Set("title", page.Title))
            .Build());
    }

    // A page's departure guard: counts the ask, waits for a held answer, or refuses while veto is set.
    internal async ValueTask<bool> CanDepartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _asks);
        var held = Interlocked.Exchange(ref _unclaimed, null);
        return held is null ? !Veto : await held.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal override void Apply(JsonObject mutation, Func<IDisposable>? unit)
    {
        switch (mutation["op"]!.GetValue<string>())
        {
            case "assign" when mutation["member"]!.GetValue<string>() == "veto":
                Veto = mutation["value"]!.GetValue<bool>();
                break;
            case "holdGuard":
                _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _unclaimed, _held);
                break;
            case "syncPath":
                if (_path is null) SyncPath();
                break;
            case "settleGuard":
                (_held ?? throw new InvalidOperationException("settleGuard without holdGuard.")).TrySetResult(mutation["allow"]!.GetValue<bool>());
                break;
            default:
                throw new NotSupportedException($"The tabs View has no mutation {mutation.ToJsonString()}.");
        }
    }

    internal override JsonNode? Read(string member) => member switch
    {
        "veto" => Veto,
        "asks" => Volatile.Read(ref _asks),
        _ => throw new NotSupportedException($"The tabs View has no member {member}."),
    };

    public void Dispose()
    {
        _path?.Dispose();
        _navigator.Dispose();
    }
}

// The page View that each region of the tabs View presents.
internal sealed class TabsPageViewModel(TabsViewModel owner, string title) : ConformanceModel, INavigationDepartureGuard
{
    public string Title { get; } = title;

    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        owner.CanDepartAsync(cancellationToken);

    internal override void Apply(JsonObject mutation, Func<IDisposable>? unit) =>
        throw new NotSupportedException("The page View has no mutations.");

    internal override JsonNode? Read(string member) => member == "title" ? Title
        : throw new NotSupportedException($"The page View has no member {member}.");
}

internal sealed class TabsPageBridge(IBridgeTransport transport, TabsPageViewModel vm, string route, WindowContentSession content)
    : ViewModelBridge(transport, vm, typeof(TabsPageViewModel), route,
        [new("title", ConformanceWire.Value<TabsPageViewModel>(model => model.Title))],
        [new("Title", "title", model => ((TabsPageViewModel)model).Title, null) { TextMember = true }],
        [], [], "conformance-page", content);

internal sealed class TabsBridge(IBridgeTransport transport, TabsViewModel vm, WindowContentSession content)
    : ViewModelBridge(transport, vm, typeof(TabsViewModel), "tabs",
        [
            new("documents", (writer, model) => WriteRegions(writer, ((TabsViewModel)model), ((TabsViewModel)model).Documents, "Documents", content)),
            new("pinned", (writer, model) => WriteRegions(writer, ((TabsViewModel)model), ((TabsViewModel)model).Pinned, "Pinned", content)),
        ],
        [
            new("Documents", "documents", model => ((TabsViewModel)model).Documents, null, static model => ((TabsViewModel)model).Documents) { RegionCollection = true, RegionClose = true },
            new("Pinned", "pinned", model => ((TabsViewModel)model).Pinned, null, static model => ((TabsViewModel)model).Pinned) { RegionCollection = true, RegionClose = false },
        ],
        [], [], "conformance-tabs", content)
{
    // {regions: [{key, content}], selected}: each region's Current is presented like an item of a content collection,
    // so content that no region presents any more is pruned.
    private static void WriteRegions(Utf8JsonWriter writer, TabsViewModel model, NavigationSelection selection, string property,
        WindowContentSession content)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        writer.WriteStartObject();
        writer.WriteStartArray("regions");
        foreach (var region in selection.Regions)
        {
            writer.WriteStartObject();
            writer.WriteString("key", BridgeRegionCollections.Key(region));
            writer.WritePropertyName("content");
            if (region.Current is TabsPageViewModel page)
            {
                var reference = content.PresentItem(model, property, "page", page,
                    (pageTransport, current, route) => new TabsPageBridge(pageTransport, current, route, content));
                active.Add(reference.Id);
                writer.WriteStartObject();
                writer.WriteString("kind", reference.Kind);
                writer.WriteString("id", reference.Id);
                writer.WriteEndObject();
            }
            else writer.WriteNullValue();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (selection.SelectedIndex >= 0) writer.WriteNumber("selected", selection.SelectedIndex);
        else writer.WriteNull("selected");
        writer.WriteEndObject();
        content.PruneCollection(model, property, active);
    }
}

// The tabs window's Bridge, then its navigator.
internal sealed class TabsAttachment(TabsBridge bridge, TabsViewModel vm) : IDisposable
{
    public void Dispose()
    {
        try { bridge.Dispose(); }
        finally { vm.Dispose(); }
    }
}
