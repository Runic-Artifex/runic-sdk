using System.Windows;
using Comparison.RoutingStateApp.S1;
using Comparison.RoutingStateApp.S3;
using Comparison.RoutingStateApp.Shell;
using ReactiveUI.Builder;

namespace Comparison.RoutingStateApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithWpf()
            .RegisterView<NotesListView, NotesListViewModel>() // [S1]
            .RegisterView<NoteDetailView, NoteDetailViewModel>() // [S1]
            .RegisterView<SettingsView, SettingsViewModel>() // [S3]
            .RegisterView<GeneralTabView, GeneralTabViewModel>() // [S3]
            .RegisterView<AdvancedTabView, AdvancedTabViewModel>() // [S3]
            .BuildApp();
        new MainWindow { DataContext = new ShellViewModel(new NoteStore()) }.Show();
    }
}
