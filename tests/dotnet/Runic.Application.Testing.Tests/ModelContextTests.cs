using System.ComponentModel;
using ReactiveUI.Primitives.Concurrency;
using ReactiveUI.Binding;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Application.Views.ReactiveUI.Generated;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Called by Program.cs so this executable test host can keep its ordinary end-to-end
// fixture separate from model-lane concurrency tests.
public sealed class ModelContextTests
{
    // A window session that owns its context closes it from inside one of its turns without
    // waiting, and observes the shutdown in the background. A failure is logged as 1033 with
    // the session's own logger (category Runic.Application.Views), not the context's. The
    // session only owns a RunicModelContext, whose shutdown does not fault, so the test drives
    // the session's disposal helper with a context whose shutdown it controls.
    [Test]
    public async Task SessionContextShutdownFailureIsLoggedToTheSession()
    {
        var logs = new CategoryLog();
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { LoggerFactory = logs });
        var shutdown = new TaskCompletionSource();
        var context = new ManualShutdownContext(shutdown.Task);
        RunicModelContextDisposal.DisposeSynchronously(context, session.Logger);
        Require(context.Disposed, "The session's disposal helper did not start the shutdown.");
        shutdown.SetException(new IOException("shutdown"));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!logs.Has(1033) && DateTime.UtcNow < deadline) await Task.Delay(10);
        var entry = logs.Entries.SingleOrDefault(entry => entry.Id == 1033);
        Require(entry is { Category: RunicViewsTelemetry.LogCategory } && entry.Error is IOException,
            $"The context shutdown failure was not logged under {RunicViewsTelemetry.LogCategory}: {string.Join(", ", logs.Entries)}");
    }

    private sealed record LogEntry(string Category, int Id, Exception? Error);

    private sealed class CategoryLog : ILoggerFactory
    {
        private readonly List<LogEntry> _entries = [];
        public IReadOnlyList<LogEntry> Entries { get { lock (_entries) return [.. _entries]; } }
        public bool Has(int id) => Entries.Any(entry => entry.Id == id);
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose() { }

        private sealed class Logger(CategoryLog owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._entries) owner._entries.Add(new(category, eventId.Id, exception));
            }
        }
    }

    // A context inside one of its own turns, whose shutdown completes when the test decides.
    private sealed class ManualShutdownContext(Task shutdown) : IRunicModelContext
    {
        public bool IsExecuting => true;
        public bool Disposed { get; private set; }
        public event EventHandler<RunicModelTurnExceptionEventArgs>? UnhandledTurnException { add { } remove { } }
        public bool TryPost(Action turn) => false;
        public ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return new(shutdown);
        }
    }

    [Test]
    public async Task SessionBindsTheSuppliedApplicationContext()
    {
        await using var applicationContext = new RunicModelContext();
        await using var otherContext = new RunicModelContext();
        var model = new object();
        using var transport = new InMemoryBridgeTransport();
        using (var session = new WindowContentSession(transport, new WindowContentSessionOptions { RootModel = model, ModelContext = applicationContext }))
        {
            Require(ReferenceEquals(session.ModelContext, applicationContext)
                && ReferenceEquals(RunicModelContextRegistry.Shared.GetRequired(model), applicationContext),
                "The session did not bind its root to the supplied application context.");
            using var conflicting = new InMemoryBridgeTransport();
            Require(Throws<InvalidOperationException>(() =>
                _ = new WindowContentSession(conflicting, new WindowContentSessionOptions { RootModel = model, ModelContext = otherContext })),
                "A session silently split a root model between two contexts.");
        }
        Require(!RunicModelContextRegistry.Shared.TryGet(model, out _),
            "Closing the session retained the supplied context binding.");
        await applicationContext.InvokeAsync(() => { }); // still owned by the application
    }

    [Test]
    public async Task DisposedApplicationContextStillReleasesBridgesAndViews()
    {
        var applicationContext = new RunicModelContext();
        var model = new NotifyingModel();
        var child = new NotifyingModel();
        using var transport = new InMemoryBridgeTransport();
        var session = new WindowContentSession(transport, new WindowContentSessionOptions { RootModel = model, ModelContext = applicationContext });
        var root = new ProbeBridge(transport, model, "probe", session);
        var reference = session.Expose("child", child, (_, current, route) =>
            session.AttachPresentation<ProbeView, NotifyingModel>(current, route,
                (presentationTransport, presentationModel, presentationRoute) =>
                    new ProbeBridge(presentationTransport, presentationModel, presentationRoute, session),
                () => new ProbeView()));
        var contentRoute = $"content{reference.Id}";
        var open = transport.Open(contentRoute, "browser:view", "connection", "client");
        Require(open.Contains("\"presentation\":\"ok\"", StringComparison.Ordinal), $"The probe View did not mount: {open}");
        Require(ProbeView.Attached == 1 && model.Subscribers == 1 && child.Subscribers == 1,
            "The probe bridges or View did not attach.");

        await applicationContext.DisposeAsync();
        root.Dispose();
        session.Dispose();
        Require(model.Subscribers == 0 && child.Subscribers == 0,
            "Bridge teardown after application-context disposal retained PropertyChanged subscriptions.");
        Require(ProbeView.Detached == 1, "Window teardown after context disposal did not release the mounted View.");
        var dispatcher = BridgeDispatcher.For(transport);
        Require(!dispatcher.Serves("probe") && !dispatcher.Serves(contentRoute), "Window teardown retained routes.");
    }

    [Test]
    public async Task ThirdPartyContextRunsNestedWorkInline()
    {
        await using var context = new DedicatedThreadContext();
        var model = new object();
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { RootModel = model, ModelContext = context });
        var exposure = context.InvokeAsync(() => session.Expose("item", new object(),
            static (_, _, _) => new NoopAttachment())).AsTask();
        Require(await Task.WhenAny(exposure, Task.Delay(TestTimeouts.Scale(TimeSpan.FromSeconds(5)))) == exposure,
            "Content exposed from inside a non-inlining application context deadlocked.");
        _ = await exposure;
    }

    [Test]
    public async Task SessionAcquiresAndReleasesItsRootContext()
    {
        var registry = RunicModelContextRegistry.Shared;
        var model = new object();
        IRunicModelContext context;
        using var firstTransport = new InMemoryBridgeTransport();
        using var secondTransport = new InMemoryBridgeTransport();
        var first = new WindowContentSession(firstTransport, new WindowContentSessionOptions { RootModel = model });
        var second = new WindowContentSession(secondTransport, new WindowContentSessionOptions { RootModel = model });
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

    [Test]
    public async Task ReactiveSchedulerDeliversOnTheModelContext()
    {
        await using var context = new RunicModelContext();
        ISequencer scheduler = ReactiveSchedulers.For(context);
        var delivered = 0;
        scheduler.Schedule(() => Interlocked.Increment(ref delivered));
        await context.InvokeAsync(() => { }); // scheduled drain is ahead of this barrier
        Require(delivered == 1, "The ReactiveUI scheduler did not deliver through the model context.");
    }

    [Test]
    public async Task PostedTurnsRetainTheirOwnAmbientInvocation()
    {
        await using var context = new RunicModelContext();
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport);
        using var drainStarted = new ManualResetEventSlim();
        using var releaseDrain = new ManualResetEventSlim();
        Require(context.TryPost(() =>
        {
            drainStarted.Set();
            releaseDrain.Wait();
        }), "The direct-post batch barrier was rejected.");
        Require(drainStarted.Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(5))), "The direct-post batch barrier did not start.");

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

    [Test]
    public async Task ScheduledInteractionsRetainTheirOwnAmbientInvocation()
    {
        await using var context = new RunicModelContext();
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport);
        var model = new ScheduledInteractionModel();
        const string route = "scheduledInteraction";
        const string name = "confirm";
        const string contract = "testing.scheduled-interaction.v1";
        using var descriptor = ReactiveInteractionDescriptor.Create<ScheduledInteractionModel, string, bool>(
            name, contract, value => value.Confirm,
            static value => JsonSerializer.Serialize(value), static value => value.GetBoolean()).Attach(session, model, route);
        using var rootMount = session.AttachRootInteractionPresentation(route);
        using var server = new RouteServerBridge(transport, route, session);
        // Each presentation registers its handlers with its open (generation 0); requests arrive as interaction pushes.
        var handlers = JsonSerializer.Serialize(new[] { new { name, contract } });
        Require(transport.Open(route, "present", "connection-a", "client-a", handlers).Contains("\"registration\":\"ok\"", StringComparison.Ordinal),
            "The first scheduled interaction presentation did not register its handler.");
        Require(transport.Open(route, "present-b", "connection-b", "client-b", handlers).Contains("\"registration\":\"ok\"", StringComparison.Ordinal),
            "The second scheduled interaction presentation did not register its handler.");

        var scheduler = ReactiveSchedulers.For(context);
        using var drainStarted = new ManualResetEventSlim();
        using var releaseDrain = new ManualResetEventSlim();
        Require(context.TryPost(() =>
        {
            drainStarted.Set();
            releaseDrain.Wait();
        }), "The model context rejected the scheduler batch barrier.");
        Require(drainStarted.Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(5))), "The scheduler batch barrier did not start.");
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

        using var firstRequest = await Within(NextInteractionAsync(transport, "connection-a"),
            "The first scheduled interaction did not reach its browser endpoint.");
        using var secondRequest = await Within(NextInteractionAsync(transport, "connection-b"),
            "The second scheduled interaction inherited the wrong browser invocation.");
        Require(firstRequest.RootElement.GetProperty("input").GetString() == "first"
            && secondRequest.RootElement.GetProperty("input").GetString() == "second",
            "Batched scheduled interactions crossed their trusted invocation scopes.");
        Reply(transport, firstRequest.RootElement, "client-a", "connection-a", true);
        Reply(transport, secondRequest.RootElement, "client-b", "connection-b", false);
        Require(firstAnswer is not null && await firstAnswer, "The first scheduled interaction response was not delivered.");
        Require(secondAnswer is not null && !await secondAnswer, "The second scheduled interaction response was not delivered.");
        await context.InvokeAsync(() => { });
        Require(observedScopes.SequenceEqual(["first", "second"]),
            "Scheduled work did not retain its own trusted invocation scope.");
        Require(suppressedScope is null, "ExecutionContext.SuppressFlow was ignored for scheduled work.");
        Require(leakedScope is null, "A completed scheduled callback leaked its interaction scope into the next turn.");
    }

    // The first interaction push to a connection, read from the transport's messages without taking them.
    private static async Task<JsonDocument> NextInteractionAsync(InMemoryBridgeTransport transport, string connectionKey)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var message in transport.Messages)
            {
                if (message.ConnectionKey != connectionKey) continue;
                var push = JsonDocument.Parse(message.Json);
                if (push.RootElement.GetProperty("kind").GetString() == "interaction") return push;
                push.Dispose();
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"No interaction reached {connectionKey}.");
    }

    // Answers an interaction push with an interaction-reply request.
    private static void Reply(InMemoryBridgeTransport transport, JsonElement request, string clientKey, string connectionKey, bool output)
    {
        var reply = JsonSerializer.Serialize(new
        {
            kind = "interaction-reply",
            route = request.GetProperty("route").GetString(),
            presentation = request.GetProperty("presentation").GetString(),
            requestId = request.GetProperty("requestId").GetString(),
            ownerEpoch = request.GetProperty("ownerEpoch").GetInt64(),
            name = request.GetProperty("name").GetString(),
            contract = request.GetProperty("contract").GetString(),
            outcome = "answered",
            output,
        });
        using var accepted = JsonDocument.Parse(transport.Send(reply, connectionKey, clientKey));
        Require(accepted.RootElement.GetProperty("ok").GetBoolean() && accepted.RootElement.GetProperty("result").GetString() == "ok",
            "The browser response to a scheduled interaction was rejected.");
    }

    private static async Task<T> Within<T>(Task<T> task, string message)
    {
        if (await Task.WhenAny(task, Task.Delay(TestTimeouts.Scale(TimeSpan.FromSeconds(5)))) != task)
            throw new InvalidOperationException(message);
        return await task;
    }

    [Test]
    public async Task RootlessSessionReleasesForgottenContentIdentity()
    {
        var registry = RunicModelContextRegistry.Shared;
        var model = new object();
        IRunicModelContext context;
        using (var transport = new InMemoryBridgeTransport())
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

    [Test]
    public async Task SessionCloseFromModelTurnDoesNotDeadlock()
    {
        using var transport = new InMemoryBridgeTransport();
        var model = new object();
        var session = new WindowContentSession(transport, new WindowContentSessionOptions { RootModel = model });
        var context = session.ModelContext ?? throw new InvalidOperationException("The root model did not receive a context.");
        var close = context.InvokeAsync(session.Dispose).AsTask();
        Require(await Task.WhenAny(close, Task.Delay(TestTimeouts.Scale(TimeSpan.FromSeconds(5)))) == close,
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
        : ViewModelBridge<NotifyingModel>(transport, model, route, [], [], [], [], content: content);

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
        public event EventHandler<RunicModelTurnExceptionEventArgs>? UnhandledTurnException { add { } remove { } }

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
            }, CancellationToken.None);
            return new(completion.Task);
        }

        public ValueTask DisposeAsync()
        {
            _queue.CompleteAdding();
            return ValueTask.CompletedTask;
        }
    }
}
