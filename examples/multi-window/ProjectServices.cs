using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;

namespace MultiWindow;

public static class ProjectServices
{
    // Program.cs opens the first Window with these services; the tests build the same graph.
    public static IServiceCollection AddProject(this IServiceCollection services)
    {
        // Once for the application: every Window reads and writes the same project and settings file.
        services.AddSingleton<ProjectService>();
        services.AddSingleton(new SettingsStore(SettingsStore.DefaultPath));
        // Once per Window: each Window's scope has its own model context and ViewModel. ReactiveUI commands
        // deliver their notifications on the model context of the Window that runs them.
        services.AddRunicReactiveModelContext();
        // Command failures that are not declared reach the console through ObserveBridgeExceptions.
        services.AddLogging(logging => logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddScoped<ProjectViewModel>();
        services.AddRunicViews();
        // The per-Window dialogs and clipboard, and IDesktopSettings for the desktop's appearance.
        services.AddRunicPlatformServices();
        // IRunicWindowService, which ProjectViewModel uses to open more Windows.
        services.AddRunicWindowService();
        return services;
    }
}
