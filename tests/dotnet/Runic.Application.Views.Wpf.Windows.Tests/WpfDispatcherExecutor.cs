using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using TUnit.Core;
using TUnit.Core.Interfaces;

/// <summary>
/// Runs every case on one STA thread that owns the suite's WPF <see cref="Application"/> and runs its dispatcher,
/// as the former Main did: a case starts as a dispatcher operation, so its awaits resume on the UI thread and the
/// dispatcher pumps in between. The thread starts with the first case; the Application shuts down after the last.
/// An exception that reaches the dispatcher fails the case it happened in.
/// </summary>
internal sealed class WpfDispatcherExecutor : ITestExecutor
{
    private static readonly Lock Gate = new();
    private static readonly List<Exception> Unhandled = [];
    private static Task<Application>? _started;
    private static Thread? _thread;

    public async ValueTask ExecuteTest(TestContext context, Func<ValueTask> action)
    {
        var application = await StartAsync();
        var dispatcher = application.Dispatcher;
        if (dispatcher.HasShutdownStarted)
            throw new InvalidOperationException("The suite's WPF dispatcher has already shut down.");
        Exception? failure = null;
        Func<Task> run = async () => await action();
        try { await await dispatcher.InvokeAsync(run); }
        catch (Exception error) { failure = error; }
        var reached = TakeUnhandled();
        if (reached is null && failure is not null) ExceptionDispatchInfo.Throw(failure);
        if (reached is not null) throw failure is null ? reached : new AggregateException(failure, reached);
    }

    // After the last case: shut the Application down on its thread and wait for the thread to end.
    [After(HookType.Assembly)]
    public static async Task ShutDownAsync()
    {
        Task<Application>? started;
        lock (Gate) started = _started;
        if (started is null) return;
        var application = await started;
        if (!application.Dispatcher.HasShutdownStarted)
            await application.Dispatcher.InvokeAsync(application.Shutdown);
        if (_thread is { } thread && !thread.Join(TestTimeouts.Scale(TimeSpan.FromSeconds(30))))
            throw new TimeoutException("The WPF dispatcher thread did not stop after Application.Shutdown.");
        if (TakeUnhandled() is { } reached) throw reached;
    }

    private static Task<Application> StartAsync()
    {
        lock (Gate)
        {
            if (_started is not null) return _started;
            var ready = new TaskCompletionSource<Application>(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() =>
            {
                try
                {
                    var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    application.DispatcherUnhandledException += (_, e) =>
                    {
                        lock (Gate) Unhandled.Add(e.Exception);
                        e.Handled = true;
                    };
                    application.Startup += (_, _) => ready.TrySetResult(application);
                    application.Run();
                }
                catch (Exception error) { ready.TrySetException(error); }
            })
            { IsBackground = true, Name = "WPF test dispatcher" };
            _thread.SetApartmentState(ApartmentState.STA);
            // The thread must not inherit the first case's context (TestContext.Current, its captured output):
            // each dispatcher operation carries the context of the case that queued it.
            using (ExecutionContext.SuppressFlow()) _thread.Start();
            return _started = ready.Task;
        }
    }

    private static InvalidOperationException? TakeUnhandled()
    {
        lock (Gate)
        {
            if (Unhandled.Count == 0) return null;
            var error = new InvalidOperationException(
                $"Exceptions reached the dispatcher: {string.Join(Environment.NewLine, Unhandled)}", Unhandled[0]);
            Unhandled.Clear();
            return error;
        }
    }
}
