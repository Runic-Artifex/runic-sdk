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
        Tabs = [new GeneralTabViewModel(ShowAdvanced), advanced];
        Tab = navigator.CreateRegion<object>(this, NavigationTarget.Borrow<object>(Tabs[0]));
    }

    public IReadOnlyList<object> Tabs { get; }
    public NavigationRegion<object> Tab { get; }

    private void ShowAdvanced() => _ = Tab.ReplaceAsync(NavigationTarget.Borrow(Tabs[1]));
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
