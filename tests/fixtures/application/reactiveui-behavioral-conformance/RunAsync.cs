// #471 (I-015): RunAsync runs a command from hand-written code with the generated path's semantics. I-034:
// ReactiveSchedulers.For(object viewModel) schedules on the context the ViewModel is bound to when the work is scheduled.
// These cases run after PlainExecuteTests, with the model-context main-thread scheduler installed.
using Runic.Navigation;
using TUnit.Core;

#if SYSTEM_REACTIVE
using ReactiveUI.Reactive;
using Runic.Application.Views.ReactiveUI.Reactive;
using Runic.Application.Views.ReactiveUI.Reactive.Generated;
using FlavorUnit = System.Reactive.Unit;
using FlavorScheduler = System.Reactive.Concurrency.IScheduler;
#else
using ReactiveUI;
using Runic.Application.Views.ReactiveUI;
using Runic.Application.Views.ReactiveUI.Generated;
using FlavorUnit = ReactiveUI.Primitives.RxVoid;
using FlavorScheduler = ReactiveUI.Primitives.Concurrency.ISequencer;
#endif

internal sealed class RunAsyncTests
{
    [Test, NotInParallel(Order = 24)]
    public async Task RunAsyncSemanticsAsync()
    {
        Require(ReactiveSchedulers.InstallMainThreadScheduler(), "The model-context main-thread scheduler is not installed.");
        await using var context = new RunicModelContext();

        // Completion after zero or more values; the values are not returned.
        var empty = ReactiveCommand.CreateFromObservable<int, int>(_ => new Values<int>());
        await empty.RunAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        var several = ReactiveCommand.CreateFromObservable<int, int>(count => new Values<int>([.. Enumerable.Range(0, count)]));
        await several.RunAsync(3).WaitAsync(TimeSpan.FromSeconds(5));

        // A failure is rethrown unchanged and never logged as event 1042, in a turn and outside any turn.
        var failure = new InvalidOperationException("disk full");
        var failing = ReactiveCommand.CreateFromTask<int>(async (_, token) =>
        {
            await Task.Delay(5, token);
            throw failure;
        });
        var log = new RecordingLogger();
        using (ReactiveCommandExecution.ObserveFailures(failing, log, "Failing"))
        {
            Exception? inTurn = null;
            await context.InvokeAsync(() => _ = CaptureAsync(failing, error => inTurn = error));
            Exception? outside = null;
            await CaptureAsync(failing, error => outside = error);
            await WaitUntilAsync(() => inTurn is not null, "RunAsync in a turn did not fail.");
            Require(ReferenceEquals(inTurn, failure) && ReferenceEquals(outside, failure), "RunAsync did not rethrow the command's exception unchanged.");
            await Task.Delay(150);
            await context.InvokeAsync(() => { });
            Require(log.Entries.IsEmpty, $"A failure that RunAsync received was logged: {string.Join(", ", log.Entries)}");
        }

        // Cancelling the token disposes the execution, which cancels the body's token, and cancels the task.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = ReactiveCommand.CreateFromTask<int, int>(async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { bodyCancelled.TrySetResult(); throw; }
            return 1;
        });
        using (var cancellation = new CancellationTokenSource())
        {
            var run = waiting.RunAsync(1, cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();
            await RequireThrowsAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)), "A cancelled RunAsync did not cancel.");
            Require(run.IsCanceled, "A cancelled RunAsync did not end as cancelled.");
            await bodyCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        // An already cancelled token does not start the execution.
        var runs = 0;
        var counting = ReactiveCommand.Create<int>(_ => { Interlocked.Increment(ref runs); });
        var precancelled = counting.RunAsync(1, new CancellationToken(canceled: true));
        await RequireThrowsAsync<OperationCanceledException>(() => precancelled, "RunAsync with a cancelled token did not cancel.");
        Require(Volatile.Read(ref runs) == 0, "RunAsync with a cancelled token started the execution.");

        // A token-taking command started in a turn delivers its result on that turn's context.
        var delivered = new TaskCompletionSource<RunicModelContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = ReactiveCommand.CreateFromTask<int, int>(async (value, token) =>
        {
            await Task.Delay(10, token);
            return value;
        });
        using (delayed.Subscribe(new Observer<int>(_ => delivered.TrySetResult(RunicModelContext.Current))))
        {
            Task? run = null;
            await context.InvokeAsync(() => run = delayed.RunAsync(2));
            await run!.WaitAsync(TimeSpan.FromSeconds(5));
            Require(await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)) == context, "RunAsync's result left the turn's context.");
        }

        // A null command fails at the call.
        var threw = false;
        try { _ = ReactiveCommandExecutionExtensions.RunAsync<int, int>(null!, 1); }
        catch (ArgumentNullException) { threw = true; }
        Require(threw, "RunAsync did not throw ArgumentNullException for a null command.");

        static async Task CaptureAsync(global::ReactiveUI.IReactiveCommand<int, FlavorUnit> command, Action<Exception> capture)
        {
            try { await command.RunAsync(1); }
            catch (Exception error) { capture(error); }
        }
    }

    [Test, NotInParallel(Order = 25)]
    public async Task ViewModelBoundSchedulerAsync()
    {
        var logs = new RecordingLoggerFactory();
        Require(ReactiveSchedulers.InstallMainThreadScheduler(logs), "The model-context main-thread scheduler is not installed.");
        await using var first = new RunicModelContext();
        await using var second = new RunicModelContext();
        var firstModel = new BoundViewModel();
        var secondModel = new BoundViewModel();
        var scheduler = ReactiveSchedulers.For(firstModel);
        Require(ReferenceEquals(scheduler, ReactiveSchedulers.For(firstModel)), "For(viewModel) returned another scheduler for the same ViewModel.");
        Require(ReferenceEquals(ReactiveSchedulers.For((object)first), ReactiveSchedulers.For(first)), "For(object) did not treat a context as For(context).");

        // Not bound yet: ReactiveUI's main-thread scheduler, which runs work from outside any turn on its fallback.
        Require(await ObserveAsync(scheduler) is null, "Work for an unbound ViewModel ran on a model context.");

        // Bound: the context, resolved when the work is scheduled; each ViewModel on its own context.
        using (RunicModelContextRegistry.Shared.Bind(first, firstModel))
        using (RunicModelContextRegistry.Shared.Bind(second, secondModel))
        {
            Require(await ObserveAsync(scheduler) == first, "Work for a bound ViewModel did not run on its context.");
            Require(await ObserveAsync(ReactiveSchedulers.For(secondModel)) == second, "Work for the second ViewModel did not run on its context.");

            // Ordered with the context's own scheduler.
            var order = new List<int>();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await first.InvokeAsync(() =>
            {
                _ = ConformanceTests.Schedule(ReactiveSchedulers.For(first), 1, value => order.Add(value));
                _ = ConformanceTests.Schedule(scheduler, 2, value => order.Add(value));
                _ = ConformanceTests.Schedule(ReactiveSchedulers.For(first), 3, value => { order.Add(value); done.TrySetResult(); });
            });
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(order.SequenceEqual([1, 2, 3]), $"Work for the ViewModel was not ordered with its context's work: {string.Join(",", order)}.");
        }

        // Released: back to the main-thread scheduler.
        Require(await ObserveAsync(scheduler) is null, "Work for a released ViewModel ran on its former context.");

        // A closed context is not a target.
        var closing = new RunicModelContext();
        using (RunicModelContextRegistry.Shared.Bind(closing, firstModel))
        {
            await closing.DisposeAsync();
            Require(await ObserveAsync(scheduler) is null, "Work for a ViewModel of a closed context ran on a model context.");
        }

        static async Task<RunicModelContext?> ObserveAsync(FlavorScheduler scheduler)
        {
            var observed = new TaskCompletionSource<RunicModelContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Task.Run(() => ConformanceTests.Schedule(scheduler, 0, _ => observed.TrySetResult(RunicModelContext.Current)));
            return await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

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

    // Emits the given values, then completes.
    private sealed class Values<T>(params T[] values) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            foreach (var value in values) observer.OnNext(value);
            observer.OnCompleted();
            return EmptyDisposable.Instance;
        }
    }

    private sealed class Observer<T>(Action<T> next) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => next(value);
    }
}

sealed class BoundViewModel;
