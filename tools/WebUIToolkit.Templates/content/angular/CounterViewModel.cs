using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WebUIToolkitStarter;

public sealed partial class CounterViewModel : ObservableValidator
{
    [ObservableProperty] private int _count;
    [ObservableProperty, NotifyDataErrorInfo, Range(1, 10)] private int _step = 1;
    public ObservableCollection<int> History { get; } = [0];
    public string Summary => $"{Count} after {History.Count - 1} increment(s)";

    [RelayCommand]
    private void Increment()
    {
        Count += Step;
        History.Add(Count);
        OnPropertyChanged(nameof(Summary));
    }
}
