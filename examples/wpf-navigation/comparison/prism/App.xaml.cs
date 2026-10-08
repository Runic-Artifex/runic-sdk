using System.Windows;
using Comparison.PrismApp.S1;
using Comparison.PrismApp.S2;
using Comparison.PrismApp.S3;
using Comparison.PrismApp.Shell;
using Prism.DryIoc;
using Prism.Ioc;
using Prism.Navigation.Regions;

namespace Comparison.PrismApp;

public partial class App : PrismApplication
{
    protected override Window CreateShell() => Container.Resolve<ShellWindow>();

    protected override void RegisterTypes(IContainerRegistry registry)
    {
        registry.RegisterSingleton<NoteStore>();
        registry.RegisterForNavigation<NotesListView, NotesListViewModel>(); // [S1]
        registry.RegisterForNavigation<NoteDetailView, NoteDetailViewModel>(); // [S1]
        registry.RegisterDialog<ConfirmDialog, ConfirmDialogViewModel>(); // [S2]
        registry.RegisterForNavigation<SettingsView>(); // [S3]
        registry.RegisterForNavigation<GeneralTabView, GeneralTabViewModel>(); // [S3]
        registry.RegisterForNavigation<AdvancedTabView, AdvancedTabViewModel>(); // [S3]
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        var regions = Container.Resolve<IRegionManager>();
        regions.RegisterViewWithRegion<GeneralTabView>("SettingsTabs"); // [S3]
        regions.RegisterViewWithRegion<AdvancedTabView>("SettingsTabs"); // [S3]
        var shell = (ShellWindowViewModel)MainWindow.DataContext; // [S4]
        regions.Regions["Main"].NavigationService.Navigated += (_, _) => shell.GoBackCommand.RaiseCanExecuteChanged(); // [S4]
        regions.RequestNavigate("Main", nameof(NotesListView)); // [S1]
    }
}
