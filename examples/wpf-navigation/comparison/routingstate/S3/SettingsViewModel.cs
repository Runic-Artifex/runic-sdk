using ReactiveUI;
using ReactiveUI.Primitives;

namespace Comparison.RoutingStateApp.S3;

// Tabs are plain child ViewModels owned by the page; RoutingState is not involved.
public sealed class SettingsViewModel : ReactiveObject, IRoutableViewModel
{
    private ReactiveObject _selectedTab;

    public SettingsViewModel(IScreen host)
    {
        HostScreen = host;
        var advanced = new AdvancedTabViewModel();
        Tabs = [new GeneralTabViewModel(() => SelectedTab = advanced), advanced];
        _selectedTab = Tabs[0];
    }

    public string UrlPathSegment => "settings";
    public IScreen HostScreen { get; }
    public IReadOnlyList<ReactiveObject> Tabs { get; }
    public ReactiveObject SelectedTab { get => _selectedTab; set => this.RaiseAndSetIfChanged(ref _selectedTab, value); }
}

public sealed class GeneralTabViewModel(Action showAdvanced) : ReactiveObject
{
    public string Title => "General";
    public ReactiveCommand<RxVoid, RxVoid> ShowAdvanced { get; } = ReactiveCommand.Create(showAdvanced);
}

public sealed class AdvancedTabViewModel : ReactiveObject
{
    public string Title => "Advanced";
}
