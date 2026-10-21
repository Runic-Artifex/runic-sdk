// #473 (W319-010 B11): a plain ReactiveCommand.Execute started by hand-written code inside a model turn, or on behalf
// of a Bridge call, keeps the model turn's flow and, where the exception reaches a Bridge call, the Bridge's failure
// bookkeeping. These cases run after the others: they need the model-context main-thread scheduler that
// AmbientMainThreadSchedulerSemanticsAsync installs.
using System.ComponentModel;
using System.Windows.Input;
using Runic.Application.Views.Generated;
using Runic.Navigation;
using TUnit.Core;

#if SYSTEM_REACTIVE
using ReactiveUI.Reactive;
using System.Reactive.Threading.Tasks;
using Runic.Application.Views.ReactiveUI.Reactive;
using Runic.Application.Views.ReactiveUI.Reactive.Generated;
using FlavorUnit = System.Reactive.Unit;
using FlavorScheduler = System.Reactive.Concurrency.IScheduler;
#else
using ReactiveUI;
using ReactiveUI.Primitives.Signals;
using Runic.Application.Views.ReactiveUI;
using Runic.Application.Views.ReactiveUI.Generated;
using FlavorUnit = ReactiveUI.Primitives.RxVoid;
using FlavorScheduler = ReactiveUI.Primitives.Concurrency.ISequencer;
#endif

internal sealed class PlainExecuteTests
{
    // Hazard 1: a token-taking command, created without an output scheduler, executed with a plain Execute in a turn.
    // Its body completes outside the turn, and its result and IsExecuting used to run on the fallback (event 1043).
    [Test, NotInParallel(Order = 20)]
    public async Task PlainExecuteInATurnDeliversOnThatTurnsContextAsync()
    {
        Require(ReactiveSchedulers.InstallMainThreadScheduler(), "The model-context main-thread scheduler is not installed.");
        await using var first = new RunicModelContext();
        await using var second = new RunicModelContext();
        foreach (var awaiting in new[] { false, true })
        {
            var runs = await Task.WhenAll(RunAsync(first, awaiting), RunAsync(second, awaiting));
            Require(runs[0].All(observed => observed == first) && runs[1].All(observed => observed == second),
                $"A plain Execute in a turn ({(awaiting ? "awaited" : "subscribed")}) delivered outside its turn's context: "
                + $"first=[{Describe(runs[0], first, second)}] second=[{Describe(runs[1], first, second)}].");
        }

        static async Task<List<RunicModelContext?>> RunAsync(RunicModelContext context, bool awaiting)
        {
            var observed = new List<RunicModelContext?>();
            var command = ReactiveCommand.CreateFromTask(async (CancellationToken token) =>
            {
                await Task.Delay(10, token);
                return 1;
            });
            using var output = command.Subscribe(new Recorder<int>(_ => { lock (observed) observed.Add(RunicModelContext.Current); }));
            using var executing = command.IsExecuting.Subscribe(new Recorder<bool>(SkipFirst(() => { lock (observed) observed.Add(RunicModelContext.Current); })));
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await context.InvokeAsync(() =>
            {
                if (awaiting) _ = AwaitAsync(command, done);
                else _ = command.Execute().Subscribe(new Recorder<int>(_ => { }, () => done.TrySetResult()));
            });
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => { lock (observed) return observed.Count >= 3; }, "The command did not report its execution.");
            await context.InvokeAsync(() => { });
            lock (observed) return [.. observed];
        }

