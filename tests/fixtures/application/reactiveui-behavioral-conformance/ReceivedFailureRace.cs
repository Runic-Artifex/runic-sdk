// I-196: ReactiveUI 26 notifies an execution's own observer on the thread that ended the execution, right after it
// scheduled the ThrownExceptions delivery of the same failure on the command's output scheduler. With a scheduler that
// runs elsewhere, such as a model context's, the two run at the same time. When the delivery reached Runic before the
// caller had received the failure, and the caller's call ended before Runic decided whether to hold it, nothing held it
// any more and it was logged as event 1042, although the caller had received it.
// These cases force that order: the gate lets the execution notify its observer only after the delivery reached Runic
// (BridgeCommandFailures.UnreceivedDelivery), and that seam lets Runic decide only after the receiving call ended.
using Runic.Navigation;
using TUnit.Core;

#if SYSTEM_REACTIVE
using ReactiveUI.Reactive;
using System.Reactive.Threading.Tasks;
using Runic.Application.Views.ReactiveUI.Reactive;
using Runic.Application.Views.ReactiveUI.Reactive.Generated;
using FlavorScheduler = System.Reactive.Concurrency.IScheduler;
#else
using ReactiveUI;
using ReactiveUI.Primitives.Signals;
using Runic.Application.Views.ReactiveUI;
using Runic.Application.Views.ReactiveUI.Generated;
using FlavorScheduler = ReactiveUI.Primitives.Concurrency.ISequencer;
#endif

internal sealed class ReceivedFailureRaceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Test, NotInParallel(Order = 28)]
    public async Task FailureReceivedWhileItsDeliveryIsDecidedIsNotLoggedAsync()
    {
        await using var context = new RunicModelContext();
        var failures = new List<string>();
        foreach (var caller in new[] { "run-async-in-turn", "run-async-outside-turn", "plain-execute-in-bridge-call" })
        {
            var logged = await RaceAsync(context, caller);
            if (logged.Count != 0) failures.Add($"{caller}: {string.Join(", ", logged)}");
        }
        Require(failures.Count == 0, "A failure that its caller received was logged as event 1042: " + string.Join("; ", failures));
    }

    // Returns the event 1042 entries logged for the native command.
    private static async Task<List<string>> RaceAsync(RunicModelContext context, string caller)
    {
        var failure = new InvalidOperationException("disk full");
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new DeliveryGate(ReactiveSchedulers.For(context), delivered.Task);
        var native = ReactiveCommand.CreateFromTask<int, int>(async (_, token) =>
        {
            await go.Task.WaitAsync(token);
            gate.Arm();
            throw failure;
        }, null, gate);
        // Hand-written code that lets the exception of a plain Execute propagate to its Bridge call. It resumes off the
        // model context, which the seam blocks.
        var wrapper = ReactiveCommand.CreateFromTask<int>(async id => await PlainAwait(native.Execute(id)).ConfigureAwait(false));
        var log = new RecordingLogger();
        Task? receiver = null;
        BridgeCommandFailures.UnreceivedDelivery = error =>
        {
            if (!ReferenceEquals(error, failure)) return;
            delivered.TrySetResult();
            // The delivery runs on the model context; the receiving call ends off it.
            ((IAsyncResult)Volatile.Read(ref receiver)!).AsyncWaitHandle.WaitOne(Wait);
        };
        try
        {
            using (ReactiveCommandExecution.ObserveFailures(native, log, "Native"))
            using (ReactiveCommandExecution.ObserveFailures(wrapper, log, "Wrapper"))
            {
                switch (caller)
                {
                    case "run-async-in-turn":
                        await context.InvokeAsync(() => Volatile.Write(ref receiver, native.RunAsync(3)));
                        break;
                    case "run-async-outside-turn":
                        Volatile.Write(ref receiver, native.RunAsync(3));
                        break;
                    default:
                        await context.InvokeAsync(() =>
                            Volatile.Write(ref receiver, ReactiveCommandExecution.ExecuteCompletion(wrapper, 3, CancellationToken.None)));
                        break;
                }
                go.TrySetResult();
                try
                {
                    await receiver!.WaitAsync(Wait);
                    Require(false, $"{caller}: the call did not fail.");
                }
                catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { }
                await delivered.Task.WaitAsync(Wait);
                // The delivery's turn decides before this one runs.
                await context.InvokeAsync(() => { });
                return [.. log.Entries.Where(entry => entry.Id == 1042).Select(entry => $"{entry.Exception?.Message} ({entry.Source})")];
            }
        }
        finally
        {
            BridgeCommandFailures.UnreceivedDelivery = null;
        }
    }

    private static Task<T> PlainAwait<T>(IObservable<T> source) =>
#if SYSTEM_REACTIVE
        source.ToTask();
#else
        Signal.ToTask(source);
#endif

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Delegates to the model context's scheduler. Once armed (the execution is about to fail), each item scheduled from
    // outside the context holds the scheduling thread until it ran or the failure's delivery reached Runic: ReactiveUI
    // schedules the execution's end and then the delivery, and notifies the execution's observer after both returned.
#if SYSTEM_REACTIVE
    private sealed class DeliveryGate(FlavorScheduler inner, Task delivered) : FlavorScheduler
    {
        private volatile bool _armed;

        public void Arm() => _armed = true;

        public DateTimeOffset Now => inner.Now;

        public IDisposable Schedule<TState>(TState state, Func<FlavorScheduler, TState, IDisposable> action)
        {
            if (!_armed || RunicModelContext.Current is not null) return inner.Schedule(state, action);
            var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scheduled = inner.Schedule(state, (scheduler, value) =>
            {
                try { return action(scheduler, value); }
                finally { ran.TrySetResult(); }
            });
            Task.WaitAny([ran.Task, delivered], Wait);
            return scheduled;
        }

        public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<FlavorScheduler, TState, IDisposable> action) =>
            inner.Schedule(state, dueTime, action);

        public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<FlavorScheduler, TState, IDisposable> action) =>
            inner.Schedule(state, dueTime, action);
    }
#else
    private sealed class DeliveryGate(FlavorScheduler inner, Task delivered) : FlavorScheduler
    {
        private volatile bool _armed;

        public void Arm() => _armed = true;

        public DateTimeOffset Now => inner.Now;

        public long Timestamp => inner.Timestamp;

        public void Schedule(ReactiveUI.Primitives.Concurrency.IWorkItem item)
        {
            if (!_armed || RunicModelContext.Current is not null)
            {
                inner.Schedule(item);
                return;
            }
            var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            inner.Schedule(new SignallingItem(item, ran));
            Task.WaitAny([ran.Task, delivered], Wait);
        }

        public void Schedule(ReactiveUI.Primitives.Concurrency.IWorkItem item, long dueTimestamp) => inner.Schedule(item, dueTimestamp);

        private sealed class SignallingItem(ReactiveUI.Primitives.Concurrency.IWorkItem inner, TaskCompletionSource ran)
            : ReactiveUI.Primitives.Concurrency.IWorkItem
        {
            public void Execute()
            {
                try { inner.Execute(); }
                finally { ran.TrySetResult(); }
            }
        }
    }
#endif
}
