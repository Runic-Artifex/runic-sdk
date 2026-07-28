using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebUIToolkit.MVVM;

namespace WebUIToolkitStarter;

[WebUiFrontendContract(
    "webuitoolkitstarter.counter",
    "Counter",
    typeof(CounterJsonContext),
    GeneratedClassName = "CounterContracts")]
public sealed partial class CounterViewModel : ObservableValidator
{
    [ObservableProperty]
    [WebUiFrontendProperty(1, "count", SourceMember = "Count", ReadOnly = true)]
    private int _count;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 10)]
    [WebUiFrontendProperty(2, "step", SourceMember = "Step", IncludeValidation = true)]
    private int _step = 1;

    [WebUiFrontendCollection(3, "history")]
    public ObservableCollection<int> History { get; } = [0];

    [WebUiFrontendProperty(4, "summary", ReadOnly = true)]
    public string Summary => $"{Count} after {History.Count - 1} increment(s)";

    [RelayCommand]
    [WebUiFrontendCommand(10, "increment")]
    private void Increment()
    {
        Count += Step;
        History.Add(Count);
        OnPropertyChanged(nameof(Summary));
    }
}
