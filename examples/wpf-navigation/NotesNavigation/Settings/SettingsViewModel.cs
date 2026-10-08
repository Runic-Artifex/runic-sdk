using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Runic.Navigation.Examples.Notes;

// The page owns a child region for the selected tab. It closes with the page, and leaving the page asks the
// current tab's guard too.
public sealed class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(RunicNavigator navigator)
    {
        var advanced = new AdvancedTabViewModel();
        Tabs = [new GeneralTabViewModel(() => SelectedTab = advanced), advanced];
        Tab = navigator.CreateRegion<object>(this, NavigationTarget.Borrow<object>(Tabs[0]));
        Tab.PropertyChanged += (_, e) => OnPropertyChanged(nameof(SelectedTab));
    }

    public IReadOnlyList<object> Tabs { get; }
    public NavigationRegion<object> Tab { get; }

    // Selecting a tab navigates the child region; the TabControl follows the region.
    public object? SelectedTab
    {
        get => Tab.Current;
        set { if (value is not null) _ = Tab.ReplaceAsync(NavigationTarget.Borrow(value)); }
    }
}

public sealed class GeneralTabViewModel(Action showAdvanced) : ObservableObject
{
    public string Title => "General";
    public IRelayCommand ShowAdvancedCommand { get; } = new RelayCommand(showAdvanced);
}

public sealed class AdvancedTabViewModel : ObservableObject
{
    public string Title => "Advanced";
}
