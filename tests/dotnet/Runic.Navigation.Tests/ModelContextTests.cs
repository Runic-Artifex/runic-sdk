using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Runic.Navigation.Tests;

// The model context and its registry (moved from Runic.Application.Testing.Tests;
// the window-session cases stay there).
internal static class ModelContextTests
{
    public static async Task RunAsync()
    {
        await SerialTurnsPreservePostingOrder();
        await PostedTurnFailureDoesNotStopTheQueue();
        await RegistryRejectsConflictingOwnersAndReusesExistingOwner();
        await ConcurrentAcquisitionUsesOneOwner();
        await ShutdownRejectsQueuedTurnsAndWaitsForCurrentTurn();
        await PostedTurnDroppedByDisposalIsReported();
        await ContextLogsUnderItsOwnCategory();
        await LosingCandidateShutdownFailureIsLogged();
    }

    // RunicModelContext logs 1030-1033 through ILogger<RunicModelContext>, whose category
    // moved with the type to Runic.Navigation.RunicModelContext.
    private static async Task ContextLogsUnderItsOwnCategory()
    {
        var logs = new CategoryLog();
        var context = new RunicModelContext(new Logger<RunicModelContext>(logs));
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Require(context.TryPost(() => throw new InvalidOperationException("turn")) && context.TryPost(ran.SetResult),
            "The turns were rejected.");
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10)); // turns run in order, so the failing one has run
        await context.DisposeAsync();
        var entry = logs.Entries.SingleOrDefault(entry => entry.Id == 1030);
        Require(entry is { Category: "Runic.Navigation.RunicModelContext" } && entry.Error is InvalidOperationException,
            $"1030 was not logged under Runic.Navigation.RunicModelContext: {string.Join(", ", logs.Entries)}");
    }

    // A racing Acquire that loses disposes its unused candidate. When that happens inside the
    // candidate's own turn, the shutdown is observed in the background, and a failure is logged
    // as 1033 to the candidate's logger: the Trace fallback for a context that is not a
    // RunicModelContext.
    private static async Task LosingCandidateShutdownFailureIsLogged()
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
        public event Action<Exception>? UnhandledTurnException { add { } remove { } }
        public bool TryPost(Action turn) => false;
        public ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return new(shutdown);
        }
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
