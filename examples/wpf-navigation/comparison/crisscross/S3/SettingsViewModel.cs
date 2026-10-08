using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Disposables;
using CrissCross;
using ReactiveUI;

namespace Comparison.CrissCrossApp.S3;

// Tabs are plain child ViewModels owned by the page; navigation hosts are not involved.
public sealed class SettingsViewModel : RxObject
{
    private RxObject _selectedTab;

    public SettingsViewModel()
    {
        var advanced = new AdvancedTabViewModel();
        Tabs = [new GeneralTabViewModel(() => SelectedTab = advanced), advanced];
        _selectedTab = Tabs[0];
    }

    public IReadOnlyList<RxObject> Tabs { get; }
    public RxObject SelectedTab { get => _selectedTab; set => this.RaiseAndSetIfChanged(ref _selectedTab, value); }
}

public sealed class GeneralTabViewModel : RxObject
{
    public GeneralTabViewModel(Action showAdvanced)
    {
        DisplayName = "General";
        ShowAdvanced = ReactiveCommand.Create(showAdvanced);
    }

    public ReactiveCommand<RxVoid, RxVoid> ShowAdvanced { get; }
}

public sealed class AdvancedTabViewModel : RxObject
{
    public AdvancedTabViewModel() => DisplayName = "Advanced";
}
