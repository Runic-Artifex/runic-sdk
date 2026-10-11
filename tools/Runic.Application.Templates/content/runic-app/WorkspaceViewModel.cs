#if (viewModels == "reactiveui")
using ReactiveUI;
using ReactiveUI.Primitives;

namespace RunicWindowApp;

public interface IWorkspacePage { }

public sealed class WorkspaceViewModel : ReactiveObject, IDisposable
{
    private IWorkspacePage _main;

    // The Window runs these commands in its model turns, and AddRunicReactiveModelContext
    // makes ReactiveUI deliver their notifications there, ordered with Bridge replies,
    // so a command needs no scheduler argument.
    public WorkspaceViewModel(WelcomeViewModel welcome, CounterViewModel counter)
    {
        _main = welcome;
        ShowWelcomeCommand = ReactiveCommand.Create(() => { Main = welcome; });
        ShowCounterCommand = ReactiveCommand.Create(() => { Main = counter; });
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
    private int _step = 1;

    public CounterViewModel() =>
        IncrementCommand = ReactiveCommand.Create(() => { Count += Step; });

    // As for any MVVM View, the setter's accessibility decides what the web
    // frontend may set: Count is read-only state, Step is a form field, so the
    // generated client has setStep but no setCount.
    public int Count
    {
        get => _count;
        private set => this.RaiseAndSetIfChanged(ref _count, value);
    }

    public int Step
    {
        get => _step;
        set => this.RaiseAndSetIfChanged(ref _step, value);
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
    public CounterViewModel() => Step = 1;

    // As for any MVVM View, the setter's accessibility decides what the web
    // frontend may set: Count is read-only state, Step is a form field, so the
    // generated client has setStep but no setCount.
    [ObservableProperty]
    public partial int Count { get; private set; }

    [ObservableProperty]
    public partial int Step { get; set; }

    [RelayCommand]
    private void Increment() => Count += Step;
}
#endif
