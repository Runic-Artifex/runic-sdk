using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Views;
using Runic.Application.Views.Generated;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests.Conformance;

// The tabs View (specs/application/conformance/README.md, "Test Views"): region collections (region-collections/1).
// documents is a [RunicRegionCollection] NavigationSelection of three regions that each present a page View, created
// first on the window's own navigator, so their keys are 1, 2 and 3; pinned is an empty one the page may not close. Each
// page's departure guard refuses while the model's veto is set. The hand-written Bridge writes the collections as the
// generator does.
internal sealed class TabsViewModel : ConformanceModel, IDisposable
{
    private static readonly string[] Titles = ["one", "two", "three"];
    private readonly RunicNavigator _navigator;

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
        return vm;
    }

    internal override void Apply(JsonObject mutation, Func<IDisposable>? unit)
    {
        if (mutation["op"]!.GetValue<string>() == "assign" && mutation["member"]!.GetValue<string>() == "veto")
            Veto = mutation["value"]!.GetValue<bool>();
        else throw new NotSupportedException($"The tabs View has no mutation {mutation.ToJsonString()}.");
    }

    internal override JsonNode? Read(string member) => member switch
    {
        "veto" => Veto,
        _ => throw new NotSupportedException($"The tabs View has no member {member}."),
    };

    public void Dispose() => _navigator.Dispose();
}

// The page View that each region of the tabs View presents.
internal sealed class TabsPageViewModel(TabsViewModel owner, string title) : ConformanceModel, INavigationDepartureGuard
{
    public string Title { get; } = title;

    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        ValueTask.FromResult(!owner.Veto);

    internal override void Apply(JsonObject mutation, Func<IDisposable>? unit) =>
        throw new NotSupportedException("The page View has no mutations.");

    internal override JsonNode? Read(string member) => member == "title" ? Title
        : throw new NotSupportedException($"The page View has no member {member}.");
}

internal sealed class TabsPageBridge(IBridgeTransport transport, TabsPageViewModel vm, string route, WindowContentSession content)
    : ViewModelBridge<TabsPageViewModel>(transport, vm, route,
        [new("title", ConformanceWire.Value<TabsPageViewModel>(model => model.Title))],
        [new("Title", "title", model => model.Title, null) { TextMember = true }],
        [], [], "conformance-page", content);

internal sealed class TabsBridge(IBridgeTransport transport, TabsViewModel vm, WindowContentSession content)
    : ViewModelBridge<TabsViewModel>(transport, vm, "tabs",
        [
            new("documents", (writer, model) => WriteRegions(writer, model, model.Documents, "Documents", content)),
            new("pinned", (writer, model) => WriteRegions(writer, model, model.Pinned, "Pinned", content)),
        ],
        [
            new("Documents", "documents", model => model.Documents, null, static model => model.Documents) { RegionCollection = true, RegionClose = true },
            new("Pinned", "pinned", model => model.Pinned, null, static model => model.Pinned) { RegionCollection = true, RegionClose = false },
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
