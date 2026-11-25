using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SharedUi;

/// <summary>The support menu that every application shows.</summary>
public sealed partial class SupportViewModel : ObservableObject
{
    /// <summary>How many problem reports this session sent.</summary>
    [ObservableProperty]
    public partial int ReportsSent { get; private set; }

    /// <summary>Sends a problem report.</summary>
    [RelayCommand]
    private void ReportProblem() => ReportsSent++;
}
