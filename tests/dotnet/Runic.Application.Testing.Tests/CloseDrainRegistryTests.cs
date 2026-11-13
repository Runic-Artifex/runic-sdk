using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// sdk#900 (W4a): AcceptedWorkScope.WaitForAcceptedAsync, and what a window's close does to operations by their
// RunicOperationClose: Cancel at the decision, Default at the timeout, Durable never.
internal sealed class CloseDrainRegistryTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Test]
    public async Task WaitForAcceptedWaitsForEarlierWorkAndKeepsAdmissionOpenAsync()
    {
        var scope = new AcceptedWorkScope();
        var first = Signal();
        var later = Signal();
        var earlier = scope.RunAsync(() => first.Task);
        var wait = scope.WaitForAcceptedAsync();
        // Admission stays open, and work accepted after the call is not waited for.
        var accepted = scope.RunAsync(() => later.Task);
        Require(!wait.IsCompleted, "The wait ended before the earlier work.");
        first.SetResult();
        await wait.WaitScaledAsync(Deadline);
        Require(!accepted.IsCompleted, "The later work ended unexpectedly.");
        Require(scope.WaitForAcceptedAsync().IsCompleted is false, "A second wait did not wait for the later work.");
        later.SetResult();
        await Task.WhenAll(earlier, accepted).WaitScaledAsync(Deadline);
        // The scope learns of the end in a continuation of the work, which can run after the awaiter's.
        await scope.WaitForAcceptedAsync().WaitScaledAsync(Deadline);
        await scope.DisposeAsync();
    }

    [Test]
    public async Task WaitForAcceptedFromAcceptedWorkDoesNotWaitForItselfAsync()
    {
        var scope = new AcceptedWorkScope();
        var other = Signal();
        var otherWork = scope.RunAsync(() => other.Task);
        var waited = Signal();
        var self = scope.RunAsync(async () =>
        {
            await Task.Yield();
            // Nested work of the scope waits for the others, not for itself or its parent.
            await scope.RunAsync(async () =>
            {
                await Task.Yield();
                await scope.WaitForAcceptedAsync();
            });
            waited.SetResult();
        });
        await Task.Delay(50);
        Require(!waited.Task.IsCompleted, "A nested wait did not wait for the other work.");
        other.SetResult();
        await Task.WhenAll(self, otherWork, waited.Task).WaitScaledAsync(Deadline);
        // Admission stayed open.
        await scope.RunAsync(() => Task.CompletedTask).WaitScaledAsync(Deadline);
        await scope.DisposeAsync();
    }

    [Test]
    public async Task CancellingTheWaitStopsOnlyTheWaitAsync()
    {
        var logs = new LogCapture();
        var scope = new AcceptedWorkScope(logs);
        var release = Signal();
        var work = scope.RunAsync(async () =>
        {
            await release.Task;
            throw new InvalidOperationException("disk full");
        });
        using var cancellation = new CancellationTokenSource();
        var cancelled = scope.WaitForAcceptedAsync(cancellation.Token);
        var waiting = scope.WaitForAcceptedAsync();
        await cancellation.CancelAsync();
        try
        {
            await cancelled.WaitScaledAsync(Deadline);
            throw new InvalidOperationException("The cancelled wait completed.");
        }
        catch (OperationCanceledException) { }
        Require(!work.IsCompleted && !waiting.IsCompleted, "Cancelling a wait ended the work or another wait.");
        release.SetResult();
        // A faulted task ends the wait without failing it, and is logged once (event 1013).
        await waiting.WaitScaledAsync(Deadline);
        try { await work; } catch (InvalidOperationException) { }
        Require(logs.Entries.Count(entry => entry.EventId.Id == 1013) == 1,
            $"The faulted work was logged {logs.Entries.Count(entry => entry.EventId.Id == 1013)} times.");
        await scope.DisposeAsync();
    }

    [Test]
    public async Task ACloseCancelsCancelAtOnceDefaultAtItsTimeoutAndNeverDurableAsync()
    {
        var time = new FakeTimeProvider();
        using var registry = new BridgeOperationRegistry("close-policies", timeProvider: time);
        var query = Accept(registry, "query", BridgeOperationClosePolicy.Cancel);
        var refresh = Accept(registry, "refresh", BridgeOperationClosePolicy.Default);
        var save = Accept(registry, "save", BridgeOperationClosePolicy.Durable);
        await Task.WhenAll(query.Started.Task, refresh.Started.Task, save.Started.Task).WaitScaledAsync(Deadline);

        var close = registry.BeginCloseAsync(TimeSpan.FromSeconds(10)).AsTask();
        // Cancel: asked to cancel when the close is decided; the close still waits for its end.
        Require((await registry.WaitForTerminalAsync(Key("query")).AsTask().WaitScaledAsync(Deadline)).Kind is BridgeOperationStatusKind.Cancelled,
            "The Cancel operation was not cancelled at the close's start.");
        Require(!refresh.Cancelled && !save.Cancelled && !close.IsCompleted, "The close cancelled before its timeout.");

        time.Advance(TimeSpan.FromSeconds(10));
        var result = await close.WaitScaledAsync(Deadline);
        Require(!result.Drained && result.RemainingRunningOperations == 1 && result.RemainingDurableWork == 1,
            $"The close's result is {result}.");
        Require((await registry.WaitForTerminalAsync(Key("refresh")).AsTask().WaitScaledAsync(Deadline)).Kind is BridgeOperationStatusKind.Cancelled,
            "The Default operation was not cancelled at the timeout.");
        await Task.Delay(50);
        Require(!save.Cancelled && !result.Remaining.IsCompleted, "The Durable operation was cancelled or not waited for.");

        save.Release.SetResult();
        await result.Remaining.WaitScaledAsync(Deadline);
        Require(registry.Lookup(Key("save")).Kind is BridgeOperationStatusKind.Succeeded, "The Durable operation did not succeed.");
    }

    [Test]
    public async Task OwnerShutdownAndDisposalNeverCancelDurableWorkAsync()
    {
        using var shutdown = new CancellationTokenSource();
        var registry = new BridgeOperationRegistry("owner-shutdown", ownerShutdown: shutdown.Token);
        var refresh = Accept(registry, "refresh", BridgeOperationClosePolicy.Default);
        var save = Accept(registry, "save", BridgeOperationClosePolicy.Durable);
        var query = Accept(registry, "query", BridgeOperationClosePolicy.Cancel);
        await Task.WhenAll(refresh.Started.Task, save.Started.Task, query.Started.Task).WaitScaledAsync(Deadline);

        await shutdown.CancelAsync();
        Require((await registry.WaitForTerminalAsync(Key("refresh")).AsTask().WaitScaledAsync(Deadline)).Kind is BridgeOperationStatusKind.Cancelled
                && (await registry.WaitForTerminalAsync(Key("query")).AsTask().WaitScaledAsync(Deadline)).Kind is BridgeOperationStatusKind.Cancelled,
            "The owner's shutdown did not cancel the Default and Cancel operations.");
        registry.Dispose();
        await Task.Delay(50);
        Require(!save.Cancelled && !save.Ended, "The owner's shutdown or disposal cancelled a Durable operation.");

        // The client's cancel() still cancels it.
        var durable = new BridgeOperationRegistry("client-cancel");
        var other = Accept(durable, "save", BridgeOperationClosePolicy.Durable);
        await other.Started.Task.WaitScaledAsync(Deadline);
        Require(durable.RequestCancellation(Key("save")), "cancel() was refused.");
        Require((await durable.WaitForTerminalAsync(Key("save")).AsTask().WaitScaledAsync(Deadline)).Kind is BridgeOperationStatusKind.Cancelled,
            "cancel() did not cancel a Durable operation.");
        durable.Dispose();
        save.Release.SetResult();
    }

    [Test]
    public async Task RetirementCancelStillCancelsADurableOperationAsync()
    {
        using var registry = new BridgeOperationRegistry("retire-durable");
        var owner = new object();
        var work = new RetireWork("save");
        var request = new BridgeOperationRequest("contract", "Save", "save", "save");
        registry.AcceptUnlessRefused(request, () => null, async cancellation =>
            {
                await work.RunAsync(cancellation);
                return BridgeOperationResult.None;
            },
            new BridgeOperationRetirement(RunicOperationRetirement.Cancel, owner, [], "route"),
            close: BridgeOperationClosePolicy.Durable);
        await work.Started.Task.WaitScaledAsync(Deadline);
        // The content's departure is not the window's close: the retirement policy applies.
        await registry.Retire(owner).WaitScaledAsync(Deadline);
        Require(work.Cancelled, "Retirement with Cancel did not cancel a Durable operation.");
    }

    [Test]
    public async Task UnmarkedOperationsCloseAsBeforeAsync()
    {
        var time = new FakeTimeProvider();
        using var registry = new BridgeOperationRegistry("unmarked", timeProvider: time);
        var first = Accept(registry, "first", BridgeOperationClosePolicy.Default);
        var second = Accept(registry, "second", BridgeOperationClosePolicy.Default);
        await Task.WhenAll(first.Started.Task, second.Started.Task).WaitScaledAsync(Deadline);
        // A close without a timeout, as DisposeAsync: every unmarked operation is asked to cancel at once.
        var result = await registry.BeginCloseAsync(TimeSpan.Zero).AsTask().WaitScaledAsync(Deadline);
        Require(!result.Drained && result.RemainingRunningOperations == 2 && result.RemainingDurableWork == 0,
            $"The close's result is {result}.");
        await result.Remaining.WaitScaledAsync(Deadline);
        Require(first.Cancelled && second.Cancelled, "An unmarked operation was not asked to cancel.");
    }

    [Test]
    public async Task DrainedScopesCountAsDurableWorkAsync()
    {
        var time = new FakeTimeProvider();
        using var registry = new BridgeOperationRegistry("drained-scopes", timeProvider: time);
        var scope = new AcceptedWorkScope();
        var release = Signal();
        var work = scope.RunAsync(() => release.Task);
        var close = registry.BeginCloseAsync(TimeSpan.FromSeconds(1), [scope.WaitForAcceptedAsync()], CancellationToken.None).AsTask();
        Require(!close.IsCompleted, "The close did not wait for the drained scope.");
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await close.WaitScaledAsync(Deadline);
        Require(!result.Drained && result.RemainingRunningOperations == 0 && result.RemainingDurableWork == 1,
            $"The close's result is {result}.");
        release.SetResult();
        await Task.WhenAll(work, result.Remaining).WaitScaledAsync(Deadline);
        await scope.DisposeAsync();
    }

    private static RetireWork Accept(BridgeOperationRegistry registry, string id, BridgeOperationClosePolicy close)
    {
        var work = new RetireWork(id);
        var admission = registry.AcceptUnlessRefused(new BridgeOperationRequest("contract", id, id, id), () => null,
            async cancellation =>
            {
                await work.RunAsync(cancellation);
                return BridgeOperationResult.None;
            }, close: close);
        Require(admission.Kind is BridgeOperationAdmissionKind.Accepted, $"{id} was not admitted: {admission}.");
        return work;
    }

    private static string Key(string id) => new BridgeOperationRequest("contract", id, id, id).Identity.RegistryKey;

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class LogCapture : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, EventId EventId)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Enqueue((logLevel, eventId));
    }
}