        static async Task AwaitAsync(ReactiveCommand<FlavorUnit, int> command, TaskCompletionSource done)
        {
            await PlainAwait(command.Execute());
            done.TrySetResult();
        }
    }

    // The accepted behaviour change: main-thread work that a turn's async flow schedules after leaving the turn runs
    // on that turn's context, and a turn of another context, or an item of its scheduler, runs in that context's flow.
    [Test, NotInParallel(Order = 21)]
    public async Task MainThreadWorkFollowsTheTurnsFlowAsync()
    {
        var logs = new RecordingLoggerFactory();
        Require(ReactiveSchedulers.InstallMainThreadScheduler(logs), "The model-context main-thread scheduler is not installed.");
        await using var first = new RunicModelContext();
        await using var second = new RunicModelContext();
        var command = ReactiveCommand.Create(() => 1);
        var viewModel = new TurnFlowViewModel();

        Require(await ObserveAsync(first, viewModel.PushFromTaskRun) == first, "Task.Run in a turn did not run on that turn's context.");
        Require(await ObserveAsync(first, report =>
            {
                _ = command.Execute().Subscribe(new Recorder<int>(_ => { }));
                viewModel.PushFromTaskRun(report);
            }) == first, "Task.Run after a command in a turn did not run on that turn's context.");
        Require(await ObserveAsync(first, viewModel.PushAfterConfigureAwait) == first,
            "Work after ConfigureAwait(false) in a turn did not run on that turn's context.");
        Require(await ObserveAsync(first, viewModel.PushFromTimer) == first, "A timer created in a turn did not run on that turn's context.");
        Require(await ObserveAsync(first, report => second.TryPost(() => viewModel.PushFromTaskRun(report))) == second,
            "A turn of the second context, posted from the first one's flow, ran its work on the first.");
        Require(await ObserveAsync(first, report => ConformanceTests.Schedule(ReactiveSchedulers.For(second), 0,
                _ => viewModel.PushFromTaskRun(report))) == second,
            "An item of the second context's scheduler, scheduled from the first one's flow, ran its work on the first.");
        Require(!logs.Logger.Entries.Any(entry => entry.Id == 1043 && entry.Source == typeof(TurnFlowViewModel).FullName),
            $"Work from a turn's flow was reported as outside a model turn: {string.Join(", ", logs.Logger.Entries)}");

        // The flow stays in the turn: the code that awaited the turn is not in it.
        await first.InvokeAsync(() => { });
        var caller = new TaskCompletionSource<RunicModelContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.Run(() => ConformanceTests.Schedule(RxSchedulers.MainThreadScheduler, 0, _ => caller.TrySetResult(RunicModelContext.Current)));
        Require(await caller.Task.WaitAsync(TimeSpan.FromSeconds(5)) is null, "The turn's flow reached the code that awaited the turn.");

        static async Task<RunicModelContext?> ObserveAsync(RunicModelContext context, Action<Action<RunicModelContext?>> start)
        {
            var result = new TaskCompletionSource<RunicModelContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await context.InvokeAsync(() => start(observed => result.TrySetResult(observed)));
            return await result.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    // The closed-context guard: work from the flow of a context that has closed runs on the fallback, as work outside
    // a turn does, instead of being queued to a context that no longer runs it.
    [Test, NotInParallel(Order = 22)]
    public async Task WorkFromAClosedContextsFlowRunsOnTheFallbackAsync()
    {
        var logs = new RecordingLoggerFactory();
        Require(ReactiveSchedulers.InstallMainThreadScheduler(logs), "The model-context main-thread scheduler is not installed.");
        var viewModel = new ClosedContextViewModel();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<RunicModelContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new RunicModelContext();
        await context.InvokeAsync(() => viewModel.PushAfter(release.Task, observed => result.TrySetResult(observed)));
        await context.DisposeAsync();
        release.TrySetResult();
        Require(await result.Task.WaitAsync(TimeSpan.FromSeconds(5)) is null, "Work from a closed context's flow ran on a model context.");
        Require(logs.Logger.Entries.Count(entry => entry.Id == 1043 && entry.Source == typeof(ClosedContextViewModel).FullName) == 1,
            $"Work from a closed context's flow was not reported as outside a model turn: {string.Join(", ", logs.Logger.Entries)}");
    }

    // Hazard 2: a native command presented by a Bridge (its ThrownExceptions logged as event 1042) and run with a plain
    // Execute by a hand-written wrapper command, which a Bridge call executes, as in #471.
    [Test, NotInParallel(Order = 23)]
    public async Task HandWrittenCallerFailureBookkeepingAsync()
    {
        Require(ReactiveSchedulers.InstallMainThreadScheduler(), "The model-context main-thread scheduler is not installed.");
        await using var context = new RunicModelContext();
        var failures = new List<string>();
        foreach (var scheduler in new[] { "main-thread", "context", "immediate" })
        {
            foreach (var caller in new[] { "propagates", "wraps", "wraps-aggregate", "handles", "fire-and-forget", "helper-propagates", "helper-handles" })
            {
                // Logged: the remaining case (the caller handled it, which Runic cannot tell from fire-and-forget) and a
                // fire-and-forget failure, when the Bridge call ends.
                var expected = caller is "handles" or "fire-and-forget";
                var logged = await InBridgeCallAsync(context, scheduler, caller);
                if (logged != expected) failures.Add($"{scheduler}/{caller}: logged={logged}");
            }
            // The remaining case outside any Bridge call: a plain Execute in a turn whose caller handles the exception.
            if (!await OutsideBridgeCallAsync(context, scheduler)) failures.Add($"{scheduler}/outside-call-handles: logged=False");
            if (!await InStreamCallAsync(context, scheduler)) failures.Add($"{scheduler}/fire-and-forget-in-stream: not logged while the stream ran");
        }
        Require(failures.Count == 0, "Event 1042 for the native command differs: " + string.Join("; ", failures));
    }

    private static FlavorScheduler? SchedulerOf(RunicModelContext context, string kind) => kind switch
    {
        "context" => ReactiveSchedulers.For(context),
#if SYSTEM_REACTIVE
        "immediate" => System.Reactive.Concurrency.ImmediateScheduler.Instance,
#else
        "immediate" => ReactiveUI.Primitives.Concurrency.Sequencer.Immediate,
#endif
        _ => null,
    };

    private static ReactiveCommand<int, int> Native(RunicModelContext context, string scheduler) =>
        ReactiveCommand.CreateFromTask<int, int>(async (id, token) =>
        {
            await Task.Delay(5, token);
            throw new InvalidOperationException("disk full " + id);
        }, null, SchedulerOf(context, scheduler));

    private static async Task<bool> InBridgeCallAsync(RunicModelContext context, string scheduler, string caller)
    {
        var native = Native(context, scheduler);
        var nativeLog = new RecordingLogger();
        var wrapperLog = new RecordingLogger();
        var wrapper = ReactiveCommand.CreateFromTask<int>(async id =>
        {
            switch (caller)
            {
                case "propagates":
                    await PlainAwait(native.Execute(id));
                    break;
                case "wraps":
                    try { await PlainAwait(native.Execute(id)); }
                    catch (InvalidOperationException error) { throw new InvalidOperationException("Could not delete the item.", error); }
                    break;
                case "wraps-aggregate":
                    try { await PlainAwait(native.Execute(id)); }
                    catch (InvalidOperationException error) { throw new AggregateException("Could not delete the items.", error, new TimeoutException()); }
                    break;
                case "handles":
                    try { await PlainAwait(native.Execute(id)); }
                    catch (InvalidOperationException) { }
                    break;
                case "helper-propagates":
                    await ReactiveCommandExecution.ExecuteCompletion(native, id, CancellationToken.None);
                    break;
                case "helper-handles":
                    try { await ReactiveCommandExecution.ExecuteCompletion(native, id, CancellationToken.None); }
                    catch (InvalidOperationException) { }
                    break;
                default:
                    ((ICommand)native).Execute(id);
                    await Task.Delay(50);
                    break;
            }
        });
        using (ReactiveCommandExecution.ObserveFailures(native, nativeLog, "Native"))
        using (ReactiveCommandExecution.ObserveFailures(wrapper, wrapperLog, "Wrapper"))
        {
            Task? call = null;
            await context.InvokeAsync(() => call = ReactiveCommandExecution.ExecuteCompletion(wrapper, 7, CancellationToken.None));
            try { await call!.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (error is InvalidOperationException or AggregateException) { }
            // Let a late ThrownExceptions delivery arrive; the context runs its turns in order.
            await Task.Delay(150);
            await context.InvokeAsync(() => { });
            Require(wrapperLog.Entries.IsEmpty, $"{scheduler}/{caller}: the wrapper's own failure, which its Bridge call received, was logged.");
            return nativeLog.Entries.Any(entry => entry.Id == 1042);
        }
    }

    private static async Task<bool> OutsideBridgeCallAsync(RunicModelContext context, string scheduler)
    {
        var native = Native(context, scheduler);
        var nativeLog = new RecordingLogger();
        using (ReactiveCommandExecution.ObserveFailures(native, nativeLog, "Native"))
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await context.InvokeAsync(() => _ = HandleAsync());
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);
            await context.InvokeAsync(() => { });
            return nativeLog.Entries.Any(entry => entry.Id == 1042);

            async Task HandleAsync()
            {
                try { await PlainAwait(native.Execute(3)); }
                catch (InvalidOperationException) { }
                done.TrySetResult();
            }
        }
    }

    // A stream operation can stay open as long as its client wants, so its flow holds no failures until it ends: a
    // fire-and-forget failure in it is logged while the stream still runs.
    private static async Task<bool> InStreamCallAsync(RunicModelContext context, string scheduler)
    {
        var native = Native(context, scheduler);
        var nativeLog = new RecordingLogger();
        var streaming = ReactiveCommand.CreateFromTask<int, int>(async (id, token) =>
        {
            ((ICommand)native).Execute(id);
            await Task.Delay(Timeout.Infinite, token);
            return id;
        });
        using var cancellation = new CancellationTokenSource();
        using (ReactiveCommandExecution.ObserveFailures(native, nativeLog, "Native"))
        {
            Task? call = null;
            await context.InvokeAsync(() => call = ReactiveCommandExecution.ExecuteStream(streaming, 5, new BridgeOperationStream(),
                static value => value.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellation.Token));
            for (var attempt = 0; attempt < 200 && !nativeLog.Entries.Any(entry => entry.Id == 1042); attempt++) await Task.Delay(10);
            var logged = nativeLog.Entries.Any(entry => entry.Id == 1042) && !call!.IsCompleted;
            await cancellation.CancelAsync();
            try { await call!.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            return logged;
        }
    }

    // Plain hand-written awaiting, as #471 describes: the flavor's own Task conversion.
    private static Task<T> PlainAwait<T>(IObservable<T> source) =>
#if SYSTEM_REACTIVE
        source.ToTask();
#else
        Signal.ToTask(source);
#endif

    private static Action<bool> SkipFirst(Action action)
    {
        var first = true;
        return _ =>
        {
            if (first) first = false;
            else action();
        };
    }

    private static string Describe(List<RunicModelContext?> observed, RunicModelContext first, RunicModelContext second) =>
        string.Join(",", observed.Select(context => context is null ? "outside" : context == first ? "first" : context == second ? "second" : "other"));

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10);
        Require(condition(), message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Recorder<T>(Action<T> next, Action? completed = null) : IObserver<T>
    {
        public void OnCompleted() => completed?.Invoke();
        public void OnError(Exception error) { }
        public void OnNext(T value) => next(value);
    }
}

// Pushes values onto ReactiveUI's main-thread scheduler from a turn's background flow. Event 1043 would name this type.
sealed class TurnFlowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public void PushFromTaskRun(Action<RunicModelContext?> report) =>
        _ = Task.Run(() => ConformanceTests.Schedule(RxSchedulers.MainThreadScheduler, 0, _ => report(RunicModelContext.Current)));

    public void PushAfterConfigureAwait(Action<RunicModelContext?> report) => _ = PushAsync(report);

    private static async Task PushAsync(Action<RunicModelContext?> report)
    {
        await Task.Delay(5).ConfigureAwait(false);
        _ = ConformanceTests.Schedule(RxSchedulers.MainThreadScheduler, 0, _ => report(RunicModelContext.Current));
    }

    public void PushFromTimer(Action<RunicModelContext?> report)
    {
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            timer?.Dispose();
            _ = ConformanceTests.Schedule(RxSchedulers.MainThreadScheduler, 0, _ => report(RunicModelContext.Current));
        }, null, 5, Timeout.Infinite);
    }
}

sealed class ClosedContextViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public void PushAfter(Task released, Action<RunicModelContext?> report) => _ = PushAsync(released, report);

    private static async Task PushAsync(Task released, Action<RunicModelContext?> report)
    {
        await released.ConfigureAwait(false);
        _ = ConformanceTests.Schedule(RxSchedulers.MainThreadScheduler, 0, _ => report(RunicModelContext.Current));
    }
}
