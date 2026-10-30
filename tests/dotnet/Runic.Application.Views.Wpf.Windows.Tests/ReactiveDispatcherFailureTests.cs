using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives.Concurrency;
using ReactiveUI.Primitives.Signals;
using Runic.Application.Views.ReactiveUI.Generated;
using Runic.Navigation.Wpf;
using TUnit.Core;

// #473 in a WPF hybrid: the Bridge runs its calls in DispatcherModelContext turns on the UI thread, and ReactiveUI's
// main-thread scheduler is WPF's DispatcherSequencer (WithWpf()). RunicModelContext.Current is null there, so only
// the failure hold applies: it needs the dispatcher to run a ThrownExceptions delivery in the execution context of
// the flow that scheduled it, which descends from the Bridge call. The Linux conformance fixture covers the other
// schedulers.
internal sealed class ReactiveDispatcherFailureTests
{
    [Test, NotInParallel("WpfSuite", Order = 30)]
    public async Task PlainExecuteFailureBookkeepingOnTheDispatcher()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        await using var context = new DispatcherModelContext(dispatcher);
        var sequencer = new DispatcherSequencer(dispatcher);
        var failures = new List<string>();
        foreach (var caller in new[] { "propagates", "wraps", "handles", "fire-and-forget" })
        {
            var expected = caller is "handles" or "fire-and-forget";
            var logged = await RunAsync(dispatcher, context, sequencer, caller);
            if (logged != expected) failures.Add($"{caller}: logged={logged}");
        }
        Program.Check(failures.Count == 0, "Event 1042 for the native command differs on the WPF dispatcher: " + string.Join("; ", failures));
    }

    private static async Task<bool> RunAsync(Dispatcher dispatcher, DispatcherModelContext context, ISequencer sequencer, string caller)
    {
        var native = ReactiveCommand.CreateFromTask<int, int>(async (id, token) =>
        {
            await Task.Delay(5, token);
            throw new InvalidOperationException("disk full " + id);
        }, null, sequencer);
        var wrapper = ReactiveCommand.CreateFromTask<int>(async id =>
        {
            switch (caller)
            {
                case "propagates":
                    await Signal.ToTask(native.Execute(id));
                    break;
                case "wraps":
                    try { await Signal.ToTask(native.Execute(id)); }
                    catch (InvalidOperationException error) { throw new InvalidOperationException("Could not delete the item.", error); }
                    break;
                case "handles":
                    try { await Signal.ToTask(native.Execute(id)); }
                    catch (InvalidOperationException) { }
                    break;
                default:
                    ((ICommand)native).Execute(id);
                    await Task.Delay(50);
                    break;
            }
        }, null, sequencer);
        var nativeLog = new CountingLogger();
        var wrapperLog = new CountingLogger();
        using (ReactiveCommandExecution.ObserveFailures(native, nativeLog, "Native"))
        using (ReactiveCommandExecution.ObserveFailures(wrapper, wrapperLog, "Wrapper"))
        {
            Task? call = null;
            // On the UI thread the context runs the turn inline, as a generated Bridge's route does.
            await context.InvokeAsync(() => call = ReactiveCommandExecution.ExecuteCompletion(wrapper, 7, CancellationToken.None));
            try { await call!.WaitScaledAsync(TimeSpan.FromSeconds(10)); }
            catch (InvalidOperationException) { }
            await Task.Delay(150);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Program.Check(wrapperLog.Failed == 0, $"{caller}: the wrapper's own failure, which its Bridge call received, was logged.");
            return nativeLog.Failed > 0;
        }
    }

    private sealed class CountingLogger : ILogger
    {
        private int _failed;

        public int Failed => Volatile.Read(ref _failed);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 1042) Interlocked.Increment(ref _failed);
        }
    }
}
