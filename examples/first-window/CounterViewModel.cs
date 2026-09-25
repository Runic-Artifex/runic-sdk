using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FirstWindow;

public sealed partial class CounterViewModel : ObservableObject
{
    private int _count;
    public int Count
    {
        get => _count;
        private set => SetProperty(ref _count, value);
    }

    [ObservableProperty] private int step = 1;

    [RelayCommand]
    private void Increment() => Count += Step;
}
