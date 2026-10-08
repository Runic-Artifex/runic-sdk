using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation.Regions;

namespace Comparison.PrismApp.Shell;

public sealed class ShellWindowViewModel : BindableBase
{
    public ShellWindowViewModel(IRegionManager regions)
    {
        // The Main region exists only once the shell is shown; App raises CanExecuteChanged on each navigation.
        IRegionNavigationJournal Journal() => regions.Regions["Main"].NavigationService.Journal; // [S4]
        GoBackCommand = new DelegateCommand(() => Journal().GoBack(), // [S4]
            () => regions.Regions.ContainsRegionWithName("Main") && Journal().CanGoBack); // [S4]
    }

    public DelegateCommand GoBackCommand { get; } // [S4]
}
