// I-188: a ReactiveCommand reports a cancelled execution on ThrownExceptions when its work stops, which can be after
// the Bridge call was cancelled and the Bridge detached (a retiring navigation entry cancels its operation). Runic keeps
// observing the command until the executions it started have ended, so with no application subscriber nothing reaches
// ReactiveUI's default handler, which would end the process. These cases run after BridgeExceptionSemanticsAsync and
// read the recording default handler it installs.
using Runic.Navigation;
using TUnit.Core;

#if SYSTEM_REACTIVE
using ReactiveUI.Reactive;
using Runic.Application.Views.ReactiveUI.Reactive;
using Runic.Application.Views.ReactiveUI.Reactive.Generated;
using FlavorUnit = System.Reactive.Unit;
#else
using ReactiveUI;
using Runic.Application.Views.ReactiveUI;
using Runic.Application.Views.ReactiveUI.Generated;
using FlavorUnit = ReactiveUI.Primitives.RxVoid;
#endif

internal sealed class DetachedExecutionTests
{
    [Test, NotInParallel(Order = 26)]
    public async Task DetachedExecutionEndsObservedAsync()
    {
        var handled = DefaultExceptionHandlerRecorder.Install();
        handled.Clear();
        await using var context = new RunicModelContext();
        var scheduler = ReactiveSchedulers.For(context);

        // A cancelled operation whose work stops after the Bridge detached: its cancellation reaches no subscriber of
        // the application's, and is not logged.
        var late = new LateWork();
        var command = ReactiveCommand.CreateFromTask<FlavorUnit, int>(late.RunAsync, scheduler);
        var log = new RecordingLogger();
        await CancelThenDetachAsync(command, late, ReactiveCommandExecution.ObserveFailures(command, log, "Page.Upload"));
        late.Release.TrySetResult();
        await WhenIdleAsync(command, context);
        Require(handled.IsEmpty, $"A cancellation reported after the Bridge detached reached the default handler: {string.Join(", ", handled)}");
        Require(log.Entries.IsEmpty, $"A cancellation reported after the Bridge detached was logged: {string.Join(", ", log.Entries)}");

        // Once the execution ended, the detached Bridge no longer observes the command: ReactiveUI's behaviour is back.
        var unobserved = new InvalidOperationException("after the operation ended");
        late.Next = unobserved;
        using (command.Execute(UnitValue()).Subscribe(new IgnoringObserver<int>()))
            await WaitUntilAsync(() => !handled.IsEmpty, "A failure after the Bridge's executions ended did not reach the default handler.");
        Require(ReferenceEquals(handled.Single(), unobserved) && log.Entries.IsEmpty,
            "The detached Bridge still observed the command after its executions ended.");
        handled.Clear();

        // A cancelled operation that fails otherwise after the Bridge detached: no caller received the failure, so it is
        // logged once as event 1042 with the detached Bridge's logger.
        var failing = new LateWork { Failure = new InvalidOperationException("disk full") };
        var failingCommand = ReactiveCommand.CreateFromTask<FlavorUnit, int>(failing.RunAsync, scheduler);
        var failingLog = new RecordingLogger();
        await CancelThenDetachAsync(failingCommand, failing, ReactiveCommandExecution.ObserveFailures(failingCommand, failingLog, "Page.Export"));
        failing.Release.TrySetResult();
        await WaitUntilAsync(() => !failingLog.Entries.IsEmpty, "A failure after the Bridge detached was not logged.");
        await WhenIdleAsync(failingCommand, context);
        Require(failingLog.Entries.Single() is { Id: 1042, Source: "Page.Export", Exception: InvalidOperationException { Message: "disk full" } },
            $"A failure after the Bridge detached was not logged once as event 1042: {string.Join(", ", failingLog.Entries)}");
        Require(handled.IsEmpty, "A failure after the Bridge detached reached the default handler.");

        // RunAsync from hand-written code with no Bridge at all: a cancellation reported after the call returned does not
        // reach the default handler either.
        var unbridged = new LateWork();
        var unbridgedCommand = ReactiveCommand.CreateFromTask<FlavorUnit, int>(unbridged.RunAsync, scheduler);
        using (var cancellation = new CancellationTokenSource())
        {
            var run = unbridgedCommand.RunAsync(UnitValue(), cancellation.Token);
            await unbridged.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();
            await RequireThrowsAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)), "A cancelled RunAsync did not cancel.");
        }
        unbridged.Release.TrySetResult();
        await WhenIdleAsync(unbridgedCommand, context);
        Require(handled.IsEmpty, "A cancellation reported after RunAsync returned reached the default handler.");

        // A failure that the caller received is still not logged after the Bridge detached (q1).
        var received = new InvalidOperationException("received");
        var receiving = ReactiveCommand.CreateFromTask<FlavorUnit, int>(async (_, token) =>
        {
            await Task.Delay(20, token);
            throw received;
        }, scheduler);
        var receivingLog = new RecordingLogger();
        var receivingBridge = ReactiveCommandExecution.ObserveFailures(receiving, receivingLog, "Page.Save");
        var receivingRun = ReactiveCommandExecution.ExecuteCompletion(receiving, UnitValue(), CancellationToken.None);
        receivingBridge.Dispose();
        await RequireThrowsAsync<InvalidOperationException>(() => receivingRun.WaitAsync(TimeSpan.FromSeconds(5)), "The caller did not receive the failure.");
        await WhenIdleAsync(receiving, context);
        Require(receivingLog.Entries.IsEmpty && handled.IsEmpty,
            $"A failure that the caller received was logged or reached the default handler after the Bridge detached: {string.Join(", ", receivingLog.Entries)}");
    }

    // Starts an operation as a Bridge does, cancels it as retirement does, then detaches the Bridge while the work still
    // runs.
    private static async Task CancelThenDetachAsync(global::ReactiveUI.IReactiveCommand<FlavorUnit, int> command, LateWork work, IDisposable bridge)
    {
        using var cancellation = new CancellationTokenSource();
        var running = ReactiveCommandExecution.ExecuteCompletion(command, UnitValue(), cancellation.Token);
        await work.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await RequireThrowsAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)), "The cancelled operation did not end cancelled.");
        bridge.Dispose();
    }

    // Waits until the command is no longer executing, then until the model context ran what ReactiveUI scheduled after
    // it (the ThrownExceptions delivery follows the IsExecuting change on the output scheduler).
    private static async Task WhenIdleAsync(global::ReactiveUI.IReactiveCommand command, RunicModelContext context)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (command.IsExecuting.Subscribe(new Next<bool>(executing => { if (!executing) idle.TrySetResult(); })))
            await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var turn = 0; turn < 3; turn++)
        {
            await Task.Delay(20);
            await context.InvokeAsync(() => { });
        }
    }

    private static FlavorUnit UnitValue() => FlavorUnit.Default;

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10);
        Require(condition(), message);
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> operation, string message) where TException : Exception
    {
        try { await operation(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Work that notices its cancellation only once the test releases it, then throws Failure, or the cancellation.
    // A later execution throws Next at once.
    private sealed class LateWork
    {
        private int _runs;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Failure { get; init; }
        public Exception? Next { get; set; }

        public async Task<int> RunAsync(FlavorUnit _, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _runs) > 1)
            {
                await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
                throw Next ?? new InvalidOperationException("No failure for a later execution.");
            }
            Started.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            if (Failure is not null) throw Failure;
            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }
    }

    private sealed class Next<T>(Action<T> next) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => next(value);
    }
}
