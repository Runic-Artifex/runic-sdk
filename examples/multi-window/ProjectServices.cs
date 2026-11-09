using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;
using Runic.Platform;
using Runic.Translations;

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
        // Command failures that are not declared reach the console: the generated Bridge logs them.
        services.AddLogging(logging => logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddScoped<ProjectViewModel>();
        services.AddScoped<DocumentViewModel>();
        services.AddScoped<AccountViewModel>();
        services.AddRunicViews();
        // The per-Window dialogs and clipboard, and IDesktopSettings for the desktop's appearance.
        services.AddRunicPlatformServices();
        // IRunicWindowService, which ProjectViewModel uses to open more Windows, find open documents and ask for an account.
        // It also registers IRunicApplicationLocale, the application's language.
        services.AddRunicWindowService();
        // The C# side of the catalog: menus and other native text. StartLanguageAsync makes it follow the application.
        services.AddSingleton(_ => TranslationContext.CreateBuilder().Add(ProjectTextCatalog.Registration).Build());
        return services;
    }

    /// <summary>
    /// The application's language at start: <paramref name="requested"/> (Program.cs passes <c>RUNIC_LOCALE</c>, for a test
    /// or screenshot run in a language), else the language the user chose, else the best match for the operating
    /// system's languages. The C# catalog follows every switch before the Windows do.
    /// </summary>
    public static async Task StartLanguageAsync(this IServiceProvider services, string? requested, CancellationToken cancellationToken = default)
    {
        var translations = services.GetRequiredService<TranslationContext>();
        var locale = services.GetRequiredService<IRunicApplicationLocale>();
        locale.Follow(translations.SetLocaleAsync);
        var chosen = !string.IsNullOrWhiteSpace(requested) ? requested.Trim()
            : services.GetRequiredService<SettingsStore>().Load().Language
            ?? LocaleNegotiation.Negotiate(DesktopLanguages.Preferred, translations.SupportedLocales, ProjectTextCatalog.DefaultLocale);
        await locale.SetLocaleAsync(chosen, cancellationToken);
    }
}
