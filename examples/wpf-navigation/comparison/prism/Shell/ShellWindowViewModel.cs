using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation.Regions;

namespace Comparison.PrismApp.Shell;

public sealed class ShellWindowViewModel : BindableBase
{
    public ShellWindowViewModel(IRegionManager regions) =>
        GoBackCommand = new DelegateCommand(() => regions.Regions["Main"].NavigationService.Journal.GoBack()); // [S4]

    public DelegateCommand GoBackCommand { get; } // [S4]
}
