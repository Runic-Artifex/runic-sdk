using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Runic.Navigation.Wpf.Tests;

/// <summary>
/// Runs a case on the one STA thread that owns the suite's WPF <see cref="Application"/> (OnExplicitShutdown) and
/// runs its dispatcher, as the former Main's thread did: the case is a dispatcher operation, and its pump helpers
/// (Ui.PumpUntil) push nested frames on that dispatcher. The thread starts with the first case that uses this
/// executor, so "registration without an Application" (Order 1, its own STA thread) runs before any Application
/// exists. After each case, windows it left open are closed, as before. An exception that reaches the
/// dispatcher fails the case it happened in. "Application.Shutdown" (last) shuts the Application down; if a filter
/// skipped it, the After(Assembly) hook does.
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
            throw new InvalidOperationException("The suite's WPF dispatcher has already shut down; Application.Shutdown runs last.");
        var name = context.Metadata.DisplayName;
        Exception? cleanup = null;
        Func<Task> run = async () =>
        {
            try { await action(); }
            finally
            {
                if (Dispatcher.FromThread(Thread.CurrentThread) is { HasShutdownStarted: false })
                {
                    try { Ui.CloseStrayWindows(name); }
                    catch (Exception error) { cleanup = error; }
                }
            }
        };
        Exception? failure = null;
        try { await await dispatcher.InvokeAsync(run); }
        catch (Exception error) { failure = error; }
        List<Exception> errors = [];
        if (failure is not null) errors.Add(failure);
        if (cleanup is not null) errors.Add(new InvalidOperationException($"{name} (cleanup) failed.", cleanup));
        if (TakeUnhandled() is { } reached) errors.Add(reached);
        if (errors is [var only])
        {
            if (ReferenceEquals(only, failure)) ExceptionDispatchInfo.Throw(only);
            throw only;
        }
        if (errors.Count > 1) throw new AggregateException(errors);
    }

    // After the last case: Application.Shutdown normally has run already; wait for the thread and report
    // exceptions that reached the dispatcher after the last case.
    [After(HookType.Assembly)]
    public static async Task ShutDownAsync()
    {
        Task<Application>? started;
        lock (Gate) started = _started;
        if (started is null) return;
        var application = await started;
        if (!application.Dispatcher.HasShutdownStarted)
            await application.Dispatcher.InvokeAsync(application.Shutdown);
        if (_thread is { } thread && !thread.Join(Ui.WaitLimit))
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
                    Ui.Initialize();
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
