using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Runic.Navigation;
using Runic.Navigation.Wpf;

namespace HybridNotes.Wpf;

public partial class App : Application
{
    private ServiceProvider? _services;
    private IServiceScope? _windowScope;

    public static IServiceCollection AddServices(IServiceCollection services) => services
            .AddRunicWpfNavigation(options =>
            {
                options.NavigatorLifetime = ServiceLifetime.Scoped;
                options.MapView<NotesListViewModel, NotesListView>()
                    .MapView<EditorViewModel, EditorView>()
                    .MapView<ConfirmViewModel, ConfirmView>();
            })
            .AddHybridNotes()
            .AddRunicViews();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _services = AddServices(new ServiceCollection()).BuildServiceProvider();
        _windowScope = _services.CreateScope();
        var regions = _windowScope.ServiceProvider.GetRequiredService<AppRegions>();
        regions.Main.Start(NavigationTarget.Create<NotesListViewModel>()); // throws if the list can't be created
        new MainWindow { DataContext = regions }.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _windowScope?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }
}
