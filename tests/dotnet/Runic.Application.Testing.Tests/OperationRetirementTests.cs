using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// #284 / #61 acceptance: push a page, start progress operations, unmount the presentation and finish the work. Keep
// keeps running and stays observable; Cancel ends cancelled and Drain succeeds, and the navigator disposes the page only
// after both ended; a retained page never cancels; a retired owner cannot navigate late.
internal sealed class OperationRetirementTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const int OperationRetired = 1017;

    [Test]
    public async Task ReactiveOperationsFollowTheirRetirementPolicyAsync()
    {
        await using var window = new Window();
        RetireReactivePageViewModel? page = null;
        // Pushed for a result, so its entry context can try to complete it late.
        var request = window.Shell.Main.PushForResult<string>(NavigationTarget.Create<IRetirePage>((_, entry) =>
            page = new RetireReactivePageViewModel(entry, window.Context)));
        await Wait(request.Transition);
        var state = page!.State;
        var entryId = window.Shell.Main.CurrentEntry!.Id;
        var view = window.Host.Root.View<RetireReactivePageViewModel>(vm => vm.Main);
        var mount = view.Mount();
        var keep = view.Start(vm => vm.KeepCommand);
        var cancel = view.Start(vm => vm.CancelCommand);
        var drain = view.Start(vm => vm.DrainCommand);
        var detail = view.View<RetireDetailViewModel>(vm => vm.Detail).Start(vm => vm.SyncCommand);
        Require(new[] { keep, cancel, drain, detail }.All(operation => operation.Admission is RunicOperationAdmissionKind.Accepted),
            $"An operation was not admitted: {string.Join(", ", new[] { keep, cancel, drain, detail }.Select(operation => operation.AdmissionJson))}");
        await Wait(Task.WhenAll(state.Keep.Started.Task, state.Cancel.Started.Task, state.Drain.Started.Task, page.Detail.Sync.Started.Task));

        // Retained under another page, then current again and remounted: every operation keeps running.
        await Wait(window.Shell.Main.PushAsync(NavigationTarget.Own<IRetirePage>(new RetireCoverViewModel("cover"))));
        await Task.Delay(50);
        Require(!state.Cancel.Cancelled && !state.Drain.Ended && cancel.GetStatus().Kind is RunicOperationStatusKind.Running,
            "Retaining the page cancelled or ended its operations.");
        await Wait(window.Shell.Main.BackAsync());
        var remounted = window.Host.Root.View<RetireReactivePageViewModel>(vm => vm.Main);
        var remount = remounted.Mount();
        Require(new[] { keep, cancel, drain, detail }.All(operation => operation.GetStatus().Kind is RunicOperationStatusKind.Running),
            "An operation is not observable as running after the page was remounted.");
        // Unmounting the presentation is not retirement.
        remounted.Unmount(remount);
        Require(cancel.GetStatus().Kind is RunicOperationStatusKind.Running && !state.Cancel.Cancelled, "Unmounting cancelled an operation.");

        // Retire the page. Back completes without waiting for the work.
        var back = await Wait(window.Shell.Main.BackAsync());
        Require(back is NavigationResult<IRetirePage>.Committed, $"Back gave {back}.");
        await Wait(window.Navigator.WhenIdleAsync());
        var cancelled = await Wait(cancel.WaitAsync());
        Require(cancelled.Kind is RunicOperationStatusKind.Cancelled && state.Cancel.Cancelled, $"The Cancel operation ended {cancelled.Json}.");
        Require(page.State.Disposed == 0 && keep.GetStatus().Kind is RunicOperationStatusKind.Running,
            "The page was disposed while a Drain operation ran, or retirement ended a Keep operation.");

        // A retired owner cannot navigate late: its entry context and a Requester-tagged request are NotCurrent.
        Require(await Wait(request.Completion) is NavigationCompletion<string>.Dismissed, "Retirement did not dismiss the page's result request.");
        var lateBack = await Wait(page.Entry.BackAsync());
        var lateComplete = await Wait(page.Entry.CompleteAsync("late"));
        var latePush = await Wait(window.Shell.Main.PushAsync(NavigationTarget.Own<IRetirePage>(new RetireCoverViewModel("late")),
            new NavigationRequestOptions { Requester = entryId }));
        Require(lateBack is NavigationResult<object>.Rejected { Reason: NavigationRejection.NotCurrent }
                && lateComplete is NavigationResult<object>.Rejected { Reason: NavigationRejection.NotCurrent }
                && latePush is NavigationResult<IRetirePage>.Rejected { Reason: NavigationRejection.NotCurrent } rejected && rejected.By == entryId
                && window.Shell.Main.Current is RetireCoverViewModel { Title: "home" },
            $"A retired owner navigated late: {lateBack}, {lateComplete}, {latePush}.");

        state.Drain.Release.SetResult();
        Require((await Wait(drain.WaitAsync())).Kind is RunicOperationStatusKind.Succeeded, "The Drain operation did not succeed.");
        await Task.Delay(50);
        Require(page.State.Disposed == 0, "The page was disposed while the Drain operation of a model it presented ran.");
        page.Detail.Sync.Release.SetResult();
        Require((await Wait(detail.WaitAsync())).Kind is RunicOperationStatusKind.Succeeded, "The presented model's Drain operation did not succeed.");
        await Until(() => page.State.Disposed == 1, "The page was not disposed after its Cancel and Drain operations ended.");
        Require(state.RunningAtDispose.SequenceEqual(["Keep"]),
            $"At disposal these operations still ran: {string.Join(", ", state.RunningAtDispose)}; only Keep may.");
        await Until(() => !RunicModelContextRegistry.Shared.TryGet(page, out _),
            "The page's lease or deferred release outlived its operations.");

        // Keep finishes after retirement and stays observable from the window.
        state.Keep.Release.SetResult();
        Require((await Wait(keep.WaitAsync())).Kind is RunicOperationStatusKind.Succeeded
                && keep.GetStatus().Kind is RunicOperationStatusKind.Succeeded,
            "The Keep operation did not complete observably after retirement.");
        var retired = window.Logs.All(OperationRetired).ToArray();
        Require(retired.Length == 3 && retired.All(entry => entry.Level == LogLevel.Debug)
                && retired.Select(entry => $"{entry.State["Member"]}:{entry.State["Policy"]}").Order().SequenceEqual(["Cancel:Cancel", "Drain:Drain", "Sync:Drain"]),
            $"Retirement logged {string.Join(", ", retired.Select(entry => entry.Message))}.");
        _ = mount;
    }

    [Test]
    public async Task ToolkitOperationsFollowTheClassAndPropertyPolicyAsync()
    {
        await using var window = new Window();
        var page = new RetireToolkitPageViewModel();
        await Wait(window.Shell.Main.PushAsync(NavigationTarget.Own<IRetirePage>(page)));
        var state = page.State;
        var view = window.Host.Root.View<RetireToolkitPageViewModel>(vm => vm.Main);
        var upload = view.Start(vm => vm.UploadCommand);
        var export = view.Start(vm => vm.ExportCommand);
        var refresh = view.Start(vm => vm.RefreshCommand);
        await Wait(Task.WhenAll(state.Upload.Started.Task, state.Export.Started.Task, state.Refresh.Started.Task));

        await Wait(window.Shell.Main.BackAsync());
        Require((await Wait(upload.WaitAsync())).Kind is RunicOperationStatusKind.Cancelled && state.Upload.Cancelled,
            "The class's Cancel did not cancel the Toolkit operation.");
        await Task.Delay(50);
        Require(page.State.Disposed == 0 && refresh.GetStatus().Kind is RunicOperationStatusKind.Running,
            "The page was disposed while its Drain operation ran, or the property's Keep was overridden by the class.");
        state.Export.Release.SetResult();
        Require((await Wait(export.WaitAsync())).Kind is RunicOperationStatusKind.Succeeded, "The forwarded Drain operation did not succeed.");
        await Until(() => page.State.Disposed == 1, "The Toolkit page was not disposed after its operations ended.");
        Require(state.RunningAtDispose.SequenceEqual(["Refresh"]), $"At disposal these still ran: {string.Join(", ", state.RunningAtDispose)}.");
        state.Refresh.Release.SetResult();
        Require((await Wait(refresh.WaitAsync())).Kind is RunicOperationStatusKind.Succeeded, "The Keep operation did not succeed.");
    }

    // D036.2: a window whose root ViewModel is a page that a navigator holds, as a WPF child view (WpfBridgeView) of a
    // NavigationHost page is, registers with that navigator: retiring the page applies the policy to its operations.
    [Test]
    public async Task AWindowOfANavigatorsPageFollowsThePolicyAsync()
    {
        await using var window = new Window();
        RetireReactivePageViewModel? page = null;
        await Wait(window.Shell.Main.PushAsync(NavigationTarget.Create<IRetirePage>((_, entry) =>
            page = new RetireReactivePageViewModel(entry, window.Context))));
        var state = page!.State;
        using (var child = new RunicWindowTestHost<RetireReactivePageViewModel>(page,
            (transport, content, vm) => new RetireReactivePageBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ModelContext = window.Context, ViewLocator = new RetireViewLocator() }))
        {
            var cancel = child.Root.Start(vm => vm.CancelCommand);
            var drain = child.Root.Start(vm => vm.DrainCommand);
            await Wait(Task.WhenAll(state.Cancel.Started.Task, state.Drain.Started.Task));
            await Wait(window.Shell.Main.BackAsync());
            Require((await Wait(cancel.WaitAsync())).Kind is RunicOperationStatusKind.Cancelled, "The child window's Cancel operation was not cancelled.");
            await Task.Delay(50);
            Require(page.State.Disposed == 0, "The page was disposed while its child window's Drain operation ran.");
            state.Drain.Release.SetResult();
            Require((await Wait(drain.WaitAsync())).Kind is RunicOperationStatusKind.Succeeded, "The Drain operation did not succeed.");
            await Until(() => page.State.Disposed == 1, "The page was not disposed after its child window's operations ended.");
        }
        state.Keep.Release.SetResult();

        // A window of borrowed content does not register: the navigator never retires it.
        var borrowed = new RetireToolkitPageViewModel();
        await Wait(window.Shell.Main.PushAsync(NavigationTarget.Borrow<IRetirePage>(borrowed)));
        using (var child = new RunicWindowTestHost<RetireToolkitPageViewModel>(borrowed,
            (transport, content, vm) => new RetireToolkitPageBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ModelContext = window.Context, ViewLocator = new RetireViewLocator() }))
        {
            var upload = child.Root.Start(vm => vm.UploadCommand);
            await Wait(borrowed.State.Upload.Started.Task);
            await Wait(window.Shell.Main.BackAsync());
            await Wait(window.Navigator.WhenIdleAsync());
            Require(upload.GetStatus().Kind is RunicOperationStatusKind.Running && borrowed.State.Disposed == 0,
                "Leaving borrowed content cancelled its operation or disposed it.");
            borrowed.State.Upload.Release.SetResult();
            Require((await Wait(upload.WaitAsync())).Kind is RunicOperationStatusKind.Succeeded, "The borrowed page's operation did not succeed.");
        }
    }

    // I-188: retirement cancels a ReactiveUI operation whose work ends after the page's Bridge detached. ReactiveUI then
    // reports the end on ThrownExceptions, and the ViewModel has no subscriber: the Bridge keeps observing the command
    // until the execution ended, so nothing reaches ReactiveUI's default handler, which ends the process. A failure that
    // no caller received is logged as event 1042 with the window's logger.
    [Test]
    public async Task AReactiveOperationEndingAfterRetirementStaysObservedAsync()
    {
        const int ReactiveCommandFailed = 1042;
        await using var window = new Window();
        var page = new RetireLatePageViewModel(window.Context);
        await Wait(window.Shell.Main.PushAsync(NavigationTarget.Own<IRetirePage>(page)));
        var view = window.Host.Root.View<RetireLatePageViewModel>(vm => vm.Main);
        var upload = view.Start(vm => vm.UploadCommand);
        var export = view.Start(vm => vm.ExportCommand);
        await Wait(Task.WhenAll(page.UploadStarted.Task, page.ExportStarted.Task));

        await Wait(window.Shell.Main.BackAsync());
        Require((await Wait(upload.WaitAsync())).Kind is RunicOperationStatusKind.Cancelled
                && (await Wait(export.WaitAsync())).Kind is RunicOperationStatusKind.Cancelled,
            "Retirement did not cancel the operations.");
        await Until(() => page.Disposed == 1, "The page was not disposed after its cancelled operations ended.");

        // The page and its Bridge are gone; now the work ends and ReactiveUI reports it.
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executing = 2;
        using (page.UploadCommand.IsExecuting.Subscribe(new IdleObserver(() => { if (Interlocked.Decrement(ref executing) == 0) idle.TrySetResult(); })))
        using (page.ExportCommand.IsExecuting.Subscribe(new IdleObserver(() => { if (Interlocked.Decrement(ref executing) == 0) idle.TrySetResult(); })))
        {
            page.Release.SetResult();
            await Wait(idle.Task);
        }
        await Until(() => window.Logs.All(ReactiveCommandFailed).Any(), "The failure after retirement was not logged.");
        for (var turn = 0; turn < 3; turn++)
        {
            await Task.Delay(20);
            await window.Context.InvokeAsync(() => { });
        }
        var failed = window.Logs.All(ReactiveCommandFailed).ToArray();
        Require(failed.Length == 1 && failed[0].Level == LogLevel.Error
                && Equals(failed[0].State["Source"], "RetireLatePageViewModel.ExportCommand"),
            $"After retirement Runic logged {string.Join(", ", failed.Select(entry => entry.Message))}; expected the export's failure once.");
    }

    // The default: an operation without a declaration keeps running and the page is disposed at once (unchanged).
    [Test]
    public async Task KeepIsTheDefaultAsync()
    {
        await using var window = new Window();
        RetireReactivePageViewModel? page = null;
        await Wait(window.Shell.Main.PushAsync(NavigationTarget.Create<IRetirePage>((_, entry) =>
            page = new RetireReactivePageViewModel(entry, window.Context))));
        var keep = window.Host.Root.View<RetireReactivePageViewModel>(vm => vm.Main).Start(vm => vm.KeepCommand);
        await Wait(page!.State.Keep.Started.Task);
        await Wait(window.Shell.Main.BackAsync());
        await Until(() => page.State.Disposed == 1, "A page with only Keep operations was not disposed at retirement.");
        Require(keep.GetStatus().Kind is RunicOperationStatusKind.Running
                && window.Logs.All(OperationRetired).Any() == false,
            "Retirement waited for, ended or logged a Keep operation.");
        page.State.Keep.Release.SetResult();
        Require((await Wait(keep.WaitAsync())).Kind is RunicOperationStatusKind.Succeeded, "The Keep operation did not succeed.");
    }

    private static async Task Wait(Task task) => await task.WaitAsync(Timeout);

    private static async Task<T> Wait<T>(Task<T> task) => await task.WaitAsync(Timeout);

    private static async Task<T> Wait<T>(ValueTask<T> task) => await task.AsTask().WaitAsync(Timeout);

    private static async Task Wait(ValueTask task) => await task.AsTask().WaitAsync(Timeout);

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message);
            await Task.Delay(5);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // One window: its model context and navigator, the shell and a test host.
    private sealed class Window : IAsyncDisposable
    {
        public Window()
        {
            Navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = Context });
            Shell = new RetireShellViewModel(Navigator);
            Host = new RunicWindowTestHost<RetireShellViewModel>(Shell,
                (transport, content, vm) => new RetireShellBridge(transport, vm, content: content),
                new RunicWindowTestHostOptions { ModelContext = Context, ViewLocator = new RetireViewLocator(), LoggerFactory = Logs });
        }

        public LogCapture Logs { get; } = new();
        public RunicModelContext Context { get; } = new();
        public RunicNavigator Navigator { get; }
        public RetireShellViewModel Shell { get; }
        public RunicWindowTestHost<RetireShellViewModel> Host { get; }

        public async ValueTask DisposeAsync()
        {
            Host.Dispose();
            await Navigator.DisposeAsync();
            await Context.DisposeAsync();
        }
    }

    // Calls idle once, when IsExecuting is false after it was true.
    private sealed class IdleObserver(Action idle) : IObserver<bool>
    {
        private bool _wasExecuting;
        private int _idle;

        public void OnCompleted() { }
        public void OnError(Exception error) { }

        public void OnNext(bool value)
        {
            if (value) _wasExecuting = true;
            else if (_wasExecuting && Interlocked.Exchange(ref _idle, 1) == 0) idle();
        }
    }

    internal sealed record LogEntry(LogLevel Level, int Id, string Message, IReadOnlyDictionary<string, object?> State);

    internal sealed class LogCapture : ILoggerFactory
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();
        public IEnumerable<LogEntry> All(int id) => _entries.Where(entry => entry.Id == id);
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(LogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
                owner._entries.Enqueue(new(logLevel, eventId.Id, formatter(state, exception),
                    values.ToDictionary(pair => pair.Key, pair => pair.Value)));
            }
        }
    }
}
