using ReactiveUI;
using ReactiveUI.Primitives;

namespace Runic.Navigation.Examples.Notes;

// The page owns a child region for the selected tab. It closes with the page, and leaving the page asks the
// current tab's guard too.
public sealed class SettingsViewModel : ReactiveObject
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

public sealed class GeneralTabViewModel(Action showAdvanced) : ReactiveObject
{
    public string Title => "General";
    public ReactiveCommand<RxVoid, RxVoid> ShowAdvancedCommand { get; } = ReactiveCommand.Create(showAdvanced);
}

public sealed class AdvancedTabViewModel : ReactiveObject
{
    public string Title => "Advanced";
}
