using System.Text.Json;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;

namespace Runic.Application.Testing.Tests;

internal static class AcceptedWorkScopeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync()
    {
        await DrainIncludesAnAcceptedFactoryBeforeItReturnsItsTask();
        await CancelledInvocationAndWindowCloseDoNotDrainRecovery();
        await CancellingDrainObservationKeepsOwnership();
        await FaultsAndCancellationRetainTheirOriginalOutcomes();
        await SynchronousFactoryFailuresReleaseAdmission();
        await ConcurrentDisposalWaitsForAcceptedWork();
    }

    private static async Task DrainIncludesAnAcceptedFactoryBeforeItReturnsItsTask()
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

    private static async Task CancelledInvocationAndWindowCloseDoNotDrainRecovery()
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
        }, new Runic.Navigation.ReactiveUI.RunicReactiveSchedulerProvider().For(context));
        var model = new RecoveryModel(command);
        using var transport = new InMemoryViewTransport();
        using var session = new WindowContentSession(transport, rootModel: model);
        using var bridge = new RecoveryBridge(transport, model, session);
        var invocation = transport.CallAsync("recoverySave").AsTask();
        try
        {
            await started.Task.WaitAsync(Deadline);
            var close = await session.BeginCloseAsync(TimeSpan.Zero);
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

    private static async Task CancellingDrainObservationKeepsOwnership()
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

    private static async Task FaultsAndCancellationRetainTheirOriginalOutcomes()
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

    private static async Task SynchronousFactoryFailuresReleaseAdmission()
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

    private static async Task ConcurrentDisposalWaitsForAcceptedWork()
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
        : ViewModelBridge<RecoveryModel>(transport, model, "recovery", static (writer, _, revision) =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("revision", revision);
            writer.WriteEndObject();
        }, [],
        [new("Save", vm => (System.Windows.Input.ICommand)vm.Save,
            ExecuteAsync: (vm, cancellation, _) => ReactiveCommandExecution.ExecuteCompletion(vm.Save, RxVoid.Default, cancellation),
            CanExecute: (_, _) => true)],
        content: content);
}
