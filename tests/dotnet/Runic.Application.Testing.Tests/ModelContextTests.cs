using ReactiveUI.Primitives.Concurrency;
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
}
