#if (viewModels == "reactiveui")
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;

namespace RunicWindowApp;

public interface IWorkspacePage { }

public sealed class WorkspaceViewModel : ReactiveObject, IDisposable
{
    private IWorkspacePage _main;

    // Commands run on the Window's model context, which orders their
    // notifications with Bridge replies. The Window binds this ViewModel and
    // each page it presents to that context.
    public WorkspaceViewModel(WelcomeViewModel welcome, CounterViewModel counter, ISequencer scheduler)
    {
        _main = welcome;
        ShowWelcomeCommand = ReactiveCommand.Create(() => { Main = welcome; }, scheduler);
        ShowCounterCommand = ReactiveCommand.Create(() => { Main = counter; }, scheduler);
    }

    public IWorkspacePage Main
    {
        get => _main;
        private set => this.RaiseAndSetIfChanged(ref _main, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> ShowWelcomeCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ShowCounterCommand { get; }

    public void Dispose()
    {
        ShowWelcomeCommand.Dispose();
        ShowCounterCommand.Dispose();
    }
}

public sealed class WelcomeViewModel : ReactiveObject, IWorkspacePage
{
    public string Greeting => "Your first Runic View is ready.";
}

public sealed class CounterViewModel : ReactiveObject, IWorkspacePage, IDisposable
{
    private int _count;

    public CounterViewModel(ISequencer scheduler) =>
        IncrementCommand = ReactiveCommand.Create(() => { Count++; }, scheduler);

    public int Count
    {
        get => _count;
        private set => this.RaiseAndSetIfChanged(ref _count, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> IncrementCommand { get; }

    public void Dispose() => IncrementCommand.Dispose();
}
#else
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RunicWindowApp;

public interface IWorkspacePage { }

public sealed partial class WorkspaceViewModel : ObservableObject
{
    private readonly WelcomeViewModel _welcome;
    private readonly CounterViewModel _counter;
    private IWorkspacePage _main;

    public WorkspaceViewModel(WelcomeViewModel welcome, CounterViewModel counter)
    {
        _welcome = welcome;
        _counter = counter;
        _main = welcome;
    }

    public IWorkspacePage Main
    {
        get => _main;
        private set => SetProperty(ref _main, value);
    }

    [RelayCommand]
    private void ShowWelcome() => Main = _welcome;

    [RelayCommand]
    private void ShowCounter() => Main = _counter;
}

public sealed partial class WelcomeViewModel : ObservableObject, IWorkspacePage
{
    public string Greeting => "Your first Runic View is ready.";
}

public sealed partial class CounterViewModel : ObservableObject, IWorkspacePage
{
    private int _count;
    public int Count { get => _count; private set => SetProperty(ref _count, value); }

    [RelayCommand]
    private void Increment() => Count++;
}
#endif
