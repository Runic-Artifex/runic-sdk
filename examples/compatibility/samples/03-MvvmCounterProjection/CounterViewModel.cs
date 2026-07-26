using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WebUIToolkit.Samples.MvvmCounterProjection;

internal sealed partial class CounterViewModel : ObservableObject
{
    [ObservableProperty]
    private int count = 1;

    [RelayCommand]
    private void Increment() => Count++;
}
