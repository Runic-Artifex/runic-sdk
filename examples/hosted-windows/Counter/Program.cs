using CounterTool;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using Runic.Translations;

await using var provider = new ServiceCollection().AddCounter().BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true,
});
// The C# catalog follows the application's language from its creation, so create it before Run: hosted, the language is
// the host's already when the body starts.
var translations = provider.GetRequiredService<TranslationContext>();

// Hosted mode is the counter's choice: it defines the parameter, `hosted`, which its usage would not list, and only this
// code path accepts a host. Runic defines no flag or environment variable for it, so an application without such a path
// cannot be hosted, and nothing the counter starts inherits it.
if (args is ["hosted"])
{
    try
    {
        // OpenWindowAsync returns without waiting for the host's window to connect the page, so the tests can run the
        // counter without a browser (Tests/HostedCounterTests.cs).
        return RunicDesktopHost.Run(provider, new DesktopHostOptions { WaitForConnection = false },
            new RunicDesktopHostOptions { Guest = RunicGuestOptions.FromStandardInput() with { ApplicationName = "Counter" } },
            async host =>
            {
                // No SetLocaleAsync: the application's language follows the host's (host.Guest.Locale).
                provider.ShowMenuBar(host.Desktop);
                return await RunWindowAsync(host);
            });
    }
    catch (RunicHostedApplicationException error)
    {
        // Started with `hosted` but not by a Runic host, such as from a terminal.
        Console.Error.WriteLine($"The counter runs hosted only when a Runic application starts it ({error.Code}): {error.Message}");
        return 1;
    }
}

return RunicDesktopHost.Run(provider, async host =>
{
    // On its own, the counter picks its language from the operating system's.
    await provider.GetRequiredService<IRunicApplicationLocale>().SetLocaleAsync(
        LocaleNegotiation.Negotiate(DesktopLanguages.Preferred, translations.SupportedLocales, CounterTextCatalog.DefaultLocale));
    provider.ShowMenuBar(host.Desktop);
    return await RunWindowAsync(host);
});

static async Task<int> RunWindowAsync(RunicDesktopHost host)
{
    // Hosted, a host that goes away while the Window opens or is open ends the body; Run then returns 0.
    await using var window = await host.OpenWindowAsync<CounterWindow>(new RunicWindowOptions { Width = 480, Height = 320 });
    await window.WaitForCloseAsync();
    return 0;
}
