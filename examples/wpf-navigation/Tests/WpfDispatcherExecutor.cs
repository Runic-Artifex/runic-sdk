using System.Runtime.ExceptionServices;
using System.Windows;
using TUnit.Core.Interfaces;

namespace Runic.Navigation.Examples.Notes.Tests;

/// <summary>
/// Runs the scenarios as async code on the WPF dispatcher of one Application, like the app itself: one STA
/// thread creates the Application (OnExplicitShutdown) and runs it, and each scenario starts on its dispatcher
/// once it has started up, so awaits resume on the UI thread and the dispatcher keeps pumping in between.
/// A scenario fails when it throws, takes longer than twice Scene.Timeout, or an exception reaches the
/// dispatcher while it runs. The Application shuts down after the last scenario.
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
        if (application.Dispatcher.HasShutdownStarted)
            throw new InvalidOperationException("The test Application has already shut down.");
        Exception? failure = null;
        Func<Task> run = async () => await action();
        try
        {
            var test = await application.Dispatcher.InvokeAsync(run);
            await test.WaitAsync(Scene.Timeout * 2);
        }
        catch (Exception error) { failure = error; }
        var reached = TakeUnhandled();
        if (reached is null && failure is not null) ExceptionDispatchInfo.Throw(failure);
        if (reached is not null) throw failure is null ? reached : new AggregateException(failure, reached);
    }

    // After the last scenario: shut the Application down and wait for its thread to end.
    [After(HookType.Assembly)]
    public static async Task ShutDownAsync()
    {
        Task<Application>? started;
        lock (Gate) started = _started;
        if (started is null) return;
        var application = await started;
        if (!application.Dispatcher.HasShutdownStarted)
            await application.Dispatcher.InvokeAsync(application.Shutdown);
        if (_thread is { } thread && !thread.Join(Scene.Timeout))
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
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.DispatcherUnhandledException += (_, e) =>
                    {
                        lock (Gate) Unhandled.Add(e.Exception);
                        e.Handled = true;
                    };
                    app.Startup += (_, _) => ready.TrySetResult(app);
                    app.Run();
                }
                catch (Exception error) { ready.TrySetException(error); }
            })
            { IsBackground = true, Name = "WPF test dispatcher" };
            _thread.SetApartmentState(ApartmentState.STA);
            // The thread must not inherit the first scenario's context (TestContext.Current, its captured output):
            // each dispatcher operation carries the context of the scenario that queued it.
            using (ExecutionContext.SuppressFlow()) _thread.Start();
            return _started = ready.Task;
        }
    }

    private static InvalidOperationException? TakeUnhandled()
    {
        lock (Gate)
        {
            if (Unhandled.Count == 0) return null;
            var error = new InvalidOperationException($"dispatcher: {string.Join(Environment.NewLine, Unhandled)}", Unhandled[0]);
            Unhandled.Clear();
            return error;
        }
    }
}
