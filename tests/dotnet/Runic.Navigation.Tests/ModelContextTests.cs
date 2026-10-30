using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TUnit.Core;

namespace Runic.Navigation.Tests;

// The model context and its registry (moved from Runic.Application.Testing.Tests;
// the window-session cases stay there).
internal sealed class ModelContextTests
{
    // D018.5: code started in a turn resumes in a turn after each await, nested or not, until it leaves
    // the context with ConfigureAwait(false).
    [Test]
    public async Task AwaitsInATurnResumeInTurns()
    {
        await using var context = new RunicModelContext();
        var steps = new List<(string Step, bool InTurn, bool HasContext)>();
        void Record(string step) => steps.Add((step, context.IsExecuting, SynchronizationContext.Current is not null));
        async Task NestedAsync()
        {
            await Task.Delay(1);
            Record("inner");
        }
        async Task BodyAsync()
        {
            Record("start");
            await Task.Yield();
            Record("yield");
            await Task.Run(() => { });
            Record("run");
            await NestedAsync();
            Record("nested");
            // ConfigureAwait(false) that also yields when the delay has already elapsed: an await of a completed task
            // continues synchronously, still in the turn, which a 1 ms delay allowed under load (#187).
            await Task.Delay(1).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            Record("left");
        }

        var body = await context.InvokeAsync(BodyAsync);
        await body.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Require(steps.Select(step => step.Step).SequenceEqual(["start", "yield", "run", "inner", "nested", "left"]),
            $"The body did not run in order: {string.Join(", ", steps)}");
        Require(steps.Take(5).All(step => step.InTurn && step.HasContext),
            $"A continuation of code started in a turn ran outside a turn: {string.Join(", ", steps)}");
        Require(steps[^1] is { InTurn: false, HasContext: false },
            "Code after ConfigureAwait(false) stayed on the model context.");
    }

