using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests;

// Session-level content lifecycle: re-exposure of a still-mounted reference,
// and release of per-item state for churning collections.
public static class ContentLifecycleTests
{
    public static async Task RunAsync()
    {
        await ReexposedReferenceKeepsItsBrowserMount();
        await UnmountedSuspendedReferenceReleasesItsEndpoints();
        ReconnectedBrowserRemountsWithItsToken();
        CollectionChurnReleasesItemState();
    }

    // A transport reconnect keeps the page, so the generated client re-sends
    // its existing mount token from the new connection. Until .NET observes
    // the former connection's disconnect that token is still owned by it.
    private static void ReconnectedBrowserRemountsWithItsToken()
    {
        var root = new Item();
        var views = new ViewCounter();
        using var transport = new InMemoryViewTransport();
        using var session = new WindowContentSession(transport, rootModel: root);
        var route = $"content{Present(session, root, new Item(), views).Id}";
        ViewTestArguments From(string connection) =>
            new(StringValue: "browser:page", ClientKey: "client", ConnectionKey: connection);
        Require(transport.Call($"{route}Mount", From("connection-one")) == "ok" && views.Attached == 1,
            "The presentation did not mount.");
        Require(transport.Call($"{route}Mount", From("connection-two")) == "ignored",
            "A new connection took over a token its former connection still owns.");
        session.ReleaseConnection("connection-one");
        Require(views.Detached == 1, "Releasing the former connection did not release its View.");
        Require(transport.Call($"{route}Mount", From("connection-two")) == "ok" && views.Attached == 2,
            "The reconnected browser could not remount its presentation.");
        Require(transport.Call($"{route}Mount", From("connection-one")) == "disconnected",
            "The closed connection could remount its former browser session.");
    }

    // A command doing `Main = b; Main = a;` can publish a parent snapshot whose
    // reference never changed for the browser, so the browser keeps its mount.
    private static async Task ReexposedReferenceKeepsItsBrowserMount()
    {
        var root = new Item();
        var a = new Item();
        var b = new Item();
        var views = new ViewCounter();
        using var transport = new InMemoryViewTransport();
        using var session = new WindowContentSession(transport, rootModel: root);
        var reference = Present(session, root, a, views);
        var route = $"content{reference.Id}";
        Require(transport.Call($"{route}Mount", Browser("browser:a")) == "ok", "The first presentation did not mount.");
        Require(views.Attached == 1, "The mounted presentation did not create its View.");
        var before = Revision(transport, route);

        Require(Present(session, root, b, views) != reference, "A different model reused the reference.");
        Require(views.Detached == 1 && !transport.Routes.Contains($"{route}Snapshot"),
            "Replacing the content did not release its View and bridge.");
        Require(session.DormantAttachmentCount == 1 && transport.Routes.Contains($"{route}Unmount"),
            "A still-mounted suspended presentation did not keep its unmount endpoint.");

        Require(Present(session, root, a, views) == reference, "Re-exposing the model changed its reference.");
        await session.ModelContext!.InvokeAsync(() => { }); // the remount is a later model turn
        Require(views.Attached == 2 && session.DormantAttachmentCount == 0,
            "The browser's retained mount did not receive a View on the re-attached bridge.");
        var after = Revision(transport, route);
        Require(after > before, $"The re-attached bridge regressed the route revision from {before} to {after}.");
        a.Value = 1;
        Require(await PublishedRevisionAsync(transport, route) > after,
            "A publication from the re-attached bridge did not advance the route revision.");

        Require(transport.Call($"{route}Unmount", Browser("browser:a")) == "ok"
            && views.Detached == 2, "Unmounting the resumed presentation did not release its View.");
    }

