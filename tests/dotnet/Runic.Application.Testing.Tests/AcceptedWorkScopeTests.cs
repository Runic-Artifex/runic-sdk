using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Application.Views.ReactiveUI.Generated;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class AcceptedWorkScopeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Test]
    public async Task AcceptedWorkCanDisposeItsOwner()
    {
        var owner = new AcceptedWorkScope();
        var otherRelease = Signal();
        var closeRequested = Signal();
        var resourcesReleased = false;
        var other = owner.RunAsync(() => otherRelease.Task);
        // "Save and close": the accepted task disposes the model that owns it,
        // here from a thread-pool continuation that inherits its execution context.
        var saveAndClose = owner.RunAsync(async () =>
        {
            await closeRequested.Task.ConfigureAwait(false);
            await Task.Run(async () =>
            {
                await owner.DisposeAsync();
                resourcesReleased = true;
            }).ConfigureAwait(false);
        });
        try
        {
            closeRequested.SetResult();
            await WaitUntilAdmissionClosedAsync(owner);
            Require(!resourcesReleased && !saveAndClose.IsCompleted,
                "Disposal from accepted work stopped waiting for other accepted work.");
            var external = owner.DisposeAsync().AsTask();
            otherRelease.SetResult();
            await saveAndClose.WaitAsync(Deadline);
            Require(resourcesReleased, "Disposal from accepted work did not complete after other work finished.");
            await Task.WhenAll(external, other).WaitAsync(Deadline);
            await owner.DrainAsync().WaitAsync(Deadline);
        }
        finally
        {
            otherRelease.TrySetResult();
            closeRequested.TrySetResult();
        }
    }

    [Test]
    public async Task NestedAcceptedWorkCanDrainItsOwner()
    {
        var owner = new AcceptedWorkScope();
        using var cancellation = new CancellationTokenSource();
        var outer = owner.RunAsync(async () =>
        {
            await Task.Yield();
            await owner.RunAsync(async () =>
            {
                await Task.Yield();
                // The inner task excludes itself and the outer task that awaits it.
                await owner.DrainAsync(cancellation.Token);
            });
            return 7;
        });
        Require(await outer.WaitAsync(Deadline) == 7, "Nested accepted work could not drain its owner.");
        await owner.DisposeAsync().AsTask().WaitAsync(Deadline);

        // Work of another scope does not exclude this scope's accepted work.
        await using var unrelated = new AcceptedWorkScope();
        var release = Signal();
        var held = unrelated.RunAsync(() => release.Task);
        var foreignOwner = new AcceptedWorkScope();
        Task? foreignDrain = null;
        await foreignOwner.RunAsync(() =>
        {
            foreignDrain = unrelated.DrainAsync();
            return Task.CompletedTask;
        }).WaitAsync(Deadline);
        Require(foreignDrain is { IsCompleted: false }, "Work of another scope skipped this scope's accepted work.");
        release.SetResult();
        await Task.WhenAll(foreignDrain!, held).WaitAsync(Deadline);
        await foreignOwner.DisposeAsync().AsTask().WaitAsync(Deadline);
    }

    [Test]
    public async Task FaultsAreLoggedOnceWithoutFailingTheDrain()
    {
        var log = new LogCapture();
        var owner = new AcceptedWorkScope(log);
        var failure = new InvalidOperationException("Accepted work failed after its invocation detached.");
        var declared = new RunicFailureException("conflict");
        var failed = Signal();
        var domain = Signal();
        var cancelled = Signal();
        _ = owner.RunAsync(() => failed.Task);
        _ = owner.RunAsync(() => domain.Task);
        _ = owner.RunAsync(() => cancelled.Task);
        _ = owner.RunAsync(() => Task.CompletedTask);
        var drain = owner.DrainAsync();
        failed.SetException(failure);
        domain.SetException(declared);
        cancelled.SetCanceled();
        await drain.WaitAsync(Deadline);
        Require(drain.IsCompletedSuccessfully, "A logged fault failed the drain.");
        var entries = log.Entries.ToArray();
        Require(entries.Length == 2, $"Expected two accepted-work log entries, found {entries.Length}.");
        Require(entries.Any(entry => entry.EventId.Id == 1013 && entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, failure)),
            "An accepted-work fault was not logged as event 1013 with its exception.");
        Require(entries.Any(entry => entry.EventId.Id == 1014 && entry.Level == LogLevel.Debug && ReferenceEquals(entry.Exception, declared)),
            "A declared accepted-work failure was not logged as event 1014.");
    }

    // Disposal closes admission synchronously before it waits, so a rejected
    // admission proves that the accepted work has started disposing its owner.
    private static async Task WaitUntilAdmissionClosedAsync(AcceptedWorkScope owner)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (true)
        {
            try { _ = owner.RunAsync(() => Task.CompletedTask); }
            catch (InvalidOperationException) { return; }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The accepted work did not dispose its owner.");
            await Task.Yield();
        }
    }

    [Test]
    public async Task DrainIncludesAnAcceptedFactoryBeforeItReturnsItsTask()
    {
        await using var owner = new AcceptedWorkScope();
        using var releaseFactory = new ManualResetEventSlim();
        var factoryStarted = Signal();
        var releaseWork = Signal();
        Task? returned = null;
        var admission = Task.Run(() =>
        {
            returned = owner.RunAsync(() =>
            {
                factoryStarted.SetResult();
                if (!releaseFactory.Wait(Deadline)) throw new TimeoutException("The factory was not released.");
                return releaseWork.Task;
            });
        });
        try
        {
            await factoryStarted.Task.WaitAsync(Deadline);
            var drain = owner.DrainAsync();
            Require(!drain.IsCompleted, "Drain missed an accepted factory that had not returned its task.");
            var rejectedFactoryRan = false;
            RequireThrows<InvalidOperationException>(() => owner.RunAsync(() =>
            {
                rejectedFactoryRan = true;
                return Task.CompletedTask;
            }));
            Require(!rejectedFactoryRan, "Drain allowed a rejected factory to run.");

            releaseFactory.Set();
            await admission.WaitAsync(Deadline);
            Require(ReferenceEquals(returned, releaseWork.Task), "Ownership replaced the factory's actual task.");
            Require(!drain.IsCompleted, "Drain stopped waiting after the factory returned an unfinished task.");
            releaseWork.SetResult();
            await drain.WaitAsync(Deadline);
        }
        finally
        {
            releaseFactory.Set();
            releaseWork.TrySetResult();
            await admission.WaitAsync(Deadline);
        }
    }

    [Test]
    public async Task CancelledInvocationAndWindowCloseDoNotDrainRecovery()
    {
        await using var owner = new AcceptedWorkScope();
        await using var context = new Runic.Navigation.RunicModelContext();
        var started = Signal();
        var releaseRecovery = Signal();
        var recoveryPublished = false;
        Task? actualWork = null;
        using var command = ReactiveCommand.CreateFromTask(() =>
        {
            actualWork = owner.RunAsync(async () =>
            {
                started.SetResult();
                await releaseRecovery.Task.ConfigureAwait(false);
                recoveryPublished = true;
            });
            return actualWork;
        }, Runic.Application.Views.ReactiveUI.ReactiveSchedulers.For(context));
        var model = new RecoveryModel(command);
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { RootModel = model });
        using var bridge = new RecoveryBridge(transport, model, session);
        var invocation = transport.CommandAsync("recovery", "save").AsTask();
        try
        {
            await started.Task.WaitAsync(Deadline);
            var close = await session.DrainAsync(TimeSpan.Zero);
            using var reply = JsonDocument.Parse(await invocation.WaitAsync(Deadline));
            Require(reply.RootElement.GetProperty("error").GetProperty("kind").GetString() == "cancelled",
                "Window close did not cancel the ReactiveUI invocation wrapper.");
            await close.Completion.WaitAsync(Deadline);
            Require(actualWork is { IsCompleted: false } && !recoveryPublished,
                "The test did not retain real recovery after bridge invocation completion.");

            var dispose = owner.DisposeAsync().AsTask();
            Require(!dispose.IsCompleted, "Application scope disposal released uncooperative recovery work.");
            releaseRecovery.SetResult();
            await dispose.WaitAsync(Deadline);
            Require(recoveryPublished && actualWork!.IsCompletedSuccessfully,
                "Application scope disposal returned before recovery was published.");
        }
        finally
        {
            releaseRecovery.TrySetResult();
        }
    }

    [Test]
    public async Task CancellingDrainObservationKeepsOwnership()
    {
        await using var owner = new AcceptedWorkScope();
        using var cancellation = new CancellationTokenSource();
        var release = Signal();
        var work = owner.RunAsync(() => release.Task);
        try
        {
            var observation = owner.DrainAsync(cancellation.Token);
            cancellation.Cancel();
            await RequireCancelled(observation);
            Require(!work.IsCompleted, "Cancelling a drain observer changed the accepted task.");
            RequireThrows<InvalidOperationException>(() => owner.RunAsync(() => Task.CompletedTask));
            var realDrain = owner.DrainAsync();
            Require(!realDrain.IsCompleted, "A cancelled observer abandoned ownership.");
            release.SetResult();
            await realDrain.WaitAsync(Deadline);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Test]
    public async Task FaultsAndCancellationRetainTheirOriginalOutcomes()
    {
        await using var owner = new AcceptedWorkScope();
        var failed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = Signal();
        var failure = new InvalidOperationException("Accepted work failed after recovery.");
        var returnedFailure = owner.RunAsync(() => failed.Task);
        var returnedCancellation = owner.RunAsync(() => cancelled.Task);
        Require(ReferenceEquals(returnedFailure, failed.Task) && ReferenceEquals(returnedCancellation, cancelled.Task),
            "Ownership replaced an accepted task or its result contract.");
        var drain = owner.DrainAsync();
        failed.SetException(failure);
        cancelled.SetCanceled();
        await drain.WaitAsync(Deadline);
        Require(drain.IsCompletedSuccessfully, "Task failure or cancellation faulted the lifetime drain.");
        try { _ = await returnedFailure; }
        catch (InvalidOperationException error) when (ReferenceEquals(error, failure))
        {
            await RequireCancelled(returnedCancellation);
            return;
        }
        throw new InvalidOperationException("The accepted task lost its original failure.");
    }

    [Test]
    public async Task SynchronousFactoryFailuresReleaseAdmission()
    {
        await using var owner = new AcceptedWorkScope();
        RequireThrows<InvalidOperationException>(() => owner.RunAsync(() => (Task)null!));
        RequireThrows<ArgumentNullException>(() => owner.RunAsync((Func<Task>)null!));
        var result = await owner.RunAsync(() => Task.FromResult(42));
        Require(result == 42, "A completed result task lost its result.");

        var failure = new ArgumentException("Factory failed before returning a task.");
        Task? drain = null;
        var originalExceptionThrown = false;
        try
        {
            _ = owner.RunAsync(() =>
            {
                drain = owner.DrainAsync();
                throw failure;
            });
        }
        catch (ArgumentException error) when (ReferenceEquals(error, failure)) { originalExceptionThrown = true; }
        Require(originalExceptionThrown && drain is not null, "The factory lost its synchronous exception.");
        await drain!.WaitAsync(Deadline);
        RequireThrows<InvalidOperationException>(() => owner.RunAsync(() => Task.CompletedTask));
    }

    [Test]
    public async Task ConcurrentDisposalWaitsForAcceptedWork()
    {
        var owner = new AcceptedWorkScope();
        var release = Signal();
        var work = owner.RunAsync(() => release.Task);
        try
        {
            var first = owner.DisposeAsync().AsTask();
            var second = Task.Run(async () => await owner.DisposeAsync());
            Require(!first.IsCompleted, "Disposal returned before accepted work completed.");
            RequireThrows<InvalidOperationException>(() => owner.RunAsync(() => Task.CompletedTask));
            release.SetResult();
            await Task.WhenAll(first, second, work).WaitAsync(Deadline);
            await owner.DisposeAsync();
            await owner.DrainAsync();
        }
        finally
        {
            release.TrySetResult();
            await owner.DisposeAsync();
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task RequireCancelled(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("The observer or task did not retain its cancellation.");
    }

    private static void RequireThrows<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecoveryModel(IReactiveCommand<RxVoid, RxVoid> save) : ReactiveObject
    {
        public IReactiveCommand<RxVoid, RxVoid> Save { get; } = save;
    }

    private sealed class RecoveryBridge(IBridgeTransport transport, RecoveryModel model, WindowContentSession content)
        : ViewModelBridge<RecoveryModel>(transport, model, "recovery", static (writer, _, revision, _) =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("revision", revision);
            writer.WriteEndObject();
        }, [], [],
        [new("Save", vm => (System.Windows.Input.ICommand)vm.Save) { ExecuteAsync = (vm, cancellation, _) => ReactiveCommandExecution.ExecuteCompletion(vm.Save, RxVoid.Default, cancellation), CanExecute = (_, _) => true }],
        content: content);

    private sealed record LogEntry(LogLevel Level, EventId EventId, Exception? Exception);

    private sealed class LogCapture : ILogger
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Enqueue(new(logLevel, eventId, exception));
    }
}
