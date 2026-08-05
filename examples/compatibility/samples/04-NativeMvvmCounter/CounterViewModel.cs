using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RunicToolkit.Samples.NativeMvvmCounter;

/// <summary>An ordinary C# ViewModel with no browser or transport dependencies.</summary>
internal sealed partial class CounterViewModel : ObservableObject
{
    [ObservableProperty]
    private int count;

    [RelayCommand]
    private void Increment() => Count++;
}
