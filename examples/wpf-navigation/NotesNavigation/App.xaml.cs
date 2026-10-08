using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Runic.Navigation.Wpf;

namespace Runic.Navigation.Examples.Notes;

public partial class App : Application
{
    private ServiceProvider? _services;

    // The tests build the same container.
    public static IServiceCollection AddNotes(IServiceCollection services) => services
        .AddRunicWpfNavigation(options => options.UseViewNamingConvention()) // NotesListViewModel -> NotesListView
        .AddSingleton<NoteStore>()
        .AddSingleton<AppRegions>();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _services = AddNotes(new ServiceCollection()).BuildServiceProvider();
        var regions = _services.GetRequiredService<AppRegions>();
        new MainWindow { DataContext = regions }.Show();
        await regions.Main.ResetAsync<NotesListViewModel>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose(); // starts the navigator's disposal without blocking the UI thread
        base.OnExit(e);
    }
}
