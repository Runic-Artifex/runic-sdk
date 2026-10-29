using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Session-level content lifecycle: re-exposure of a still-mounted reference,
// and release of per-item state for churning collections.
public sealed class ContentLifecycleTests
{
    // A transport reconnect keeps the page, so the generated client re-sends
    // its existing presentation id from the new connection. Until .NET observes
    // the former connection's close that id is still owned by it.
    [Test]
    public void ReconnectedBrowserRemountsWithItsToken()
    {
        var root = new Item();
        var views = new ViewCounter();
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { RootModel = root });
        var route = $"content{Present(session, root, new Item(), views).Id}";
        Require(Hello(transport, "connection-one", "page-session-one") == "ok", "The first connection's hello failed.");
        Require(Open(transport, route, "page", "connection-one") == "ok" && views.Attached == 1,
            "The presentation did not mount.");
        Require(Open(transport, route, "page", "connection-two") == "ignored",
            "A new connection took over a presentation its former connection still owns.");
        transport.CloseConnection("connection-one");
        Require(views.Detached == 1, "Closing the former connection did not release its View.");
        Require(Open(transport, route, "page", "connection-two") == "ok" && views.Attached == 2,
            "The reconnected browser could not reopen its presentation.");
        // Protocol 4: the session is in the hello, and a closed connection's session cannot greet again.
        Require(Hello(transport, "connection-one", "page-session-one") == "released",
            "The closed connection could resume its former browser session.");
    }

    // A command doing `Main = b; Main = a;` can publish a parent snapshot whose
    // reference never changed for the browser, so the browser keeps its mount.
    [Test]
    public void MountTokensAreBoundedPerOwner()
    {
        var root = new Item();
        var views = new ViewCounter();
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { RootModel = root });
        var route = $"content{Present(session, root, new Item(), views).Id}";
        Require(Open(transport, route, "browser:" + new string('p', 128)) == "invalid-request",
            "A presentation id longer than 128 characters was accepted.");
        for (var index = 0; index < 64; index++)
            Require(Open(transport, route, $"browser:{index}") == "ok", $"Presentation {index} did not mount.");
        Require(Open(transport, route, "browser:64") == "capacity",
            "A 65th presentation of one connection was mounted.");
        Require(Open(transport, route, "browser:0") == "ok", "An exact duplicate open was refused.");
        Require(Open(transport, route, "browser:64", "other") is not "capacity", "Another connection's first presentation was limited.");
        Require(Close(transport, route, "browser:0") == "ok", "A presentation did not close.");
        Require(Open(transport, route, "browser:65") == "ok", "A freed presentation slot was not reused.");
    }

    [Test]
    public async Task ReexposedReferenceKeepsItsBrowserMount()
    {
        var root = new Item();
        var a = new Item();
        var b = new Item();
        var views = new ViewCounter();
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { RootModel = root });
        var reference = Present(session, root, a, views);
        var route = $"content{reference.Id}";
        Require(Open(transport, route, "browser:a") == "ok", "The first presentation did not mount.");
        Require(views.Attached == 1, "The mounted presentation did not create its View.");
        var before = Revision(transport, route);

        Require(Present(session, root, b, views) != reference, "A different model reused the reference.");
        Require(views.Detached == 1 && Released(transport, route),
            "Replacing the content did not release its View and bridge.");
        Require(session.DormantAttachmentCount == 1 && Serves(transport, route),
            "A still-mounted suspended presentation did not keep serving its close requests.");

        Require(Present(session, root, a, views) == reference, "Re-exposing the model changed its reference.");
        await session.ModelContext!.InvokeAsync(() => { }); // the remount is a later model turn
        Require(views.Attached == 2 && session.DormantAttachmentCount == 0,
            "The browser's retained mount did not receive a View on the re-attached bridge.");
        var after = Revision(transport, route);
        Require(after > before, $"The re-attached bridge regressed the route revision from {before} to {after}.");
        a.Value = 1;
        Require(await PublishedRevisionAsync(transport, route) > after,
            "A publication from the re-attached bridge did not advance the route revision.");

        Require(Close(transport, route, "browser:a") == "ok"
            && views.Detached == 2, "Unmounting the resumed presentation did not release its View.");
    }

    [Test]
    public async Task UnmountedSuspendedReferenceReleasesItsEndpoints()
    {
        var root = new Item();
        var a = new Item();
        var views = new ViewCounter();
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { RootModel = root });
        var reference = Present(session, root, a, views);
        var route = $"content{reference.Id}";
        Require(Open(transport, route, "browser:a") == "ok", "The presentation did not mount.");
        session.Clear(root, "Main");
        Require(Close(transport, route, "browser:a") == "ok",
            "The browser could not unmount a suspended presentation.");
        Require(session.DormantAttachmentCount == 0 && !Serves(transport, route),
            "A suspended presentation kept its endpoints after the browser unmounted it.");

        Require(Present(session, root, a, views) == reference, "Re-exposing the model changed its reference.");
        await session.ModelContext!.InvokeAsync(() => { });
        Require(views.Attached == 1, "An unmounted browser presentation was recreated.");
    }

    // Issue #245. A navigator forgets a page off-turn while the browser unmounts it. The
    // unmount's release turn waits for the page's View attachment, so retiring the page
    // must not hold that attachment while its nested bridges wait for a model turn.
    // The interleaving is fixed by holding the model thread until both flows are queued:
    // the unmount captures the page's View attachment and queues its release first, then
    // Forget reaches the page bridge's teardown, which queues a turn behind it.
    [Test]
    public async Task ForgettingNestedContentOffTurnWhileTheBrowserUnmountsItDoesNotDeadlock()
    {
        var root = new Item();
        var page = new Item();
        var middle = new Item();
        var leaf = new Item();
        var views = new ViewCounter();
        var context = new CountingThreadContext();
        using var transport = new InMemoryBridgeTransport();
        var session = new WindowContentSession(transport, new WindowContentSessionOptions { RootModel = root, ModelContext = context });
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var deadlocked = false;
        try
        {
            var pageRoute = $"content{PresentNested(session, root, page, views).Id}";
            var middleRoute = $"content{PresentNested(session, page, middle, views).Id}";
            var leafRoute = $"content{PresentNested(session, middle, leaf, views).Id}";
            foreach (var route in new[] { pageRoute, middleRoute, leafRoute })
                Require(Open(transport, route, "browser:view") == "ok", $"{route} did not mount.");
            Require(views.Attached == 3, $"Mounting created {views.Attached} Views instead of 3.");

            Require(context.TryPost(() => { started.Set(); release.Wait(); }), "The model thread did not accept a turn.");
            Require(started.Wait(TimeSpan.FromSeconds(5)), "The model thread did not start the blocking turn.");
            var queued = context.Invocations;
            var unmount = Task.Run(() => Close(transport, pageRoute, "browser:view"));
            Require(SpinWait.SpinUntil(() => context.Invocations == queued + 1, TimeSpan.FromSeconds(5)),
                "The browser unmount did not queue its release turn.");
            var forget = Task.Run(() => session.Forget(page));
            Require(SpinWait.SpinUntil(() => context.Invocations == queued + 2, TimeSpan.FromSeconds(5)),
                "Forget did not reach the page bridge's teardown turn.");
            release.Set();

            try { await Task.WhenAll(unmount, forget).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException)
            {
                deadlocked = true;
                throw new InvalidOperationException(
                    "Forget and the browser unmount deadlocked on the page's View attachment.");
            }
            Require(unmount.Result == "ok", $"The browser unmount answered {unmount.Result}.");
            Require(views.Detached == 3, $"Retiring the page released {views.Detached} Views instead of 3, once each.");
            Require(!Serves(transport, pageRoute),
                "The unmounted page kept its route after it was forgotten.");
            Require(new[] { pageRoute, middleRoute, leafRoute }.All(route => Released(transport, route)),
                "Forgetting the page left a nested bridge attached.");
        }
        finally
        {
            // A failed check before the release must not leave the model thread blocked.
            release.Set();
            // A deadlocked session cannot be disposed; its threads stay blocked.
            if (!deadlocked)
            {
                session.Dispose();
                await context.DisposeAsync();
            }
        }
    }

    // I-079. When an off-turn Forget takes the page's Views before the browser unmount's
    // release turn runs, the Views are still released in a model turn, as the View lifetime
    // hooks document. The model thread is held until both flows have queued their turns,
    // so Forget always wins: the unmount has removed its mount token but not yet released
    // the View, and Forget's turn comes after the unmount's.
    [Test]
    public async Task ForgetThatWinsTheBrowserUnmountReleasesViewsInATurn()
    {
        var root = new Item();
        var page = new Item();
        var views = new ViewCounter();
        var context = new CountingThreadContext();
        using var transport = new InMemoryBridgeTransport();
        var session = new WindowContentSession(transport, new WindowContentSessionOptions { RootModel = root, ModelContext = context });
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var route = $"content{session.Present(root, "Main", "item", page, (_, current, presentedRoute) =>
                session.AttachPresentation<CountedView, Item>(current, presentedRoute,
                    (bridgeTransport, presented, bridgeRoute) => new CheckedBridge(bridgeTransport, presented, bridgeRoute, session),
                    () => new CountedView(views, context))).Id}";
            Require(Open(transport, route, "browser:view") == "ok" && views.Attached == 1,
                "The page did not mount.");

            Require(context.TryPost(() => { started.Set(); release.Wait(); }), "The model thread did not accept a turn.");
            Require(started.Wait(TimeSpan.FromSeconds(5)), "The model thread did not start the blocking turn.");
            var queued = context.Invocations;
            var unmount = Task.Run(() => Close(transport, route, "browser:view"));
            Require(SpinWait.SpinUntil(() => context.Invocations == queued + 1, TimeSpan.FromSeconds(5)),
                "The browser unmount did not queue its release turn.");
            var forget = Task.Run(() => session.Forget(page));
            Require(SpinWait.SpinUntil(() => context.Invocations == queued + 2, TimeSpan.FromSeconds(5)),
                "Forget did not queue a turn.");
            release.Set();

            await Task.WhenAll(unmount, forget).WaitAsync(TimeSpan.FromSeconds(10));
            Require(unmount.Result == "ok", $"The browser unmount answered {unmount.Result}.");
            Require(views.Detached == 1, $"Retiring the page released {views.Detached} Views instead of 1.");
            Require(views.DetachedOffTurn == 0, "Forget released the page's View outside a model turn.");
        }
        finally
        {
            // A failed check before the release must not leave the model thread blocked.
            release.Set();
            session.Dispose();
            await context.DisposeAsync();
        }
    }

    [Test]
    public void CollectionChurnReleasesItemState()
    {
        var root = new Item();
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { RootModel = root });
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

    // Presents content whose bridge, like a generated one, releases its own content
    // slots after its base disposal.
    private static PageReference PresentNested(WindowContentSession session, Item owner, Item model, ViewCounter views) =>
        session.Present(owner, "Main", "item", model, (_, current, route) =>
            session.AttachPresentation<CountedView, Item>(current, route,
                (transport, presented, presentedRoute) => new SlotOwnerBridge(transport, presented, presentedRoute, session),
                () => new CountedView(views)));

    // The outcome of an open or close: the reply's presentation outcome, or its error reason or kind.
    private static string Open(InMemoryBridgeTransport transport, string route, string presentation, string connection = "connection") =>
        Outcome(transport.Open(route, presentation, connection, "client"));

    private static string Close(InMemoryBridgeTransport transport, string route, string presentation, string connection = "connection") =>
        Outcome(transport.Close(route, presentation, connection, "client"));

    // A hello with a chosen session; "ok", or the error's reason or kind.
    private static string Hello(InMemoryBridgeTransport transport, string connection, string session) =>
        Outcome(transport.Request(BridgeRequests.Json(writer =>
        {
            writer.WriteString("kind", "hello");
            writer.WriteNumber("protocol", BridgeProtocol.Version);
            writer.WriteString("session", session);
            writer.WriteStartObject("client");
            writer.WriteString("name", "Runic.Application.Testing.Tests");
            writer.WriteString("version", "1.0.0");
            writer.WriteEndObject();
        }), connection, "client"));

    private static string Outcome(string reply)
    {
        using var document = JsonDocument.Parse(reply);
        var root = document.RootElement;
        if (root.GetProperty("ok").GetBoolean())
            return root.TryGetProperty("presentation", out var outcome) ? outcome.GetString()! : "ok";
        var error = root.GetProperty("error");
        return (error.TryGetProperty("reason", out var reason) ? reason : error.GetProperty("kind")).GetString()!;
    }

    // Whether the window's dispatcher serves the route, for example only the close requests of a dormant presentation.
    private static bool Serves(InMemoryBridgeTransport transport, string route) => BridgeDispatcher.For(transport).Serves(route);

    // Whether the route's snapshot answers released: no bridge serves its state.
    private static bool Released(InMemoryBridgeTransport transport, string route) =>
        Outcome(transport.Snapshot(route, "connection")) == "released";

    private static long Revision(InMemoryBridgeTransport transport, string route)
    {
        using var reply = JsonDocument.Parse(transport.Snapshot(route, "connection"));
        return reply.RootElement.GetProperty("revision").GetInt64();
    }

    private static async Task<long> PublishedRevisionAsync(InMemoryBridgeTransport transport, string route)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var push in transport.TakePushes(route))
                using (push)
                    return push.RootElement.GetProperty("revision").GetInt64();
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
        : ViewModelBridge<Item>(transport, model, route, [new BridgeStateMember<Item>("value", static (writer, vm) => writer.WriteNumberValue(vm.Value))], [],
        [new("Value", "value", CheckedFieldValueKind.Int32, vm => vm.Value, (vm, value) => vm.Value = (int)value!)],
        [], contractFingerprint: "content-lifecycle", content: content);

    private sealed class SlotOwnerBridge : ViewModelBridge<Item>
    {
        private readonly Item _owner;
        private readonly WindowContentSession _content;

        public SlotOwnerBridge(IBridgeTransport transport, Item model, string route, WindowContentSession content)
            : base(transport, model, route, [new BridgeStateMember<Item>("value", static (writer, vm) => writer.WriteNumberValue(vm.Value))], [], [], [], contractFingerprint: "content-lifecycle-slots", content: content)
        {
            _owner = model;
            _content = content;
        }

        public override void Dispose()
        {
            base.Dispose();
            _content.ClearOwner(_owner);
        }
    }

    private sealed class ViewCounter
    {
        private int _attached;
        private int _detached;
        private int _detachedOffTurn;
        public int Attached => Volatile.Read(ref _attached);
        public int Detached => Volatile.Read(ref _detached);
        public int DetachedOffTurn => Volatile.Read(ref _detachedOffTurn);
        public void Attach() => Interlocked.Increment(ref _attached);
        public void Detach() => Interlocked.Increment(ref _detached);
        public void DetachOffTurn() => Interlocked.Increment(ref _detachedOffTurn);
    }

    // An application context with one dedicated thread, such as a UI dispatcher, whose
    // InvokeAsync always queues. Counts the turns requested through InvokeAsync.
    private sealed class CountingThreadContext : IRunicModelContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private int _invocations;

        public CountingThreadContext()
        {
            _thread = new Thread(() =>
            {
                foreach (var work in _queue.GetConsumingEnumerable()) work();
            }) { IsBackground = true, Name = "Model thread" };
            _thread.Start();
        }

        public int Invocations => Volatile.Read(ref _invocations);
        public bool IsExecuting => Thread.CurrentThread == _thread;
        public event EventHandler<RunicModelTurnExceptionEventArgs>? UnhandledTurnException { add { } remove { } }

        public bool TryPost(Action turn) => _queue.TryAdd(turn);

        public ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default) =>
            new(InvokeAsync(() => { turn(); return true; }, cancellationToken).AsTask());

        public ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            ObjectDisposedException.ThrowIf(_queue.IsAddingCompleted, this);
            _queue.Add(() =>
            {
                try { completion.SetResult(turn()); }
                catch (Exception error) { completion.SetException(error); }
            }, CancellationToken.None);
            Interlocked.Increment(ref _invocations);
            return new(completion.Task);
        }

        public ValueTask DisposeAsync()
        {
            _queue.CompleteAdding();
            return ValueTask.CompletedTask;
        }
    }

    // Implements IRunicView directly so the bridge generator does not treat
    // this private probe as an application View.
    // With a context, it also counts the detaches that ran outside the context's turns.
    private sealed class CountedView(ViewCounter counter, IRunicModelContext? context = null) : IRunicView, IRunicViewLifetime
    {
        public object? DataContext { get; set; }
        public void OnAttached() => counter.Attach();
        public void OnDetached()
        {
            if (context is { IsExecuting: false }) counter.DetachOffTurn();
            counter.Detach();
        }
    }
}
