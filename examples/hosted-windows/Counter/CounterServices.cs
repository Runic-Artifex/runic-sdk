using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.ReactiveUI;
using Runic.Desktop;
using Runic.Platform;
using Runic.Translations;

namespace CounterTool;

public static class CounterServices
{
    public const string IncrementId = "counter.increment";
    public const string ResetId = "counter.reset";
    public const string DocumentationId = "help.documentation";

    // Program.cs runs the counter with these services, on its own or hosted; the tests build the same graph.
    public static IServiceCollection AddCounter(this IServiceCollection services)
    {
        services.AddRunicReactiveModelContext();
        services.AddScoped<CounterViewModel>();
        services.AddRunicViews();
        // The Window's message boxes and file launcher. Hosted, they are the host's (dialogs/1, window-services/1).
        services.AddRunicPlatformServices();
        // IRunicApplicationLocale, the application's language. Hosted, it follows the host's.
        services.AddRunicWindowService();
        // The C# side of the catalog: the menu bar and the message box. It follows the application's language, matched
        // to the catalog's: a host in French shows the counter in English.
        services.AddSingleton(provider =>
        {
            var translations = TranslationContext.CreateBuilder().Add(CounterTextCatalog.Registration).Build();
            provider.GetRequiredService<IRunicApplicationLocale>().Follow((locale, token) => translations.SetLocaleAsync(
                LocaleNegotiation.Match(locale, translations.SupportedLocales) ?? CounterTextCatalog.DefaultLocale, token));
            return translations;
        });
        return services;
    }

    // The application's menu bar, in its language, set again after each switch. Hosted, the host shows it in the window
    // it opens for the counter, and the items the user chooses there run the Window's commands (CounterViewModel).
    public static void ShowMenuBar(this IServiceProvider services, DesktopHost desktop)
    {
        var text = new CounterText(services.GetRequiredService<TranslationContext>()).Messages;
        void Set() => desktop.SetMenuBar([
            new DesktopMenu(text.menu_counter, [
                new DesktopMenuItem(text.menu_increment, IncrementId) { Shortcut = new DesktopShortcut("I") },
                new DesktopMenuItem(text.menu_reset, ResetId),
            ]) { Role = DesktopMenuRole.File },
            new DesktopMenu(text.menu_help, [new DesktopMenuItem(text.menu_documentation, DocumentationId)]) { Role = DesktopMenuRole.Help },
        ]);
        services.GetRequiredService<IRunicApplicationLocale>().LocaleChanged += (_, _) => Set();
        Set();
    }
}