    // #473: with turn flows on (the ReactiveUI adapters' main-thread scheduler), a turn whose item carries an execution
    // context marks its async flow as its own, also after the flow leaves the turn. A turn of another context posted
    // from that flow marks it with its own context; a turn posted with suppressed flow marks nothing; the code that
    // awaited the turn is not in its flow.
    [Test]
    public async Task TurnsMarkTheirAsyncFlow()
    {
        var flowTurns = RunicModelContext.FlowTurns;
        RunicModelContext.FlowTurns = true;
        try
        {
            await using var first = new RunicModelContext();
            await using var second = new RunicModelContext();
            var observed = new TaskCompletionSource<(RunicModelContext? Flowing, RunicModelContext? Current)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await first.InvokeAsync(() => _ = Task.Run(() => observed.TrySetResult((RunicModelContext.Flowing, RunicModelContext.Current))));
            Require(await observed.Task.WaitScaledAsync(TimeSpan.FromSeconds(5)) == (first, null),
                "Work that a turn started with Task.Run did not carry the turn's context.");

            var continued = new TaskCompletionSource<RunicModelContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task LeaveAsync()
            {
                await Task.Delay(1).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                continued.TrySetResult(RunicModelContext.Flowing);
            }
            await first.InvokeAsync(() => _ = LeaveAsync());
            Require(await continued.Task.WaitScaledAsync(TimeSpan.FromSeconds(5)) == first,
                "Code after ConfigureAwait(false) in a turn did not carry the turn's context.");

            var posted = new TaskCompletionSource<RunicModelContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await first.InvokeAsync(() => second.TryPost(() => posted.TrySetResult(RunicModelContext.Flowing)));
            Require(await posted.Task.WaitScaledAsync(TimeSpan.FromSeconds(5)) == second,
                "A turn of another context, posted from a turn's flow, kept the posting context.");

            var suppressed = new TaskCompletionSource<RunicModelContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ExecutionContext.SuppressFlow())
                first.TryPost(() => suppressed.TrySetResult(RunicModelContext.Flowing));
            Require(await suppressed.Task.WaitScaledAsync(TimeSpan.FromSeconds(5)) is null, "A turn posted with suppressed flow marked its flow.");

            await first.InvokeAsync(() => { });
            Require(RunicModelContext.Flowing is null, "A turn's flow reached the code that awaited the turn.");

            RunicModelContext.FlowTurns = false;
            var off = new TaskCompletionSource<RunicModelContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await first.InvokeAsync(() => off.TrySetResult(RunicModelContext.Flowing));
            Require(await off.Task.WaitScaledAsync(TimeSpan.FromSeconds(5)) is null, "A turn marked its flow while turn flows were off.");
        }
        finally
        {
            RunicModelContext.FlowTurns = flowTurns;
        }
    }

    // Continuations become turns in the order they become ready, and each runs to its next await
    // before another turn starts.
    [Test]
    public async Task ContinuationsKeepTheirReadyOrder()
    {
        await using var context = new RunicModelContext();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<string>();
        async Task WriteAsync(string name)
        {
            await gate.Task;
            writes.Add($"{name}:1");
            writes.Add($"{name}:2");
            await Task.Yield();
            writes.Add($"{name}:3");
        }

        var bodies = await context.InvokeAsync(() => new[] { WriteAsync("a"), WriteAsync("b"), WriteAsync("c") });
        // Released in a turn, so all three continuations are queued before the first runs. Released from another
        // thread, they are posted one at a time, and a's turn could run and queue a:3 before b and c were ready (#187).
        await context.InvokeAsync(gate.SetResult);
        await Task.WhenAll(bodies).WaitScaledAsync(TimeSpan.FromSeconds(5));
        Require(writes.SequenceEqual(["a:1", "a:2", "b:1", "b:2", "c:1", "c:2", "a:3", "b:3", "c:3"]),
            $"Writes after await were not applied in ready order: {string.Join(", ", writes)}");
    }

    // A continuation never strands its task at shutdown: queued when the context closes, or posted
    // after it closed, it runs on the thread pool outside any turn, and is not reported as dropped.
    [Test]
    public async Task ContinuationsAfterCloseRunOnThePool()
    {
        var context = new RunicModelContext();
        var dropped = new List<Exception>();
        context.UnhandledTurnException += (_, args) => dropped.Add(args.Exception);
        var queuedGate = new TaskCompletionSource();
        var lateGate = new TaskCompletionSource();
        (bool InTurn, bool HasContext)? queuedResumed = null, lateResumed = null;
        async Task QueuedAsync()
        {
            await queuedGate.Task;
            queuedResumed = (context.IsExecuting, SynchronizationContext.Current is not null);
        }
        async Task LateAsync()
        {
            await lateGate.Task;
            lateResumed = (context.IsExecuting, SynchronizationContext.Current is not null);
        }

        var (queued, late) = await context.InvokeAsync(() => (QueuedAsync(), LateAsync()));
        using var running = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Require(context.TryPost(() => { running.Set(); release.Wait(); }), "The blocking turn was rejected.");
        Require(running.Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(5))), "The blocking turn did not start.");
        Task disposal;
        try
        {
            queuedGate.SetResult(); // queued behind the blocking turn
            disposal = context.DisposeAsync().AsTask();
            await queued.WaitScaledAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
        await disposal.WaitScaledAsync(TimeSpan.FromSeconds(5));
        lateGate.SetResult(); // posted after the context closed
        await late.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Require(queuedResumed == (false, false) && lateResumed == (false, false),
            $"A continuation after close did not run on the pool: {queuedResumed}, {lateResumed}");
        Require(dropped.Count == 0, $"A continuation after close was reported: {string.Join(", ", dropped)}");
    }

    // An async void method started in a turn throws into the context it resumed on: the failure is
    // reported like a failed posted turn instead of escaping to the thread pool.
    [Test]
    public async Task AsyncVoidFailureInATurnIsReported()
    {
        await using var context = new RunicModelContext();
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.UnhandledTurnException += (_, args) => reported.TrySetResult(args.Exception);
#pragma warning disable VSTHRD100 // The async void method is the case under test.
        static async void FailLater()
        {
            await Task.Yield();
            throw new InvalidOperationException("async void");
        }
#pragma warning restore VSTHRD100
        await context.InvokeAsync(FailLater);
        var error = await reported.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Require(error is InvalidOperationException { Message: "async void" }, $"The wrong failure was reported: {error}");
        Require(await context.InvokeAsync(() => true), "The context stopped running turns after the failure.");
    }

    [Test]
    public async Task SendRunsInATurnOrInlineAfterClose()
    {
        var context = new RunicModelContext();
        var synchronizationContext = await context.InvokeAsync(() => SynchronizationContext.Current!);
        var inTurn = false;
        synchronizationContext.Send(_ => inTurn = context.IsExecuting, null);
        Require(inTurn, "Send from another thread did not run in a turn.");
        await context.DisposeAsync();
        var ran = false;
        synchronizationContext.Send(_ => ran = !context.IsExecuting, null);
        Require(ran, "Send after close did not run inline.");
    }

    [Test]
    public async Task CloseSignalPrecedesQueuedRejection()
    {
        await using var context = new RunicModelContext();
        using var running = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Require(context.TryPost(() => { running.Set(); release.Wait(); }), "The blocking turn was rejected.");
        Require(running.Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(5))), "The blocking turn did not start.");
        try
        {
            var closed = ((IRunicModelContextLifetime)context).Closed;
            var acceptingAtClose = true;
            var closedAtRejection = false;
            using var registration = closed.Register(() => acceptingAtClose = context.TryPost(() => { }));
            context.UnhandledTurnException += (_, _) => closedAtRejection = closed.IsCancellationRequested;
            var queued = context.InvokeAsync(() => throw new InvalidOperationException("The queued turn ran.")).AsTask();
            Require(context.TryPost(() => throw new InvalidOperationException("The queued post ran.")), "The post was rejected early.");
            var disposal = context.DisposeAsync().AsTask();
            Require(closed.IsCancellationRequested && !acceptingAtClose && closedAtRejection,
                "Close was not signalled between admission stopping and queued work rejection.");
            Require(!disposal.IsCompleted, "Disposal did not wait for the running turn.");
            await ThrowsAsync<ObjectDisposedException>(queued);
            release.Set();
            await disposal.WaitScaledAsync(TimeSpan.FromSeconds(5));
            var lateCalls = 0;
            using var late = context.Closed.Register(() => lateCalls++);
            Require(lateCalls == 1 && context.Closed == closed, "A late observer could not use the closed token.");
        }
        finally { release.Set(); }
    }

    [Test]
    public async Task CloseCallbackFailureDoesNotInterruptShutdown()
    {
        await using var context = new RunicModelContext();
        var failures = new List<Exception>();
        context.UnhandledTurnException += (_, args) => failures.Add(args.Exception);
        using var registration = context.Closed.Register(() => throw new IOException("close callback"));
        await context.DisposeAsync();
        Require(failures is [IOException] && context.Closed.IsCancellationRequested && !context.TryPost(() => { }),
            "A throwing close callback interrupted shutdown or was not reported.");
        await ThrowsAsync<ObjectDisposedException>(context.InvokeAsync(() => { }).AsTask());
    }

    [Test]
    public async Task ConcurrentDisposalWaitsForCloseNotification()
    {
        await using var context = new RunicModelContext();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var registration = context.Closed.Register(() => { entered.Set(); release.Wait(); });
        var first = Task.Run(async () => await context.DisposeAsync());
        try
        {
            Require(entered.Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(5))), "The close callback did not start.");
            var second = context.DisposeAsync().AsTask();
            Require(!second.IsCompleted, "Concurrent disposal completed before close notification finished.");
            release.Set();
            await Task.WhenAll(first, second).WaitScaledAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
    }

    // RunicModelContext logs 1030-1033 through ILogger<RunicModelContext>, whose category
    // moved with the type to Runic.Navigation.RunicModelContext.
    [Test]
    public async Task ContextLogsUnderItsOwnCategory()
    {
        var logs = new CategoryLog();
        var context = new RunicModelContext(new Logger<RunicModelContext>(logs));
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Require(context.TryPost(() => throw new InvalidOperationException("turn")) && context.TryPost(ran.SetResult),
            "The turns were rejected.");
        await ran.Task.WaitScaledAsync(TimeSpan.FromSeconds(10)); // turns run in order, so the failing one has run
        await context.DisposeAsync();
        var entry = logs.Entries.SingleOrDefault(entry => entry.Id == 1030);
        Require(entry is { Category: "Runic.Navigation.RunicModelContext" } && entry.Error is InvalidOperationException,
            $"1030 was not logged under Runic.Navigation.RunicModelContext: {string.Join(", ", logs.Entries)}");
    }

    // A racing Acquire that loses disposes its unused candidate. When that happens inside the
    // candidate's own turn, the shutdown is observed in the background, and a failure is logged
    // as 1033 to the candidate's logger: the Trace fallback for a context that is not a
    // RunicModelContext.
    [Test]
    public async Task LosingCandidateShutdownFailureIsLogged()
    {
        var registry = RunicModelContextRegistry.Shared;
        await using var winner = new RunicModelContext();
        var model = new object();
        var shutdown = new TaskCompletionSource();
        var candidate = new ManualContext(shutdown.Task) { IsExecuting = true };
        IRunicModelContextLease? winnerLease = null;
        var listener = new CaptureListener();
        Trace.Listeners.Add(listener);
        try
        {
            using var lease = registry.Acquire(() =>
            {
                winnerLease = registry.Bind(winner, model);
                return candidate;
            }, model);
            Require(lease.Context == winner && candidate.Disposed, "The losing candidate was not disposed in favour of the winner.");
            shutdown.SetException(new IOException("shutdown"));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!listener.Contains("Releasing a model context failed with System.IO.IOException") && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Require(listener.Contains("Releasing a model context failed with System.IO.IOException"),
                $"The losing candidate's shutdown failure was not logged: {listener.Text}");
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            winnerLease?.Dispose();
        }
    }

    private sealed record LogEntry(string Category, int Id, Exception? Error);

    private sealed class CategoryLog : ILoggerFactory
    {
        private readonly List<LogEntry> _entries = [];
        public IReadOnlyList<LogEntry> Entries { get { lock (_entries) return [.. _entries]; } }
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

    private sealed class CaptureListener : TraceListener
    {
        private readonly System.Text.StringBuilder _text = new();
        public string Text { get { lock (_text) return _text.ToString(); } }
        public bool Contains(string value) => Text.Contains(value, StringComparison.Ordinal);
        public override void Write(string? message) { lock (_text) _text.Append(message); }
        public override void WriteLine(string? message) { lock (_text) _text.AppendLine(message); }
    }

    // A context whose shutdown completes when the test decides.
    private sealed class ManualContext(Task shutdown) : IRunicModelContext
    {
        public bool IsExecuting { get; init; }
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
    public async Task PostedTurnDroppedByDisposalIsReported()
    {
        var context = new RunicModelContext();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reported = new List<Exception>();
        context.UnhandledTurnException += (_, args) => { lock (reported) reported.Add(args.Exception); };
        Require(context.TryPost(() => { started.Set(); release.Wait(); }), "The blocking turn was rejected.");
        Require(started.Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(5))), "The blocking turn did not start.");
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

    [Test]
    public async Task SerialTurnsPreservePostingOrder()
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

    [Test]
    public async Task RegistryRejectsConflictingOwnersAndReusesExistingOwner()
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

    [Test]
    public async Task PostedTurnFailureDoesNotStopTheQueue()
    {
        await using var context = new RunicModelContext();
        Exception? reported = null;
        context.UnhandledTurnException += (_, args) => reported = args.Exception;
        Require(context.TryPost(() => throw new InvalidOperationException("expected")), "The failing turn was rejected.");
        var delivered = 0;
        Require(context.TryPost(() => delivered++), "The follow-up turn was rejected.");
        await context.InvokeAsync(() => { });
        Require(reported is InvalidOperationException && delivered == 1,
            "A failed fire-and-forget turn either was not reported or stopped the model queue.");
    }

    [Test]
    public async Task ConcurrentAcquisitionUsesOneOwner()
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

    [Test]
    public async Task ShutdownRejectsQueuedTurnsAndWaitsForCurrentTurn()
    {
        var context = new RunicModelContext();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Require(context.TryPost(() =>
        {
            started.Set();
            release.Wait();
        }), "The initial turn was rejected.");
        Require(started.Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(5))), "The initial model turn did not start.");

        var queued = context.InvokeAsync(() => throw new InvalidOperationException("Queued work ran during shutdown.")).AsTask();
        var disposing = context.DisposeAsync().AsTask();
        Require(!disposing.IsCompleted, "Context shutdown completed while a synchronous turn was still running.");
        await ThrowsAsync<ObjectDisposedException>(queued);
        release.Set();
        await disposing;
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
}
