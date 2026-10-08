using System.Windows;
using Comparison.CrissCrossApp.S1;
using Comparison.CrissCrossApp.S3;
using CrissCross;
using ReactiveUI.Builder;
using Splat;

namespace Comparison.CrissCrossApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var store = new NoteStore();
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithWpf()
            .RegisterView<NotesListView, NotesListViewModel>() // [S1]
            .RegisterView<NoteDetailView, NoteDetailViewModel>() // [S1]
            .RegisterView<SettingsView, SettingsViewModel>() // [S3]
            .RegisterView<GeneralTabView, GeneralTabViewModel>() // [S3]
            .RegisterView<AdvancedTabView, AdvancedTabViewModel>() // [S3]
            .WithRegistration(r =>
            {
                // Back navigation re-resolves the previous page by type, so pages are singletons.
                r.RegisterLazySingleton(() => new NotesListViewModel(store)); // [S1]
                r.RegisterLazySingleton(() => new NoteDetailViewModel(store)); // [S1]
                r.Register(() => new SettingsViewModel()); // [S3]
            })
            .BuildApp();
        AppLocator.CurrentMutable.SetupComplete();
        base.OnStartup(e);
    }
}
