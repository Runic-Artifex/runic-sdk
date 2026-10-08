using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using System.Windows.Input;

namespace Comparison.Probes;

// Stands in for ReactiveUI.Primitives.Wpf DispatcherSequencer, which always posts (Dispatcher.BeginInvoke, Normal priority).
// Drain() runs what the dispatcher would run before it processes the next input message.
public sealed class PostingSequencer : ISequencer
{
    private readonly Queue<IWorkItem> _queue = new();
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public long Timestamp => System.Diagnostics.Stopwatch.GetTimestamp();
    public void Schedule(IWorkItem item) => _queue.Enqueue(item);
    public void Schedule(IWorkItem item, long dueTimestamp) => _queue.Enqueue(item);
    public void Drain()
    {
        while (_queue.Count > 0)
        {
            _queue.Dequeue().Execute();
        }
    }
}

public sealed class Page(string name, IScreen host) : ReactiveObject, IRoutableViewModel
{
    public string UrlPathSegment => name;
    public IScreen HostScreen { get; } = host;
    public override string ToString() => name;
}

public sealed class Screen : IScreen
{
    public Screen(ISequencer sequencer) => Router = new RoutingState(sequencer);
    public RoutingState Router { get; }
}

public static class RoutingStateProbe
{
    // WPF ButtonBase.OnClick -> CommandHelpers.CriticalExecuteCommandSource: if (command.CanExecute(p)) command.Execute(p).
    private static bool Click(ICommand command)
    {
        if (!command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    private static (Screen Screen, PostingSequencer Ui) Setup(params string[] pages)
    {
        var ui = new PostingSequencer();
        RxSchedulers.MainThreadScheduler = ui; // what WithWpf() installs (DispatcherSequencer.Main)
        var screen = new Screen(ui);
        foreach (var page in pages)
        {
            screen.Router.NavigationStack.Add(new Page(page, screen));
        }

        // Activate the CanNavigateBack -> CanExecute pipeline like a bound Button would.
        ui.Drain();
        return (screen, ui);
    }

    private static string Stack(Screen s) => "[" + string.Join(", ", s.Router.NavigationStack) + "]";

    public static IEnumerable<string> Run()
    {
        {
            var (s, ui) = Setup("List", "A", "B");
            ICommand back = s.Router.NavigateBack;
            var c1 = Click(back);
            ui.Drain(); // posted work runs before the next input message
            var c2 = Click(back);
            ui.Drain();
            yield return $"R1 Router.NavigateBack bound to a Button, stack [List, A, B], two clicks: executed {c1}/{c2}, stack {Stack(s)}";
        }
        {
            var (s, ui) = Setup("List", "A", "B");
            ICommand back = s.Router.NavigateBack;
            var c1 = Click(back);
            var c2 = Click(back); // both clicks before any posted work (worst case)
            ui.Drain();
            yield return $"R2 same, both clicks before the dispatcher drains: executed {c1}/{c2}, stack {Stack(s)}";
        }
        {
            var (s, ui) = Setup("List", "A");
            ICommand back = s.Router.NavigateBack;
            var c1 = Click(back);
            var c2 = Click(back);
            ui.Drain();
            yield return $"R3 stack [List, A], two clicks: executed {c1}/{c2}, stack {Stack(s)}";
        }
        {
            var (s, ui) = Setup("List", "A");
            Exception? thrown = null;
            using var errors = s.Router.NavigateBack.ThrownExceptions.Subscribe(new DelegateObserver(e => thrown = (Exception?)e));
            s.Router.NavigateBack.Execute().Subscribe(new DelegateObserver(_ => { }));
            s.Router.NavigateBack.Execute().Subscribe(new DelegateObserver(_ => { }));
            ui.Drain();
            var afterTwo = Stack(s);
            s.Router.NavigateBack.Execute().Subscribe(new DelegateObserver(_ => { }));
            ui.Drain();
            yield return $"R4 programmatic Execute() x2 on [List, A] (Execute ignores CanExecute): stack {afterTwo}; a third Execute(): {thrown?.GetType().Name ?? "no exception"}";
        }
        {
            // The guarded Back from the RoutingState comparison app (Shell/ShellViewModel.cs).
            var (s, ui) = Setup("List", "A");
            var prompts = new List<TaskCompletionSource<bool>>();
            var goBack = ReactiveCommand.CreateFromTask(async () =>
            {
                var answer = new TaskCompletionSource<bool>();
                prompts.Add(answer);
                if (!await answer.Task)
                {
                    return;
                }

                await s.Router.NavigateBack.Execute().FirstAsync();
            }, s.Router.CanNavigateBack);
            ui.Drain();
            var c1 = Click(goBack);
            ui.Drain();
            var c2 = Click(goBack); // the dialog is still open
            prompts[0].SetResult(true);
            ui.Drain();
            yield return $"R5 guarded Back, second click while the confirm is pending: executed {c1}/{c2}, prompts {prompts.Count}, stack {Stack(s)}";
        }
        {
            var (s, ui) = Setup("List", "A", "B");
            var prompts = new List<TaskCompletionSource<bool>>();
            var goBack = ReactiveCommand.CreateFromTask(async () =>
            {
                var answer = new TaskCompletionSource<bool>();
                prompts.Add(answer);
                if (!await answer.Task)
                {
                    return;
                }

                await s.Router.NavigateBack.Execute().FirstAsync();
            }, s.Router.CanNavigateBack);
            ui.Drain();
            var c1 = Click(goBack);
            var c2 = Click(goBack); // no drain: IsExecuting is applied on the output scheduler, so CanExecute is still true
            foreach (var p in prompts.ToArray())
            {
                p.SetResult(true);
            }

            ui.Drain();
            yield return $"R6 guarded Back on [List, A, B], both clicks before the dispatcher drains: executed {c1}/{c2}, prompts {prompts.Count}, stack {Stack(s)}";
        }
    }

    private sealed class DelegateObserver(Action<object?> onNext) : IObserver<object?>, IObserver<Exception>, IObserver<IRoutableViewModel>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(object? value) => onNext(value);
        public void OnNext(Exception value) => onNext(value);
        public void OnNext(IRoutableViewModel value) => onNext(value);
    }
}