    private static async Task UnmountedSuspendedReferenceReleasesItsEndpoints()
    {
        var root = new Item();
        var a = new Item();
        var views = new ViewCounter();
        using var transport = new InMemoryViewTransport();
        using var session = new WindowContentSession(transport, rootModel: root);
        var reference = Present(session, root, a, views);
        var route = $"content{reference.Id}";
        Require(transport.Call($"{route}Mount", Browser("browser:a")) == "ok", "The presentation did not mount.");
        session.Clear(root, "Main");
        Require(transport.Call($"{route}Unmount", Browser("browser:a")) == "ok",
            "The browser could not unmount a suspended presentation.");
        Require(session.DormantAttachmentCount == 0 && !transport.Routes.Contains($"{route}Mount"),
            "A suspended presentation kept its endpoints after the browser unmounted it.");

        Require(Present(session, root, a, views) == reference, "Re-exposing the model changed its reference.");
        await session.ModelContext!.InvokeAsync(() => { });
        Require(views.Attached == 1, "An unmounted browser presentation was recreated.");
    }

    private static void CollectionChurnReleasesItemState()
    {
        var root = new Item();
        using var transport = new InMemoryViewTransport();
        using var session = new WindowContentSession(transport, rootModel: root);
        var released = Churn(session, root, count: 32);
        Require(session.RetainedContentModelLeaseCount == 1,
            $"Pruned collection items retained {session.RetainedContentModelLeaseCount} context leases.");
        Require(session.FieldWrites.RetainedModelCount == 1,
            $"Pruned collection items retained {session.FieldWrites.RetainedModelCount} field registries.");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Require(released.All(reference => !reference.TryGetTarget(out _)),
            "The window retained pruned collection items.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference<Item>> Churn(WindowContentSession session, Item root, int count)
    {
        var released = new List<WeakReference<Item>>();
        Item? previous = null;
        for (var index = 0; index != count; index++)
        {
            var item = new Item();
            var reference = session.PresentItem(root, "Items", "item", item,
                (transport, current, route) => new CheckedBridge(transport, current, route, session));
            session.PruneCollection(root, "Items", new HashSet<string>([reference.Id]));
            if (previous is not null)
            {
                Require(!RunicModelContextRegistry.Shared.TryGet(previous, out _),
                    "A pruned collection item kept its model-context binding.");
                released.Add(new(previous));
            }
            previous = item;
        }
        return released;
    }

    private static PageReference Present(WindowContentSession session, Item root, Item model, ViewCounter views) =>
        session.Present(root, "Main", "item", model, (_, current, route) =>
            session.AttachPresentation<CountedView, Item>(current, route,
                (transport, presented, presentedRoute) => new CheckedBridge(transport, presented, presentedRoute, session),
                () => new CountedView(views)));

    private static ViewTestArguments Browser(string token) =>
        new(StringValue: token, ClientKey: "client", ConnectionKey: "connection");

    private static long Revision(InMemoryViewTransport transport, string route)
    {
        using var reply = JsonDocument.Parse(transport.Call($"{route}Snapshot"));
        return reply.RootElement.GetProperty("state").GetProperty("revision").GetInt64();
    }

    private static async Task<long> PublishedRevisionAsync(InMemoryViewTransport transport, string route)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var publication in transport.DrainPublications().Where(value => value.Route == route))
            {
                using var state = JsonDocument.Parse(publication.StateJson);
                return state.RootElement.GetProperty("revision").GetInt64();
            }
            await Task.Delay(10);
        }
        return -1;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Item : INotifyPropertyChanged
    {
        private int _value;
        public int Value
        {
            get => _value;
            set { _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class CheckedBridge(IBridgeTransport transport, Item model, string route, WindowContentSession content)
        : ViewModelBridge<Item>(transport, model, route, static (writer, vm, revision, writeFields) =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("revision", revision);
            writer.WriteNumber("value", vm.Value);
            writeFields(writer);
            writer.WriteEndObject();
        }, [],
        [new("Value", "value", CheckedFieldValueKind.Int32, vm => vm.Value, (vm, value) => vm.Value = (int)value!)],
        [], contractFingerprint: "content-lifecycle", content: content);

    private sealed class ViewCounter
    {
        public int Attached { get; set; }
        public int Detached { get; set; }
    }

    // Implements IRunicView directly so the bridge generator does not treat
    // this private probe as an application View.
    private sealed class CountedView(ViewCounter counter) : IRunicView, IRunicViewLifetime
    {
        public object? DataContext { get; set; }
        public void OnAttached() => counter.Attached++;
        public void OnDetached() => counter.Detached++;
    }
}
