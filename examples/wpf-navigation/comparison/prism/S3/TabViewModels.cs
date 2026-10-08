using Prism.Commands;
using Prism.Navigation.Regions;

namespace Comparison.PrismApp.S3;

public sealed class GeneralTabViewModel
{
    public GeneralTabViewModel(IRegionManager regions) =>
        ShowAdvancedCommand = new DelegateCommand(() => regions.RequestNavigate("SettingsTabs", nameof(AdvancedTabView)));

    public string Title => "General";
    public DelegateCommand ShowAdvancedCommand { get; }
}

public sealed class AdvancedTabViewModel
{
    public string Title => "Advanced";
}
