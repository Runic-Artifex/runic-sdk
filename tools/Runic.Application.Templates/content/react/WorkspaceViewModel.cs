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
