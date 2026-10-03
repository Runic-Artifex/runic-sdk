using System.ComponentModel;
using ReactiveUI.Primitives.Concurrency;
using ReactiveUI.Binding;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;

namespace Runic.Application.Testing.Tests;

// Called by Program.cs so this executable test host can keep its ordinary end-to-end
// fixture separate from model-lane concurrency tests.
public static class ModelContextTests
{
    public static async Task RunAsync()
    {
        await SerialTurnsPreservePostingOrder();
        await PostedTurnFailureDoesNotStopTheQueue();
        await RegistryRejectsConflictingOwnersAndReusesExistingOwner();
        await ConcurrentAcquisitionUsesOneOwner();
        await SessionAcquiresAndReleasesItsRootContext();
        await RootlessSessionReleasesForgottenContentIdentity();
        await SessionCloseFromModelTurnDoesNotDeadlock();
        await ShutdownRejectsQueuedTurnsAndWaitsForCurrentTurn();
        await ReactiveSchedulerDeliversOnTheModelContext();
        await PostedTurnsRetainTheirOwnAmbientInvocation();
        await ScheduledInteractionsRetainTheirOwnAmbientInvocation();
        await PostedTurnDroppedByDisposalIsReported();
        await SessionBindsTheSuppliedApplicationContext();
        await DisposedApplicationContextStillReleasesBridgesAndViews();
        await ThirdPartyContextRunsNestedWorkInline();
    }

    private static async Task PostedTurnDroppedByDisposalIsReported()
    {
        var context = new RunicModelContext();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reported = new List<Exception>();
        context.UnhandledTurnException += error => { lock (reported) reported.Add(error); };
        Require(context.TryPost(() => { started.Set(); release.Wait(); }), "The blocking turn was rejected.");
        Require(started.Wait(TimeSpan.FromSeconds(5)), "The blocking turn did not start.");
        var ran = false;
        Require(context.TryPost(() => ran = true), "The queued turn was rejected before shutdown.");
        var disposing = context.DisposeAsync().AsTask();
        release.Set();
        await disposing;
        Require(!ran, "A posted turn ran after its context was disposed.");
        lock (reported)
            Require(reported.Count == 1 && reported[0] is ObjectDisposedException,
                "A posted turn dropped by disposal was not reported.");
    }

    private static async Task SessionBindsTheSuppliedApplicationContext()
    {
        await using var applicationContext = new RunicModelContext();
        await using var otherContext = new RunicModelContext();
        var model = new object();
        using var transport = new InMemoryViewTransport();
        using (var session = new WindowContentSession(transport, rootModel: model, modelContext: applicationContext))
        {
            Require(ReferenceEquals(session.ModelContext, applicationContext)
                && ReferenceEquals(RunicModelContextRegistry.Shared.GetRequired(model), applicationContext),
                "The session did not bind its root to the supplied application context.");
            using var conflicting = new InMemoryViewTransport();
            Require(Throws<InvalidOperationException>(() =>
                new WindowContentSession(conflicting, rootModel: model, modelContext: otherContext)),
                "A session silently split a root model between two contexts.");
        }
        Require(!RunicModelContextRegistry.Shared.TryGet(model, out _),
            "Closing the session retained the supplied context binding.");
        await applicationContext.InvokeAsync(() => { }); // still owned by the application
    }

    private static async Task DisposedApplicationContextStillReleasesBridgesAndViews()
    {
        var applicationContext = new RunicModelContext();
        var model = new NotifyingModel();
        var child = new NotifyingModel();
        using var transport = new InMemoryViewTransport();
        var session = new WindowContentSession(transport, rootModel: model, modelContext: applicationContext);
        var root = new ProbeBridge(transport, model, "probe", session);
        var reference = session.Expose("child", child, (_, current, route) =>
            session.AttachPresentation<ProbeView, NotifyingModel>(current, route,
                (presentationTransport, presentationModel, presentationRoute) =>
                    new ProbeBridge(presentationTransport, presentationModel, presentationRoute, session),
                () => new ProbeView()));
        Require(transport.Call($"content{reference.Id}Mount", new(StringValue: "browser:view",
            ClientKey: "client", ConnectionKey: "connection")) == "ok", "The probe View did not mount.");
        Require(ProbeView.Attached == 1 && model.Subscribers == 1 && child.Subscribers == 1,
            "The probe bridges or View did not attach.");

        await applicationContext.DisposeAsync();
        root.Dispose();
        session.Dispose();
        Require(model.Subscribers == 0 && child.Subscribers == 0,
            "Bridge teardown after application-context disposal retained PropertyChanged subscriptions.");
        Require(ProbeView.Detached == 1, "Window teardown after context disposal did not release the mounted View.");
        Require(transport.Routes.Count == 0, $"Window teardown retained routes: {string.Join(", ", transport.Routes)}");
    }

