using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;

// S3 (L3-5): an embedded window opens with the native direction and web view language of its window locale
// (IRunicWindowLocale), and its direction follows a later switch; DesktopWindowOptions that set them win.
internal sealed class WindowLocaleChecks
{
    [Test, NotInParallel(Order = 49)]
    public async Task TheWindowLocaleGivesTheWindowItsDirectionAndLanguage()
    {
        if (OperatingSystem.IsMacOS()) return; // AppKit takes both from the application bundle.
        await using var provider = CreateProvider();
        var factory = new NativeDispatchFactory { Directs = true };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var windows = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
        {
            SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
            WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
        });
        LocaleViewModel.Initial = "ar-EG";
        await using var window = await windows.OpenWindowAsync<LocaleWindow>();
        var options = factory.Hosts[0].Options!;
        Require(options.LayoutDirection == DesktopLayoutDirection.RightToLeft, $"Direction at open: {options.LayoutDirection}.");
        Require(options.Language == "ar-EG", $"Language at open: {options.Language}.");

        window.ViewModel.Locale.Locale = "de";
        await WaitUntilAsync(() => factory.Hosts[0].Directions.Contains(DesktopLayoutDirection.LeftToRight));
        Require(DesktopPlatformServices.GetDesktopWindow(window)!.LayoutDirection == DesktopLayoutDirection.LeftToRight,
            "The window does not report its new direction.");
        window.ViewModel.Locale.Locale = "he";
        await WaitUntilAsync(() => factory.Hosts[0].Directions.Count == 2);
        Require(factory.Hosts[0].Directions.Last() == DesktopLayoutDirection.RightToLeft, "Hebrew did not mirror the window.");

        // S4: the pages the surface serves itself follow the window locale.
        var surface = ((DesktopBridgeWindow<LocaleViewModel>)window.Host).Surface;
        using var client = new HttpClient();
        using var missing = await client.GetAsync(new Uri(surface.Url, "missing-page.txt"));
        var page = await missing.Content.ReadAsStringAsync();
        Require(missing.StatusCode == System.Net.HttpStatusCode.NotFound && page.StartsWith("<html lang=\"he\" dir=\"rtl\">", StringComparison.Ordinal),
            $"The missing page did not follow the window locale: {(int)missing.StatusCode} {page}");
    }

    [Test, NotInParallel(Order = 50)]
    public async Task WindowOptionsThatSetThemWinAndAProfileKeepsThePlatformLanguage()
    {
        if (OperatingSystem.IsMacOS()) return;
        await using var provider = CreateProvider();
        var factory = new NativeDispatchFactory { Directs = true };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var profile = Path.Combine(Path.GetTempPath(), "runic-window-locale-" + Guid.NewGuid().ToString("N"));
        var windows = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
        {
            SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
            WindowOptions = new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded, LayoutDirection = DesktopLayoutDirection.LeftToRight, ProfilePath = profile,
            },
        });
        LocaleViewModel.Initial = "ar";
        await using var window = await windows.OpenWindowAsync<LocaleWindow>();
        var options = factory.Hosts[0].Options!;
        Require(options.LayoutDirection == DesktopLayoutDirection.LeftToRight, $"Configured direction replaced: {options.LayoutDirection}.");
        Require(options.Language is null, $"A shared profile got a language: {options.Language}.");
        window.ViewModel.Locale.Locale = "he";
        await Task.Delay(100);
        Require(factory.Hosts[0].Directions.IsEmpty, "A configured direction followed the locale.");
    }

    [Test, NotInParallel(Order = 51)]
    public void TheDirectionOfALocaleWithoutCultureDataComesFromItsScriptOrLanguage()
    {
        Require(Runic.Desktop.Internal.WindowLocaleDirection.FromTag("ar") == DesktopLayoutDirection.RightToLeft, "ar");
        Require(Runic.Desktop.Internal.WindowLocaleDirection.FromTag("fa-IR") == DesktopLayoutDirection.RightToLeft, "fa-IR");
        Require(Runic.Desktop.Internal.WindowLocaleDirection.FromTag("ku-Arab") == DesktopLayoutDirection.RightToLeft, "ku-Arab");
        Require(Runic.Desktop.Internal.WindowLocaleDirection.FromTag("sd-Deva") == DesktopLayoutDirection.LeftToRight, "sd-Deva");
        Require(Runic.Desktop.Internal.WindowLocaleDirection.FromTag("de-CH") == DesktopLayoutDirection.LeftToRight, "de-CH");
        Require(Runic.Desktop.Internal.WindowLocaleDirection.Of("he") == DesktopLayoutDirection.RightToLeft, "he");
        Require(Runic.Desktop.Internal.WindowLocaleDirection.Of("pt-BR") == DesktopLayoutDirection.LeftToRight, "pt-BR");
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<LocaleViewModel>();
        services.AddScoped<Func<IBridgeTransport, LocaleViewModel, IDisposable>>(_ => static (_, _) => new NoopAttachment());
        services.AddRunicWindowService();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            if (waited > 5000) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

// Sets its window's locale in the constructor, before the window opens.
sealed class LocaleViewModel : INotifyPropertyChanged
{
    internal static string? Initial;
    public LocaleViewModel(IRunicWindowLocale locale)
    {
        Locale = locale;
        locale.Locale = Initial;
    }
    public IRunicWindowLocale Locale { get; }
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

sealed class LocaleWindow : RunicWindow<LocaleViewModel>;
