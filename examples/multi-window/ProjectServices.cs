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
        // The C# side of the catalog: menus and other native text. It follows the application's language from its
        // creation, before the Windows on every switch; both are singletons, so the subscription lasts as long as the
        // application.
        services.AddSingleton(provider =>
        {
            var translations = TranslationContext.CreateBuilder().Add(ProjectTextCatalog.Registration).Build();
            provider.GetRequiredService<IRunicApplicationLocale>().Follow(translations.SetLocaleAsync);
            return translations;
        });
        return services;
    }

    /// <summary>
    /// The application's language at start: <paramref name="requested"/> (Program.cs passes <c>RUNIC_LOCALE</c>, for a test
    /// or screenshot run in a language), else the language the user chose, else the best match for the operating
    /// system's languages. Each is matched to the catalog's languages, so <c>de-CH</c> starts in <c>de</c>; a tag that
    /// matches none, such as a mistyped <c>RUNIC_LOCALE</c> or a setting of an older version, is skipped with a warning
    /// and never stops the start. The C# catalog follows every switch before the Windows do.
    /// </summary>
    public static async Task StartLanguageAsync(this IServiceProvider services, string? requested, CancellationToken cancellationToken = default)
    {
        var translations = services.GetRequiredService<TranslationContext>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ProjectServices));
        string? Supported(string? tag, string source)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            if (SupportedLanguage(translations, tag) is { } language) return language;
            logger.LogWarning("The language {Tag} of {Source} is none of the application's languages ({Languages}); it is skipped.",
                tag, source, string.Join(", ", translations.SupportedLocales));
            return null;
        }
        var chosen = Supported(requested, "RUNIC_LOCALE")
            ?? Supported(services.GetRequiredService<SettingsStore>().Load().Language, "the settings")
            ?? LocaleNegotiation.Negotiate(DesktopLanguages.Preferred, translations.SupportedLocales, ProjectTextCatalog.DefaultLocale);
        await services.GetRequiredService<IRunicApplicationLocale>().SetLocaleAsync(chosen, cancellationToken);
    }

    /// <summary>
    /// The catalog's language for <paramref name="requested"/>, such as <c>de</c> for <c>de-CH</c>, or
    /// <see langword="null"/> when it names none of them or is not a language tag.
    /// </summary>
    public static string? SupportedLanguage(TranslationContext translations, string? requested) =>
        string.IsNullOrWhiteSpace(requested) ? null : LocaleNegotiation.Match(requested.Trim(), translations.SupportedLocales);
}