    private static async Task ThirdPartyContextRunsNestedWorkInline()
    {
        await using var context = new DedicatedThreadContext();
        var model = new object();
        using var transport = new InMemoryViewTransport();
        using var session = new WindowContentSession(transport, rootModel: model, modelContext: context);
        var exposure = context.InvokeAsync(() => session.Expose("item", new object(),
            static (_, _, _) => new NoopAttachment())).AsTask();
        Require(await Task.WhenAny(exposure, Task.Delay(TimeSpan.FromSeconds(5))) == exposure,
            "Content exposed from inside a non-inlining application context deadlocked.");
        _ = await exposure;
    }

    private static async Task SerialTurnsPreservePostingOrder()
    {
        await using var context = new RunicModelContext();
        var observed = new List<int>();
        var concurrent = 0;
        var maximumConcurrent = 0;
        for (var i = 0; i != 64; i++)
        {
            var value = i;
            Require(context.TryPost(() =>
            {
                maximumConcurrent = Math.Max(maximumConcurrent, Interlocked.Increment(ref concurrent));
                observed.Add(value);
                Interlocked.Decrement(ref concurrent);
            }), "The context rejected a turn before shutdown.");
        }

        await context.InvokeAsync(() => { }); // FIFO barrier
        Require(observed.SequenceEqual(Enumerable.Range(0, 64)), "Model turns did not preserve posting order.");
        Require(maximumConcurrent == 1, "Model turns executed concurrently.");
    }

    private static async Task RegistryRejectsConflictingOwnersAndReusesExistingOwner()
    {
        var registry = new RunicModelContextRegistry();
        var model = new object();
        var child = new object();
        await using var firstContext = new RunicModelContext();
        await using var conflictingContext = new RunicModelContext();
        await using var first = registry.Bind(firstContext, model, child);

        Require(ReferenceEquals(registry.GetRequired(model), firstContext), "The model owner was not registered.");
        Require(ReferenceEquals(registry.GetRequired(child), firstContext), "Graph children did not share their owner's context.");
        Require(Throws<InvalidOperationException>(() => registry.Bind(conflictingContext, model)),
            "A second model context was accepted for the same model.");

        var factoryCalled = false;
        await using var second = registry.Acquire(() =>
        {
            factoryCalled = true;
            return conflictingContext;
        }, model);
        Require(!factoryCalled && ReferenceEquals(second.Context, firstContext),
            "A second attachment did not reuse the existing graph owner.");
    }

    private static async Task PostedTurnFailureDoesNotStopTheQueue()
    {
        await using var context = new RunicModelContext();
        Exception? reported = null;
        context.UnhandledTurnException += error => reported = error;
        Require(context.TryPost(() => throw new InvalidOperationException("expected")), "The failing turn was rejected.");
        var delivered = 0;
        Require(context.TryPost(() => delivered++), "The follow-up turn was rejected.");
        await context.InvokeAsync(() => { });
        Require(reported is InvalidOperationException && delivered == 1,
            "A failed fire-and-forget turn either was not reported or stopped the model queue.");
    }

