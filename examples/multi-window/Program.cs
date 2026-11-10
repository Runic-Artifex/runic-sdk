using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using MultiWindow;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Platform;
using Runic.Translations;

await using var provider = new ServiceCollection().AddProject().BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true,
});

return RunicDesktopHost.Run(provider, async host =>
{
    // The theme is the application's: every Window shows it, the page's prefers-color-scheme follows, and a new choice
    // switches the open Windows at once. "system" (NoPreference) follows the desktop.
    var settings = provider.GetRequiredService<SettingsStore>();
    await host.Desktop.SetColorSchemeAsync(settings.Load().ColorScheme);
    settings.Saved += (_, saved) => _ = host.Desktop.SetColorSchemeAsync(saved.ColorScheme).AsTask();
    // The language: RUNIC_LOCALE, else the saved choice, else the operating system's. The menu bar is set again after
    // each switch, once the C# catalog has switched; its Language items run the focused Window's ChooseLanguageCommand.
    var locale = provider.GetRequiredService<IRunicApplicationLocale>();
    var translations = provider.GetRequiredService<TranslationContext>();
    var text = new ProjectText(translations);
    // The catalog's languages (runic.json "locales"), each in its own name, as language menus show them; the tag
    // without culture data (invariant globalization).
    static string NativeName(string tag)
    {
        try { return CultureInfo.GetCultureInfo(tag).NativeName; }
        catch (CultureNotFoundException) { return tag; }
    }
    var languages = translations.SupportedLocales.Select(tag => (Tag: tag, Name: NativeName(tag))).ToArray();
    void SetMenuBar() => host.Desktop.SetMenuBar([
        new DesktopMenu(text.Messages.menu_language, [
            .. languages.Select(language => new DesktopMenuItem(language.Name, "language")
            {
                RadioGroup = "language",
                CommandParameter = language.Tag,
                IsChecked = locale.Locale == language.Tag,
            }),
        ]),
    ]);
    locale.LocaleChanged += (_, _) => SetMenuBar();
    await provider.StartLanguageAsync(Environment.GetEnvironmentVariable("RUNIC_LOCALE"));
    SetMenuBar();
    // The first Window. Its ViewModel opens more with IRunicWindowService; the application ends when this one closes.
    await using var window = await host.OpenWindowAsync<ProjectWindow>(new RunicWindowOptions { Width = 640, Height = 420 });
    await window.WaitForCloseAsync();
    return 0;
});
