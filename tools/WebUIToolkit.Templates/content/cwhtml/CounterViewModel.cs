using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WebUIToolkitStarter;

public sealed partial class CounterViewModel : ObservableObject
{
    [ObservableProperty]
    private int _count;

    [RelayCommand]
    private void Increment() => Count++;
}