    private static async Task ConcurrentAcquisitionUsesOneOwner()
    {
        var registry = new RunicModelContextRegistry();
        var model = new object();
        using var bothFactoriesStarted = new Barrier(2);
        var acquisitions = Enumerable.Range(0, 2).Select(_ => Task.Run(() => registry.Acquire(() =>
        {
            if (!bothFactoriesStarted.SignalAndWait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("The concurrent context factories did not rendezvous.");
            return new RunicModelContext();
        }, model))).ToArray();

        var leases = await Task.WhenAll(acquisitions);
        try
        {
            Require(ReferenceEquals(leases[0].Context, leases[1].Context),
                "Concurrent model attachment created more than one graph owner.");
        }
        finally
        {
            foreach (var lease in leases) await lease.DisposeAsync();
        }
    }

    private static async Task ShutdownRejectsQueuedTurnsAndWaitsForCurrentTurn()
    {
        var context = new RunicModelContext();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Require(context.TryPost(() =>
        {
            started.Set();
            release.Wait();
        }), "The initial turn was rejected.");
        Require(started.Wait(TimeSpan.FromSeconds(5)), "The initial model turn did not start.");

        var queued = context.InvokeAsync(() => throw new InvalidOperationException("Queued work ran during shutdown.")).AsTask();
        var disposing = context.DisposeAsync().AsTask();
        Require(!disposing.IsCompleted, "Context shutdown completed while a synchronous turn was still running.");
        await ThrowsAsync<ObjectDisposedException>(queued);
        release.Set();
        await disposing;
        await ThrowsAsync<ObjectDisposedException>(context.InvokeAsync(() => { }).AsTask());
    }

    private static async Task SessionAcquiresAndReleasesItsRootContext()
    {
        var registry = RunicModelContextRegistry.Shared;
        var model = new object();
        IRunicModelContext context;
        using var firstTransport = new InMemoryViewTransport();
        using var secondTransport = new InMemoryViewTransport();
        var first = new WindowContentSession(firstTransport, rootModel: model);
        var second = new WindowContentSession(secondTransport, rootModel: model);
        try
        {
            context = first.ModelContext ?? throw new InvalidOperationException("The root model did not receive a context.");
            Require(ReferenceEquals(registry.GetRequired(model), context),
                "The bridge registry did not use the session root context.");
            Require(ReferenceEquals(second.ModelContext, context),
                "A second presentation of the same root created a conflicting context.");
            first.Dispose();
            Require(ReferenceEquals(registry.GetRequired(model), context),
                "Closing one of two root sessions disposed their shared context.");
        }
        finally { first.Dispose(); second.Dispose(); }
        Require(!registry.TryGet(model, out _), "Closing the final root session retained its context registration.");
        await ThrowsAsync<ObjectDisposedException>(context.InvokeAsync(() => { }).AsTask());
    }

    private static async Task ReactiveSchedulerDeliversOnTheModelContext()
    {
        await using var context = new RunicModelContext();
        ISequencer scheduler = new RunicReactiveSchedulerProvider().For(context);
        var delivered = 0;
        scheduler.Schedule(() => Interlocked.Increment(ref delivered));
        await context.InvokeAsync(() => { }); // scheduled drain is ahead of this barrier
        Require(delivered == 1, "The ReactiveUI scheduler did not deliver through the model context.");
    }

    private static async Task PostedTurnsRetainTheirOwnAmbientInvocation()
    {
        await using var context = new RunicModelContext();
        using var transport = new InMemoryViewTransport();
        using var session = new WindowContentSession(transport);
        using var drainStarted = new ManualResetEventSlim();
        using var releaseDrain = new ManualResetEventSlim();
        Require(context.TryPost(() =>
        {
            drainStarted.Set();
            releaseDrain.Wait();
        }), "The direct-post batch barrier was rejected.");
        Require(drainStarted.Wait(TimeSpan.FromSeconds(5)), "The direct-post batch barrier did not start.");

        var observed = new List<string?>();
        using (RunicInteractionInvocation.Enter(session, "direct", "client-a", "connection-a", commandName: "first"))
            Require(context.TryPost(() =>
            {
                Require(context.IsExecuting, "A direct post did not execute on its model context.");
                observed.Add(RunicInteractionInvocation.Current?.CommandName);
            }), "The first scoped direct post was rejected.");
        using (RunicInteractionInvocation.Enter(session, "direct", "client-b", "connection-b", commandName: "second"))
            Require(context.TryPost(() => observed.Add(RunicInteractionInvocation.Current?.CommandName)),
                "The second scoped direct post was rejected.");
        using (ExecutionContext.SuppressFlow())
        using (RunicInteractionInvocation.Enter(session, "direct", "client-a", "connection-a", commandName: "suppressed"))
            Require(context.TryPost(() => observed.Add(RunicInteractionInvocation.Current?.CommandName)),
                "The suppressed direct post was rejected.");
        Require(context.TryPost(() => observed.Add(RunicInteractionInvocation.Current?.CommandName)),
            "The unscoped direct post was rejected.");
        releaseDrain.Set();
        await context.InvokeAsync(() => { });
        Require(observed.SequenceEqual(["first", "second", null, null]),
            "Queued model turns crossed, ignored suppression of, or leaked their ambient invocation scopes.");
    }

    private static async Task ScheduledInteractionsRetainTheirOwnAmbientInvocation()
    {
        await using var context = new RunicModelContext();
        using var transport = new InMemoryViewTransport();
        using var session = new WindowContentSession(transport);
        var model = new ScheduledInteractionModel();
        const string route = "scheduledInteraction";
        const string name = "confirm";
        const string contract = "testing.scheduled-interaction.v1";
        using var descriptor = ReactiveInteractionDescriptor.Create<ScheduledInteractionModel, string, bool>(
            name, contract, value => value.Confirm,
            static value => JsonSerializer.Serialize(value), static value => value.GetBoolean()).Attach(session, model, route);
        using var rootMount = session.AttachRootInteractionPresentation(route);
        Require(transport.Call($"{route}Mount", new(StringValue: "browser-a:present", ClientKey: "client-a",
            ConnectionKey: "connection-a")) == "ok", "The first scheduled interaction presentation did not mount.");
        Require(transport.Call($"{route}Mount", new(StringValue: "browser-b:present", ClientKey: "client-b",
            ConnectionKey: "connection-b")) == "ok", "The second scheduled interaction presentation did not mount.");

        var firstWait = transport.CallAsync(BridgeInteractionRouter.WaitRoute,
            new(StringValue: WaitJson(route, "browser-a:present", name, contract), ClientKey: "client-a",
                ConnectionKey: "connection-a")).AsTask();
        var secondWait = transport.CallAsync(BridgeInteractionRouter.WaitRoute,
            new(StringValue: WaitJson(route, "browser-b:present", name, contract), ClientKey: "client-b",
                ConnectionKey: "connection-b")).AsTask();
        await Task.Yield();

        var scheduler = new RunicReactiveSchedulerProvider().For(context);
        using var drainStarted = new ManualResetEventSlim();
        using var releaseDrain = new ManualResetEventSlim();
        Require(context.TryPost(() =>
        {
            drainStarted.Set();
            releaseDrain.Wait();
        }), "The model context rejected the scheduler batch barrier.");
        Require(drainStarted.Wait(TimeSpan.FromSeconds(5)), "The scheduler batch barrier did not start.");
        var observedScopes = new List<string?>();
        Task<bool>? firstAnswer = null;
        Task<bool>? secondAnswer = null;
        string? suppressedScope = "not-run";
        string? leakedScope = "not-run";

        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-a", commandName: "first"))
            scheduler.Schedule(() =>
            {
                Require(context.IsExecuting, "A scheduled interaction did not execute on its model context.");
                observedScopes.Add(RunicInteractionInvocation.Current?.CommandName);
                firstAnswer = model.Confirm.Handle("first");
            });
        using (RunicInteractionInvocation.Enter(session, route, "client-b", "connection-b", commandName: "second"))
            scheduler.Schedule(() =>
            {
                Require(context.IsExecuting, "A batched scheduled interaction did not execute on its model context.");
                observedScopes.Add(RunicInteractionInvocation.Current?.CommandName);
                secondAnswer = model.Confirm.Handle("second");
            });
        using (ExecutionContext.SuppressFlow())
        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-a", commandName: "suppressed"))
            scheduler.Schedule(() => suppressedScope = RunicInteractionInvocation.Current?.CommandName);
        scheduler.Schedule(() => leakedScope = RunicInteractionInvocation.Current?.CommandName);
        releaseDrain.Set();

        using var firstRequest = JsonDocument.Parse(await Within(firstWait,
            "The first scheduled interaction did not reach its browser endpoint."));
        using var secondRequest = JsonDocument.Parse(await Within(secondWait,
            "The second scheduled interaction inherited the wrong browser invocation."));
        Require(firstRequest.RootElement.GetProperty("input").GetString() == "first"
            && secondRequest.RootElement.GetProperty("input").GetString() == "second",
            "Batched scheduled interactions crossed their trusted invocation scopes.");
        Reply(transport, firstRequest.RootElement, route, "browser-a:present", name, contract,
            "client-a", "connection-a", true);
        Reply(transport, secondRequest.RootElement, route, "browser-b:present", name, contract,
            "client-b", "connection-b", false);
        Require(firstAnswer is not null && await firstAnswer, "The first scheduled interaction response was not delivered.");
        Require(secondAnswer is not null && !await secondAnswer, "The second scheduled interaction response was not delivered.");
        await context.InvokeAsync(() => { });
        Require(observedScopes.SequenceEqual(["first", "second"]),
            "Scheduled work did not retain its own trusted invocation scope.");
        Require(suppressedScope is null, "ExecutionContext.SuppressFlow was ignored for scheduled work.");
        Require(leakedScope is null, "A completed scheduled callback leaked its interaction scope into the next turn.");
    }

    private static string WaitJson(string route, string presentationId, string name, string contract) =>
        JsonSerializer.Serialize(new { route, presentationId, handlers = new[] { new { name, contract } } });

    private static void Reply(InMemoryViewTransport transport, JsonElement request, string route, string presentationId,
        string name, string contract, string clientKey, string connectionKey, bool output)
    {
        var reply = JsonSerializer.Serialize(new
        {
            kind = "answered",
            requestId = request.GetProperty("requestId").GetString(),
            route,
            presentationId,
            ownerEpoch = request.GetProperty("ownerEpoch").GetInt64(),
            name,
            contract,
            output,
        });
        using var accepted = JsonDocument.Parse(transport.Call(BridgeInteractionRouter.ReplyRoute,
            new(StringValue: reply, ClientKey: clientKey, ConnectionKey: connectionKey)));
        Require(accepted.RootElement.GetProperty("kind").GetString() == "ok",
            "The browser response to a scheduled interaction was rejected.");
    }

    private static async Task<T> Within<T>(Task<T> task, string message)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))) != task)
            throw new InvalidOperationException(message);
        return await task;
    }

    private static async Task RootlessSessionReleasesForgottenContentIdentity()
    {
        var registry = RunicModelContextRegistry.Shared;
        var model = new object();
        IRunicModelContext context;
        using (var transport = new InMemoryViewTransport())
        using (var session = new WindowContentSession(transport))
        {
            _ = session.Expose("item", model, static (_, _, _) => new NoopAttachment());
            context = session.ModelContext ?? throw new InvalidOperationException("The standalone session did not create a model context.");
            Require(ReferenceEquals(registry.GetRequired(model), context),
                "Standalone content did not bind to its session context.");
            session.Forget(model);
            Require(!registry.TryGet(model, out _),
                "Forgetting standalone content retained its model-context identity.");
        }
        await ThrowsAsync<ObjectDisposedException>(context.InvokeAsync(() => { }).AsTask());
    }

    private static async Task SessionCloseFromModelTurnDoesNotDeadlock()
    {
        using var transport = new InMemoryViewTransport();
        var model = new object();
        var session = new WindowContentSession(transport, rootModel: model);
        var context = session.ModelContext ?? throw new InvalidOperationException("The root model did not receive a context.");
        var close = context.InvokeAsync(session.Dispose).AsTask();
        Require(await Task.WhenAny(close, Task.Delay(TimeSpan.FromSeconds(5))) == close,
            "Closing a session from a model turn deadlocked its context shutdown.");
        await close;
        await ThrowsAsync<ObjectDisposedException>(context.InvokeAsync(() => { }).AsTask());
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static async Task ThrowsAsync<T>(Task task) where T : Exception
    {
        try
        {
            await task;
            throw new InvalidOperationException($"Expected {typeof(T).Name}.");
        }
        catch (T) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class NoopAttachment : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class ScheduledInteractionModel
    {
        public Interaction<string, bool> Confirm { get; } = new();
    }

    private sealed class NotifyingModel : INotifyPropertyChanged
    {
        private PropertyChangedEventHandler? _changed;
        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => _changed += value;
            remove => _changed -= value;
        }
    }

    private sealed class ProbeBridge(IBridgeTransport transport, NotifyingModel model, string route,
        WindowContentSession content)
        : ViewModelBridge<NotifyingModel>(transport, model, route, static (writer, _, revision) =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("revision", revision);
            writer.WriteEndObject();
        }, [], [], content: content);

    // Implements IRunicView directly so the bridge generator does not treat
    // this private probe as an application View.
    private sealed class ProbeView : IRunicView, IRunicViewLifetime
    {
        public object? DataContext { get; set; }
        public static int Attached { get; private set; }
        public static int Detached { get; private set; }
        public void OnAttached() => Attached++;
        public void OnDetached() => Detached++;
    }

    // An application-supplied context whose InvokeAsync always queues. The
    // runtime must use IsExecuting rather than relying on InvokeAsync inlining.
    private sealed class DedicatedThreadContext : IRunicModelContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public DedicatedThreadContext()
        {
            _thread = new Thread(() =>
            {
                foreach (var work in _queue.GetConsumingEnumerable()) work();
            }) { IsBackground = true };
            _thread.Start();
        }

        public bool IsExecuting => Thread.CurrentThread == _thread;
        public event Action<Exception>? UnhandledTurnException { add { } remove { } }

        public bool TryPost(Action turn)
        {
            _queue.Add(turn);
            return true;
        }

        public ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default) =>
            new(InvokeAsync(() => { turn(); return true; }, cancellationToken).AsTask());

        public ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try { completion.SetResult(turn()); }
                catch (Exception error) { completion.SetException(error); }
            });
            return new(completion.Task);
        }

        public ValueTask DisposeAsync()
        {
            _queue.CompleteAdding();
            return ValueTask.CompletedTask;
        }
    }
}
